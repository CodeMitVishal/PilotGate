"""Spike echo application. Records every request DIAL Core sends to it (path, query, headers, body) and answers
in the OpenAI chat completions format, streaming or not. Commands in the last user message trigger edge cases."""
import asyncio
import json
import sys
import time
from pathlib import Path

import uvicorn
from starlette.applications import Starlette
from starlette.requests import Request
from starlette.responses import JSONResponse, StreamingResponse
from starlette.routing import Route

LOG = Path(sys.argv[1])
PORT = int(sys.argv[2])


def record(entry: dict) -> None:
    with LOG.open("a", encoding="utf-8") as f:
        f.write(json.dumps(entry, ensure_ascii=False) + "\n")


def chunk(deployment: str, delta: dict, finish: str | None = None, index: int = 0) -> str:
    body = {"id": "echo-1", "object": "chat.completion.chunk", "created": 1, "model": deployment,
            "choices": [{"index": index, "delta": delta, "finish_reason": finish}]}
    return "data: " + json.dumps(body) + "\n\n"


async def chat(request: Request):
    deployment = request.path_params["deployment"]
    raw = await request.body()
    try:
        body = json.loads(raw)
    except json.JSONDecodeError:
        body = {"_unparsed": raw.decode("utf-8", "replace")}
    record({"t": time.time(), "deployment": deployment, "method": request.method, "path": request.url.path,
            "query": request.url.query, "headers": dict(request.headers), "body": body})

    messages = body.get("messages") or [{}]
    text = messages[-1].get("content") or ""
    if isinstance(text, list):
        text = " ".join(p.get("text", "") for p in text if isinstance(p, dict))
    stream = bool(body.get("stream"))

    if "!500" in text:
        return JSONResponse({"error": {"message": "echo failure", "type": "internal_server_error", "code": "500"}},
                            status_code=500)
    if "!slow" in text:
        await asyncio.sleep(5)

    pieces = ["You ", "said: ", text]
    if "!whoami" in text:
        import httpx
        async with httpx.AsyncClient() as c:
            r = await c.get("http://localhost:8080/v1/user/info", headers={"Api-Key": request.headers.get("api-key", "")})
        pieces = [f"whoami {r.status_code} {r.text}"]
    if "!pii" in text:
        pieces = ["Contact ", "max.mu", "ster@example.com or IBAN DE89 3704 ", "0044 0532 0130 00 now."]
    if "!link" in text:
        pieces = ["See [docs](https://intra", "net.contoso.example/a) and ", "[x](https://evil.example/p?q=1) end."]

    if not stream:
        message: dict = {"role": "assistant", "content": "".join(pieces)}
        if "!toolcall" in text:
            message = {"role": "assistant", "content": None, "tool_calls": [
                {"id": "call_1", "type": "function", "function": {"name": "lookup", "arguments": "{\"q\":\"x\"}"}}]}
        if "!attach" in text:
            message["custom_content"] = {"attachments": [{"type": "text/markdown", "title": "Doc", "data": "attached body"}],
                                         "stages": [{"index": 0, "name": "Search", "status": "completed", "content": "stage body"}]}
        return JSONResponse({"id": "echo-1", "object": "chat.completion", "created": 1, "model": deployment,
                             "choices": [{"index": 0, "message": message, "finish_reason": "stop"}],
                             "usage": {"prompt_tokens": 3, "completion_tokens": 4, "total_tokens": 7}})

    async def gen():
        yield chunk(deployment, {"role": "assistant"})
        if "!toolcall" in text:
            yield chunk(deployment, {"tool_calls": [{"index": 0, "id": "call_1", "type": "function",
                                                     "function": {"name": "lookup", "arguments": ""}}]})
            yield chunk(deployment, {"tool_calls": [{"index": 0, "function": {"arguments": "{\"q\":\"x\"}"}}]})
        elif "!attach" in text:
            yield chunk(deployment, {"custom_content": {"stages": [{"index": 0, "name": "Search", "status": None}]}})
            yield chunk(deployment, {"custom_content": {"stages": [{"index": 0, "content": "stage body", "status": "completed"}]}})
            yield chunk(deployment, {"custom_content": {"attachments": [{"index": 0, "type": "text/markdown", "title": "Doc", "data": "attached body"}]}})
            yield chunk(deployment, {"content": "done"})
        else:
            for i, piece in enumerate(pieces):
                if "!midstream-error" in text and i == 1:
                    yield "data: " + json.dumps({"error": {"message": "echo broke mid-stream", "type": "internal_server_error", "code": "500"}}) + "\n\n"
                    yield "data: [DONE]\n\n"
                    return
                yield chunk(deployment, {"content": piece})
                await asyncio.sleep(0.05)
        yield chunk(deployment, {}, "stop")
        yield ("data: " + json.dumps({"id": "echo-1", "object": "chat.completion.chunk", "created": 1, "model": deployment,
                                      "choices": [], "usage": {"prompt_tokens": 3, "completion_tokens": 4, "total_tokens": 7}}) + "\n\n")
        yield "data: [DONE]\n\n"

    return StreamingResponse(gen(), media_type="text/event-stream")


async def health(_: Request):
    return JSONResponse({"status": "ok"})


app = Starlette(routes=[Route("/openai/deployments/{deployment}/chat/completions", chat, methods=["POST"]),
                        Route("/health", health)])

if __name__ == "__main__":
    uvicorn.run(app, host="127.0.0.1", port=PORT, log_level="warning")
