# Architecture review: the PilotGate suite

Review by an AI solution architect, 2026-11-27. Scope: every file in the repository (156 files outside the fixtures and
evidence packs), the IaC, the pipelines and the nine evidence packs. Method: code reading, probes, and the results of
real runs.
- **Verified** means a test, a probe or a code reference shows it.
- **Suspected** means it is likely but not shown.

## Executive summary

**Can it go to production?** One verdict per component:

| Component | Verdict | Why |
|---|---|---|
| **PilotGate** (the gate) | **Usable now** as a pre-production gate for agents that expose its HTTP or DIAL contract. **Not yet a product** other organisations can use without code changes | Nine real runs, CI on every pull request, verdicts that hold up. But the sample customer's facts are compiled into the engine: report text, insurance and German labour-law rules, the claim-ID pattern |
| **DIAL Guard** | **Closest to production** | Fails closed; 303 tests including property and fuzz tests; ran live through DIAL Core in Azure (evidence 7). Missing: high availability and package distribution |
| **ClaimsAssist** (the sample agent) | **A reference pilot, not a production app**, by design | The index is held in memory and the payout queue is a stand-in. Fixed since this review: the secure identity mode is the default; approvals are stored with expiry, in Redis when there is more than one replica (P0 status below) |
| **Production Watch** | **Works on files only** | No Log Analytics or Application Insights reader, and no Azure scheduler |
| **Infrastructure** | **Deployable, and verified live** | One region, no budget alerts, no WAF, no private endpoints. One replica by default; `highAvailability` runs two or more of ClaimsAssist and DIAL Guard, but DIAL Core stays at one |

**Do these five first (P0).** All five were fixed on 2026-11-27; see [P0 status](#p0-status-fixed-on-2026-11-27).
1. Make the secure identity mode the default, and refuse development identities outside localhost.
2. Store pending approvals with expiry, outside process memory.
3. Make the embedding cache crash-safe.
4. Run at least two replicas, with state moved out of process.
5. Stop the Python build output (`*.egg-info`) from being committed.

**Make it plug and play** by cutting four seams in order:
1. move domain and compliance text into data packs;
2. checks that declare what they need from a target;
3. target adapters;
4. one schema per input.

EPAM DIAL then becomes one adapter plus one pack, so the EPAM story stays intact.

**Measured value today:**
- a full evidence run takes 1.3 to 3.5 minutes and costs $0.11 to $0.15;
- seven real defects were caught before any user saw them;
- a live Azure deployment takes 10 minutes and a one-day test cost under $0.40.

**Job description coverage:** strong on C#, RAG and agents, Azure Container Apps, Entra ID, IaC, security and handover. Missing: a **frontend**, **Azure AI Foundry**, **API Management**, **Azure Functions**, **Teams and Microsoft Graph**, **GitHub Actions** and **Copilot-ready assets**. Six features in section 7 close those gaps and save money at the same time.

## 1. Production blockers and edge cases

### P0: before any production use

| ID | Finding | Failure scenario | Fix | Effort | Proof |
|---|---|---|---|---|---|
| P0-1 | **Insecure default identity mode** (verified). `appsettings.json:17` defaults to `AuthMode=DevPersonas`, and identity is then the `X-Dev-Persona` header (`Program.cs:129`). Kestrel binds to localhost unless told otherwise, and the image runs Dial mode, but nothing enforces either | A deployment that sets or keeps DevPersonas on a reachable address lets anyone claim to be the SIU investigator (fraud playbook) or a team lead (settlement approvals) | Default to the secure mode. Allow DevPersonas only when the environment is Development **and** every bound URL is loopback; otherwise refuse to start | S | Startup test: DevPersonas + `http://0.0.0.0` must fail |
| P0-2 | **Pending approvals are unbounded and volatile** (verified). `ClaimsAgentService.cs:60, 285`: a `ConcurrentDictionary` with no expiry, holding the live agent, its session and its trace | Undecided approvals grow memory without limit. A restart or new revision loses them all. With two replicas, an approval reaches the replica that does not hold it ("no pending approval") | `IApprovalStore`: in memory with a TTL for development; Cosmos DB or Redis in production. Store serialisable state, not live objects (guards re-run at approval time already) | M | TTL test; approve-after-restart test |
| P0-3 | **The embedding cache can crash-loop ClaimsAssist** (verified by code). `EmbeddingCache.cs:16-21` deserialises without error handling; `Save` (line 31) overwrites the file in place | A process killed during `Save` leaves truncated JSON, the next start throws, and the container restarts forever | Write to a temporary file and rename atomically; on a parse error, log and rebuild | S | Test with a truncated cache file |
| P0-4 | **No high availability.** `infra/dial/main.bicep:274, 368` run one replica each of DIAL Core (with Redis as an in-memory sidecar) and ClaimsAssist | One revision change is an outage and loses approvals (DORA Art. 11) | Managed Redis; two or more replicas; P0-2; readiness probes that check dependencies | M to L | Kill a replica during the smoke test |
| P0-5 | **Build output would be committed** (verified). `dial/guard/src/dial_guard.egg-info/` (5 files) is untracked and not ignored | Committed noise that gets stale | Delete it; add `*.egg-info/` to `.gitignore` | XS | `git status` clean after `pip install -e` |

### P0 status: fixed on 2026-11-27

| ID | What changed | Verified | Not verified |
|---|---|---|---|
| P0-1 | `AuthMode` defaults to `Dial`. `DevPersonas` must be set explicitly (`appsettings.Development.json`, `run-gate.ps1`). It then serves only if every bound address is loopback: the app starts, reads its bound addresses, and exits with code 1 otherwise. Until that check passes, every request gets 503 | Unit tests: 4 loopback and 7 reachable addresses, and the secure default. Process level: `ASPNETCORE_HTTP_PORTS` (which binds `[::]`) and `0.0.0.0` exit with code 1; `127.0.0.1` serves | |
| P0-2 | Pending approvals are plain records in an `IApprovalStore`, never live agent objects. Two stores:<br>• in memory: expiry, and a capacity that refuses new approvals rather than evicting live ones;<br>• Redis: shared by replicas, a time to live per key, Microsoft Entra ID sign-in.<br>A decision checks the approver first, so a wrong approver never uses the approval up. It then takes the record atomically (Redis `GETDEL`) and runs the stored call through the tool's own settlement code, guards included. The confirmation goes through the same output pipeline as every answer. The model is not called again, so a decision costs no tokens | 27 tests. The store contract runs on memory and on a real Redis 7.2.4:<br>• round trip;<br>• reading does not consume;<br>• one of 32 concurrent takes wins;<br>• expiry;<br>• duplicate ids.<br>The agent flow:<br>• approve and reject;<br>• a wrong approver;<br>• 8 concurrent approvals pay out once;<br>• a team lead's decision keeps the requester's authority;<br>• revoked authority is refused;<br>• expiry;<br>• another replica decides;<br>• capacity.<br>Through the real startup wiring with a real Redis: the approval waited as a key with a 30-minute time to live, the process was killed, and a new process decided it: QUEUED, no model call, a second decision got 404 | Entra ID sign-in to Azure Managed Redis (compiled, not run), which needs the HA deployment |
| P0-3 | The embedding cache writes a temporary file and renames it. An unreadable cache is ignored and rebuilt. A failed save is logged and never stops startup | 6 tests: truncated, empty and invalid files; no temporary file left behind; a directory that cannot be written | |
| P0-4 | `/health/live` and `/health/ready` (the latter reports 503 if probed while the app is stopping). Readiness checks local state only, on purpose: if a shared dependency such as Redis or the model failed readiness, every replica would leave rotation at once, including for questions that do not need it. Container Apps startup, liveness and readiness probes. `highAvailability` in `infra/dial/main.bicep` (`deploy.ps1 -HighAvailability`):<br>• ClaimsAssist runs 2 to 4 replicas, with approvals in Azure Managed Redis: B0, `NoEviction`, access keys disabled, and an identity of its own;<br>• DIAL Guard runs 2 to 3 replicas.<br>The smoke test now includes an approval round trip | The Bicep compiles and lints clean against the API 2025-07-01 types. ARM what-if succeeds with `highAvailability` false and true. Deploy-script harness: 23 of 23 | Not deployed: it needs approval for about $30 to $85 a month extra. The "kill a replica during the smoke test" proof is still open. **DIAL Core stays at one replica** (sidecar Redis, storage not shared): it is the remaining single point of failure |
| P0-5 | `*.egg-info/` is ignored, and the generated folder is deleted | `git status` | |

End to end after the fixes:
- Gate run 20261127-204736 against the real model: **GO WITH CONDITIONS** ($0.11), with the same three conditions as run 9. HUMAN_OVERSIGHT passed. A01 ended QUEUED and A02 REJECTED, both decided without a second model call. Figures were found in their sources in 30 of 30 sentences.
- DIAL end-to-end tests: 22 of 22 passed. Another handler gets 403, and the owner's decision runs once and carries the AI disclosure.
- Offline .NET tests: 340 passed.

Found while fixing, and not covered by P0:
- `SettlementService` queues payouts in process memory, so a queued instruction lives on one replica. It is a demo stand-in. In production, payouts go through the claims system's payout API, with the approval id as the idempotency key.
- The shared app identity (`id-pgdial-apps`) has model access although only ClaimsAssist calls the model. In HA mode, Redis access already has an identity of its own. Splitting model access the same way is a P1 item.
- In the same gate run, X01's PII refusal came back as the generic "sources do not confirm" text. Grounding withheld it for the figure "4" (`num:4`). The text before grounding was not kept. Most likely the model wrote "only the last four digits are shown", as it did in run 20261127-082108, and "four" does not appear in the claim file. No data leaked, but the refusal lost its explanation. A fix on the source side: the claim file the model sees could say "last four digits" itself. That counts as a change to what the model sees, so it needs a gate scenario (P2).

### P1: before scaling to more users or customers

| ID | Finding | Fix | Effort |
|---|---|---|---|
| P1-1 | **Figure grounding misses some figures** (verified by probe). "€5k", "forty" and "3rd" produce no figure at all, so an invented figure written that way is never checked | Treat every token with a digit as a figure (aware of k, Mio and Tsd suffixes); a full number-word grammar; ordinals; a corpus case for each | S to M |
| P1-2 | **Figure grounding withholds some correct answers** (verified by probe). Clock times ("9:00" yields a stray 0) and derived arithmetic (half of 3,390) | Time figures; a prompt rule against deriving new figures, or an explicit, logged arithmetic check | S |
| P1-3 | **Figures are matched without units** (verified by probe). "2.5 million" matches "2,5 Mio." only because the unit is ignored, so a wrong unit would pass | Bind a number to its unit when one follows | S |
| P1-4 | **No failure contract on `/chat`** (verified). `Program.cs:132`: a model failure in DevPersonas mode becomes a bare 500. Production hides the stack trace, but there is no reference ID, unlike the DIAL endpoint | One exception middleware for both modes: ProblemDetails with a correlation ID | S |
| P1-5 | **Request bodies are read whole** (verified). `Program.cs:104` reads up to Kestrel's 30 MB default into memory; there is no rate limiting | Set `MaxRequestBodySize` to something like 256 KB; the ASP.NET rate limiter per identity; DIAL role limits | S |
| P1-6 | **Health checks are shallow** (verified). `/health` (`Program.cs:76`) says ok without checking the model or the index | Separate `/health/live` and `/health/ready` (index loaded, token obtainable) | S |
| P1-7 | **One noisy latency sample can fail the gate** (verified, run 8). Two slow answers out of 28 decided p95 | Measure latency in a separate performance stage with enough samples and a confidence interval. Keep the threshold | M |
| P1-8 | **The AI judge scores once, from the same model family** (suspected bias). gpt-4.1 judges gpt-4.1-mini | Multi-sample judging, or a judge from another family, calibrated against a human-labelled set | M |
| P1-9 | **The docs drift** (verified). "27 scenarios" appears in four places (it is 28); `case-study.html` still says $0.90 | Generate doc facts from the evidence packs and config (see section 4) | S |
| P1-10 | **No supply-chain controls** (verified). No SBOM, no image signing, no vulnerability scanning | SBOM generation, registry signing, scanning in CI | M |
| P1-11 | **Each turn answers only the last message** (verified, by design). A follow-up question loses its context | Server-side conversation memory keyed by the DIAL conversation ID, integrity-protected | M |

### P2: production hardening

| ID | Finding | Fix |
|---|---|---|
| P2-1 | In-process index, with embeddings computed at startup: a cold start, and knowledge-base changes need a restart | Azure AI Search with a security filter |
| P2-2 | Watch reads files only, and runs on a self-hosted pipeline schedule | A Log Analytics reader; Azure Functions timer (section 7) |
| P2-3 | Guards are static classes, with configuration passed as parameters (`UntrustedContentGuard`, `PiiRedactor`) | Injectable services with options |
| P2-4 | DIAL Guard's 512-character run cap fails closed: an attacker can turn glued input into errors (availability, not leakage) | Per-identity rate limits; Watch already alerts on guard errors |
| P2-5 | DIAL keys never rotate | Key Vault references and a rotation runbook |
| P2-6 | No Azure budget alert in the IaC | A Cost Management budget resource in `infra/dial/main.bicep` |
| P2-7 | No WAF, no private endpoints for Azure OpenAI | Front Door with WAF; private endpoints; `publicNetworkAccess: Disabled` |

## 2. Robust, clear, modular, SOLID: what to change

| Where | Problem | Change |
|---|---|---|
| `src/PilotGate/Checks.cs` (623 lines) | Seven checks in one static class (single responsibility, open/closed). A new check edits this file | One `IGateCheck` class per check, with metadata (ID, audience, blocker, controls, required capabilities); a registry; checks found by dependency injection |
| `src/PilotGate/Program.cs` (387 lines) | CLI parsing, orchestration and report assembly together | System.CommandLine commands; a `RunOrchestrator`; a `ReportBuilder` |
| `src/PilotGate/Model.cs` (405 lines) | Configuration and agent contract types mixed | `Config/` (schema-validated), `Contracts/` (agent contract), `Domain/` |
| `src/PilotGate/EvidencePack.cs` (375 lines) | HTML built in strings, with the customer's facts compiled in (lines 84-88, 259-272) | A template engine (Razor or Scriban), with all target-specific text from packs |
| `src/PilotGate/Decision.cs` | AI Act and GDPR screening hardcoded, including insurance and German BetrVG rules (lines 11, 45, 79, 89, 98) | A compliance pack per jurisdiction or sector, as data, with the engine evaluating rules |
| `src/ClaimsAssist.Core/Agent/ClaimsAgentService.cs` (544 lines) | Orchestration, tool building, output pipeline, audit and approvals in one class | `ToolFactory`; an `AnswerPipeline` of `IAnswerFilter` steps (citations, links, PII, grounding) as a chain of responsibility; `IApprovalStore`; `IAuditSink` |
| `src/ClaimsAssist.Api/Program.cs` | A hand-built object graph; custom startup validation | Dependency injection; `IOptions<ClaimsAssistSettings>` with `IValidateOptions` and `ValidateOnStart`; endpoint groups per mode |
| Grounding and guard rules | Two copies each, on purpose (the gate must not share code with the agent; Python and .NET guards) | Keep both, pinned by the shared corpora. Record the decisions as ADRs so nobody "deduplicates" them |
| Naming | "persona", "handler" and "claim" leak from the sample into the generic gate | "test identity" in the engine; domain words only in packs |

## 3. Plug and play for any FDE and any organisation

The coupling sits in the engine itself, so a rewrite is not needed. Cut these seams in order; each step ships on its own.

1. **Packs instead of code.**
   - A *domain pack* holds the scenarios, canaries, attack indicators, identifier patterns (today the `CLM-####` regex in `FigureGrounding.cs:223`), business-case inputs and wording, and report text.
   - A *compliance pack* holds the screening rules for one jurisdiction or sector: EU AI Act and GDPR, German BetrVG, UK, the US NIST AI RMF, insurance (EIOPA) and finance (DORA).
   - `Decision.cs` and the target text in `EvidencePack.cs` move there.
2. **Capabilities.**
   - Each check declares what it needs: trace, citations, approvals, audit log.
   - Each target adapter declares what it provides.
   - "Not measurable" then follows automatically, generalising today's DIAL black-box logic.
3. **Target adapters** behind one `ITargetAdapter`:
   - today's HTTP contract and DIAL;
   - then OpenAI-compatible chat, Azure AI Foundry Agent Service, Copilot Studio (Direct Line), and LangGraph or LangServe.
4. **One schema per input.**
   - `gate.json` splits into a target file (connection, test identities), a gate profile (checks, thresholds) and the packs it names, each with a JSON Schema.
   - Watch gets an audit-record schema, as the guard events have, so any agent can feed it.
5. **Packaging.**
   - `dotnet tool install -g pilotgate`, a container image, a GitHub Action and an Azure DevOps task.
   - `pilotgate init` scaffolds the configuration.
   - `pilotgate scenarios suggest` drafts scenarios from a customer's documents, for an expert to approve.
6. **Guardrails for other agents.**
   - The .NET guardrails (link allow-list, PII redaction, untrusted content, figure grounding) become a NuGet package.
   - DIAL Guard ships as a container and a Python package.
   - The gate keeps its own independent figure check.

**The result:**
- EPAM DIAL is one adapter plus one integration pack.
- Insurance is one domain pack.
- Germany and the EU are one compliance pack.
- A bank, a hospital or a retailer adds its own packs without touching the engine.

## 4. Spec-driven development (for the senior developer)

**What already works as specification by example:**
- the acceptance matrix in `docs/suite/PLAN.md`;
- the guard corpus (`dial/schema/guard-corpus.v1.json`) and the grounding corpus (`tests/grounding-corpus.v1.json`);
- the event schema (`dial/schema/guard-event.v1.schema.json`).

Each of these binds two implementations or one implementation to written, reviewable cases.

**Proposal:**
- **A `specs/` folder as the single source of truth:**
  - `agent-contract.openapi.yaml`, the HTTP contract every target adapter implements, with contract tests in CI that validate live responses against it;
  - JSON Schemas for the gate configuration, scenarios, packs, audit records (v1) and the Watch configuration;
  - feature specs with acceptance IDs (for example `specs/features/figure-grounding.md`, AC-FG-01 to AC-FG-12), each named by the tests that prove it. CI fails if an acceptance criterion has no test.
- **ADRs in `docs/adr/`** for the deliberate decisions:
  - an independent gate implementation;
  - 500, not 502, for DIAL failures;
  - fail-closed defaults;
  - WARN, never PASS, for a black box.
- **Doc facts generated from the evidence and configuration,** so numbers never drift again (P1-9).
- **Workflow with GitHub Spec Kit,** an open-source toolkit that works with GitHub Copilot and other agents:
  1. seed the constitution (`.specify/memory/constitution.md`) from the rules in `AGENTS.md`;
  2. then for each feature: `/speckit.specify`, `/speckit.clarify`, `/speckit.plan`, `/speckit.tasks`, `/speckit.analyze`, `/speckit.implement`.
  3. A pull request carries its spec change, and CI validates the schemas, the contract tests and the acceptance traceability.
- **The first three specs to write:**
  - the agent contract (OpenAPI);
  - the pack and scenario schemas;
  - the approval store's behaviour (expiry, persistence, four-eyes).

## 5. Time and money: what is measured, and how to calculate the rest

**Measured in the evidence packs:**
- a full evidence run takes 1.3 to 3.5 minutes and costs $0.11 to $0.15;
- a change that leaked the fraud playbook was blocked in 36 seconds for $0.007;
- the assistant costs $0.0011 per conversation;
- deploying and smoke-testing in Azure took 10 minutes, and a one-day test cost under $0.40;
- seven defects were caught before any user saw them (README).

**Calculate the rest with your own numbers** (formulas, not measurements):
- **Savings per production decision** = (hours spent assembling evidence by hand − hours maintaining the gate) × loaded hourly rate + (cost of an incident × the chance the gate prevents it).
- **Model bake-off saving per month** = (current model's cost per conversation − cost of the cheapest model that passes the gate) × conversations per month.

**Measure next:** the time from "pilot works" to "production decision" at the next customer, with and without the gate. That number sells the product.

## 6. File-by-file review

| Path | Verdict | Note |
|---|---|---|
| `dial/guard/src/dial_guard.egg-info/` | **Remove** | Build output from `pip install -e`; unreferenced. Delete it and ignore `*.egg-info/` |
| `docs/case-study.html` | **Remove or regenerate** | A stale copy of a published page ($0.90, no DIAL suite); nothing references it |
| `dial/spike/capture_client.py`, `capture_round2.py`, `echo_app.py`, `spike_interceptor.py`, `docker-compose.yml`, `config.json`, `settings.json` | **Archive** (to `dial/spike/provenance/`) | How the golden fixtures were captured. They need the git-ignored raw captures, so they are provenance, not runtime code |
| `dial/spike/README.md`, `make_fixtures.py` | Keep | The 22 verified protocol facts, and the fixture generator |
| `src/PilotGate/Checks.cs`, `Program.cs`, `Model.cs`, `EvidencePack.cs`, `Decision.cs` | **Refactor** | Section 2; domain text into packs |
| `src/PilotGate/Runner.cs`, `DialClient.cs`, `Judge.cs`, `FigureGrounding.cs` | Keep, refactor lightly | Become target adapters (`Runner`, `DialClient`); the identifier pattern becomes configurable (`FigureGrounding`) |
| `src/ClaimsAssist.Core/Agent/ClaimsAgentService.cs` | **Refactor** | Section 2 (P0-2 done: approvals moved to `Approvals.cs` and `RedisApprovalStore.cs`) |
| `src/ClaimsAssist.Core/Knowledge/EmbeddingCache.cs` | Keep | P0-3 done |
| `src/ClaimsAssist.Core/Guardrails/*` | Keep, then package | A NuGet guardrails library (section 3) |
| `src/ClaimsAssist.Core/Dial/*`, `Security/*`, `Claims/*`, `Telemetry/*`, `Ai/*` | Keep | `UserContext` gains an Entra ID path |
| `src/ClaimsAssist.Api/Program.cs`, `appsettings.json` | **Fix** | P1-4 to P1-6 (P0-1 done) |
| `src/ClaimsAssist.Api/Dockerfile`, `Properties/launchSettings.json`, `appsettings.Development.json` | Keep | Development and image settings |
| `src/ProductionWatch/*` | Keep | Add a Log Analytics reader and an audit-record schema |
| `dial/guard/src/*`, `dial/guard/tools/build_corpus.py` | Keep | Packaging (section 3) |
| `dial/mockmodel/*`, `dial/stack/*` | Keep | The local stack and its test double, clearly labelled |
| `dial/schema/*`, `tests/grounding-corpus.v1.json` | Keep | They move under `specs/` |
| `infra/main.bicep`, `infra/ci-identity.bicep`, `infra/dial/*` | Keep | P2-6, P2-7 (P0-4 done: `highAvailability`) |
| `scripts/run-gate.ps1`, `scripts/test-guard.ps1` | Keep | Called by CI |
| `azure-pipelines.yml`, `pipelines/*` | Keep | Add a GitHub Actions twin (section 7) |
| `gate/gate.json`, `gate/scenarios/*` | Refactor | Split into target, profile and packs (section 3) |
| `watch/watch.json` | Keep | Schema-validated later |
| `data/*` | Keep | The sample domain pack's synthetic data |
| `demo/planted-leak.patch` | Keep | Used by the live demo (README) |
| `docs/production-path.md`, `docs/problem-brief.md` | **Update** | Still say "27 scenarios" |
| `docs/security-and-compliance.md`, `docs/suite/PLAN.md` | Keep | The PLAN acceptance matrix moves into `specs/` |
| `docs/evidence/1-*` to `9-*` | Keep | The evidence story. Pack 2 repeats pack 1's traces on purpose (a re-score) |
| `tests/*` (340 .NET tests), `dial/guard/tests/*` (303 plus 22 end-to-end), fixtures | Keep | Add contract tests (section 4) |
| `README.md`, `AGENTS.md`, `dial/README.md`, `infra/dial/README.md` | Keep | Generate the numbers in them (P1-9) |

## 7. Job description coverage

| Job description | Evidence in this repository | Gap |
|---|---|---|
| C#/.NET, Python | Agent, gate and Watch in .NET 10; DIAL Guard in Python | None |
| TypeScript/JavaScript | None | A frontend (feature 1) |
| Frontend, backend, APIs, data, auth, deployment | Backend, APIs, Entra ID for the model, managed identity, Bicep, Container Apps | **No frontend; no database; no user sign-in** (features 1, 4) |
| Generative AI: RAG, copilots, agents | Hybrid RAG with permission trimming, an Agent Framework agent with approvals, figure grounding, a DIAL interceptor | None: the strongest area |
| Azure; Container Apps | Deployed live with ACR, Log Analytics and managed identity (evidence 7) | None |
| Azure Functions, App Service, AKS, API Management | None | Functions (feature 5) and **API Management** (feature 3). AKS and App Service are not worth building into this product; at most, a Helm chart to demo |
| Azure AI Foundry, Azure ML | Azure OpenAI used directly | **Foundry** (feature 2) |
| Teams, Microsoft Graph | None | **Teams approvals with Graph** (feature 4) |
| GitHub, GitHub Actions, GitHub Copilot | Azure DevOps and Azure Pipelines (optional in the job description); `AGENTS.md`, which Copilot's coding agent can read | **A GitHub Actions workflow and Copilot-ready assets** (feature 6). Honesty: this repository was built with Claude Code; make it Copilot-ready, never imply Copilot built it |
| Power Platform, Copilot Studio (optional) | None | A Copilot Studio target adapter proves plug-and-play. Power Platform is not worth building in; at most, a Power Automate approval demo |
| Security, identity, privacy, governance, RBAC | Entra ID only, least-privilege roles, fail-closed guard, pseudonymisation, [security and compliance analysis](security-and-compliance.md) | User sign-in (feature 1) |
| Live demos; explaining decisions, limits and risks | Demo script, screenshots of the live run (evidence 7), production path, honest conditions in every report | A UI makes the demo land (feature 1) |
| Iterating on feedback; handover; reusable components | Nine evidence packs telling the iteration story; production path; DIAL Guard and the corpora as reusable parts | Packaging and ADRs (sections 3, 4) |

**Six features, each closing several gaps and saving money or time.** Every Azure addition needs the project's cost approval before deployment.

| # | Feature | Proves | Value | Cost class |
|---|---|---|---|---|
| 1 | **Reviewer dashboard and handler chat UI** in TypeScript (React) with MSAL sign-in: evidence packs and their trend over runs for reviewers; chat with citations and approval buttons for handlers | Frontend, TypeScript, Entra ID sign-in, demo skills | Reviewers sign off in one place; demos land | Static Web Apps has a free tier |
| 2 | **Model bake-off with Azure AI Foundry**: the gate runs against several catalog models and recommends the cheapest one that passes; Foundry evaluators as extra checks | Azure AI Foundry, evaluation, product thinking | Cuts model spend with evidence | Pay per token |
| 3 | **API Management as the AI gateway** in front of Azure OpenAI and DIAL: token limits per user, token metrics for chargeback, semantic cache | API Management, governance | Cost control and chargeback; fewer repeated calls | Consumption tier is pay per call |
| 4 | **Approvals as Teams Adaptive Cards, stored in Cosmos DB**, with Microsoft Graph for group membership | Teams, Graph, a data layer | Approvals where handlers already work, on the `IApprovalStore` contract from P0-2 | Bot Service standard channels are free; Cosmos DB serverless is pay per use |
| 5 | **Production Watch on an Azure Functions timer**, reading Log Analytics | Azure Functions, monitoring | Closes P2-2; no self-hosted agent needed | Consumption plans include a free grant |
| 6 | **GitHub Actions workflow and Copilot-ready repository**: `.github/copilot-instructions.md`; prompt files for repeatable tasks (add a scenario, add a detector, add a target adapter); a Spec Kit constitution | GitHub Actions, GitHub Copilot, spec-driven development | New FDEs extend the product in hours | GitHub-hosted runners and Copilot per the organisation's plan |

**Suggested order:**
1. Feature 6 and the P0 fixes, which cost little and unblock everything else.
2. Feature 1, then 4: the biggest skill gaps. Feature 4 builds on the approval store from P0-2.
3. Features 2 and 3: the strongest money story.
4. Feature 5.

## Sources

- [GitHub changelog: Copilot coding agent supports AGENTS.md custom instructions (28 August 2025)](https://github.blog/changelog/2025-08-28-copilot-coding-agent-now-supports-agents-md-custom-instructions), and [GitHub docs: custom instructions support](https://docs.github.com/en/copilot/reference/custom-instructions-support)
- [GitHub Spec Kit documentation](https://github.github.com/spec-kit/)
- [Microsoft developer blog: spec-driven development with Spec Kit, 15 September 2025](https://developer.microsoft.com/blog/spec-driven-development-spec-kit/)
- [Microsoft Learn: examine the GitHub Spec Kit](https://learn.microsoft.com/en-us/training/modules/spec-driven-development-github-spec-kit-enterprise-developers/3-examine-github-spec-kit)
