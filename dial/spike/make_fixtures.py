"""Turns the raw spike captures into redacted golden fixtures for the guard's tests.
Usage: python make_fixtures.py <captures dir> <fixtures dir>"""
import json
import sys
from pathlib import Path

CAPTURES = Path(sys.argv[1])
OUT = Path(sys.argv[2])
VOLATILE_HEADERS = {"traceparent", "date", "x-dial-trace-id", "x-dial-span-id"}


def load(name: str) -> list[dict]:
    path = CAPTURES / name
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()] if path.exists() else []


def redact_headers(headers: dict) -> dict:
    out = {}
    for key, value in headers.items():
        k = key.lower()
        if k in ("api-key", "authorization"):
            out[key] = f"<redacted {len(value)} chars>"
        elif k in VOLATILE_HEADERS:
            out[key] = "<volatile>"
        elif k == "host":
            out[key] = "<host>"
        else:
            out[key] = value
    return out


def parse_sse(lines: list[str]) -> list:
    events = []
    for line in lines:
        if not line.startswith("data: "):
            continue
        payload = line[len("data: "):]
        events.append(payload if payload == "[DONE]" else json.loads(payload))
    return events


def write(rel: str, data) -> None:
    path = OUT / rel
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


# 1. What the client receives from DIAL Core, per case
for entry in [e for f in sorted(CAPTURES.glob("client*.jsonl"), key=lambda f: (len(f.name), f.name)) for e in load(f.name)]:
    case = entry["case"]
    fixture = {"case": case, "deployment": entry.get("deployment"), "stream": entry.get("stream"),
               "request_body": entry.get("request_body"), "status": entry.get("status"),
               "content_type": (entry.get("headers") or {}).get("content-type"),
               "response_header_names": sorted((entry.get("headers") or {}).keys())}
    if "sse_lines" in entry:
        fixture["sse_events"] = parse_sse(entry["sse_lines"])
    elif "json" in entry:
        fixture["body"] = entry["json"]
    else:
        fixture["body_text"] = entry.get("raw", entry.get("error"))
    write(f"client/{case}.json", fixture)  # later captures of the same case overwrite earlier ones

# 2. What an interceptor receives, and the response chunks it sees, grouped per request
groups: list[dict] = []
for entry in load("interceptor.jsonl"):
    if entry["kind"] == "request":
        groups.append({"request": entry, "chunks": []})
    elif groups:
        groups[-1]["chunks"].append(entry["chunk"])

for group in groups:
    req = group["request"]
    conv = req["headers"].get("x-conversation-id", "")
    if not conv.startswith("conv-"):
        continue  # second hop of a chain carries no conversation id
    case = conv[len("conv-"):]
    write(f"interceptor/{case}.json", {
        "case": case,
        "deployment_id": req["deployment_id"],
        "upstream_deployment_id": req["upstream_deployment_id"],
        "api_key_present": req["api_key_present"],
        "authorization_present": req["authorization_present"],
        "headers": redact_headers(req["headers"]),
        "configuration": req["config"],
        "body": req["body"],
        "response_chunks": group["chunks"],
    })

# 3. What the application receives, with and without an interceptor in front of it
seen = set()
for entry in load("echo.jsonl"):
    text = (entry["body"].get("messages") or [{}])[-1].get("content")
    key = (entry["deployment"], text)
    if key in seen:
        continue
    seen.add(key)
    name = f"{entry['deployment']}__{str(text).replace(' ', '_').replace('!', '')[:40]}"
    write(f"app/{name}.json", {"deployment": entry["deployment"], "path": entry["path"], "query": entry["query"],
                               "headers": redact_headers(entry["headers"]), "body": entry["body"]})

print("fixtures written to", OUT)
for sub in ("client", "interceptor", "app"):
    print(f"  {sub}: {len(list((OUT / sub).glob('*.json')))} files")
