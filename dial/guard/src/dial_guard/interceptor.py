"""The DIAL Guard interceptor, built on EPAM's `aidial-interceptors-sdk` (pinned 0.9.0).

Hooks used (read from the installed SDK source, see `dial/spike/README.md`):

* `on_request_message` for every request message (request side),
* `on_stream_chunk` for every response chunk; the SDK turns a non-streaming upstream response into one chunk,
* `on_stream_end` / `on_stream_error` to release or drop what is still held back.

Failure handling: anything unexpected inside the guard is a guard error. `fail_closed` (default) ends the response
with a sanitized error; content still held back is never sent. `fail_open` sends what is held back unchecked,
forwards the rest of the response unchanged, and records each passthrough.
"""

from __future__ import annotations

import time
from contextvars import ContextVar

from aidial_interceptors_sdk.chat_completion.annotated_value import AnnotatedException
from aidial_interceptors_sdk.chat_completion.base import ChatCompletionInterceptor
from aidial_interceptors_sdk.chat_completion.element_path import ElementPath
from aidial_sdk.exceptions import HTTPException as DialException
from aidial_sdk.pydantic_v1 import PrivateAttr
from pydantic import BaseModel

from .config import GuardConfig
from .detectors import Scanner
from .errors import GuardError, GuardInternalError
from .events import Recorder
from .links import LinkPolicy
from .request import RequestGuard
from .response import ResponseGuard

CURRENT_RECORDER: ContextVar[Recorder | None] = ContextVar("dial_guard_recorder", default=None)
DIAL_URL: ContextVar[str | None] = ContextVar("dial_guard_dial_url", default=None)


class _State:
    __slots__ = ("config", "recorder", "request_guard", "response_guard", "passthrough", "started", "scan_seconds")

    def __init__(self, config: GuardConfig, recorder: Recorder, dial_url: str | None) -> None:
        policy = LinkPolicy(tuple(config.allowed_link_hosts), dial_url)
        enforce = config.mode == "enforce"
        self.config = config
        self.recorder = recorder
        self.request_guard = RequestGuard(
            config, policy, lambda loc, det, act, n: recorder.finding("request", loc, det, act, n))
        self.response_guard = ResponseGuard(
            Scanner(config, policy), policy, enforce,
            lambda loc, det, act, n: recorder.finding("response", loc, det, act, n))
        self.passthrough = False
        self.started = time.perf_counter()
        self.scan_seconds = 0.0

    def timed(self, fn, *args):
        """Runs one guard step and charges its time to the request's budget (max_scan_seconds)."""
        started = time.perf_counter()
        result = fn(*args)
        self.scan_seconds += time.perf_counter() - started
        if self.scan_seconds > self.config.max_scan_seconds:
            raise GuardInternalError("the request used up its scan time budget")
        return result


class GuardInterceptor(ChatCompletionInterceptor):
    _guard: _State | None = PrivateAttr(None)

    @classmethod
    async def configuration_schema(cls) -> type[BaseModel] | None:
        return GuardConfig

    # --- state --------------------------------------------------------------------------------------------------
    def _state(self) -> _State:
        if self._guard is None:
            config = self.configuration if isinstance(self.configuration, GuardConfig) else GuardConfig()
            recorder = CURRENT_RECORDER.get()
            if recorder is None:  # used outside the app wrapper (tests): record into a throwaway recorder
                recorder = Recorder(None, self.upstream_deployment_id, bool(self.request.stream))
            recorder.configure(config.profile, config.mode)
            self._guard = _State(config, recorder, DIAL_URL.get())
        return self._guard

    def _on_error(self, st: _State, error: Exception, phase: str) -> None:
        """Records a guard error. Raises the sanitized error when failing closed; returns when failing open."""
        st.recorder.guard_error(type(error).__name__, phase)
        if st.config.on_guard_error == "fail_closed":
            raise GuardError(st.recorder.request_id) from None
        st.passthrough = True

    # --- request ------------------------------------------------------------------------------------------------
    async def on_request_message(self, path: ElementPath, message: dict) -> list[dict]:
        st = self._state()
        if st.passthrough:
            return [message]
        try:
            return [st.timed(st.request_guard.process_message, path.message_idx or 0, message)]
        except DialException:
            raise  # deliberate rejections (400) keep their message
        except Exception as error:  # noqa: BLE001 - every unexpected failure goes through the failure mode
            self._on_error(st, error, "request")
            st.recorder.fail_open(f"messages[{path.message_idx or 0}]")
            return [message]

    # --- response -----------------------------------------------------------------------------------------------
    async def on_stream_chunk(self, chunk: dict) -> None:
        st = self._state()
        if st.passthrough:
            self.send_chunk(chunk)
            return
        try:
            out = st.timed(st.response_guard.process, chunk)
        except Exception as error:  # noqa: BLE001
            self._on_error(st, error, "response")
            self._release_raw(st)
            self.send_chunk(chunk)
            return
        if st.config.mode == "monitor":
            self.send_chunk(chunk)
        else:
            for c in out:
                self.send_chunk(c)

    def _release_raw(self, st: _State) -> None:
        for loc in st.response_guard.held_locations():
            st.recorder.fail_open(loc)
        if st.config.mode == "enforce":
            for c in st.response_guard.release_raw():
                self.send_chunk(c)

    async def on_stream_end(self) -> None:
        st = self._state()
        if st.passthrough:
            return
        try:
            out = st.timed(st.response_guard.finish)
        except Exception as error:  # noqa: BLE001
            self._on_error(st, error, "response_end")
            self._release_raw(st)
            return
        if st.config.mode == "enforce":
            for c in out:
                self.send_chunk(c)

    async def on_stream_error(self, error: AnnotatedException) -> None:
        # The upstream failed mid-stream. Release what is held back (fully checked), then pass the upstream error on.
        st = self._state()
        if not st.passthrough:
            try:
                out = st.response_guard.finish()
                if st.config.mode == "enforce":
                    for c in out:
                        self.send_chunk(c)
            except Exception as guard_failure:  # noqa: BLE001
                st.recorder.guard_error(type(guard_failure).__name__, "response_end")
        raise error.error
