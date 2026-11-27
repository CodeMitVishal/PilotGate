"""The DIAL Guard service: a DIAL app exposing the interceptor at /openai/deployments/dial-guard/chat/completions.

`SanitizingChatCompletion` wraps the SDK's chat completion so that
* every request gets a `Recorder`, and its events are written when the request ends, whatever the outcome;
* the SDK's own internal failures, which it would return as raw exception text, become a sanitized `GuardError`.
  DIAL Core's errors (upstream failures) and deliberate DIAL errors pass through unchanged.
"""

from __future__ import annotations

import logging
import time
from collections.abc import Awaitable, Callable

import httpx
import openai
from aidial_interceptors_sdk.chat_completion.adapter import interceptor_to_chat_completion
from aidial_sdk import DIALApp
from aidial_sdk.chat_completion import ChatCompletion as DialChatCompletion
from aidial_sdk.chat_completion import Request as DialRequest
from aidial_sdk.chat_completion import Response as DialResponse
from aidial_sdk.deployment.configuration import ConfigurationRequest
from aidial_sdk.exceptions import HTTPException as DialException

from . import __version__
from .config import Settings
from .errors import AttachmentRejected, GuardError
from .events import EventSink, JsonLinesSink, Recorder
from .interceptor import CURRENT_RECORDER, DIAL_URL, GuardInterceptor

INTERCEPTOR_ID = "dial-guard"
HTTPClientFactory = Callable[[], Awaitable[httpx.AsyncClient]]

_log = logging.getLogger("dial_guard")


def _passes_through(error: DialException) -> bool:
    """True for errors that may reach the client as they are."""
    if isinstance(error, GuardError | AttachmentRejected):
        return True
    cause = error.__cause__
    return cause is None or cause is error or isinstance(cause, DialException | openai.APIError)


def _outcome(error: DialException) -> str:
    if isinstance(error, GuardError):
        return "error"
    if isinstance(error, AttachmentRejected):
        return "rejected"
    return "upstream_error" if error.status_code >= 500 or error.status_code in (401, 403, 404, 429) else "rejected"


class SanitizingChatCompletion(DialChatCompletion):
    def __init__(self, inner: DialChatCompletion, sink: EventSink, dial_url: str) -> None:
        self._inner = inner
        self._sink = sink
        self._dial_url = dial_url

    async def chat_completion(self, request: DialRequest, response: DialResponse) -> None:
        recorder = Recorder(request.headers.get("x-conversation-id"), request.headers.get("x-dial-deployment-id"),
                            bool(request.stream))
        token = CURRENT_RECORDER.set(recorder)
        url_token = DIAL_URL.set(self._dial_url)
        started = time.perf_counter()
        outcome = "ok"
        try:
            await self._inner.chat_completion(request, response)
            if recorder.blocked:
                outcome = "blocked"
        except DialException as error:
            if _passes_through(error):
                outcome = _outcome(error)
                raise
            recorder.guard_error(type(error.__cause__).__name__, "sdk")
            outcome = "error"
            raise GuardError(recorder.request_id) from None
        except Exception as error:  # noqa: BLE001 - defensive: the SDK normally converts everything
            recorder.guard_error(type(error).__name__, "sdk")
            outcome = "error"
            raise GuardError(recorder.request_id) from None
        finally:
            CURRENT_RECORDER.reset(token)
            DIAL_URL.reset(url_token)
            try:
                self._sink.emit(recorder.events(outcome, (time.perf_counter() - started) * 1000))
            except Exception:  # noqa: BLE001 - losing events must never break a response
                _log.exception("could not write guard events")

    async def configuration(self, request: ConfigurationRequest) -> dict:
        return await self._inner.configuration(request)


def default_client_factory() -> HTTPClientFactory:
    client: httpx.AsyncClient | None = None

    async def factory() -> httpx.AsyncClient:
        nonlocal client
        if client is None:
            client = httpx.AsyncClient(timeout=httpx.Timeout(600, connect=10), follow_redirects=False,
                                       limits=httpx.Limits(max_connections=500, max_keepalive_connections=100))
        return client

    return factory


def create_app(settings: Settings, sink: EventSink | None = None,
               client_factory: HTTPClientFactory | None = None) -> DIALApp:
    app = DIALApp(
        description=f"DIAL Guard {__version__}",
        dial_url=settings.dial_url,
        propagate_auth_headers=True,
        add_healthcheck=True,
        allow_extra_request_fields=True,
        headers_to_proxy=["x-conversation-id"],
    )
    sink = sink or JsonLinesSink(stdout=settings.events_stdout, path=settings.events_file)
    impl = interceptor_to_chat_completion(GuardInterceptor, settings.dial_url,
                                          client_factory or default_client_factory())
    app.add_chat_completion(INTERCEPTOR_ID, SanitizingChatCompletion(impl, sink, settings.dial_url))
    return app


def app_from_env() -> DIALApp:
    """ASGI factory for uvicorn: `uvicorn --factory dial_guard.app:app_from_env`."""
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s %(message)s")
    return create_app(Settings.from_env())
