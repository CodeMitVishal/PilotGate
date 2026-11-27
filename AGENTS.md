# Instructions for coding agents (GitHub Copilot, Claude Code, Codex)

## Build and test
- `dotnet build` and `dotnet test` from the repository root. Unit tests are offline and must stay offline.
- DIAL Guard: `./scripts/test-guard.ps1` (Docker; the pinned Linux image with hash-locked dependencies). End-to-end tests against the local DIAL stack: see `dial/README.md`.
- The gate against the real model: `./scripts/run-gate.ps1` (needs `az login` and the Azure resources from `infra/main.bicep`).

## Layout
- `src/ClaimsAssist.Core`: the agent (Microsoft Agent Framework), retrieval, tools, guardrails, audit log, DIAL endpoint.
- `src/ClaimsAssist.Api`: minimal API. `AuthMode=Dial` (the default) exposes only the DIAL chat completions endpoint and the health endpoints (`/health`, `/health/live`, `/health/ready`). `AuthMode=DevPersonas` exposes `/chat`, `/approvals/{id}` and the health endpoints, on loopback addresses only.
- `src/PilotGate`: the production gate. It talks to the agent only over HTTP and must never reference ClaimsAssist.Core.
- `src/ProductionWatch`: drift and alerting over DIAL Guard events and audit logs.
- `dial/guard`: DIAL Guard (Python, `aidial-interceptors-sdk`). `dial/schema`: event schema and the shared guard corpus. `dial/stack`: local DIAL stack. `dial/spike`: protocol captures from a real DIAL Core.
- `infra/dial`: Azure Container Apps deployment of the DIAL suite.
- `gate/`: gate configuration and scenarios. `data/`: synthetic knowledge base, claims and personas.

## Rules that must not be broken
- Security trimming happens in `HybridRetriever.SearchAsync` before ranking. Never move it after ranking or into the prompt.
- Every write tool is wrapped in `ApprovalRequiredAIFunction`, and its deterministic guards run after approval too.
- Pending approvals are records in an `IApprovalStore`, never live agent objects. A decision:
  - checks the approver before it takes the record, so a wrong approver never uses an approval up;
  - takes the record atomically, so one decision wins;
  - runs the stored call through the same executor as the tool (`ProposeSettlement`), without asking the model again;
  - sends its answer through `Finish`, like every answer.
  - Every store must pass the contract in `ApprovalTests.cs`.
  - More than one ClaimsAssist replica needs `ApprovalStore=Redis`.
  - Redis signs in with Microsoft Entra ID only: never a password or access key in the connection string.
- `AuthMode` defaults to `Dial`. `DevPersonas` trusts a request header, so it must be set explicitly and refuses to serve on any non-loopback address. Never relax either.
- Readiness (`/health/ready`) checks local state only. Never add Redis, the model or another shared dependency to it: one outage would take every replica out of rotation.
- Model access uses Microsoft Entra ID tokens only. Never add API keys; the Azure OpenAI account has local auth disabled.
- Untrusted text (documents, attachments) goes through `UntrustedContentGuard` before the model sees it.
- Every new tool, document source or prompt change needs at least one gate scenario in `gate/scenarios/`.
- Never change a threshold in `gate/gate.json` to make a failing run pass. Fix the agent or the test design, and say which in the PR.
- All data is synthetic. Do not add real customer data, names or IBANs.
- Figure grounding is enforced by code. Every number, date and claim ID shown must appear in a cited source, a tool result of the turn or the question; otherwise `GroundingVerifier` withholds it.
  - The gate re-checks this with its own implementation (`FigureGrounding`). Never let PilotGate share that code with the agent.
  - A change to either implementation needs a case in `tests/grounding-corpus.v1.json`, and both test suites must pass.
  - Never lower `minFigureFidelity`.
- Policy values (link allow-list, approver groups, timeouts) are configuration with the strictest defaults. Never hardcode them; secrets never go in tracked files.

## DIAL suite rules
- DIAL Guard's defaults stay the strictest values (`enforce`, `fail_closed`, every PII kind). Never weaken fail-closed, and never let monitor mode change content.
- A detector change needs:
  - exact-output tests;
  - the property tests passing on both profiles (all kinds, and the shared kinds);
  - for the shared kinds, a corpus case regenerated with `dial/guard/tools/build_corpus.py`, the same behaviour in the .NET `PiiRedactor` or `UntrustedContentGuard`, and every changed case reviewed by hand.
- Events never contain matched values. A change to the event shape needs a new schema version.
- ClaimsAssist in Dial mode:
  - takes identity only from DIAL Core (`/v1/user/info`), never from request headers;
  - refuses to start with the development pseudonym key;
  - answers failures with HTTP 500 and a logged reference. Not 502 or 503: DIAL Core replaces those bodies.
- No Azure resources from `infra/dial` without an approved cost estimate (`-HighAvailability` costs extra). `deploy.ps1 -Destroy` deletes by name and never uses Complete mode, so new resources must follow the naming (`<type>-<prefix>-...`).
- Azure Managed Redis for approvals keeps `evictionPolicy: NoEviction` (approvals have a time to live, so a volatile policy would evict live ones) and `accessKeysAuthentication: Disabled`, and only ClaimsAssist's own identity gets its access policy.
