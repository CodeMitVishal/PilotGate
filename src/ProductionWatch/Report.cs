using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ProductionWatch;

public sealed record WatchReport(
    DateTimeOffset GeneratedUtc,
    Window Window,
    Window Baseline,
    int EventsRead,
    List<InvalidLine> InvalidEvents,
    List<string> MissingFiles,
    List<GuardStats> Guard,
    List<GuardStats> GuardBaseline,
    AgentStats? Agent,
    RescoreResult? Rescoring,
    List<Alert> Alerts,
    Delivery? Delivery)
{
    public string Summary()
    {
        var critical = Alerts.Count(a => a.Severity == Severity.Critical);
        var requests = Guard.Sum(g => g.Requests);
        return Alerts.Count == 0
            ? $"Production Watch: all clear ({requests} guarded request(s), window {Window.Start:u} to {Window.End:u})."
            : $"Production Watch: {Alerts.Count} alert(s), {critical} critical ({requests} guarded request(s), window {Window.Start:u} to {Window.End:u}). "
              + string.Join(" | ", Alerts.Take(5).Select(a => $"{a.Rule} {a.Scope}: {a.Message}"));
    }

    public string Markdown()
    {
        var c = CultureInfo.InvariantCulture;
        var md = new StringBuilder();
        md.AppendLine("# Production Watch").AppendLine();
        md.AppendLine(c, $"Window {Window.Start:u} to {Window.End:u}; baseline {Baseline.Start:u} to {Baseline.End:u}.").AppendLine();
        md.AppendLine(Summary()).AppendLine();
        if (Alerts.Count > 0)
        {
            md.AppendLine("## Alerts").AppendLine().AppendLine("| Severity | Rule | Scope | Message |").AppendLine("|---|---|---|---|");
            foreach (var a in Alerts)
            {
                md.AppendLine(c, $"| {a.Severity} | {a.Rule} | {a.Scope} | {a.Message.Replace("|", "/", StringComparison.Ordinal)} |");
            }

            md.AppendLine();
        }

        md.AppendLine("## Guard").AppendLine().AppendLine("| Deployment | Profile | Requests | Outcomes | Requests with findings | Guard errors | Fail-open | p95 |")
            .AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var g in Guard)
        {
            var outcomes = string.Join(", ", g.Outcomes.OrderBy(o => o.Key, StringComparer.Ordinal).Select(o => $"{o.Key} {o.Value}"));
            var findings = string.Join(", ", g.RequestsWithFinding.OrderBy(o => o.Key, StringComparer.Ordinal).Select(o => string.Create(c, $"{o.Key} {g.FindingRate(o.Key):P0}")));
            md.AppendLine(c, $"| {g.Deployment} | {g.Profile} | {g.Requests} | {outcomes} | {(findings.Length == 0 ? "none" : findings)} | {g.GuardErrors} | {g.FailOpenPassthroughs} | {g.P95DurationMs / 1000:0.0} s |");
        }

        if (Agent is not null)
        {
            md.AppendLine().AppendLine("## Application").AppendLine();
            var statuses = string.Join(", ", Agent.Statuses.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => $"{s.Key} {s.Value}"));
            var writes = Agent.WriteOutcomes.Count == 0 ? "none" : string.Join(", ", Agent.WriteOutcomes.OrderBy(w => w.Key, StringComparer.Ordinal).Select(w => $"{w.Key} {w.Value}"));
            md.AppendLine(c, $"{Agent.Turns} turn(s); statuses {statuses}; {Agent.ApprovalRequests} approval request(s); write outcomes {writes}; p95 latency {Agent.P95LatencyMs / 1000:0.0} s; {Agent.InputTokens:N0}/{Agent.OutputTokens:N0} tokens.");
        }

        if (Rescoring is not null)
        {
            md.AppendLine().AppendLine("## Re-scoring").AppendLine();
            var mean = Rescoring.MeanScore is { } m ? m.ToString("0.0", c) : "n/a";
            var cap = Rescoring.StoppedAtCap ? " (stopped at the cost cap)" : string.Empty;
            var error = Rescoring.Error is null ? "." : $"; {Rescoring.Error}.";
            md.AppendLine(c, $"{Rescoring.Scored.Count} of {Rescoring.Eligible} eligible answer(s) scored, mean {mean}/5, spent ${Rescoring.SpentUsd:0.0000}{cap}{error}");
        }

        if (InvalidEvents.Count > 0 || MissingFiles.Count > 0)
        {
            md.AppendLine().AppendLine("## Input problems").AppendLine();
            foreach (var i in InvalidEvents.Take(20))
            {
                md.AppendLine(c, $"- {Path.GetFileName(i.File)} line {i.Line}: {i.Reason}");
            }

            foreach (var f in MissingFiles)
            {
                md.AppendLine(c, $"- missing: {f}");
            }
        }

        if (Delivery is not null)
        {
            md.AppendLine().AppendLine(c, $"Alert delivery: {Delivery.Detail}.");
        }

        return md.ToString();
    }

    public string Json() => JsonSerializer.Serialize(this, JsonDefaults.Indented);
}
