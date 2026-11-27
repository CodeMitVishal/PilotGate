using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProductionWatch;

/// <summary>Configuration of a watch run (watch/watch.json). Paths are relative to the config file.</summary>
public sealed class WatchConfig
{
    /// <summary>DIAL Guard event logs (JSON lines, schema dial-guard.event.v1).</summary>
    public List<string> EventFiles { get; set; } = [];

    /// <summary>Application audit logs (ClaimsAssist JSON lines).</summary>
    public List<string> AuditFiles { get; set; } = [];

    public string SchemaFile { get; set; } = "../dial/schema/guard-event.v1.schema.json";

    public int WindowMinutes { get; set; } = 60;

    /// <summary>How many windows before the current one form the baseline for drift.</summary>
    public int BaselineWindows { get; set; } = 24;

    public WatchThresholds Thresholds { get; set; } = new();

    public DriftConfig Drift { get; set; } = new();

    public AlertConfig Alerts { get; set; } = new();

    public RescoreConfig Rescoring { get; set; } = new();

    public string OutputDirectory { get; set; } = "out";

    [JsonIgnore]
    public string BaseDirectory { get; set; } = ".";

    public string ResolvePath(string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(BaseDirectory, path));

    public static WatchConfig Load(string path)
    {
        var full = Path.GetFullPath(path);
        var config = JsonSerializer.Deserialize<WatchConfig>(File.ReadAllText(full), JsonDefaults.Options)
            ?? throw new InvalidOperationException($"Could not read {path}.");
        config.BaseDirectory = Path.GetDirectoryName(full)!;
        config.Validate();
        return config;
    }

    public void Validate()
    {
        if (WindowMinutes is < 1 or > 7 * 24 * 60)
        {
            throw new InvalidOperationException("windowMinutes must be between 1 and 10080.");
        }

        if (BaselineWindows is < 1 or > 1000)
        {
            throw new InvalidOperationException("baselineWindows must be between 1 and 1000.");
        }

        if (Drift.Factor <= 1)
        {
            throw new InvalidOperationException("drift.factor must be greater than 1.");
        }

        if (Rescoring.SampleSize < 0 || Rescoring.MaxUsd < 0)
        {
            throw new InvalidOperationException("rescoring.sampleSize and rescoring.maxUsd must not be negative.");
        }

        if (Alerts.MaxAttempts is < 1 or > 10)
        {
            throw new InvalidOperationException("alerts.maxAttempts must be between 1 and 10.");
        }
    }
}

public sealed class WatchThresholds
{
    /// <summary>Below this many requests in a window, rate rules do not fire: too little data.</summary>
    public int MinRequests { get; set; } = 20;

    public double MaxGuardErrorRate { get; set; } = 0.01;

    public int MaxFailOpenPassthroughs { get; set; }

    /// <summary>Share of requests stopped by a canary (a leak attempt reached the guard).</summary>
    public double MaxBlockedRate { get; set; } = 0.01;

    public double MaxRejectedRate { get; set; } = 0.05;

    /// <summary>Share of requests with at least one finding of a kind (pii, link, injection, attachment_url, canary).</summary>
    public Dictionary<string, double> MaxFindingRate { get; set; } = new(StringComparer.Ordinal)
    {
        ["pii"] = 0.2,
        ["link"] = 0.2,
        ["injection"] = 0.1,
        ["attachment_url"] = 0.05,
        ["canary"] = 0.01,
    };

    /// <summary>p95 of the request duration through the guard (includes the application's own time).</summary>
    public double MaxP95DurationMs { get; set; } = 20_000;

    public double MaxAgentBlockedRate { get; set; } = 0.05;

    public double MaxAgentP95LatencyMs { get; set; } = 20_000;
}

public sealed class DriftConfig
{
    /// <summary>A rate counts as drifted when it is this many times the baseline rate...</summary>
    public double Factor { get; set; } = 3;

    /// <summary>...and at least this high (small rates move a lot by chance).</summary>
    public double MinRate { get; set; } = 0.05;

    public int MinBaselineRequests { get; set; } = 50;
}

public sealed class AlertConfig
{
    /// <summary>The webhook URL is a secret: it is read from this environment variable, never from the file.</summary>
    public string WebhookUrlEnvironmentVariable { get; set; } = "PILOTGATE_WATCH_WEBHOOK";

    public int TimeoutSeconds { get; set; } = 10;

    public int MaxAttempts { get; set; } = 4;

    public int InitialBackoffMilliseconds { get; set; } = 500;
}

public sealed class RescoreConfig
{
    public bool Enabled { get; set; }

    public int SampleSize { get; set; } = 5;

    /// <summary>Hard cap on what one run may spend on re-scoring.</summary>
    public double MaxUsd { get; set; } = 0.05;

    public string Deployment { get; set; } = "gpt-4.1-mini";

    public string EndpointEnvironmentVariable { get; set; } = "PILOTGATE_JUDGE_ENDPOINT";

    public double InputPer1M { get; set; } = 0.4;

    public double OutputPer1M { get; set; } = 1.6;

    public int MaxOutputTokens { get; set; } = 200;

    public double MinMeanScore { get; set; } = 4;
}

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
