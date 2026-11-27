# 7. DIAL suite live in Azure Container Apps (2026-11-27)

> Azure resource names in this pack are anonymized to the standard naming pattern the templates use (`<abbreviation>-<workload>-<suffix>`, with `demo001` in place of the real unique suffix, and `demoenv-00000001` for the environment's domain). Everything else is as captured.

A one-day test run of `infra/dial/deploy.ps1`: deployed, smoke-tested, exercised end to end, gated with PilotGate, then
removed with `-Destroy`. Every request below went to the real deployment through its public HTTPS address. DIAL keys
are redacted in every file. All data is synthetic.

Open [flow.html](flow.html) for the whole story on one page, or look at the screenshots:

| Screenshot | What it shows |
|---|---|
| [01-architecture](screenshots/01-architecture.png) | How a request flows: DIAL Core, the guard as interceptor, ClaimsAssist's identity callback, the managed identity to Azure OpenAI |
| [02-deploy](screenshots/02-deploy.png) | `deploy.ps1 -ApproveCost`: both phases, image import and push, and the smoke test. It passed on the first attempt, with no retries, in 10 minutes |
| [03-resources](screenshots/03-resources.png) | The four container apps (ingress, replicas, images pinned by digest, sizes), the environment with peer-to-peer encryption, the registry without an admin user, the identity's two roles |
| [04-flow](screenshots/04-flow.png) | 16 live requests through the public DIAL URL, each with what was sent, what came back, and the verdict |
| [05-logs](screenshots/05-logs.png) | What reached Log Analytics: 54 DIAL Guard events by kind and detector, and 34 pseudonymised ClaimsAssist audit records |
| [06-gate](screenshots/06-gate.png) | PilotGate `--dial` against the deployment |
| [07-pilotgate-report](screenshots/07-pilotgate-report.png) | The top of PilotGate's own report for that run (full pack in [pilotgate-run](pilotgate-run/report.html)) |
| [08-removal](screenshots/08-removal.png) | `deploy.ps1 -Destroy` and what was left in the resource group |

## Results

- **Deployment** (18:20 to 18:30 CET). The registry, identity, workspace and environment came first. DIAL Core and Redis were imported by digest, and the three images built from this repository were pushed. Then the container apps.
- **Smoke test, first attempt:**
  - DIAL Core is healthy over HTTPS;
  - the guard redacts PII returned by the mock model;
  - ClaimsAssist answers a property handler: identity from Core, model through the managed identity;
  - a role without a persona gets 403;
  - the guard and ClaimsAssist do not answer from the internet.
- **Live flow: 16 of 16 requests behaved as expected:**
  - plain HTTP is redirected (301);
  - an e-mail address, an IBAN and a `0049` phone number never reach the client, streamed or not;
  - a phishing link and a tracking pixel are removed and the intranet link stays;
  - a leaked canary ends the answer with `content_filter`;
  - a planted instruction in a tool result is quarantined before the application;
  - ClaimsAssist answers with its source and the AI disclosure;
  - a spoofed `X-Dev-Persona: sam.siu` header gets no SIU content;
  - an intern gets 403;
  - a settlement needs approval, another handler gets 403 on it, and the owner's approval queues it;
  - the three internal apps answer 404 from the internet.
- **Log Analytics.** 54 guard events reached the workspace: 6 PII, 1 link, 1 canary, 3 injection, 43 requests completed. They record kind, detector and count, never the matched value. 34 ClaimsAssist audit records came with them, carrying pseudonyms made with the deployment's own key. Queried with Entra ID; the workspace has local auth disabled.
- **PilotGate against the deployment: GO WITH CONDITIONS.** 27 scenarios; 15/15 tasks correct; 29/29 answers carry the AI disclosure; 0 PII; p95 latency 3.4 s; run cost $0.112. The checks that need the agent's trace report WARN, as designed for a black box. The audit half of PII protection is not measurable here, because the trail goes to Log Analytics and not to a file.
- **Removal** (18:38 to 19:05 CET). Deleted in order:
  - the app identity's role assignments;
  - the four container apps;
  - the environment (about 20 minutes on Azure's side);
  - the identity, the workspace and the registry.

  The resource group is back to its two original resources, the Azure OpenAI account and the CI identity, and no role assignment of the app identity remains. See [destroy.log](destroy.log) and [final.json](final.json).
- **Cost of the run.**
  - Model tokens: about $0.12. That is the gate run at $0.112, plus five ClaimsAssist turns in the smoke test and the flow.
  - Azure resources: at most $0.26. That is the registry's daily rate plus about 25 minutes of Container Apps, which is likely inside the monthly free grant.
  - Azure Cost Management shows the exact figure about a day later.

## Files

| File | Content |
|---|---|
| `flow.html` | The evidence page (self-contained) |
| `flow.json` | Every live request and response (keys redacted) |
| `azure.json` | The deployed state from read-only `az` queries: names and settings, no ids or keys |
| `logs.json` | Counts and samples from the Log Analytics workspace |
| `gate.json`, `pilotgate-run/` | PilotGate's summary and full evidence pack for the run against Azure |
| `deploy.log`, `destroy.log`, `final.json` | Console output of `deploy.ps1` and what was left afterwards |
| `tools/` | The scripts that captured, rendered and screenshotted all of this (headless Microsoft Edge, throwaway profile) |

## Reproduce

With the stack images built and an approved cost estimate:

```powershell
./infra/dial/deploy.ps1 -ApproveCost
```

```powershell
python docs/evidence/7-dial-suite-live-in-azure/tools/capture_azure.py rg-pilotgate-demo pgdial <out-dir> state
```

```powershell
dial/guard/.venv/Scripts/python docs/evidence/7-dial-suite-live-in-azure/tools/capture_flow.py <dialUrl> infra/dial/.keys.json <environment default domain> <out-dir>/flow.json
```

Then gate it (`--dial <dialUrl>`, with `dial.keysFile` pointing to `infra/dial/.keys.json`), run `capture_azure.py ... logs`, `render_flow.py <out-dir>`, `shoot.py <out-dir> <out-dir>/screenshots architecture deploy resources flow logs gate --report <report.html>`, and finally `./infra/dial/deploy.ps1 -Destroy`.
