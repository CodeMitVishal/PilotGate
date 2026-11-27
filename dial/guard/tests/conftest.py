"""Shared test helpers.

`GuardHarness` runs the real guard app (DIAL SDK + interceptors SDK) in-process. Its upstream (the DIAL Core callback
the interceptor makes) is the deterministic mock model, mounted through httpx's ASGI transport, so the SDK's real
conversion between streaming and non-streaming responses is exercised without Docker. Requests carry the headers
and body shape DIAL Core sends to an interceptor (golden fixtures, `fixtures/dial-core-0.50.8/interceptor/`).
"""

from __future__ import annotations

import asyncio
import json
import sys
from dataclasses import dataclass, field
from pathlib import Path

import httpx
import jsonschema
import pytest

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "dial" / "mockmodel"))

import mockmodel  # noqa: E402

from dial_guard.app import create_app  # noqa: E402
from dial_guard.config import GuardConfig, Settings  # noqa: E402
from dial_guard.detectors import Scanner  # noqa: E402
from dial_guard.events import MemorySink  # noqa: E402
from dial_guard.links import LinkPolicy  # noqa: E402

FIXTURES = Path(__file__).parent / "fixtures" / "dial-core-0.50.8"
SCHEMA_DIR = ROOT / "dial" / "schema"
DIAL_URL = "http://core:8080"
ALLOWED = ["intranet.contoso.example"]
CANARY = "SYS-CANARY-9921"

EVENT_SCHEMA = json.loads((SCHEMA_DIR / "guard-event.v1.schema.json").read_text(encoding="utf-8"))
_validator = jsonschema.Draft202012Validator(EVENT_SCHEMA)


def validate_events(events: list[dict]) -> None:
    for e in events:
        errors = sorted(_validator.iter_errors(e), key=lambda err: err.path)
        assert not errors, f"event does not match the schema: {errors[0].message}\n{json.dumps(e)}"


def scanner(**overrides) -> Scanner:
    cfg = GuardConfig(**{"allowed_link_hosts": ALLOWED, "canaries": [CANARY], **overrides})
    return Scanner(cfg, LinkPolicy(tuple(cfg.allowed_link_hosts), DIAL_URL))


def policy_config(**overrides) -> dict:
    return {"allowed_link_hosts": ALLOWED, "canaries": [CANARY], "profile": "test", **overrides}


class RecordingTransport(httpx.AsyncBaseTransport):
    def __init__(self, inner: httpx.AsyncBaseTransport, log: list[dict]) -> None:
        self._inner = inner
        self._log = log

    async def handle_async_request(self, request: httpx.Request) -> httpx.Response:
        body = request.content
        self._log.append({"url": str(request.url), "headers": dict(request.headers),
                          "json": json.loads(body) if body else None})
        return await self._inner.handle_async_request(request)


@dataclass
class Result:
    status: int
    headers: dict
    text: str
    json: dict | None = None
    events: list = field(default_factory=list)  # SSE events: dicts, or the string "[DONE]"

    def content(self) -> str:
        """Concatenated assistant content of choice 0, from either response shape."""
        if self.json is not None:
            return self.json["choices"][0]["message"].get("content") or ""
        return "".join((c.get("delta") or {}).get("content") or "" for e in self.events if isinstance(e, dict)
                       for c in e.get("choices", []) if c.get("index", 0) == 0)

    def errors(self) -> list[dict]:
        return [e["error"] for e in self.events if isinstance(e, dict) and "error" in e]


def parse_sse(text: str) -> list:
    out = []
    for line in text.split("\n"):
        if line.startswith("data: "):
            payload = line[6:]
            out.append(payload if payload == "[DONE]" else json.loads(payload))
    return out


class GuardHarness:
    def __init__(self, upstream=None) -> None:
        self.sink = MemorySink()
        self.upstream_requests: list[dict] = []
        transport = RecordingTransport(httpx.ASGITransport(app=upstream or mockmodel.app), self.upstream_requests)
        self._client: httpx.AsyncClient | None = None

        async def factory() -> httpx.AsyncClient:
            if self._client is None:
                self._client = httpx.AsyncClient(transport=transport, timeout=30)
            return self._client

        self.app = create_app(Settings(DIAL_URL, False, None), sink=self.sink, client_factory=factory)

    async def acall(self, messages: list[dict] | None = None, *, stream: bool = False, config: dict | None = None,
                    body: dict | None = None, headers: dict | None = None, raw: bytes | None = None) -> Result:
        if body is None:
            body = {"messages": messages or [{"role": "user", "content": "hello"}], "stream": stream}
            body["custom_fields"] = {"interceptor_configuration": config if config is not None else policy_config()}
        hdrs = {"api-key": "per-request-key-for-tests-0001", "x-dial-deployment-id": "mock-echo",
                "x-conversation-id": "conv-test-1", "content-type": "application/json"}
        hdrs.update({k.lower(): v for k, v in (headers or {}).items()})
        hdrs = {k: v for k, v in hdrs.items() if v is not None}
        async with httpx.AsyncClient(transport=httpx.ASGITransport(app=self.app), base_url="http://guard",
                                     timeout=60) as client:
            r = await client.post("/openai/deployments/dial-guard/chat/completions",
                                  content=raw if raw is not None else json.dumps(body), headers=hdrs)
        result = Result(r.status_code, dict(r.headers), r.text)
        if r.headers.get("content-type", "").startswith("text/event-stream"):
            result.events = parse_sse(r.text)
        else:
            try:
                result.json = r.json()
            except ValueError:
                result.json = None
        return result

    def call(self, *args, **kwargs) -> Result:
        return asyncio.run(self.acall(*args, **kwargs))


@pytest.fixture
def harness() -> GuardHarness:
    return GuardHarness()
