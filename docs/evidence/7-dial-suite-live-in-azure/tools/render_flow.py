"""Renders the captured evidence (flow.json, azure.json, logs.json, gate.json, final.json, deploy.log, destroy.log) into
one self-contained page, flow.html. Each section can be shown alone with ?only=<id>, for screenshots.
Usage: python render_flow.py <evidence-dir>
"""

from __future__ import annotations

import html
import json
import re
import sys
from pathlib import Path

D = Path(sys.argv[1])


def load(name: str):
    p = D / name
    return json.loads(p.read_text(encoding="utf-8")) if p.exists() else None


def text(name: str) -> str:
    p = D / name
    return p.read_text(encoding="utf-8", errors="replace") if p.exists() else ""


def e(value) -> str:
    return html.escape("" if value is None else str(value))


def cut(value: str, n: int) -> str:
    value = value or ""
    return value if len(value) <= n else value[: n - 1] + "…"


flow, azure, logs, gate, final = load("flow.json"), load("azure.json"), load("logs.json"), load("gate.json"), load("final.json")
deploy_log, destroy_log = text("deploy.log"), text("destroy.log")
dial_url = (flow or {}).get("dial_url", "")
sections: list[tuple[str, str, str]] = []

# --- architecture -------------------------------------------------------------------------------------------------
svg = """
<svg viewBox="0 0 1180 350" width="1180" height="350" role="img" aria-label="Request flow of the deployment">
 <defs><marker id="a" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse">
  <path d="M0,0 L10,5 L0,10 z" fill="#57606a"/></marker></defs>
 <style>.b{fill:#fff;stroke:#57606a;stroke-width:1.5}.t{font:600 14px system-ui,Segoe UI,sans-serif;fill:#1f2328}
  .s{font:12px system-ui,Segoe UI,sans-serif;fill:#57606a}.l{stroke:#57606a;stroke-width:1.5;fill:none;marker-end:url(#a)}
  .env{fill:#f6f8fa;stroke:#8c959f;stroke-dasharray:6 4}</style>
 <rect x="10" y="140" width="150" height="70" rx="8" class="b"/><text x="85" y="170" text-anchor="middle" class="t">DIAL user</text>
 <text x="85" y="190" text-anchor="middle" class="s">or PilotGate</text>
 <rect x="200" y="14" width="760" height="326" rx="12" class="env"/>
 <text x="215" y="36" class="s">Container Apps environment (peer-to-peer encryption)</text>
 <rect x="230" y="140" width="200" height="70" rx="8" class="b"/><text x="330" y="168" text-anchor="middle" class="t">DIAL Core 0.50.8</text>
 <text x="330" y="188" text-anchor="middle" class="s">external HTTPS · Redis sidecar</text>
 <rect x="560" y="48" width="200" height="64" rx="8" class="b"/><text x="660" y="75" text-anchor="middle" class="t">DIAL Guard</text>
 <text x="660" y="94" text-anchor="middle" class="s">interceptor · internal</text>
 <rect x="560" y="160" width="200" height="64" rx="8" class="b"/><text x="660" y="187" text-anchor="middle" class="t">ClaimsAssist</text>
 <text x="660" y="206" text-anchor="middle" class="s">AuthMode Dial · internal</text>
 <rect x="560" y="262" width="200" height="64" rx="8" class="b"/><text x="660" y="289" text-anchor="middle" class="t">Mock model</text>
 <text x="660" y="308" text-anchor="middle" class="s">mock-echo, tests only · internal</text>
 <rect x="1000" y="160" width="170" height="64" rx="8" class="b"/><text x="1085" y="187" text-anchor="middle" class="t">Azure OpenAI</text>
 <text x="1085" y="206" text-anchor="middle" class="s">existing account</text>
 <rect x="1000" y="48" width="170" height="64" rx="8" class="b"/><text x="1085" y="75" text-anchor="middle" class="t">Log Analytics</text>
 <text x="1085" y="94" text-anchor="middle" class="s">guard events · audit</text>
 <path d="M160,175 L228,175" class="l"/><text x="194" y="166" text-anchor="middle" class="s">key</text>
 <path d="M430,158 C480,120 510,85 558,78" class="l"/><text x="478" y="104" class="s">1</text>
 <path d="M560,96 C510,110 480,140 432,166" class="l"/><text x="506" y="136" class="s">2</text>
 <path d="M430,176 L558,180" class="l"/><text x="492" y="170" class="s">3</text>
 <path d="M380,210 C420,280 500,294 558,294" class="l"/><text x="452" y="296" class="s">3</text>
 <path d="M760,192 L998,192" class="l"/><text x="880" y="183" text-anchor="middle" class="s">managed identity, no keys</text>
 <path d="M960,80 L998,80" class="l"/><text x="878" y="72" text-anchor="middle" class="s">stdout of every app</text>
 <path d="M560,206 L432,202" class="l"/><text x="452" y="224" class="s">/v1/user/info</text>
</svg>
<ol class="legend"><li>Core sends the request through the guard (request side: quarantine, attachment check).</li>
<li>The guard calls Core again with the same per-request key for the next hop, and checks every streamed chunk on the way back.</li>
<li>Core routes to the deployment's application: ClaimsAssist, or the mock model for <code>mock-echo</code>. ClaimsAssist asks Core
who the caller is and never trusts request headers.</li></ol>"""
sections.append(("architecture", "How a request flows", svg))

# --- deployment and smoke test --------------------------------------------------------------------------------------
if deploy_log:
    lines = [l for l in deploy_log.splitlines() if l.strip() and not l.startswith("started ")]
    sections.append(("deploy", "Deployment and smoke test (deploy.ps1 -ApproveCost)",
                     "<pre class='term'>" + e("\n".join(lines[-40:])) + "</pre>"))

# --- resources ------------------------------------------------------------------------------------------------------
if azure:
    rows = "".join(
        f"<tr><td><b>{e(a['name'])}</b></td><td>{'external' if a['external'] else 'internal'}</td>"
        f"<td>{'from inside the environment only' if a.get('allowInsecure') else 'redirected to HTTPS'}</td><td>{e(a['replicas'])} running "
        f"({e(a['minReplicas'])}–{e(a['maxReplicas'])})</td><td>{e(', '.join(cut(i, 64) for i in a['images']))}</td>"
        f"<td>{e(', '.join(str(c) for c in a['cpu']))} vCPU / {e(', '.join(a['memory']))}</td></tr>" for a in azure["apps"])
    env = azure["environments"][0] if azure["environments"] else {}
    reg = azure["registries"][0] if azure["registries"] else {}
    ws = azure["workspaces"][0] if azure["workspaces"] else {}
    roles = "".join(f"<li>{e(r['role'])} on <code>{e(r['scope'])}</code></li>" for r in azure["identity"]["roles"])
    body = f"""<table><tr><th>Container app</th><th>Ingress</th><th>Plain HTTP</th><th>Replicas</th><th>Images</th>
<th>Size</th></tr>{rows}</table>
<div class="grid"><div><h4>Environment <code>{e(env.get('name'))}</code></h4><ul>
<li>Region: {e(env.get('location'))}, workload profile: {e(', '.join(env.get('workloadProfiles') or []))}</li>
<li>Peer-to-peer encryption: <b>{'on' if env.get('peerEncryption') else 'off'}</b></li>
<li>Logs: {e(env.get('logs'))} → Log Analytics <code>{e(ws.get('name'))}</code> ({e(ws.get('retentionInDays'))} days,
local auth disabled: {e(ws.get('localAuthDisabled'))})</li></ul></div>
<div><h4>Registry <code>{e(reg.get('name'))}</code> ({e(reg.get('sku'))})</h4><ul>
<li>Admin user: {'enabled' if reg.get('adminUserEnabled') else 'disabled'}; pulls with the managed identity</li>
<li>Repositories: {e(', '.join(reg.get('repositories') or []))}</li></ul>
<h4>Identity <code>{e(azure['identity']['name'])}</code></h4><ul>{roles}</ul></div></div>"""
    sections.append(("resources", f"What ran in Azure (resource group {azure['resource_group']})", body))

# --- the flow -------------------------------------------------------------------------------------------------------
if flow:
    cards = []
    for i, s in enumerate(flow["steps"], 1):
        req, res = s["request"], s["response"]
        hdrs = {k: v for k, v in (req.get("headers") or {}).items() if k != "Content-Type"}
        msgs = (req.get("body") or {}).get("messages") or []
        shown = msgs[-1]["content"] if msgs and msgs[-1].get("content") else ""
        tool = next((m["content"] for m in msgs if m.get("role") == "tool"), None)
        out = res.get("text")
        extra = ""
        if res.get("model_sent"):
            extra += f"<div class='kv'><span>model sent</span><code>{e(res['model_sent'])}</code></div>"
        if tool:
            extra += f"<div class='kv'><span>tool message</span><code>{e(tool)}</code></div>"
        if res.get("application_received_tool_message"):
            extra += (f"<div class='kv'><span>app received</span><code>{e(res['application_received_tool_message'])}"
                      "</code></div>")
        if out and not res.get("application_received_tool_message"):
            extra += f"<div class='kv'><span>client received</span><code>{e(cut(out, 420))}</code></div>"
        elif res.get("body") is not None:
            extra += f"<div class='kv'><span>response</span><code>{e(cut(json.dumps(res['body']), 300))}</code></div>"
        elif res.get("error"):
            extra += f"<div class='kv'><span>result</span><code>{e(res['error'])}: {e(cut(res.get('detail', ''), 160))}</code></div>"
        elif res.get("location"):
            extra += f"<div class='kv'><span>location</span><code>{e(res['location'])}</code></div>"
        meta = f"HTTP {e(res.get('status'))}" if res.get("status") else ""
        if res.get("ms"):
            meta += f" · {e(res['ms'])} ms"
        if res.get("sse_chunks"):
            meta += f" · {e(res['sse_chunks'])} SSE chunks"
        hdr_line = "".join(f"<code class='h'>{e(k)}: {e(v)}</code>" for k, v in hdrs.items())
        cards.append(f"""<div class="card"><div class="top"><span class="n">{i}</span><b>{e(s['title'])}</b>
<span class="badge {'ok' if s['ok'] else 'bad'}">{'as expected' if s['ok'] else 'NOT as expected'}</span></div>
<div class="why">{e(s['why'])}</div>
<div class="kv"><span>request</span><code>{e(req.get('method'))} {e(req.get('url'))}</code></div>
{f"<div class='kv'><span>headers</span><div>{hdr_line}</div></div>" if hdr_line else ""}
{f"<div class='kv'><span>user message</span><code>{e(cut(shown, 200))}</code></div>" if shown else ""}
{extra}<div class="foot">{meta}<span class="verdict">{e(cut(s['verdict'], 140))}</span></div></div>""")
    ok = sum(1 for s in flow["steps"] if s["ok"])
    sections.append(("flow", f"Live requests through {dial_url} ({ok}/{len(flow['steps'])} as expected)",
                     "<div class='cards'>" + "".join(cards) + "</div>"))

# --- logs -----------------------------------------------------------------------------------------------------------
if logs:
    kinds = "".join(f"<tr><td>{e(k)}</td><td>{e(v)}</td></tr>" for k, v in sorted(logs["by_kind"].items()))
    dets = "".join(f"<tr><td>{e(k)}</td><td>{e(v)}</td></tr>" for k, v in sorted(logs["by_detector"].items()))
    samples = "\n".join(json.dumps(x, ensure_ascii=False) for x in logs["sample_events"][-4:])
    audit = "".join(f"<tr><td>{e(a.get('timestamp'))}</td><td><code>{e(a.get('user'))}</code></td><td>{e(a.get('status'))}</td>"
                    f"<td>{e(cut(a.get('question') or '', 70))}</td><td>{e(a.get('inputTokens'))}/{e(a.get('outputTokens'))}</td>"
                    f"<td>{e(a.get('latencyMs'))} ms</td></tr>" for a in logs["audit_sample"])
    body = f"""<p>{e(logs['guard_events'])} DIAL Guard events and {e(logs['audit_records'])} ClaimsAssist audit records reached the
workspace. Events carry kind, detector and counts, never the matched value; audit records are pseudonymised and redacted.</p>
<div class="grid"><table><tr><th>Event kind</th><th>Count</th></tr>{kinds}</table>
<table><tr><th>Detector</th><th>Count</th></tr>{dets}</table></div>
<h4>Latest guard events (as stored)</h4><pre class="term small">{e(samples)}</pre>
<h4>Latest audit records</h4><table><tr><th>Time</th><th>Handler (pseudonym)</th><th>Status</th><th>Question (redacted)</th>
<th>Tokens in/out</th><th>Latency</th></tr>{audit}</table>"""
    sections.append(("logs", "What the workspace recorded (Log Analytics, queried with Entra ID)", body))

# --- gate -----------------------------------------------------------------------------------------------------------
if gate:
    rows = "".join(f"<tr><td><b>{e(c['id'])}</b></td><td><span class='badge {'ok' if c['status'] == 'Pass' else ('warn' if c['status'] == 'Warn' else 'bad')}'>"
                   f"{e(c['status']).upper()}</span></td><td>{e(cut(c['result'], 230))}</td></tr>" for c in gate["checks"])
    body = f"""<p><b>Verdict: {e(gate['verdict'])}</b> · {e(gate['scenarios'])} scenarios through the Azure deployment ·
run cost ${e(gate['cost'])} · evidence pack <code>{e(gate['run'])}</code></p>
<table><tr><th>Check</th><th>Status</th><th>Result</th></tr>{rows}</table>
<p class="why">A DIAL target is a black box: checks that need the agent's trace report "not measurable" (WARN) and can
never pass. The audit-log half of PII protection is not measurable in Azure, where the trail goes to Log Analytics.</p>"""
    sections.append(("gate", "PilotGate against the Azure deployment (--dial)", body))

# --- removal --------------------------------------------------------------------------------------------------------
if destroy_log or final:
    left = "".join(f"<li><code>{e(r['name'])}</code> ({e(r['type'])})</li>" for r in (final or {}).get("resources", []))
    lines = [l for l in destroy_log.splitlines() if l.strip()]
    body = (f"<pre class='term'>{e(chr(10).join(lines[-30:]))}</pre>" if lines else "") + \
           (f"<h4>Left in the resource group afterwards</h4><ul>{left}</ul>" if final else "")
    sections.append(("removal", "Removal (deploy.ps1 -Destroy)", body))

css = """body{font:15px/1.45 system-ui,'Segoe UI',sans-serif;color:#1f2328;margin:0;background:#fff}
main{max-width:1200px;margin:0 auto;padding:28px 32px}h1{font-size:26px;margin:0 0 4px}h2{font-size:20px;margin:30px 0 12px;
border-bottom:1px solid #d0d7de;padding-bottom:6px}h4{margin:16px 0 6px}.meta{color:#57606a;margin-bottom:8px}
table{border-collapse:collapse;width:100%;margin:8px 0;font-size:13.5px}th,td{border:1px solid #d0d7de;padding:6px 8px;
text-align:left;vertical-align:top}th{background:#f6f8fa}code{font:12.5px/1.4 Consolas,ui-monospace,monospace;
background:#f6f8fa;padding:1px 4px;border-radius:4px;word-break:break-all}.grid{display:grid;grid-template-columns:1fr 1fr;
gap:18px}.cards{display:grid;grid-template-columns:1fr;gap:12px}.card{border:1px solid #d0d7de;border-radius:10px;
padding:12px 14px}.top{display:flex;gap:10px;align-items:center}.n{background:#1f2328;color:#fff;border-radius:50%;
width:24px;height:24px;display:inline-flex;align-items:center;justify-content:center;font-size:12px}
.badge{margin-left:auto;font-size:12px;padding:2px 8px;border-radius:10px;font-weight:600}.ok{background:#dafbe1;
color:#1a7f37}.bad{background:#ffebe9;color:#cf222e}.warn{background:#fff8c5;color:#7d4e00}.why{color:#57606a;
margin:4px 0 8px}.kv{display:grid;grid-template-columns:130px 1fr;gap:8px;margin:3px 0;align-items:start}.kv>span{
color:#57606a;font-size:13px}.h{display:inline-block;margin:0 6px 4px 0}.foot{display:flex;gap:14px;color:#57606a;
font-size:13px;margin-top:6px}.verdict{margin-left:auto;font-weight:600;color:#1f2328}.term{background:#0d1117;
color:#e6edf3;padding:14px;border-radius:8px;font:12.5px/1.45 Consolas,ui-monospace,monospace;white-space:pre-wrap;
word-break:break-all}.small{font-size:11.5px}.legend{color:#57606a;font-size:13.5px}"""
script = """const only=new URLSearchParams(location.search).get('only');if(only){for(const s of document.querySelectorAll('section'))
{if(s.id!==only)s.remove()}const h=document.querySelector('header');if(only!=='architecture'&&h)h.querySelector('.intro')?.remove()}"""
first = (flow or {}).get("captured_at", "")[:10]
page = f"""<!doctype html><html lang="en"><head><meta charset="utf-8"><title>DIAL suite live in Azure: evidence</title>
<style>{css}</style></head><body><main><header><h1>PilotGate DIAL suite, live in Azure Container Apps</h1>
<div class="meta">Captured {e(first)} from real requests to the deployment, which was removed afterwards with
<code>deploy.ps1 -Destroy</code>. DIAL keys are redacted; all data is synthetic.</div>
<p class="intro">DIAL Core with Redis, DIAL Guard, ClaimsAssist and a mock model, deployed by <code>infra/dial/deploy.ps1</code>
into one Container Apps environment next to the existing Azure OpenAI account. Only DIAL Core is public.</p></header>
{''.join(f'<section id="{sid}"><h2>{e(title)}</h2>{body}</section>' for sid, title, body in sections)}
</main><script>{script}</script></body></html>"""
(D / "flow.html").write_text(page, encoding="utf-8")
print("sections:", [s[0] for s in sections])
