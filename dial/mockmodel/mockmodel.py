"""Deterministic OpenAI-compatible model for tests and the local DIAL stack.

POST /openai/deployments/{deployment}/chat/completions answers in the chat completions format, streaming or not,
in the same shape as the spike's echo application (`dial/spike/echo_app.py`, golden fixtures). Commands in the last
user message select a scripted answer, so tests can drive every guard path through a real DIAL Core:

  !echo-request      answer with the JSON of the messages it received (shows what the guard forwarded)
  !pii               answer with an e-mail address and an IBAN
  !link              answer with an allowed markdown link, a refused link and a refused image
  !canary            answer that leaks the configured canary SYS-CANARY-9921 mid-sentence
  !toolcall          answer with a tool call whose arguments contain an e-mail address
  !attach            answer with a stage and two attachments (one refused URL)
  !long-url          answer with a 5,000-character link
  !500               fail with HTTP 500 before answering
  !midstream-error   stream two chunks, then an error event
  !slow              wait 3 seconds before answering
  !n2                (with n=2) answer with two choices
  !headers           answer with the request headers it received (api-key redacted)

Anything else is echoed back as "You said: <text>".
"""

from __future__ import annotations

import asyncio
import json
import os

from starlette.applications import Starlette
from starlette.requests import Request
from starlette.responses import JSONResponse, StreamingResponse
from starlette.routing import Route

CANARY = "SYS-CANARY-9921"


def _script(text: str, body: dict, headers: dict) -> list[dict]:
    """The answer as a list of deltas (one choice)."""
    if "!echo-request" in text:
        return [{"content": json.dumps(body.get("messages"), ensure_ascii=False)}]
    if "!headers" in text:
        shown = {k: ("<redacted>" if k.lower() in ("api-key", "authorization") else v) for k, v in headers.items()}
        return [{"content": json.dumps(shown, sort_keys=True)}]
    if "!pii" in text:
        return [{"content": p} for p in ["Contact ", "max.mu", "ster@example.com or IBAN DE89 3704 ",
                                         "0044 0532 0130 00 now."]]
    if "!link" in text:
        return [{"content": p} for p in ["See [docs](https://intra", "net.contoso.example/a), ",
                                         "[portal](https://evil.example/p?q=1) and ",
                                         "![pixel](https://evil.example/t.gif) end."]]
    if "!canary" in text:
        return [{"content": p} for p in ["The configuration marker is SYS-CAN", "ARY-9921 and more ", "text."]]
    if "!toolcall" in text:
        return [{"tool_calls": [{"index": 0, "id": "call_1", "type": "function",
                                 "function": {"name": "lookup", "arguments": ""}}]},
                {"tool_calls": [{"index": 0, "function": {"arguments": "{\"email\":\"max@exam"}}]},
                {"tool_calls": [{"index": 0, "function": {"arguments": "ple.com\"}"}}]}]
    if "!attach" in text:
        return [{"custom_content": {"stages": [{"index": 0, "name": "Search", "status": None}]}},
                {"custom_content": {"stages": [{"index": 0, "content": "Found max@example.com in ",
                                                "status": None}]}},
                {"custom_content": {"stages": [{"index": 0, "content": "the register.", "status": "completed"}]}},
                {"custom_content": {"attachments": [
                    {"index": 0, "type": "text/markdown", "title": "Notes", "data": "Call +49 151 23456789."},
                    {"index": 1, "type": "text/html", "title": "External", "url": "https://evil.example/doc"}]}},
                {"content": "Done."}]
    if "!long-url" in text:
        return [{"content": "Link: https://evil.example/" + "a" * 2500}, {"content": "b" * 2500 + " end."}]
    return [{"content": p} for p in ["You ", "said: ", text]]


def _chunk(deployment: str, choice_delta: dict, index: int = 0, finish: str | None = None) -> str:
    body = {"id": "mock-1", "object": "chat.completion.chunk", "created": 1, "model": deployment,
            "choices": [{"index": index, "delta": choice_delta, "finish_reason": finish}]}
    return "data: " + json.dumps(body, ensure_ascii=False) + "\n\n"


def _block_message(deltas: list[dict]) -> dict:
    message: dict = {"role": "assistant"}
    for d in deltas:
        for key, value in d.items():
            if key == "content":
                message["content"] = (message.get("content") or "") + value
            elif key == "tool_calls":
                calls = message.setdefault("tool_calls", [])
                for call in value:
                    i = call["index"]
                    while len(calls) <= i:
                        calls.append({"id": "", "type": "function", "function": {"name": "", "arguments": ""}})
                    target = calls[i]
                    target["id"] = call.get("id") or target["id"]
                    fn = call.get("function", {})
                    target["function"]["name"] += fn.get("name", "")
                    target["function"]["arguments"] += fn.get("arguments", "")
            elif key == "custom_content":
                cc = message.setdefault("custom_content", {})
                for list_key in ("stages", "attachments"):
                    for item in value.get(list_key, []):
                        items = cc.setdefault(list_key, [])
                        while len(items) <= item["index"]:
                            items.append({})
                        for k, v in item.items():
                            if k == "index" or v is None:
                                continue
                            if k in ("content", "data", "name", "title") and isinstance(items[item["index"]].get(k), str):
                                items[item["index"]][k] += v
                            else:
                                items[item["index"]][k] = v
    if "tool_calls" in message and "content" not in message:
        message["content"] = None
    return message


async def chat(request: Request):
    deployment = request.path_params["deployment"]
    try:
        body = json.loads(await request.body())
    except json.JSONDecodeError:
        return JSONResponse({"error": {"message": "invalid JSON", "type": "invalid_request_error"}}, status_code=400)
    messages = body.get("messages") or [{}]
    text = messages[-1].get("content") or ""
    if isinstance(text, list):
        text = " ".join(p.get("text", "") for p in text if isinstance(p, dict))
    if "!500" in text:
        return JSONResponse({"error": {"message": "mock failure", "type": "internal_server_error", "code": "500"}},
                            status_code=500)
    if "!slow" in text:
        await asyncio.sleep(3)
    deltas = _script(text, body, dict(request.headers))
    choices = 2 if "!n2" in text and body.get("n") == 2 else 1
    usage = {"prompt_tokens": 3, "completion_tokens": 4, "total_tokens": 7}

    if not body.get("stream"):
        msg = _block_message(deltas)
        return JSONResponse({"id": "mock-1", "object": "chat.completion", "created": 1, "model": deployment,
                             "choices": [{"index": i, "message": msg,
                                          "finish_reason": "tool_calls" if "tool_calls" in msg else "stop"}
                                         for i in range(choices)],
                             "usage": usage})

    async def stream():
        for i in range(choices):
            yield _chunk(deployment, {"role": "assistant"}, i)
        for n, delta in enumerate(deltas):
            if "!midstream-error" in text and n == 2:
                yield "data: " + json.dumps({"error": {"message": "mock broke mid-stream",
                                                       "type": "internal_server_error", "code": "500"}}) + "\n\n"
                yield "data: [DONE]\n\n"
                return
            for i in range(choices):
                yield _chunk(deployment, delta, i)
            await asyncio.sleep(0)
        finish = "tool_calls" if any("tool_calls" in d for d in deltas) else "stop"
        for i in range(choices):
            yield _chunk(deployment, {}, i, finish)
        yield "data: " + json.dumps({"id": "mock-1", "object": "chat.completion.chunk", "created": 1,
                                     "model": deployment, "choices": [], "usage": usage}) + "\n\n"
        yield "data: [DONE]\n\n"

    return StreamingResponse(stream(), media_type="text/event-stream")


async def health(_: Request):
    return JSONResponse({"status": "ok"})


app = Starlette(routes=[Route("/openai/deployments/{deployment}/chat/completions", chat, methods=["POST"]),
                        Route("/health", health)])

if __name__ == "__main__":
    import uvicorn

    uvicorn.run(app, host=os.environ.get("HOST", "0.0.0.0"), port=int(os.environ.get("PORT", "7001")),  # noqa: S104
                log_level="warning")
