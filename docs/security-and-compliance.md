# Security and compliance analysis

A screening aid for the people who sign off (CISO, DPO, compliance, works council), not legal advice. It covers
ClaimsAssist, PilotGate, DIAL Guard, Production Watch, the IaC in `infra/` and the pipelines, as of 2026-11-27. Every
"met" row names the code, test or real run that shows it; every gap is stated as a gap.

## Summary

- **Strongest controls.** The controls that matter most are deterministic code paths, not prompt instructions:
  - permission trimming before ranking;
  - one write tool behind human approval, with its guards re-run after approval;
  - Entra ID only, with no keys anywhere;
  - output guards for links and PII;
  - figure grounding enforced by code: every figure in the answer text must appear in a source the answer cites, a claim file or settlement result from that turn, or the handler's own question;
  - a streaming guard that fails closed.

  They are tested offline (340 .NET and 303 guard tests), on every pull request (the gate), and live in Azure (evidence 7).
- **Before go-live:**
  - high availability and recovery (DIAL Core runs one replica; ClaimsAssist and DIAL Guard run two or more only in the opt-in HA mode, which is not deployed yet);
  - supply-chain hygiene (no SBOM, signing or vulnerability scanning yet);
  - Entra ID sign-in for DIAL users (untested);
  - a second injection layer (Prompt Shields);
  - the organisational items: a DPIA, a works agreement, AI literacy training, and the DORA register of information.

## 1. Hardcoded values and secrets

| Checked | Result | Evidence |
|---|---|---|
| Secrets in tracked files (keys, tokens, passwords, connection strings) | None | Pattern scans of `src`, `dial`, `infra`, `scripts`, `pipelines`, `gate`, `watch`; `git add --dry-run` scans before every commit offer |
| Generated secrets | Only in git-ignored files, created with a cryptographic random generator, never printed | `dial/stack/.env`, `keys.json`, `config/core-config.json`; `infra/dial/.keys.json`, `.secrets.json` |
| Model access | Entra ID only. Managed identity in Azure, Azure CLI locally, workload identity federation in CI. The Azure OpenAI account has local auth disabled | `AgentCredential`, `infra/main.bicep` |
| Policy that was hardcoded, now configuration | Link allow-list (`ClaimsAssist:AllowedLinkHosts`, one Bicep parameter shared with DIAL Guard); approver groups (`ClaimsAssist:ApproverGroups`); model timeout (`ClaimsAssist:ModelTimeoutSeconds`); Azure OpenAI account name (`deploy.ps1` reads it from the infra deployment's outputs) | Startup validation tests; 21-check deploy-script harness |
| Defaults | The strictest value applies when configuration is missing: no links allowed, only the requester approves, the guard enforces and fails closed with every PII kind on. Dial mode refuses to start with the public development pseudonymisation key | `GroundingTests.Policy_lists_default_to_the_strictest_value`, `StartupAndAuditTests` |
| Deliberately fixed in code | The prompt canary `SYS-CANARY-9921`, a test marker the prompt, the gate and the guard must all know; the agent's tool names (facts of the code); Entra's token scope (an Azure constant) | Comments in `main.bicep` and `UntrustedContentGuard` |

## 2. OWASP Top 10 for LLM Applications (2025)

| Risk | How the system meets it | Evidence | Residual risk, next step |
|---|---|---|---|
| LLM01 Prompt Injection | Untrusted text (documents, attachments, tool output) is cleaned and quarantined before the model sees it. DIAL Guard quarantines tool messages and attachment text. Attack indicators are scanned in every answer | Gate I01 to I04 on every PR; run 7: planted tool instruction quarantined live | Quarantine is a heuristic. Add Azure AI Content Safety Prompt Shields (needs a Content Safety resource: cost approval) |
| LLM02 Sensitive Information Disclosure | Permission trimming before ranking; data-minimised claim views; PII redaction in answers and audit; streaming redaction in DIAL Guard | Gate P01 to P04, X01, X02 (blockers); 303 guard tests incl. property tests on two profiles; run 7 | Patterns cover structured PII. Names in free text need an NER model, as an extra interceptor |
| LLM03 Supply Chain | Images pinned by digest; Python dependencies hash-locked; DIAL Core and Redis imported into a private registry by digest; NuGet versions pinned | Dockerfiles, `requirements*.lock`, `deploy.ps1` | **Gap:** no SBOM, no image signing, no automated vulnerability scanning |
| LLM04 Data and Model Poisoning | Ingestion strips hidden markup and invisible characters and quarantines instructions; no fine-tuning | Run 2: a hidden FAQ comment caught and fixed | **Gap:** change control for knowledge-base documents (who may publish) |
| LLM05 Improper Output Handling | Link allow-list in both guards; PII redaction; figure grounding; `tool_calls` scanned and passed byte-identical | `UntrustedContentGuard`, DIAL Guard tests | Downstream apps must render answers as text, not HTML |
| LLM06 Excessive Agency | One write tool, behind approval; payee fixed to the IBAN on file; authority limit re-checked after approval; approvals bound to their owner | Gate A01, A02, HUMAN_OVERSIGHT (blocker); run 7: another handler got 403 | None known |
| LLM07 System Prompt Leakage | Canary in the prompt; DIAL Guard stops a choice that leaks it; the gate checks every answer | Run 7: canary stopped with `content_filter` | No secrets live in the prompt |
| LLM08 Vector and Embedding Weaknesses | Filtering by role before ranking in both BM25 and vector search; tools built per request for the caller | `HybridRetriever.SearchAsync`; run 4 (planted leak caught) | Production: Azure AI Search with a security filter |
| LLM09 Misinformation | Citations checked against what was retrieved for the caller. **Figure grounding enforced by code:** every number, date and claim ID in the answer text must appear in a cited source (exactly as the model saw it), a claim file or settlement result from that turn, or the handler's question; otherwise the sentence is withheld. The approval block's arguments are covered by the human decision and the re-run guards, not by this check. Refusals when sources are silent; LLM judge for semantic grounding | Run 9: figures found in their sources in 29/29 sentences, 16/16 tasks, groundedness 5.0/5. Replay of 103 real answers (runs 1, 3, 5, 6): 1 justified withhold. Mutation test: 195 of 210 invented figures caught | A wrong figure that also appears in the sources passes the code check, which is why the judge stays. Azure AI Content Safety groundedness detection is the next layer |
| LLM10 Unbounded Consumption | 700 output tokens per answer, at most 4 queries per search, top-k caps, 20 s model timeout with retries, guard scan budget and run cap | COST_LATENCY on every PR. Run 8 failed it with a 104 s answer, consistent with the SDK's 100-second default timeout; after a 20 s timeout with retries, run 9's p95 was 2.4 s | **Gap:** per-user quotas through DIAL role limits (currently empty) |

## 3. OWASP Top 10 for Agentic Applications (2026)

| Risk | How the system meets it | Gap |
|---|---|---|
| ASI01 Agent Goal Hijack | Retrieved content and tool output are untrusted and quarantined; objectives are fixed in code | Heuristic quarantine (see LLM01) |
| ASI02 Tool Misuse & Exploitation | Tools bound per request to the caller's permissions; the write tool has no payee parameter, needs approval, and is re-checked | None known |
| ASI03 Identity & Privilege Abuse | Identity only from DIAL Core (`/v1/user/info`) or Entra ID; spoofed headers ignored (tested live); least-privilege managed identity (AcrPull and OpenAI User only) | Entra ID sign-in for DIAL users untested |
| ASI04 Agentic Supply Chain Vulnerabilities | As LLM03 | SBOM, signing, scanning |
| ASI05 Unexpected Code Execution | No code-execution tool; containers run as non-root, with read-only file systems and all capabilities dropped locally | None known |
| ASI06 Memory & Context Poisoning | Stateless turns: only the last user message is answered, and client-sent history or state is ignored; no long-term memory | None known |
| ASI07 Insecure Inter-Agent Communication | One agent; service hops through DIAL with per-request keys; peer-to-peer encryption in Azure | None known |
| ASI08 Cascading Failures | Guard fails closed; Core returns 502 when the guard is down; model timeout and retries; Watch alerts | Single replicas (see DORA) |
| ASI09 Human-Agent Trust Exploitation | AI disclosure on every answer; warnings when content was withheld or quarantined; approvals show the exact arguments | None known |
| ASI10 Rogue Agents | Gate on every PR; Production Watch drift alerts; one-command removal (`deploy.ps1 -Destroy`) or delisting in DIAL Core | Automated kill switch on alert |

## 4. OWASP Top 10 (2021), HTTP surfaces

| Risk | Status |
|---|---|
| A01 Broken Access Control | Identity from DIAL Core, permission trimming, owner-bound approvals; internal ingress for the guard and ClaimsAssist (they answer 404 from the internet, run 7) |
| A02 Cryptographic Failures | HTTPS only at the edge (HTTP gets 301); peer-to-peer encryption inside the environment; HMAC pseudonyms with a secret key; Container Apps secrets |
| A03 Injection | No SQL or shell; strict JSON parsing; prompt injection under LLM01 |
| A04 Insecure Design | Threat model and trust decisions in [docs/suite/PLAN.md](suite/PLAN.md); deterministic guards |
| A05 Security Misconfiguration | Local auth disabled (Azure OpenAI, Log Analytics); registry admin user off; no published ports except DIAL Core; startup refusals for weak settings |
| A06 Vulnerable and Outdated Components | Pinned versions. **Gap:** no automated vulnerability scanning |
| A07 Identification and Authentication Failures | One random 160-bit DIAL key per identity; Entra ID for the model. **Gap:** Entra ID sign-in for DIAL users untested |
| A08 Software and Data Integrity Failures | Hash-locked dependencies, image digests. **Gap:** no image signing |
| A09 Security Logging and Monitoring Failures | Guard events (never the matched value) and the audit trail reach Log Analytics (run 7: 54 events, 34 records); Watch alerts through a webhook |
| A10 Server-Side Request Forgery | The guard follows no redirects; the DIAL URL is fixed by configuration; links to IP addresses are refused |

## 5. DORA (Regulation (EU) 2022/2554), applicable to insurers since 17 January 2025

| Pillar | What the system contributes | Evidence | Gap |
|---|---|---|---|
| ICT risk management (Art. 5 to 16; RTS (EU) 2024/1774) | Inventory as code (Bicep); protection by Entra ID, least privilege, network isolation and encryption (Art. 9); detection by Watch rules for guard errors, fail-open, latency and drift (Art. 10); recovery by redeploying from IaC (Art. 11); learning from evidence packs per run (Art. 13) | Run 7: deployed and smoke-tested in 10 minutes, removed with one command | **DIAL Core runs one replica**. The opt-in HA mode runs two or more replicas of ClaimsAssist and DIAL Guard, with pending approvals in Azure Managed Redis, so a restart or a new revision loses none; it is not deployed yet. **No RTO/RPO** or DR region defined (Art. 11, 12) |
| Incident management (Art. 17 to 23; RTS (EU) 2024/1772, (EU) 2025/301) | Alerts with severity through a webhook; every failure carries a reference that is also logged | Watch tests; DIAL failure contract | Wire the alerts into the entity's incident process and its classification criteria |
| Resilience testing (Art. 24 to 27) | Security scenarios on every PR; property and fuzz tests; end-to-end tests; live smoke test | Gate runs 1 to 9; 10x fuzz run | **No TLPT** (Art. 26) and no load or chaos tests |
| ICT third-party risk (Art. 28 to 44) | Exit strategy built in: PilotGate gates any OpenAI-compatible or DIAL target as a black box; DIAL abstracts the model provider; everything redeploys from code | `--dial` mode; run 7 | Register of information entries for Microsoft Azure (OpenAI, Container Apps, Log Analytics) and for the DIAL components; Art. 30 contract terms; concentration risk |

## 6. EU AI Act (Regulation (EU) 2024/1689), as amended by the Digital Omnibus on AI

**Classification** (the same as the gate's own screening in `gate/gate.json`):
- **Use.** ClaimsAssist is an internal assistant for claims handlers.
- **Not high-risk.** It is not used for risk assessment or pricing in life and health insurance, for creditworthiness, for employment decisions or for biometrics, so it is not an Annex III high-risk system.
- **Transparency duty.** It interacts with natural persons, so the Art. 50 transparency duty applies.

| Topic | Status |
|---|---|
| Art. 50(1) transparency (applies from 2 August 2026, not delayed) | AI disclosure on every response: 30/30 in run 9 (gate TRANSPARENCY) |
| Art. 50(2) machine-readable marking of generated text (applies from 2 August 2026; the grace period to 2 December 2026 covers only systems on the market before August, which a system going live now is not) | Direct-mode API: the response carries a separate `aiDisclosure` field. **Gap in DIAL mode:** the disclosure is in the answer text only. Add a machine-readable marker to the DIAL response and confirm that DIAL Core forwards it, or mark at the chat-client layer |
| Art. 4 AI literacy (applies since 2 February 2025) | **Organisational gap:** train handlers before go-live |
| High-risk requirements, followed as good practice | Risk management (gate + Watch), record-keeping (audit trail, evidence packs), human oversight (approvals), accuracy and robustness (figure grounding, guard, tests) |
| Timeline | Prohibitions since 2 February 2025. General-purpose model obligations since 2 August 2025 (on the model provider). Annex III high-risk obligations moved to 2 December 2027 and Annex I to 2 August 2028 by the Digital Omnibus on AI, in force since 27 July 2026 |
| Re-screening trigger | Any move towards claim decisions, or towards risk assessment or pricing in life and health insurance, makes it high-risk: rerun `pilotgate screen` |

## 7. GDPR

| Article | Status |
|---|---|
| Art. 5(1)(c) data minimisation | Claim views minimised for the model; PII redacted from answers and audit |
| Art. 5(1)(f), Art. 32 security | Sections 2 to 4 |
| Art. 9 special categories (health data) | Processed, so a **DPIA is required (Art. 35)**. The gate lists it as a condition |
| Art. 22 automated decisions | No solely automated decision: every write needs a handler's approval |
| Art. 25 privacy by design and by default | Strictest defaults; redaction; pseudonymised audit with a secret key (enforced in Dial mode) |
| Art. 28 processors, Art. 44 transfers | Azure OpenAI in EU Data Zone deployments; DIAL and the apps in Sweden Central |
| Retention | Log Analytics keeps 30 days. A pending approval holds claim data (the question, the trace and the requester's role), so its expiry is also its retention limit: 30 minutes by default (`ApprovalTtlMinutes`, 1 to 1440), in memory or in Redis. **Gap:** an audit-retention policy agreed with the DPO |

## 8. Insurance supervision and works council

| Topic | Status |
|---|---|
| **EIOPA Opinion on AI governance and risk management (6 August 2025).** It covers data governance, record-keeping, fairness, cyber security, explainability and human oversight for AI systems outside the AI Act's high-risk and prohibited classes, such as ClaimsAssist | Data governance: synthetic data, minimisation, KB ingestion cleaning. Record-keeping: audit trail, evidence packs. Cyber security: this document. Explainability: citations, figure grounding, warnings. Human oversight: approvals. **Gap: fairness is not measured yet.** Add paired scenarios across customer groups and regions |
| Works council (BetrVG §87(1) no. 6) | The audit trail could monitor individual handlers, so a works agreement is needed. Handlers are pseudonymised already. The gate lists it as a condition |

## 9. Next steps, in order

1. **Resilience (DORA Art. 11, 12).** Deploy and test the HA mode (approved cost), then two or more DIAL Core replicas on a managed Redis and shared storage; RTO/RPO and a DR region.
2. **Supply chain (LLM03, ASI04, A06, A08).**
   - SBOMs for the images and packages.
   - Image signing in the registry.
   - Vulnerability scanning in the pipeline: `dotnet list package --vulnerable`, pip-audit, and Microsoft Defender for Containers.
3. **Second-layer model safety (LLM01, LLM09).** Azure AI Content Safety Prompt Shields and groundedness detection. This needs a Content Safety resource and cost approval.
4. **Identity.** Test Entra ID sign-in for DIAL users, with app roles mapped to the DIAL roles.
5. **Fairness scenarios (EIOPA).** Add them to the gate.
6. **Incident process (DORA Art. 17 to 19).** Classification and the reporting path for Watch alerts.
7. **Testing before go-live.** A penetration test, and TLPT where the entity is in scope (DORA Art. 24 to 26).
8. **Organisational items.** DPIA, works agreement, AI literacy training, register of information, knowledge-base change control.
9. **EU AI Act Art. 50(2).** A machine-readable AI marker in DIAL responses, confirmed to pass through DIAL Core.

## Sources

- [EIOPA: Opinion on AI governance and risk management](https://www.eiopa.europa.eu/publications/opinion-artificial-intelligence-governance-and-risk-management_en), and [press release, 6 August 2025](https://www.eiopa.europa.eu/eiopa-publishes-opinion-ai-governance-and-risk-management-2025-08-06_en).
- Digital Omnibus on AI:
  - [AI Act Explorer](https://artificialintelligenceact.eu/ai-act-explorer/digital-omnibus/);
  - adopted, Annex III moved to 2 December 2027 ([Obligara, 13 November 2026](https://obligara.com/news/2026-11-13-eu-ai-act-digital-omnibus-delay), [aiacto](https://www.aiacto.eu/en/blog/digital-omnibus-ai-act-report-2027));
  - Article 50 not delayed ([Usercentrics](https://usercentrics.com/knowledge-hub/eu-ai-act-high-risk-delay-article-50-transparency-consent/));
  - Art. 50(2) grace period only for systems on the market before 2 August 2026 ([Cloud Security Alliance research note](https://labs.cloudsecurityalliance.org/research/csa-research-note-eu-ai-act-article50-watermarking-deadline/), [Commission guidelines overview](https://www.regulation-ai.eu/en/transparency-obligations/guidelines/)). These are secondary sources; check against the Official Journal text before relying on them.
- [OWASP Top 10 for Agentic Applications for 2026](https://genai.owasp.org/resource/owasp-top-10-for-agentic-applications-for-2026/). Item names were cross-checked with the [FINOS reference](https://air-governance-framework.finos.org/references/owasp-asi) and [Cycode](https://cycode.com/blog/owasp-top-10-agentic-applications/).
- DORA technical standards:
  - [FMA Liechtenstein list of DORA delegated regulations](https://www.fma-li.li/en/supervision-regulation/dora/dora-related-delegated-regulations-and-directives);
  - [DLA Piper on the first DORA delegated regulations](https://www.dlapiper.com/en-eu/insights/publications/law-in-tech/2024/dora-first-delegated-regulations-published-in-eu-official-journal).
