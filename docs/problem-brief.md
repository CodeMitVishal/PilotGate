# Problem brief: where EPAM's AI-native growth gets stuck, and what an FDE can do about it

*27 November 2026. Public sources only; every number is linked.*

## 1. The situation in EPAM's own numbers

| Signal | Number | Source |
|---|---|---|
| Total revenue, Q3 2026 | $1.426B, +2.3% year over year (+2.5% organic constant currency) | [SEC 8-K, Exhibit 99.1](https://www.sec.gov/Archives/edgar/data/0001352010/000135201026000048/exhibit99_q3x2026.htm) |
| Q4 2026 guidance | +0.5% to +1.6% organic constant currency | same |
| AI-native revenue | More than $185M in Q3 (about 13% of revenue, up from about 11%), growing at a double-digit rate quarter over quarter; full-year target raised to at least $650M | [Q3 2026 earnings call](https://www.fool.com/earnings/call-transcripts/2026/11/05/epam-systems-epam-q3-2026-earnings-call-transcript/) |
| Where growth is | Financial Services $328M, +13.7% (led by insurance and asset management); EMEA +5.8%, 41% of revenue | same |
| Where revenue shrinks | Software & Hi-Tech -6.1%; Business Information & Media -5.5%; Consumer -2.5% | same |
| Delivery model | Platform engagements led by forward-deployed engineers, and agentic managed services. More than 5,800 OpenAI-certified forward-deployed engineers. Microsoft Frontier certifications (2,800+) trail Google (8,700+), Anthropic (8,300+) and OpenAI | same |
| EPAM's own diagnosis | EPAM's VP Technology Solutions: *"very few scaled generative AI to production"*. EPAM names two barriers: data readiness, and organisational alignment (pilots driven by technical teams without full business buy-in, so the return is hard to see). Its teams also spent the year working through compliance | [EPAM newsroom, 6 March 2026](https://www.epam.com/about/newsroom/in-the-news/2026/epam-accelerates-agentic-ai-adoption-for-enterprises) |

## 2. The precise problem

EPAM's growth now runs through one conversion step: **a forward-deployed engineer's pilot becoming a production platform or a managed service.** EPAM itself says few pilots make that step. It names why: the value is not visible to the business, and compliance has to be worked through.

In the markets where EPAM grows, that step is a decision by four reviewers with four different questions:

| Reviewer | Question | What usually exists after a pilot |
|---|---|---|
| Business sponsor | Does it move my metric, by how much, at what cost? | A demo and an opinion |
| CISO | Can it leak data, be hijacked, or act on its own? | A slide on "guardrails" |
| Data protection officer | What personal data goes where, and what is logged? | Nothing written down |
| Works council (Germany) | Can this be used to monitor employees? | Usually not asked yet |

The engineer can answer all four, but only by assembling evidence by hand, weeks later, and once. Every prompt, model or index change makes that evidence stale.

**Problem statement:** an FDE pilot has no repeatable, reviewer-ready evidence for its go/no-go decision. So the decision is slow, subjective and not repeatable. That delays AI-native revenue, and it weakens the case for agentic managed services, which need the same evidence every month.

## 3. The answer: PilotGate

PilotGate is an FDE accelerator that produces the go/no-go evidence automatically, and keeps producing it.

- **One run takes about 3 minutes and costs about $0.12.** It sends 28 scenarios to the pilot over its HTTP contract: business questions, permission probes with canaries, prompt-injection payloads, PII probes and approval flows.
- **Evidence for each reviewer:**
  - business sponsor: task success with a Wilson confidence interval, and a business case;
  - CISO: an OWASP Top 10 for LLM applications mapping;
  - DPO: GDPR;
  - works council: the BetrVG condition;
  - delivery team: a production path.
- **A CI gate.** A NO-GO verdict fails the pull request, so the evidence is current for every change. That is also the operating evidence an agentic managed service needs.

## 4. Where it sits next to EPAM's own assets

This is positioning from public descriptions only. I have not checked whether these assets already include an equivalent gate.

| EPAM asset | Public description | Relation to PilotGate |
|---|---|---|
| [DIAL](https://www.epam.com/about/newsroom/press-releases/2025/epam-releases-dial-3-0-an-evolution-of-open-source-genai-enterprise-platform) | Open-source (Apache 2.0) platform that orchestrates LLM assistants and agents across model providers; version 3.0 (June 2025) adds multi-agent support | DIAL runs agents. PilotGate decides whether a given agent build may go to production. A DIAL application that exposes the chat contract can be gated through an adapter. |
| [AI/Run](https://www.epam.com/about/newsroom/press-releases/2025/epam-launches-ai-run-transform-to-accelerate-ai-native-transformation-for-the-enterprise) | EPAM's framework for AI-native delivery; AI/Run.Transform (October 2025) packages blueprints, talent and tools into one playbook | PilotGate would be a go/no-go step inside such a playbook: a repeatable gate with evidence for each reviewer. |
| [Agentic QA](https://seekingalpha.com/pr/20282613-revolutionizing-qa-epams-agentic-qa-brings-human-ai-synergy-to-software-testing) | Agents applied to software testing, part of AI/Run tools for testing as a service | Agentic QA uses AI to test software. PilotGate tests an AI system's own behaviour against security, compliance and business criteria. |
| [Frontier AI Service](https://www.epam.com/about/newsroom/press-releases/2026/epam-unveils-new-frontier-ai-service-bridging-the-enterprise-intelligence-gap-for-genai-and-autonomous-agents) | Launched October 2026: data generation, model evaluation and simulation environments for frontier AI labs, to test agent conversations, reasoning and tool use before production | Lab side versus client side. The scenario suites written on each client engagement are the kind of enterprise test cases that service builds on. |

## 5. Proof that it finds real problems

The gate was built together with a real pilot, ClaimsAssist: an insurance claims assistant on Microsoft Agent Framework and Azure OpenAI in the EU Data Zone. It was then run against the real model six times. The packs are in [evidence](evidence).

1. **v1: NO-GO.** A note planted in a contractor invoice made the agent tell the handler the claim was pre-approved. A jailbreak blocked by Azure's content filter surfaced as an HTTP 500.
2. **Gate blind spot found and fixed.** A hidden HTML comment in a broker FAQ put a phishing link into three ordinary business answers. The LLM judge scored those answers 5/5 for groundedness. Attack indicators are now scanned in every answer, with a regression test.
3. **v2: GO WITH CONDITIONS.** The fix for one question caused a regression in another, where a deductible was applied to the wrong product. The gate caught it.
4. **Planted one-line permission bug: NO-GO.** The fraud playbook leaked to a motor handler.
5. **v3: NO-GO.** A defect in the agent's own citation validator was dropping valid citations. The threshold stayed unchanged.
6. **v4: GO WITH CONDITIONS.** 15/15 tasks correct, 0 leaks, 0 successful attacks, $0.0011 per conversation. The remaining conditions are honest:
   - 15 questions prove only at least 80% (95% CI);
   - a DPIA is needed;
   - a works agreement is needed.

## 6. What this means in an engagement

| Week | FDE activity | PilotGate role |
|---|---|---|
| 1 | Discovery: one workflow, one metric, data access | The sponsor's baseline goes into `businessCase`; SMEs write the first 20 golden questions |
| 2 | Walking skeleton on the client's Azure | First gate run: the NO-GO list becomes the backlog |
| 3 | Hardening and demo | The gate runs in CI on every change; the demo includes the evidence pack |
| 4 | Decision meeting | One pack, five reviewers, one verdict; the conditions become the production plan |

The handover to the delivery team is the evidence pack, the scenario suites and the production-path list.
