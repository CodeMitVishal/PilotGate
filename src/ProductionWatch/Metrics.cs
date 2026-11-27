namespace ProductionWatch;

public sealed record Window(DateTimeOffset Start, DateTimeOffset End)
{
    public bool Contains(DateTimeOffset t) => t >= Start && t < End;
}

/// <summary>Guard metrics for one deployment and profile in one window.</summary>
public sealed class GuardStats
{
    public required string Deployment { get; init; }

    public required string Profile { get; init; }

    public int Requests { get; set; }

    public Dictionary<string, int> Outcomes { get; } = new(StringComparer.Ordinal);

    /// <summary>Requests with at least one finding of a kind.</summary>
    public Dictionary<string, int> RequestsWithFinding { get; } = new(StringComparer.Ordinal);

    /// <summary>Findings of a kind, summed over all requests.</summary>
    public Dictionary<string, int> Findings { get; } = new(StringComparer.Ordinal);

    public int GuardErrors { get; set; }

    public int FailOpenPassthroughs { get; set; }

    public double P50DurationMs { get; set; }

    public double P95DurationMs { get; set; }

    public double Rate(int count) => Requests == 0 ? 0 : (double)count / Requests;

    public double FindingRate(string kind) => Rate(RequestsWithFinding.GetValueOrDefault(kind));

    public double OutcomeRate(string outcome) => Rate(Outcomes.GetValueOrDefault(outcome));
}

/// <summary>Application metrics from the audit log in one window.</summary>
public sealed class AgentStats
{
    public int Turns { get; set; }

    public Dictionary<string, int> Statuses { get; } = new(StringComparer.Ordinal);

    public int ApprovalRequests { get; set; }

    public Dictionary<string, int> WriteOutcomes { get; } = new(StringComparer.Ordinal);

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    public double P95LatencyMs { get; set; }

    public double BlockedRate => Turns == 0 ? 0 : (double)Statuses.GetValueOrDefault("blocked") / Turns;
}

public static class Metrics
{
    public static (Window Current, Window Baseline) Windows(DateTimeOffset end, WatchConfig config)
    {
        var length = TimeSpan.FromMinutes(config.WindowMinutes);
        var current = new Window(end - length, end);
        return (current, new Window(current.Start - (length * config.BaselineWindows), current.Start));
    }

    /// <summary>Per deployment and profile, from the request_completed events in the window.</summary>
    public static List<GuardStats> Guard(IEnumerable<GuardEvent> events, Window window)
    {
        var groups = new Dictionary<(string, string), (GuardStats Stats, List<double> Durations)>();
        foreach (var e in events.Where(e => e.Kind == "request_completed" && window.Contains(e.Time)))
        {
            var key = (e.Deployment ?? "(unknown)", e.Profile);
            if (!groups.TryGetValue(key, out var g))
            {
                g = (new GuardStats { Deployment = key.Item1, Profile = key.Item2 }, []);
                groups[key] = g;
            }

            var s = g.Stats;
            s.Requests++;
            Bump(s.Outcomes, e.Outcome ?? "unknown");
            foreach (var (kind, count) in e.Findings)
            {
                if (count > 0)
                {
                    Bump(s.RequestsWithFinding, kind);
                    Bump(s.Findings, kind, count);
                }
            }

            s.GuardErrors += e.GuardErrors;
            s.FailOpenPassthroughs += e.FailOpenPassthroughs;
            g.Durations.Add(e.DurationMs);
        }

        foreach (var (stats, durations) in groups.Values)
        {
            stats.P50DurationMs = Percentile(durations, 0.50);
            stats.P95DurationMs = Percentile(durations, 0.95);
        }

        return groups.Values.Select(g => g.Stats).OrderBy(s => s.Deployment, StringComparer.Ordinal).ThenBy(s => s.Profile, StringComparer.Ordinal).ToList();
    }

    public static AgentStats Agent(IEnumerable<AuditRecord> records, Window window)
    {
        var stats = new AgentStats();
        var latencies = new List<double>();
        foreach (var r in records.Where(r => window.Contains(r.Timestamp)))
        {
            stats.Turns++;
            Bump(stats.Statuses, string.IsNullOrEmpty(r.Status) ? "unknown" : r.Status);
            if (r.Status == "approval_required")
            {
                stats.ApprovalRequests++;
            }

            foreach (var call in r.ToolCalls)
            {
                var arrow = call.LastIndexOf("=> ", StringComparison.Ordinal);
                if (arrow < 0)
                {
                    continue;
                }

                var outcome = call[(arrow + 3)..].Split(':', 2)[0].Trim();
                if (outcome.Length > 0 && outcome.Length <= 32 && outcome.All(c => char.IsUpper(c) || c == '_'))
                {
                    Bump(stats.WriteOutcomes, outcome);
                }
            }

            stats.InputTokens += r.InputTokens;
            stats.OutputTokens += r.OutputTokens;
            latencies.Add(r.LatencyMs);
        }

        stats.P95LatencyMs = Percentile(latencies, 0.95);
        return stats;
    }

    /// <summary>Nearest-rank percentile (0 for no values).</summary>
    public static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.Order().ToList();
        var rank = (int)Math.Ceiling(percentile * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }

    private static void Bump(Dictionary<string, int> map, string key, int by = 1) => map[key] = map.GetValueOrDefault(key) + by;
}
