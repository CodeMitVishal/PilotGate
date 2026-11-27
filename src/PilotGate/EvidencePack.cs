using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace PilotGate;

public sealed record RunCost(
    long AgentInputTokens,
    long AgentOutputTokens,
    double AgentUsd,
    long JudgeInputTokens,
    long JudgeOutputTokens,
    long JudgeCalls,
    double JudgeUsd,
    double TotalUsd);

public sealed record ScenarioSummary(
    string Id,
    string Suite,
    string Persona,
    string Question,
    string Status,
    string Answer,
    bool AnswerFound,
    List<string> Citations,
    List<string> Retrieved,
    List<string> ToolCalls,
    List<string> WriteActions,
    bool SuspiciousFlag,
    double? Groundedness,
    string? GroundednessReason,
    double? Completeness,
    string? CompletenessReason,
    long InputTokens,
    long OutputTokens,
    double CostUsd,
    long LatencyMs,
    List<string> Findings,
    string? Error);

public sealed class GateReport
{
    public required string RunId { get; init; }

    public string Label { get; init; } = string.Empty;

    public required DateTimeOffset StartedUtc { get; init; }

    public required DateTimeOffset FinishedUtc { get; init; }

    public required PilotInfo Pilot { get; init; }

    public JsonElement? AgentHealth { get; init; }

    public string? JudgeModel { get; init; }

    public required Verdict Verdict { get; init; }

    public required List<CheckResult> Checks { get; init; }

    public required ScreeningResult Screening { get; init; }

    public required BusinessCaseResult BusinessCase { get; init; }

    public required TaskStats Tasks { get; init; }

    public required CostStats Cost { get; init; }

    public required RunCost RunCost { get; init; }

    public required List<ScenarioSummary> Scenarios { get; init; }

    public required List<string> ProductionPath { get; init; }
}

public static class EvidencePack
{
    private static readonly CultureInfo C = Fmt.Culture;

    /// <summary>What is prototype-grade today and the production replacement for each part.</summary>
    public static readonly List<string> ProductionPath =
    [
        "Identity: replace development personas with Microsoft Entra ID sign-in (Microsoft.Identity.Web), take groups from the token's groups claim, and give the agent its own Entra Agent ID.",
        "Retrieval: move the in-process hybrid index to Azure AI Search (hybrid query plus semantic ranker) with a security filter on the allowed-groups field, fed by an indexer from SharePoint or Blob Storage.",
        "AI gateway: route every model call through Azure API Management with llm-token-limit per handler, llm-emit-token-metric for chargeback, and Azure AI Content Safety Prompt Shields on prompts and documents.",
        "Network: private endpoints for Azure OpenAI and AI Search with public network access disabled; host the API on Azure Container Apps with a managed identity.",
        "Approvals: persist pending approvals (Cosmos DB) and move the approval step into a Teams Adaptive Card for the handler or team lead.",
        "Observability: export Agent Framework OpenTelemetry traces to Application Insights, and let the gate read traces from there instead of the development trace field.",
        "CI: run this gate on every pull request that changes prompts, models, tools or the index (Azure Pipelines build validation with workload identity federation, no stored secrets).",
        "Evidence: grow the golden set to at least 100 SME-written questions and re-baseline the business case with a time-and-motion sample.",
    ];

    public static List<ScenarioSummary> Summarise(IReadOnlyList<ScenarioRun> runs, IReadOnlyList<CheckResult> checks, GateConfig config)
    {
        var findings = checks.SelectMany(c => c.Findings.Select(f => (f.ScenarioId, Text: $"{c.Id}: {f.Detail}")))
            .GroupBy(f => f.ScenarioId)
            .ToDictionary(g => g.Key, g => g.Select(f => f.Text).ToList());

        return runs.Select(r =>
        {
            var final = r.Final;
            var trace = final?.Trace;
            var price = trace is not null && config.Pricing.Models.TryGetValue(trace.Model, out var p) ? p : null;
            return new ScenarioSummary(
                r.Scenario.Id,
                r.Scenario.Suite,
                r.Scenario.Persona,
                r.Scenario.Question,
                final?.Status ?? "error",
                string.Join("\n---\n", r.Turns.Select(t => t.Answer)),
                final?.AnswerFound ?? false,
                final?.Citations.Select(c => c.Ref).ToList() ?? [],
                trace?.Retrievals.SelectMany(x => x.Results).Select(x => x.ChunkId).Distinct().ToList() ?? [],
                trace?.ToolCalls.Select(t => $"{t.Name} {t.ArgumentsJson}{(t.RequiresApproval ? $" [approval: {(t.Approved is null ? "pending" : t.Approved.Value ? "granted" : "rejected")}]" : string.Empty)}{(t.Executed ? " -> executed" : " -> not executed")}").ToList() ?? [],
                trace?.WriteActions.Select(w => $"{w.Outcome}: {w.Message}").ToList() ?? [],
                trace?.SuspiciousContentDetected ?? false,
                r.Judge?.Groundedness,
                r.Judge?.GroundednessReason,
                r.Judge?.Completeness,
                r.Judge?.CompletenessReason,
                trace?.InputTokens ?? 0,
                trace?.OutputTokens ?? 0,
                price is null || trace is null ? 0 : price.Cost(trace.InputTokens, trace.OutputTokens),
                r.LatencyMs,
                findings.GetValueOrDefault(r.Scenario.Id, []),
                r.Error);
        }).ToList();
    }

    public static string Write(GateReport report, IReadOnlyList<ScenarioRun> runs, string? auditDelta, string outputRoot, bool updateLatest)
    {
        var dir = Path.Combine(outputRoot, report.RunId);
        Directory.CreateDirectory(dir);
        if (!string.IsNullOrEmpty(auditDelta))
        {
            // The redacted audit records this run produced: the DPO can inspect exactly what was logged.
            File.WriteAllText(Path.Combine(dir, "audit-delta.jsonl"), auditDelta, new UTF8Encoding(false));
        }

        File.WriteAllText(Path.Combine(dir, "report.json"), JsonSerializer.Serialize(report, Json.Indented));
        File.WriteAllLines(Path.Combine(dir, "traces.jsonl"), runs.Select(r => JsonSerializer.Serialize(new
        {
            scenario = r.Scenario,
            first = r.First,
            afterDecision = r.AfterDecision,
            judge = r.Judge,
            r.LatencyMs,
            r.Error,
        }, Json.Compact)));
        File.WriteAllText(Path.Combine(dir, "report.html"), Html(report), new UTF8Encoding(false));
        if (!updateLatest)
        {
            return dir;
        }

        var latest = Path.Combine(outputRoot, "latest");
        if (Directory.Exists(latest))
        {
            Directory.Delete(latest, recursive: true);
        }

        Directory.CreateDirectory(latest);
        foreach (var file in Directory.GetFiles(dir))
        {
            File.Copy(file, Path.Combine(latest, Path.GetFileName(file)), overwrite: true);
        }

        return dir;
    }

    public static string Html(GateReport r)
    {
        var sb = new StringBuilder();
        var verdictClass = r.Verdict.Decision switch
        {
            GateVerdict.NoGo => "nogo",
            GateVerdict.Go => "go",
            _ => "cond",
        };
        var check = r.Checks.ToDictionary(c => c.Id);
        string Status(string id) => check.TryGetValue(id, out var c) ? Pill(c.Status) : string.Empty;
        string ResultOf(string id) => check.TryGetValue(id, out var c) ? H(c.Result) : string.Empty;
        var health = r.AgentHealth;
        string HealthValue(string name) => health is { } h && h.TryGetProperty(name, out var v) ? v.ToString() : "unknown";
        var duration = r.FinishedUtc - r.StartedUtc;
        var b = r.BusinessCase;

        sb.Append($$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>PilotGate evidence pack: {{H(r.Pilot.Name)}} ({{H(r.Verdict.Decision)}})</title>
            <style>
            :root { --ink:#1d2433; --muted:#5b6475; --line:#e3e6ec; --bg:#f6f7f9; --card:#fff;
                    --go:#067647; --go-bg:#e8f5ee; --cond:#a15c07; --cond-bg:#fdf3e1; --nogo:#b42318; --nogo-bg:#fdecea; --accent:#2f5bd3; }
            * { box-sizing: border-box; }
            body { margin:0; background:var(--bg); color:var(--ink); font:15px/1.55 "Segoe UI", system-ui, -apple-system, Roboto, Arial, sans-serif; }
            main { max-width:1120px; margin:0 auto; padding:32px 24px 64px; }
            header { background:var(--card); border:1px solid var(--line); border-radius:14px; padding:28px 32px; }
            .eyebrow { margin:0; color:var(--muted); font-size:13px; letter-spacing:.04em; text-transform:uppercase; }
            h1 { margin:6px 0 4px; font-size:28px; line-height:1.2; }
            h2 { margin:40px 0 12px; font-size:20px; }
            h3 { margin:0 0 8px; font-size:15px; }
            .decision { margin:0 0 18px; color:var(--muted); }
            .verdict { display:inline-block; padding:8px 18px; border-radius:999px; font-weight:700; font-size:18px; letter-spacing:.02em; }
            .verdict.nogo { color:var(--nogo); background:var(--nogo-bg); } .verdict.cond { color:var(--cond); background:var(--cond-bg); } .verdict.go { color:var(--go); background:var(--go-bg); }
            .reasons { margin:14px 0 0; padding-left:20px; }
            .meta { display:grid; grid-template-columns:repeat(auto-fit,minmax(170px,1fr)); gap:12px 24px; margin:22px 0 0; padding-top:18px; border-top:1px solid var(--line); }
            .meta div dt { color:var(--muted); font-size:12px; text-transform:uppercase; letter-spacing:.04em; } .meta div dd { margin:2px 0 0; font-weight:600; }
            .grid { display:grid; grid-template-columns:repeat(auto-fit,minmax(300px,1fr)); gap:16px; }
            .card { background:var(--card); border:1px solid var(--line); border-radius:12px; padding:18px 20px; }
            .card p { margin:6px 0; } .card .who { color:var(--muted); font-size:12px; text-transform:uppercase; letter-spacing:.04em; margin:0 0 4px; }
            .big { font-size:22px; font-weight:700; }
            table { width:100%; border-collapse:collapse; background:var(--card); border:1px solid var(--line); border-radius:12px; overflow:hidden; }
            th, td { text-align:left; vertical-align:top; padding:10px 12px; border-bottom:1px solid var(--line); }
            th { background:#fafbfc; color:var(--muted); font-size:12px; text-transform:uppercase; letter-spacing:.04em; }
            tr:last-child td { border-bottom:0; }
            .pill { display:inline-block; padding:2px 10px; border-radius:999px; font-size:12px; font-weight:700; white-space:nowrap; }
            .pill.pass { color:var(--go); background:var(--go-bg); } .pill.warn { color:var(--cond); background:var(--cond-bg); } .pill.fail { color:var(--nogo); background:var(--nogo-bg); }
            .small { font-size:13px; color:var(--muted); }
            .tag { display:inline-block; margin:2px 4px 2px 0; padding:1px 8px; border:1px solid var(--line); border-radius:6px; font-size:12px; color:var(--muted); background:#fafbfc; }
            ul.tight { margin:6px 0; padding-left:20px; } ul.tight li { margin:3px 0; }
            details { background:var(--card); border:1px solid var(--line); border-radius:10px; margin:8px 0; }
            details summary { cursor:pointer; padding:10px 14px; display:flex; gap:10px; align-items:baseline; flex-wrap:wrap; }
            details[open] summary { border-bottom:1px solid var(--line); }
            details .body { padding:12px 16px; }
            .q { font-weight:600; }
            pre { white-space:pre-wrap; word-break:break-word; background:#fafbfc; border:1px solid var(--line); border-radius:8px; padding:10px 12px; margin:6px 0; font:13px/1.5 Consolas, "Cascadia Mono", monospace; }
            .finding { color:var(--nogo); }
            footer { margin-top:40px; color:var(--muted); font-size:13px; }
            @media print { body { background:#fff; } header, .card, table, details { break-inside:avoid; } details { display:block; } }
            </style>
            </head>
            <body>
            <main>
            <header>
              <p class="eyebrow">PilotGate evidence pack &middot; run {{H(r.RunId)}}{{(r.Label.Length > 0 ? " &middot; " + H(r.Label) : string.Empty)}}</p>
              <h1>{{H(r.Pilot.Name)}}: {{H(r.Pilot.Client)}}</h1>
              <p class="decision">Decision requested: {{H(r.Pilot.Decision)}}.</p>
              <span class="verdict {{verdictClass}}">{{H(r.Verdict.Decision)}}</span>
              <ul class="reasons">{{string.Concat(r.Verdict.Reasons.Select(x => $"<li>{H(x)}</li>"))}}</ul>
              <dl class="meta">
                <div><dt>Agent model</dt><dd>{{H(HealthValue("model"))}} (EU Data Zone)</dd></div>
                <div><dt>Judge model</dt><dd>{{H(r.JudgeModel ?? "not run")}}</dd></div>
                <div><dt>Scenarios</dt><dd>{{r.Scenarios.Count}} ({{r.Scenarios.Count(s => s.Findings.Count == 0 && s.Error is null)}} without findings)</dd></div>
                <div><dt>Run</dt><dd>{{r.StartedUtc.ToString("yyyy-MM-dd HH:mm", C)}} UTC, {{duration.TotalMinutes.ToString("0.0", C)}} min</dd></div>
                <div><dt>Cost of this run</dt><dd>${{r.RunCost.TotalUsd.ToString("0.000", C)}} (agent ${{r.RunCost.AgentUsd.ToString("0.000", C)}}, judge ${{r.RunCost.JudgeUsd.ToString("0.000", C)}})</dd></div>
              </dl>
            </header>

            <h2>What each reviewer needs to know</h2>
            <div class="grid">
              <div class="card"><p class="who">Business sponsor</p>
                <p class="big">{{r.Tasks.Successes}}/{{r.Tasks.Total}} tasks correct</p>
                <p class="small">95% confidence interval {{r.Tasks.WilsonLow.ToString("P0", C)}} to {{r.Tasks.WilsonHigh.ToString("P0", C)}}</p>
                <p>About <b>{{b.ExpectedHoursPerMonth.ToString("N0", C)}} handler hours a month</b> saved (conservative {{b.ConservativeHoursPerMonth.ToString("N0", C)}}), worth EUR {{b.ExpectedValueEurPerMonth.ToString("N0", C)}} (conservative EUR {{b.ConservativeValueEurPerMonth.ToString("N0", C)}}), for about ${{b.ModelCostPerMonth.ToString("N2", C)}} a month in model cost.</p>
              </div>
              <div class="card"><p class="who">CISO</p>
                <p>{{Status("PERMISSION_TRIMMING")}} {{ResultOf("PERMISSION_TRIMMING")}}</p>
                <p>{{Status("PROMPT_INJECTION")}} {{ResultOf("PROMPT_INJECTION")}}</p>
                <p>{{Status("HUMAN_OVERSIGHT")}} {{ResultOf("HUMAN_OVERSIGHT")}}</p>
              </div>
              <div class="card"><p class="who">Data protection officer</p>
                <p>{{Status("PII_PROTECTION")}} {{ResultOf("PII_PROTECTION")}}</p>
                <p>{{Status("TRANSPARENCY")}} {{ResultOf("TRANSPARENCY")}}</p>
                <p class="small">Processing in the EU Data Zone. Customer PII is redacted from logs; handlers are pseudonymised.</p>
              </div>
              <div class="card"><p class="who">Works council</p>
                <p>The audit trail stores pseudonymised handler IDs, redacted questions and answers, sources and tool calls. It is suitable for monitoring individual handlers, so a works agreement is a condition before production.</p>
              </div>
              <div class="card"><p class="who">Production engineering</p>
                <p>{{Status("COST_LATENCY")}} {{ResultOf("COST_LATENCY")}}</p>
                <p>{{r.ProductionPath.Count}} prototype-grade parts have a named production replacement (below).</p>
              </div>
            </div>

            <h2>Checks</h2>
            <table>
              <tr><th>Check</th><th>Status</th><th>Result</th><th>Threshold</th><th>Mapped controls</th></tr>
            """);

        foreach (var c in r.Checks)
        {
            sb.Append($"""
                <tr><td><b>{H(c.Id)}</b><br><span class="small">{H(c.Title)}</span><br><span class="small">{(c.Blocker ? "Blocker" : "Quality")} &middot; {H(c.Audience)}</span></td>
                <td>{Pill(c.Status)}</td>
                <td>{H(c.Result)}{(c.Findings.Count > 0 ? "<ul class=\"tight\">" + string.Concat(c.Findings.Select(f => $"<li class=\"finding\"><b>{H(f.ScenarioId)}</b>: {H(f.Detail)}</li>")) + "</ul>" : string.Empty)}{(c.Notes.Count > 0 ? "<ul class=\"tight small\">" + string.Concat(c.Notes.Select(n => $"<li>{H(n)}</li>")) + "</ul>" : string.Empty)}</td>
                <td class="small">{H(c.Threshold)}</td>
                <td>{string.Concat(c.Controls.Select(x => $"<span class=\"tag\">{H(x)}</span>"))}</td></tr>
                """);
        }

        sb.Append($$"""
            </table>

            <h2>Business case</h2>
            <table>
              <tr><th>Item</th><th>Expected</th><th>Conservative</th></tr>
              <tr><td>Task success rate</td><td>{{r.Tasks.Rate.ToString("P0", C)}}</td><td>{{r.Tasks.WilsonLow.ToString("P0", C)}} (Wilson 95% lower bound)</td></tr>
              <tr><td>Minutes saved per successful lookup</td><td>{{b.MinutesSavedPerSuccess.ToString("0.#", C)}}</td><td>{{(b.MinutesSavedPerSuccess / 2).ToString("0.#", C)}} (half the estimate)</td></tr>
              <tr><td>Net minutes per lookup (failures waste the assisted minutes)</td><td>{{b.ExpectedNetMinutesPerLookup.ToString("0.0", C)}}</td><td>{{b.ConservativeNetMinutesPerLookup.ToString("0.0", C)}}</td></tr>
              <tr><td>Handler hours saved per month</td><td>{{b.ExpectedHoursPerMonth.ToString("N0", C)}}</td><td>{{b.ConservativeHoursPerMonth.ToString("N0", C)}}</td></tr>
              <tr><td>Value per month (loaded cost)</td><td>EUR {{b.ExpectedValueEurPerMonth.ToString("N0", C)}}</td><td>EUR {{b.ConservativeValueEurPerMonth.ToString("N0", C)}}</td></tr>
              <tr><td>Model cost per month</td><td colspan="2">{{H(b.ModelCostCurrency)}} {{b.ModelCostPerMonth.ToString("N2", C)}} (measured mean cost per conversation &times; volume)</td></tr>
            </table>
            <p class="small">Metric: {{H(b.Metric)}}. Inputs: {{H(b.Source)}}</p>

            <h2>EU AI Act and GDPR screening</h2>
            <div class="card">
              <p><b>Tier:</b> {{H(r.Screening.Tier)}}</p>
              <p class="small">Basis: {{H(r.Screening.Basis)}}</p>
              {{List("Obligations", r.Screening.Obligations)}}
              {{List("Conditions before production", r.Screening.Conditions)}}
              {{List("Notes", r.Screening.Notes)}}
            </div>

            <h2>Conditions and path to production</h2>
            <div class="grid">
              <div class="card">{{List("Open conditions", r.Verdict.Conditions.Count > 0 ? r.Verdict.Conditions : ["None. Fix the failed checks first; conditions are listed once the verdict is not NO-GO."])}}</div>
              <div class="card">{{List("Prototype-grade parts and their production replacement", r.ProductionPath)}}</div>
            </div>

            <h2>Scenario evidence</h2>
            <p class="small">Every scenario with its answer, the sources retrieved for the persona, tool calls, judge scores and findings. Raw traces are in traces.jsonl next to this file.</p>
            """);

        foreach (var s in r.Scenarios)
        {
            var ok = s.Findings.Count == 0 && s.Error is null;
            sb.Append($"""
                <details>
                  <summary><span class="pill {(ok ? "pass" : "fail")}">{(ok ? "OK" : "FINDING")}</span> <b>{H(s.Id)}</b> <span class="tag">{H(s.Suite)}</span> <span class="tag">{H(s.Persona)}</span> <span class="q">{H(s.Question)}</span></summary>
                  <div class="body">
                    {(s.Findings.Count > 0 ? "<ul class=\"tight\">" + string.Concat(s.Findings.Select(f => $"<li class=\"finding\">{H(f)}</li>")) + "</ul>" : string.Empty)}
                    {(s.Error is not null ? $"<p class=\"finding\">Error: {H(s.Error)}</p>" : string.Empty)}
                    <p class="small">Status: {H(s.Status)} &middot; answer found: {(s.AnswerFound ? "yes" : "no")} &middot; suspicious content flagged: {(s.SuspiciousFlag ? "yes" : "no")} &middot; {s.InputTokens.ToString("N0", C)} in / {s.OutputTokens.ToString("N0", C)} out tokens &middot; ${s.CostUsd.ToString("0.0000", C)} &middot; {(s.LatencyMs / 1000.0).ToString("0.0", C)} s</p>
                    <pre>{H(s.Answer)}</pre>
                    <p><b>Cited:</b> {(s.Citations.Count > 0 ? string.Concat(s.Citations.Select(x => $"<span class=\"tag\">{H(x)}</span>")) : "<span class=\"small\">none</span>")}</p>
                    <p><b>Retrieved for this persona:</b> {(s.Retrieved.Count > 0 ? string.Concat(s.Retrieved.Select(x => $"<span class=\"tag\">{H(x)}</span>")) : "<span class=\"small\">nothing</span>")}</p>
                    {(s.ToolCalls.Count > 0 ? "<p><b>Tool calls:</b></p><ul class=\"tight small\">" + string.Concat(s.ToolCalls.Select(x => $"<li>{H(x)}</li>")) + "</ul>" : string.Empty)}
                    {(s.WriteActions.Count > 0 ? "<p><b>Write actions:</b></p><ul class=\"tight small\">" + string.Concat(s.WriteActions.Select(x => $"<li>{H(x)}</li>")) + "</ul>" : string.Empty)}
                    {(s.Groundedness is not null ? $"<p><b>Judge:</b> groundedness {s.Groundedness.Value.ToString("0.#", C)}/5, completeness {(s.Completeness ?? 0).ToString("0.#", C)}/5</p><p class=\"small\">{H(s.GroundednessReason)}</p>" : string.Empty)}
                  </div>
                </details>
                """);
        }

        sb.Append($"""
            <footer>
              <p>Generated by PilotGate. Reproduce with <code>dotnet run --project src/PilotGate -- run --config gate/gate.json</code> against the same agent build. Deterministic checks use canary strings, traces and tool calls; only answer quality uses an LLM judge ({H(r.JudgeModel ?? "not run")}).</p>
              <p>The EU AI Act and GDPR screening is a screening aid for the client's legal team, not legal advice. All client data in this pilot is synthetic.</p>
            </footer>
            </main>
            </body>
            </html>
            """);

        return sb.ToString();
    }

    private static string List(string title, IReadOnlyList<string> items) =>
        items.Count == 0 ? string.Empty : $"<h3>{H(title)}</h3><ul class=\"tight\">{string.Concat(items.Select(i => $"<li>{H(i)}</li>"))}</ul>";

    private static string Pill(CheckStatus status) => status switch
    {
        CheckStatus.Pass => "<span class=\"pill pass\">PASS</span>",
        CheckStatus.Warn => "<span class=\"pill warn\">WARN</span>",
        _ => "<span class=\"pill fail\">FAIL</span>",
    };

    private static string H(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);
}
