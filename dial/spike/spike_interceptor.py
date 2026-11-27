"""Spike interceptor built on aidial-interceptors-sdk 0.9.0. Records the headers and body DIAL Core sends to an
interceptor, its configuration, and every response chunk it sees. Commands in the last user message make it fail
on purpose, to see how DIAL Core reports interceptor failures."""
import json
import os
import sys
import time
from pathlib import Path

import uvicorn
from aidial_sdk import DIALApp
from aidial_sdk.pydantic_v1 import PrivateAttr
from pydantic import BaseModel

from aidial_interceptors_sdk.chat_completion.adapter import interceptor_to_chat_completion
from aidial_interceptors_sdk.chat_completion.base import ChatCompletionInterceptor
from aidial_interceptors_sdk.utils._http_client import get_http_client

LOG = Path(sys.argv[1])
PORT = int(sys.argv[2])
DIAL_URL = os.environ["DIAL_URL"]


def record(entry: dict) -> None:
    with LOG.open("a", encoding="utf-8") as f:
        f.write(json.dumps(entry, ensure_ascii=False, default=str) + "\n")


class SpikeConfig(BaseModel):
    mode: str = "unset"


class SpikeLogger(ChatCompletionInterceptor):
    _break_mid: bool = PrivateAttr(False)
    _chunks: int = PrivateAttr(0)

    @classmethod
    async def configuration_schema(cls) -> type[BaseModel] | None:
        return SpikeConfig

    async def on_request(self, request: dict) -> dict:
        text = str(request.get("messages", [{}])[-1].get("content"))
        record({"t": time.time(), "kind": "request", "deployment_id": self.request.deployment_id,
                "upstream_deployment_id": self.upstream_deployment_id,
                "api_key_present": bool(self.request.api_key), "api_key_len": len(self.request.api_key or ""),
                "authorization_present": self.request.bearer_token is not None,
                "headers": dict(self.request.headers), "config": self.configuration.model_dump() if self.configuration else None,
                "body": request})
        if "!break-before" in text:
            raise RuntimeError("spike interceptor failed before calling upstream")
        self._break_mid = "!break-mid" in text
        self._chunks = 0
        return request

    async def on_stream_chunk(self, chunk: dict) -> None:
        record({"t": time.time(), "kind": "chunk", "chunk": chunk})
        self._chunks += 1
        if self._break_mid and self._chunks == 2:
            raise RuntimeError("spike interceptor failed mid-stream")
        self.send_chunk(chunk)


async def client_factory():
    return get_http_client()


app = DIALApp(dial_url=DIAL_URL, propagate_auth_headers=True, add_healthcheck=True, allow_extra_request_fields=True)
app.add_chat_completion("spike-logger", interceptor_to_chat_completion(SpikeLogger, DIAL_URL, client_factory))

if __name__ == "__main__":
    uvicorn.run(app, host="127.0.0.1", port=PORT, log_level="warning")
