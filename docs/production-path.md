# Production path

What is prototype-grade in this repository, and what replaces it in production. The same list is printed in every evidence pack.

| Area | Prototype | Production |
|---|---|---|
| Identity | DIAL mode by default (identity from DIAL Core). The `X-Dev-Persona` header, mapped to `data/personas.json`, only when `AuthMode=DevPersonas` is set explicitly, and only on loopback addresses | Microsoft Entra ID sign-in with Microsoft.Identity.Web; groups from the token's `groups` claim (or app roles); the agent gets its own Entra Agent ID |
| Retrieval | In-process hybrid index (BM25 + embeddings + RRF) | Azure AI Search hybrid query with semantic ranker, `allowedGroups` field filtered per query (or native document-level access control), indexer from SharePoint or Blob Storage |
| Untrusted content | Regex sanitize and quarantine at ingestion and render time | Same deterministic layer, plus Azure AI Content Safety Prompt Shields for prompts and documents |
| Model access | Direct calls to Azure OpenAI v1 endpoint | Azure API Management AI gateway: `llm-token-limit` per handler, `llm-emit-token-metric` for chargeback, backend pool with fallback, semantic cache where safe |
| Network | Public endpoints with Microsoft Entra ID only (Azure OpenAI, and Azure Managed Redis in HA mode) | Private endpoints for Azure OpenAI, AI Search and Redis, `publicNetworkAccess: Disabled`, a VNet-integrated Container Apps environment |
| Approvals | Pending approvals as records with expiry: in memory (one replica) or in Azure Managed Redis (HA mode). Approval over HTTP or a DIAL message. The decision runs the stored call through the tool's guards, with no second model call. Payouts go to an in-process queue (stand-in) | The same store contract on Redis or Cosmos DB; approval as a Teams Adaptive Card to the handler or team lead; four-eyes above authority; payouts through the claims system's payout API, with the approval id as the idempotency key |
| Availability | One replica each by default. `highAvailability`: ClaimsAssist 2 to 4 and DIAL Guard 2 to 3 replicas, approvals in Redis, probes on `/health/live` and `/health/ready`. DIAL Core stays at one replica (in-memory Redis sidecar, storage not shared) | DIAL Core scaled out on a managed Redis and shared storage (EPAM's Helm charts on AKS, or Container Apps with Azure Blob Storage); a zone-redundant environment; RTO and RPO agreed, with a DR region |
| Audit and telemetry | JSON Lines file, pseudonymised and redacted | OpenTelemetry from Agent Framework to Application Insights; retention per works agreement; the gate reads traces from there |
| Evaluation | 28 scenarios, 16 golden questions | 100+ SME-written questions, sampled production traffic re-scored weekly, red-team set extended with each incident |
| CI | Azure Pipelines gate on every pull request (build-validation policy), self-hosted agent, workload identity federation | Microsoft-hosted agents after the free parallelism grant, approvals on a pipeline environment, a cached judge |

## CI setup (one time, Azure DevOps)

1. Deploy the CI identity. It references the Azure OpenAI account as an existing resource and never modifies it:
   `az deployment group create -g rg-pilotgate-demo -n pilotgate-ci -f infra/ci-identity.bicep -p accountName=<accountName output of main.bicep>`.
   This creates the user-assigned managed identity `id-pilotgate-ci` with the **Cognitive Services OpenAI User** role on the account, and nothing else: no subscription role and no secret.
2. In the Azure DevOps project, create:
   - the Azure Resource Manager service connection `pilotgate-azure`, using workload identity federation (manual), the output `ciClientId` and your tenant;
   - the variable group `pilotgate-ci` with `AZURE_OPENAI_ENDPOINT`, which is not a secret.
3. Run step 1 again with `-p ciFederationIssuer=<issuer> ciFederationSubject=<subject>`, copied from the service connection. The identity then trusts that one service connection and nothing else.
4. Create a pipeline from `azure-pipelines.yml`, allow it to use the service connection, the variable group and the `Default` agent pool, and add a build-validation branch policy on `main` that runs it.
5. **Agents.** A new organization gets no free Microsoft-hosted minutes for private projects until you request the free grant at https://aka.ms/azpipelines-parallelism-request (2-3 business days). Until then, run a self-hosted agent in the `Default` pool:
   - `config.cmd --url https://dev.azure.com/<org> --auth pat --pool Default --agent <name> --work _work`, with a personal access token scoped to *Agent Pools (Read & manage)*;
   - then `run.cmd`.

   After the grant, queue the pipeline with `useMicrosoftHostedAgent: true`, or change that parameter's default.

Note: redeploying `main.bicep` repeatedly can trip Azure's anti-abuse preflight check on the Azure OpenAI account (error 715-123420). That is why the CI identity lives in its own file that only references the account.

## Cost model

Measured in the final run (v4): mean $0.0011 and p95 $0.0015 per conversation with gpt-4.1-mini in the EU Data Zone, about $6.81 a month at 6,000 lookups. One full gate run costs about $0.12 (agent $0.03, judge $0.09). Judge answers are cached, so re-runs on unchanged answers cost nothing.

## Backlog from the evidence

- Replace the example chunk id in the system prompt with a neutral one: in run 6 the model cited `KB-PROP-GUIDE#settlement-authority` from the instructions without retrieving it (the validator dropped it).
- Add Prompt Shields as a fourth injection layer and an extra gate check.
- Grow the golden set to at least 100 questions; add multi-turn scenarios.
- Add a load test (p95 latency under 20 concurrent handlers) before the production decision.
