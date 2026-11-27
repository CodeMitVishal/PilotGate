using System.Globalization;

namespace ProductionWatch;

public enum Severity
{
    Warning,
    Critical,
}

public sealed record Alert(Severity Severity, string Rule, string Scope, string Message);

/// <summary>The alert rules: deterministic, from thresholds and drift against the baseline window.</summary>
public static class Rules
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    public static List<Alert> Evaluate(
        IReadOnlyList<GuardStats> current,
        IReadOnlyList<GuardStats> baseline,
        AgentStats? agent,
        int invalidEvents,
        WatchConfig config)
    {
        var t = config.Thresholds;
        var alerts = new List<Alert>();

        if (invalidEvents > 0)
        {
            alerts.Add(new(Severity.Warning, "EVENTS_INVALID", "event log",
                $"{invalidEvents} event line(s) do not match schema dial-guard.event.v1; they were not counted."));
        }

        foreach (var s in current)
        {
            var scope = $"{s.Deployment} ({s.Profile})";
            if (s.FailOpenPassthroughs > t.MaxFailOpenPassthroughs)
            {
                alerts.Add(new(Severity.Critical, "GUARD_FAIL_OPEN", scope,
                    $"{s.FailOpenPassthroughs} field(s) passed to users unchecked after a guard error (fail-open)."));
            }

            var enoughData = s.Requests >= t.MinRequests;
            if (enoughData && s.Rate(s.GuardErrors) > t.MaxGuardErrorRate)
            {
                alerts.Add(new(Severity.Critical, "GUARD_ERRORS", scope,
                    string.Create(C, $"guard error rate {s.Rate(s.GuardErrors):P1} > {t.MaxGuardErrorRate:P1} ({s.GuardErrors} in {s.Requests} requests).")));
            }
            else if (!enoughData && s.GuardErrors > 0)
            {
                alerts.Add(new(Severity.Warning, "GUARD_ERRORS", scope,
                    $"{s.GuardErrors} guard error(s) in {s.Requests} request(s) (too few requests for a rate)."));
            }

            if (!enoughData)
            {
                continue;
            }

            if (s.OutcomeRate("blocked") > t.MaxBlockedRate)
            {
                alerts.Add(new(Severity.Critical, "LEAK_BLOCKS", scope,
                    string.Create(C, $"{s.OutcomeRate("blocked"):P1} of requests were stopped by a leak marker (> {t.MaxBlockedRate:P1}).")));
            }

            if (s.OutcomeRate("rejected") > t.MaxRejectedRate)
            {
                alerts.Add(new(Severity.Warning, "REJECTIONS", scope,
                    string.Create(C, $"{s.OutcomeRate("rejected"):P1} of requests were rejected (> {t.MaxRejectedRate:P1}).")));
            }

            foreach (var (kind, max) in t.MaxFindingRate.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (s.FindingRate(kind) > max)
                {
                    alerts.Add(new(kind == "canary" ? Severity.Critical : Severity.Warning, "FINDING_RATE", scope,
                        string.Create(C, $"{s.FindingRate(kind):P1} of requests had {kind} findings (> {max:P1}).")));
                }
            }

            if (s.P95DurationMs > t.MaxP95DurationMs)
            {
                alerts.Add(new(Severity.Warning, "LATENCY", scope,
                    string.Create(C, $"p95 request duration {s.P95DurationMs / 1000:0.0} s > {t.MaxP95DurationMs / 1000:0.0} s.")));
            }

            var before = baseline.FirstOrDefault(b => b.Deployment == s.Deployment && b.Profile == s.Profile);
            if (before is null || before.Requests < config.Drift.MinBaselineRequests)
            {
                continue;
            }

            foreach (var kind in s.RequestsWithFinding.Keys.Union(before.RequestsWithFinding.Keys).Order(StringComparer.Ordinal))
            {
                var now = s.FindingRate(kind);
                var then = before.FindingRate(kind);
                if (now >= config.Drift.MinRate && now > then * config.Drift.Factor)
                {
                    alerts.Add(new(Severity.Warning, "DRIFT", scope, then == 0
                        ? string.Create(C, $"{kind} findings appeared: {now:P1} of requests, none in the baseline.")
                        : string.Create(C, $"{kind} findings rose to {now:P1} of requests from {then:P1} in the baseline ({now / then:0.0}x).")));
                }
            }
        }

        foreach (var before in baseline.Where(b => b.Requests >= config.Drift.MinBaselineRequests))
        {
            if (!current.Any(s => s.Deployment == before.Deployment && s.Profile == before.Profile))
            {
                alerts.Add(new(Severity.Warning, "NO_TRAFFIC", $"{before.Deployment} ({before.Profile})",
                    $"no requests in this window after {before.Requests} in the baseline: the guard may be bypassed or the deployment down."));
            }
        }

        if (agent is not null && agent.Turns >= t.MinRequests)
        {
            if (agent.BlockedRate > t.MaxAgentBlockedRate)
            {
                alerts.Add(new(Severity.Warning, "AGENT_BLOCKED", "application",
                    string.Create(C, $"{agent.BlockedRate:P1} of turns were blocked by the content filter (> {t.MaxAgentBlockedRate:P1}).")));
            }

            if (agent.P95LatencyMs > t.MaxAgentP95LatencyMs)
            {
                alerts.Add(new(Severity.Warning, "AGENT_LATENCY", "application",
                    string.Create(C, $"p95 turn latency {agent.P95LatencyMs / 1000:0.0} s > {t.MaxAgentP95LatencyMs / 1000:0.0} s.")));
            }
        }

        return alerts;
    }
}
