"""Round 2 of the spike: identity through a per-request key, interceptor configuration without defaults,
interceptor chains, and (with argument 'down') what DIAL Core returns while the interceptor or the app is down."""
import json
import sys
import time
from pathlib import Path

import httpx

CORE = "http://localhost:8080"
OUT = Path(sys.argv[1])
PHASE = sys.argv[2]

ROUNDS = {
    "up": [
        ("whoami_direct_adjuster", "echo-direct", "spike_key_adjuster", False, "please !whoami", {}),
        ("whoami_via_interceptor_adjuster", "echo", "spike_key_adjuster", False, "please !whoami", {}),
        ("nodefaults_client_config", "echo-nodefaults", "spike_key_default", False, "client config nodefaults",
         {"custom_fields": {"interceptor_configuration": {"mode": "from-client"}}}),
        ("nodefaults_no_config", "echo-nodefaults", "spike_key_default", False, "no config nodefaults", {}),
        ("chain_two_interceptors", "echo-global", "spike_key_default", True, "chain", {}),
    ],
    "headers": [
        ("persona_header_direct", "echo-direct", "spike_key_default", False, "persona header direct", {},
         {"X-Dev-Persona": "sam.siu", "X-DIAL-Deployment-Id": "spoofed", "X-Upstream-Key": "spoofed"}),
        ("persona_header_via_interceptor", "echo", "spike_key_default", False, "persona header via interceptor", {},
         {"X-Dev-Persona": "sam.siu", "X-DIAL-Deployment-Id": "spoofed", "X-Upstream-Key": "spoofed"}),
    ],
    "conv_proxy": [
        ("conv_id_proxied", "echo", "spike_key_default", False, "conversation id proxied", {}),
    ],
    "interceptor_down": [
        ("block_interceptor_down", "echo", "spike_key_default", False, "interceptor down", {}),
        ("stream_interceptor_down", "echo", "spike_key_default", True, "interceptor down", {}),
    ],
    "app_down": [
        ("block_app_down", "echo", "spike_key_default", False, "app down", {}),
        ("stream_app_down", "echo", "spike_key_default", True, "app down", {}),
        ("block_app_down_direct", "echo-direct", "spike_key_default", False, "app down direct", {}),
    ],
}


def run_case(client: httpx.Client, case) -> dict:
    name, deployment, key, stream, text, extra = case[:6]
    extra_headers = case[6] if len(case) > 6 else {}
    body = {"messages": [{"role": "user", "content": text}], "stream": stream}
    body.update(extra)
    headers = {"Api-Key": key, "Content-Type": "application/json", "X-Conversation-Id": f"conv-{name}"}
    headers.update(extra_headers)
    started = time.time()
    try:
        with client.stream("POST", f"{CORE}/openai/deployments/{deployment}/chat/completions",
                           headers=headers, json=body, timeout=90) as r:
            raw = r.read().decode("utf-8", "replace")
            result = {"status": r.status_code, "headers": dict(r.headers)}
    except Exception as e:  # noqa: BLE001
        return {"case": name, "error": f"{type(e).__name__}: {e}", "seconds": round(time.time() - started, 2)}
    result.update({"case": name, "deployment": deployment, "stream": stream, "request_body": body,
                   "seconds": round(time.time() - started, 2)})
    if stream and raw.startswith("data:"):
        result["sse_lines"] = raw.split("\n")
    else:
        try:
            result["json"] = json.loads(raw)
        except json.JSONDecodeError:
            result["raw"] = raw
    return result


with httpx.Client() as c, OUT.open("a", encoding="utf-8") as f:
    for case in ROUNDS[PHASE]:
        res = run_case(c, case)
        f.write(json.dumps(res, ensure_ascii=False) + "\n")
        body = res.get("json", res.get("raw", res.get("sse_lines", res.get("error"))))
        print(f"{case[0]:<34} {('HTTP ' + str(res['status'])) if 'status' in res else 'ERR':<9} {res['seconds']}s  {json.dumps(body)[:330]}")
