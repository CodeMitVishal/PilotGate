# PilotGate suite: plan, decisions and acceptance matrix

Status legend: [ ] not started, [~] in progress, [x] verified (with the test or evidence that proves it).

## Goal

Three products that support EPAM's own AI platform (DIAL, open source, Apache 2.0) and AI in production:

| Product | Stage | What it does |
|---|---|---|
| **PilotGate** (exists) + **DIAL target mode** | Before release | Gates any app, including apps hosted on DIAL, with evidence for each reviewer |
| **DIAL Guard** | Runtime | A DIAL interceptor (EPAM's official `aidial-interceptors-sdk`) that applies deterministic guards to every request and response of the deployments it is attached to |
| **Production Watch** | In production | Reads guard events and audit logs, computes drift against thresholds, optionally re-scores a capped sample, alerts through a webhook |

## Rules (settled before building)

1. **Cost.**
   - No new Azure resources until the user approves a monthly estimate.
   - Never redeploy `infra/main.bicep` or create Cognitive Services accounts (Azure's anti-abuse check `715-123420`).
   - Most tests use a deterministic mock model. Real-model runs are few, and the running total is reported.
2. **No secrets in git.**
   - No service-principal secrets.
   - DIAL keys and encryption values are generated at setup into git-ignored files.
   - Model access stays Entra-only.
3. **Memory.** The local stack runs DIAL Core (JVM heap capped), Redis, the guard, the mock model and ClaimsAssist only: no chat UI or themes. `.wslconfig` is not changed.
4. **Pinning.** Every image is pinned by tag and digest. Python dependencies are locked with hashes. The SDK is pinned to 0.9.0 because it warns of breaking changes.
5. **Facts before code.** DIAL protocol details come from captures of a real DIAL Core, saved as golden fixtures (`dial/spike/README.md` lists 22 verified facts).

## DIAL Guard decisions

**What is guarded**
- **Request side, quarantined:** content addressed to an AI that arrives through a channel the user does not write:
  - tool and function messages;
  - inline attachment `data` in any message;
  - stage `content` in assistant history.

  Quarantine uses the same placeholder as the .NET `UntrustedContentGuard`.
- **Request side, flagged only:** user messages get an event, and their text is not changed. System messages are never changed.
- **Request side, rejected:** request attachments whose URL is neither DIAL storage nor an allowed host get HTTP 400, the same rule as EPAM's `reject-external-links` example.
- **Response side, guarded text fields:**
  - `delta.content`, `refusal` and `reasoning_content`;
  - stage `name` and `content`;
  - inline attachment `data`.

  Links to hosts outside the allow-list are removed (the link text is kept, markdown images are dropped). PII is redacted. A configured canary blocks the rest of that choice, which then ends with `finish_reason: content_filter`.
- **Response side, attachment URLs:** a URL that is neither DIAL storage nor an allowed host replaces the attachment with a safe stub at the same index. Indices must stay contiguous, because DIAL merges lists by index.
- **`tool_calls` stay byte-identical,** because editing would break their JSON arguments. They are scanned, and findings raise an event.

**How it detects**
- **Normalised views:**
  - Detectors run on a view with invisible characters removed: zero-width characters, bidi controls, soft hyphen.
  - The PII and injection views also fold confusable letters and apply NFKC.
  - Matches are mapped back to the original text.
- **Link hosts:** taken from the original URL, then IDNA-encoded. So a homoglyph host never matches an allowed host. The checks also cover userinfo, raw IPs (decimal, octal and hex), punycode, trailing dots and `hxxp`.
- **One host allow-list for both directions:** subdomains are allowed, and `intranet.contoso.example.evil.com` is not. Relative DIAL storage paths (`files/...`) are always allowed.
- **Phone numbers:**
  - Three German forms: `+49`, `0049` and the national `0`.
  - International numbers with `+` or the `00` dialling prefix.
  - A national-form number glued to a digit that stays in the text is part of a longer digit run and is not redacted, so that claim ids such as `CLM-2026-0142` survive. It is redacted when that digit is replaced too.
  - Accepted over-redactions, safe side:
    - a decimal like `0.0012345678`;
    - a bare number right after an IBAN, which joins the run.

**Streaming**
- One hold-back buffer per (choice, field). Text is released up to the earliest start of any partial or still-extendable match. Partial matching uses the `regex` module's `partial=True`.
- A context tail keeps lookbehinds correct across releases.
- A choice's buffers are flushed before the chunk that carries its `finish_reason`. Every choice index is still sent.
- **Cap:** a link longer than 2,048 characters is removed in both streaming and block mode, so the buffer stays bounded and the two modes agree. A run of overlapping PII-shaped matches longer than 512 characters (for example 25 e-mail addresses glued together) fails closed in every mode.
- **Partial matching:** with `partial=True`, the `regex` module returns a complete match at a later start in place of a partial match at an earlier one. A form that another alternative completes one position later must therefore be searched as its own pattern: `0049 151` vs the national `049 151`. Otherwise streaming released it unredacted, which the corpus test found.
- **Invariant (property-tested on two profiles, all PII kinds and the shared kinds):** for any split of the text into chunks, the output equals the block-mode result.

**Settings, failures and events**
- **Configuration:**
  - Comes from the interceptor's `defaults.custom_fields.interceptor_configuration`. The spike showed Core replaces or removes any value a client sends.
  - Every schema default is the strictest value.
  - Per-deployment profiles are several interceptor entries pointing at the same endpoint with different defaults.
- **Modes:**
  - `mode`: `enforce` (default) or `monitor` (shadow: events only, content unchanged).
  - `on_guard_error`: `fail_closed` (default) or `fail_open`, which passes the remaining content through unchecked and logs an event each time.
- **Errors:**
  - The guard's own failures become a generic error with a correlation id.
  - An outer wrapper sanitises the SDK's internal exceptions, which would otherwise leak raw exception text (spike fact 15).
  - Core's own error responses pass through unchanged.
- **Events:**
  - One JSON line per finding, plus one `request_completed` line per request, in `dial/schema/guard-event.v1.schema.json`.
  - Each event records the type, location, detector, count, conversation id and latency, never the matched value.
  - Python tests validate written events against the schema, and the .NET Watch tests validate events they read.
- **Conversation id:** the guard sets `headers_to_proxy=["x-conversation-id"]`, so applications and Watch can join records (spike fact 21).

**Tests and positioning**
- **Shared test corpus** (`dial/schema/guard-corpus.v1.json`): attack, PII and link cases with expected verdicts. The Python tests and the .NET guard tests run the same file, so the two implementations cannot drift. The corpus checks verdicts. The output format may differ, because the .NET guard removes whole answer sentences.
- **Positioning (checked 2026-11-27):** EPAM's public interceptors are the SDK's examples and the archived `ai-dial-interceptor-google` (Google Cloud DLP). The examples are:
  - `reject-external-links`: rejects request attachments whose URL points outside DIAL;
  - `reject-blacklisted-words`: raises only after the response text has already streamed;
  - anonymizers: spaCy and Google DLP, which mask PII in requests.

  None of them quarantines instructions planted in tool output or attachments, holds back streamed text until it is checked, filters links in the model's answer, or emits events for monitoring. The guard covers those gaps and runs alongside the existing interceptors.

## ClaimsAssist DIAL mode decisions (trust model)

- **Auth mode `Dial`:**
  - Maps only `POST /openai/deployments/{id}/chat/completions` and `/health`.
  - `/chat`, `/approvals` and the `X-Dev-Persona` header do not exist in this mode. In development mode the DIAL endpoint does not exist. One process never offers both.
- **Identity:**
  - Taken only from `GET {configured DIAL_URL}/v1/user/info`, called with the per-request key DIAL sends.
  - A configured role-to-persona map turns the DIAL role into a persona. An unknown role, or roles that map to two personas, gets HTTP 403.
  - Client headers are never trusted: the spike showed Core forwards even a spoofed `X-DIAL-Deployment-Id`.
- **Stateless turns:**
  - Each request answers the last user message, which is how `AskAsync` already works.
  - Approvals stay on the server with their owner. A user approves with a message `approve <id>` or `reject <id>`. `ResolveApprovalAsync` checks that the caller is the owner (or a team lead), and its deterministic guards run again.
  - State the client supplies (`custom_content.state`, `form_value`) is ignored.
- **Failures:**
  - A failing turn returns HTTP 500 with a reference that is also logged. The message distinguishes the model being unavailable from an internal fault.
  - Not 502 or 503: DIAL Core 0.50.8 replaces such a body with its own "No route", observed through Core. A 500 body reaches the client unchanged (spike fact 13).
  - A request the client cancelled is not answered.
- **Secrets and audit:**
  - Dial mode refuses to start with the public development pseudonymisation key, or with a key shorter than 32 characters.
  - In Azure the audit trail goes to standard output and then Log Analytics (`AuditSink=Stdout`), because a replica's disk is lost on restart.
- **Network:**
  - ClaimsAssist and the guard publish no ports in compose and get internal-only ingress in Azure. Only DIAL Core is reachable.
  - In Azure, Core calls the internal apps by name over HTTP. Their internal ingress allows HTTP, and peer-to-peer encryption protects the hop. The apps call Core through its HTTPS name.
  - Break-it test: the app port is unreachable from outside the network.
- **Spoofed headers:** a spoofed `X-Dev-Persona` or `X-DIAL-*` header through DIAL gains nothing. The end-to-end test `test_a_spoofed_persona_header_gains_nothing` proves it. It is not a PilotGate scenario, because gate scenarios do not carry custom headers.

## PilotGate DIAL mode decisions

- Talks to DIAL's OpenAI-compatible API with one DIAL key per persona (generated, git-ignored). It sends a warm-up request before timing anything.
- **Black-box checks:** a DIAL target returns no trace. So positive controls for permission trimming, unapproved writes and invalid citations report **not measurable** (WARN), never PASS. No trace data travels in `custom_content`, because every DIAL user would see it.
- **Measurable from outside:** answers, forbidden strings, PII, attack indicators, the AI disclosure, latency, and cost when usage is returned.

## Production Watch decisions

- Reads guard events (schema v1) and the ClaimsAssist audit log from files (local stack). In Azure both go to Log Analytics. A Log Analytics reader follows only once the Azure deployment is approved.
- Deterministic windowed metrics: request volume and finding rates per deployment and kind, guard errors, fail-open passthroughs, latency p95.
- Thresholds and drift against a baseline window are configured in `watch/watch.json`.
- LLM re-scoring is optional, sampled and cost-capped. A fake judge is used in tests.
- Alerts go to a webhook URL the user supplies, with retry and backoff. Tests use a local fake receiver.
- Scheduled on the existing Azure Pipelines self-hosted agent, at no extra cost.

## Build order

1. Event schema and shared corpus
2. Guard with offline tests: unit, property and break-it, plus the real SDK app driven through an httpx `MockTransport` replaying the golden fixtures
3. Compose stack with a mock model and an end-to-end test (G7 early)
4. ClaimsAssist DIAL endpoint
5. PilotGate DIAL mode
6. Production Watch
7. Azure IaC (what-if only)
8. CI and docs

**Where each guard is proven:** ClaimsAssist runs its tools internally, so the guard in front of ClaimsAssist only proves the output guards. Tool-message quarantine is proven with the guard in front of the mock model, which echoes back what it received.

## Acceptance matrix

| # | Product | Criterion | Proof | Status |
|---|---|---|---|---|
| S1 | Spike | Interceptor and application protocol captured from a real DIAL Core (paths, headers, SSE framing, error bodies) | Golden fixtures in `dial/guard/tests/fixtures/dial-core-0.50.8/`, facts in `dial/spike/README.md` | [x] |
| S2 | Spike | Behaviour when the interceptor is down is known (fail closed or not) | Core returns 502 and never calls the app: `client/block_interceptor_down.json` | [x] |
| E1 | Schema | Guard events validate against the v1 schema; events never contain matched values | Writer: `test_events.py` + every app/e2e test; reader: `EventReaderTests` (real guard events validate in .NET, invalid lines counted); schema refuses free-text fields | [x] |
| G1 | Guard | Planted instructions in tool messages, attachment data and history stages are quarantined; user and system messages are not modified | `test_request.py`, shared corpus (`test_corpus.py`), e2e `test_tool_message_is_quarantined_before_the_application` | [x] |
| G2 | Guard | No PII value or disallowed URL reaches the client in any guarded field, for any split of the stream into chunks; streaming output equals block output | Property tests (hypothesis) on two PII profiles: split invariance, no-leak rescan, idempotence, planted PII (block and streamed). 25x fuzz run clean (28,750 generated cases). After the phone changes, 10x run clean (303 tests in the pinned image). The fuzzing found and pinned the `00` prefix gap, the `0049` streaming hold-back gap and a decimal rule that broke idempotence | [x] |
| G3 | Guard | `tool_calls` pass through byte-identical (scanned, event only); disallowed attachment URLs are replaced by a stub at the same index | `test_response.py`, `test_app.py`, e2e | [x] |
| G4 | Guard | Fail-closed and fail-open both behave as configured when the guard itself errors; monitor mode changes nothing | `test_app.py` (fail closed/open, request side, SDK internal), e2e monitor profile | [x] |
| G5 | Guard | URL obfuscation is handled (look-alike suffix hosts, punycode and homoglyphs, raw IPs in decimal, octal and hex, `hxxp`, userinfo, trailing dot) | `test_links.py` (60 cases), `test_textguard.py` | [x] |
| G6 | Guard | Malformed requests, malformed upstream chunks, oversized payloads, upstream 5xx and timeouts, concurrent streams: no crash, no leak, no exception text in client bodies | `test_app.py` (30 concurrent streams, 2 MB payloads, malformed chunks), e2e (20 concurrent streams via Core) | [x] |
| G7 | Guard | Works through a real DIAL Core in Docker, streaming and non-streaming, with the conversation id passed on | `tests/e2e/test_stack.py`: 15 tests against DIAL Core 0.50.8, including Core failing closed (502) when the guard is down | [x] |
| A1 | ClaimsAssist | Callable as a DIAL application (streaming and non-streaming); identity from `/v1/user/info` | `DialTests.cs` (offline, framing checked against the spike fixture). End-to-end `test_claimsassist.py` through DIAL Core + guard with the real model. The container image was run on the stack's network: non-root user, identity via Core, 403 for a role without a persona | [x] |
| A2 | ClaimsAssist | Trust model: spoofed headers ignored, unknown role denied, approvals bound to the owner, failures without internals, app port unreachable from outside | Verified: spoofed `X-Dev-Persona` gains nothing (SIU positive control); intern 403; another handler gets 403 on an approval; an approval is decided once; dev endpoints 404 in Dial mode; the dev pseudonym key stops startup (`StartupAndAuditTests`, real process and image); a model failure reaches the client as a 500 with a logged reference, through Core and the guard. Network isolation in Azure, verified live: the three internal apps answer 404 from the internet, Core redirects plain HTTP (301), and the same checks passed through the deployment (evidence 7). Locally ClaimsAssist binds to 127.0.0.1, a local trust boundary only | [x] |
| P1 | PilotGate | DIAL mode runs the scenarios against a DIAL deployment and produces an evidence pack | Local run 20261127-152836: 27 scenarios through DIAL Core + DIAL Guard, GO WITH CONDITIONS, p95 3.2 s, $0.147. Azure run 20261127-173245 against the Container Apps deployment: GO WITH CONDITIONS, p95 3.4 s, $0.112 | [x] |
| P2 | PilotGate | Without a trace, permission and tool-call checks report not measurable, never pass | `DialModeTests.cs` (clean black-box runs WARN, visible leaks FAIL); the real run shows WARN with reasons | [x] |
| W1 | Watch | Metrics and drift computed correctly from event logs | `MetricsAndRulesTests` (exact counts, rates, percentiles, thresholds at their boundaries, drift factor/min rate/min baseline, silence); real run over today's logs | [x] |
| W2 | Watch | Alerts delivered to a webhook; failures retried and reported | `NotifierTests`: retries with backoff, permanent 4xx, Retry-After, give-up reported (exit 3), https-only, delivery to a real TCP receiver | [x] |
| W3 | Watch | LLM re-scoring respects its sample size and cost cap | `RescorerTests` (sample size, deterministic sample, cap never crossed); real run: 5 of 32 answers, mean 4.8/5, $0.0006 | [x] |
| D1 | Deploy | Whole local stack starts with one command, health-checked, pinned images, only DIAL Core exposed | `dial/stack`: `setup.ps1` + `docker compose up -d --build --wait`; e2e `test_only_dial_core_is_reachable_from_outside` | [x] |
| D2 | Deploy | Azure IaC prepared and validated (what-if), not deployed without cost approval | `az bicep build` passes the linter cleanly. `what-if` succeeds for both phases: only creates, and the Azure OpenAI account and CI identity show as Ignore. The planned configuration was checked: peer-to-peer encryption, ingress per app, the apps' DIAL URL, the secret references | [x] |
| D3 | Deploy | `deploy.ps1` is safe to run | Windows PowerShell 5.1, 18 checks: parses; refuses without `-ApproveCost` (exit 2); az errors keep az's message (this found the `-o` parameter-binding bug); JSON and tsv parsing; missing identity tolerated; DIAL status codes over HTTP (local stack) and HTTPS (Azure); the smoke test's retry helper (ready at once, deadline on 5xx); `az containerapp` on the core CLI. `-Destroy` filter dry-run on the real resource group selects nothing and never the Azure OpenAI account. PowerShell 7 not tested | [x] |
| D4 | Deploy | The deployment works at runtime in Azure | One-day run on 2026-11-27, approved by the user: `deploy.ps1 -ApproveCost` took 10 minutes and its smoke test passed on the first attempt. 16/16 live flow steps passed through the public URL. 54 guard events and 34 audit records reached Log Analytics. PilotGate `--dial` against Azure: GO WITH CONDITIONS, $0.112. Then removed with `-Destroy`. Evidence: `docs/evidence/7-dial-suite-live-in-azure` | [x] |
| R1 | Regression | All offline tests and one gate run pass before each PR | `dotnet test` 192/192; `scripts/test-guard.ps1` 303/303 (10x fuzz); end-to-end 22/22; gate run 20261127-170516: GO WITH CONDITIONS, every blocker PASS, p95 2.8 s, $0.138 | [x] |
