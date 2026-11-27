# Getting started

Pick the path that matches what you want to do. Each step says what to run and what you should see. All commands are PowerShell, run from the repository root.

| Path | What you get | Time | Cost | You need |
|---|---|---|---|---|
| [1. See it work offline](#1-see-it-work-offline) | The test suites, a real evidence pack, an EU AI Act screening | 5 minutes | Free | .NET 10 SDK |
| [2. Gate the sample agent](#2-gate-the-sample-agent-against-a-real-model) | A live GO / NO-GO verdict for ClaimsAssist | 15 minutes | About $0.12 per run | Azure subscription, Azure CLI |
| [3. Gate your own agent](#3-gate-your-own-agent) | The same verdict and evidence for your AI app | 1 to 2 hours | About $0.12 per run | Your agent, Azure OpenAI for the judge |
| [4. Run the DIAL suite locally](#4-run-the-dial-suite-locally) | DIAL Guard and ClaimsAssist on EPAM's AI DIAL, gated and monitored | 20 minutes | About $0.15 per gate run | Docker Desktop, path 2 |
| [5. Gate every pull request](#5-gate-every-pull-request) | A NO-GO blocks the merge | 30 minutes | Model tokens only | Azure DevOps |
| [6. Deploy to Azure](#6-deploy-to-azure-container-apps) | The DIAL suite live in Azure Container Apps | 15 minutes | About $45 to $100 a month while it runs | An approved cost estimate |

## 1. See it work offline

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download).
2. Get the code:
   ```powershell
   git clone https://github.com/CodeMitVishal/PilotGate.git
   ```
   ```powershell
   cd PilotGate
   ```
3. Run the tests. **Expect 340 passed**, no network needed:
   ```powershell
   dotnet test
   ```
4. Open a real evidence pack in your browser: `docs/evidence/9-figure-grounding-GO-WITH-CONDITIONS/report.html`. It has one card per reviewer (sponsor, CISO, data protection officer, works council, engineering), the seven checks and the verdict.
5. Screen the use case against the EU AI Act and GDPR. It runs offline and is a screening aid, not legal advice:
   ```powershell
   dotnet run --project src/PilotGate -- screen --config gate/gate.json
   ```
6. Re-score the first, failing run with today's gate. It reuses the stored traces, so it makes no model calls. **Expect NO-GO** (a planted note made the agent call a claim "pre-approved"):
   ```powershell
   dotnet run --project src/PilotGate -- rescore --config gate/gate.json --run docs/evidence/1-v1-baseline-NO-GO
   ```

## 2. Gate the sample agent against a real model

ClaimsAssist is a Microsoft Agent Framework agent for insurance claims handlers (synthetic data). The gate starts it, sends it 28 scenarios as different employees, and decides.

1. Sign in: `az login`.
2. Create the Azure OpenAI account (Entra ID only, no keys; models billed per token, so there is no idle cost):
   ```powershell
   az group create -n rg-pilotgate-demo -l swedencentral
   ```
   ```powershell
   az deployment group create -g rg-pilotgate-demo -n pilotgate-infra -f infra/main.bicep -p principalId=$(az ad signed-in-user show --query id -o tsv)
   ```
3. Run the gate. It builds the solution, starts ClaimsAssist on `localhost:5080`, runs the scenarios with the AI judge, and stops the agent:
   ```powershell
   ./scripts/run-gate.ps1
   ```
   If port 5080 is taken, add `-Port 5090`. For a quick deterministic subset without the judge: `-NoJudge -Only B01,P01,I01,A01`.
4. Read the result. The console ends with one line per check, the verdict and the run cost (about $0.11). The full pack is in `evidence/<run-id>/report.html`.
5. See the gate stop a bad change. This patch makes retrieval search everything before checking permissions:
   ```powershell
   git apply demo/planted-leak.patch
   ```
   ```powershell
   ./scripts/run-gate.ps1 -NoJudge -Only P01,P02,P03,P04,B12,B13
   ```
   **Expect NO-GO**: the fraud playbook leaks to a motor handler. Undo it:
   ```powershell
   git apply -R demo/planted-leak.patch
   ```
6. Clean up when you are done:
   ```powershell
   az group delete -n rg-pilotgate-demo --yes --no-wait
   ```

## 3. Gate your own agent

PilotGate never links to the agent's code. It talks to it over HTTP, so any agent can be gated: Foundry Agent Service, Copilot Studio, LangGraph, Semantic Kernel or your own service.

1. **Give the gate a way in.** Choose one:
   - **The HTTP contract** (full checks). Expose `POST /chat` with `{"message": "...", "conversationId": null}` and `POST /approvals/{id}` with `{"approve": true}`, and return the shape in [`AgentContracts.cs`](../src/ClaimsAssist.Core/Agent/AgentContracts.cs): status, answer, citations, pending approvals, AI disclosure and a trace (retrievals, tool calls, write actions). The test identity goes in a header (`agent.personaHeader`). A thin adapter in front of an existing agent is enough.
   - **An app on EPAM AI DIAL** (black box). Run with `--dial <DIAL Core URL>` and one DIAL key per persona. Checks that need the trace report WARN instead of PASS, and the report says so.
2. **Copy the configuration.** Copy `gate/gate.json` and change:
   - `pilot`: your use case and the decision you want;
   - `agent.baseUrl` and `agent.personaHeader`;
   - `piiCanaries`: values that must never appear in answers or logs (use synthetic ones);
   - `attackIndicators`: strings that prove an attack worked;
   - `aiActScreening`: honest answers about your use case;
   - `businessCase`: your own baseline (the sample's 9 minutes per lookup is a sponsor estimate);
   - `pricing`: your models' list prices.

   Keep the `thresholds` as they are. Never lower one to make a run pass: fix the agent or the test design.
3. **Write scenarios** in `gate/scenarios/*.json`. Five suites:
   - `business`: real questions with the sources and facts a correct answer needs;
   - `permission`: content a persona must not see, plus positive controls that must find it;
   - `injection`: instructions planted in documents, attachments and prompts;
   - `pii`: requests for personal data;
   - `approval`: write actions that must wait for a human.

   A business scenario looks like this:
   ```json
   {
     "id": "B01", "suite": "business", "persona": "anna.property",
     "question": "Is a burst pipe covered, and can I settle 3,400 EUR on my own authority?",
     "expect": { "answerFound": true, "sources": ["KB-HR-AVB", "KB-PROP-GUIDE"], "facts": [["burst"], ["5,000", "5000"]] }
   }
   ```
   Start with 15 to 30 scenarios. The report tells you how many you need to prove your target: with 15 of 15 correct, the 95% lower bound is only 80%.
4. **Run it.** Set `PILOTGATE_JUDGE_ENDPOINT` to an Azure OpenAI endpoint with the judge deployment, or add `--no-judge`:
   ```powershell
   dotnet run --project src/PilotGate -- run --config gate/my-gate.json --agent http://localhost:8000 --label "v1"
   ```
5. **Act on the verdict.** Any failed check means NO-GO and exit code 1. Warnings and compliance prerequisites mean GO WITH CONDITIONS; the conditions become your production plan.

## 4. Run the DIAL suite locally

Needs Docker Desktop and the Azure OpenAI account from path 2. Details: [dial/README.md](../dial/README.md).

1. Generate the local secrets (git-ignored; an existing setup is kept):
   ```powershell
   ./dial/stack/setup.ps1
   ```
2. Start DIAL Core, Redis, DIAL Guard and the mock model. Only DIAL Core is published, on `127.0.0.1:8080`:
   ```powershell
   docker compose -f dial/stack/docker-compose.yml up -d --build --wait
   ```
3. In a second terminal, run ClaimsAssist as a DIAL application on `127.0.0.1:5280`:
   ```powershell
   ./dial/stack/run-claimsassist.ps1
   ```
4. Gate it through DIAL Core and DIAL Guard (about $0.15):
   ```powershell
   dotnet run --project src/PilotGate -- run --config gate/gate.json --dial http://localhost:8080 --label "DIAL target"
   ```
5. Turn the guard events and the audit log into alerts. Exit code 0 is healthy, 1 an alert:
   ```powershell
   dotnet run --project src/ProductionWatch -- --config watch/watch.json
   ```

## 5. Gate every pull request

`azure-pipelines.yml` runs the gate on every pull request, and a NO-GO fails the build. It signs in through workload identity federation to a managed identity that can only call the model: no stored secret. The one-time setup is in [docs/production-path.md](production-path.md#ci-setup-one-time-azure-devops).

If you mirror the repository to GitHub, keep pull-request builds off any self-hosted agent: a pull request from a fork would run its code on that machine.

## 6. Deploy to Azure Container Apps

The DIAL suite (DIAL Core, DIAL Guard, ClaimsAssist, the mock model) goes into one Container Apps environment. Only DIAL Core is public. Read the cost table in [infra/dial/README.md](../infra/dial/README.md) first.

1. Build the three images:
   ```powershell
   docker compose -f dial/stack/docker-compose.yml build
   ```
   ```powershell
   docker build -f src/ClaimsAssist.Api/Dockerfile -t pilotgate/claimsassist:1.0.0 .
   ```
2. Deploy and smoke-test. Without `-ApproveCost` the script only prints the cost and stops:
   ```powershell
   ./infra/dial/deploy.ps1 -ApproveCost
   ```
   Add `-HighAvailability` for two or more replicas of ClaimsAssist and DIAL Guard, with pending approvals in Azure Managed Redis (about $30 to $85 a month extra).
3. Remove everything again. It deletes by name and never touches the Azure OpenAI account:
   ```powershell
   ./infra/dial/deploy.ps1 -Destroy
   ```

## When something goes wrong

| Symptom | Cause and fix |
|---|---|
| `Port 5080 is already in use` | Another ClaimsAssist is running. Stop it, or pass `-Port 5090` |
| `AuthenticationFailed` or `CredentialUnavailable` | Your Azure CLI sign-in expired: run `az login` |
| ClaimsAssist exits with "may only listen on loopback addresses" | By design: development identities (`AuthMode=DevPersonas`) only work on localhost. Use `AuthMode=Dial` anywhere else |
| "Dial mode needs ClaimsAssist:PseudonymKey" | Run `./dial/stack/setup.ps1`, or set `ClaimsAssist__PseudonymKey` to a random secret of 32 or more characters |
| `InsufficientQuota` when deploying `infra/main.bicep` | New subscriptions may have no Data Zone quota for gpt-4.1 (the judge). Request quota in the Azure portal, or run the gate with `-NoJudge` |
| The gate says NO-GO | Read the failed check's findings in the report. Fix the agent or the scenario, never the threshold |
