using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace ProductionWatch;

/// <summary>A DIAL Guard event (schema dial-guard.event.v1), the fields the watch uses.</summary>
public sealed class GuardEvent
{
    public DateTimeOffset Time { get; init; }

    public string RequestId { get; init; } = string.Empty;

    public string? Deployment { get; init; }

    public string Profile { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string Kind { get; init; } = string.Empty;

    public string? Detector { get; init; }

    public string? Action { get; init; }

    public int Count { get; init; }

    public string? Outcome { get; init; }

    public double DurationMs { get; init; }

    public Dictionary<string, int> Findings { get; init; } = [];

    public int GuardErrors { get; init; }

    public int FailOpenPassthroughs { get; init; }
}

public sealed record InvalidLine(string File, int Line, string Reason);

public sealed record EventLoad(List<GuardEvent> Events, List<InvalidLine> Invalid, List<string> MissingFiles);

/// <summary>Reads guard event logs line by line and validates every line against the v1 schema: the reader side of the
/// contract whose writer side the guard's Python tests check. Invalid lines are counted and reported, never fatal.
/// A last line without a newline is a write in progress and is skipped.</summary>
public sealed class EventReader(JsonSchema schema)
{
    /// <summary>Each load gets its own registry: the schema's $id would otherwise be registered globally, and a second
    /// load of the same schema in one process would fail.</summary>
    public static EventReader FromSchemaFile(string path) =>
        new(JsonSchema.FromText(File.ReadAllText(path), new BuildOptions { SchemaRegistry = new SchemaRegistry() }));

    public EventLoad Read(IEnumerable<string> paths)
    {
        var events = new List<GuardEvent>();
        var invalid = new List<InvalidLine>();
        var missing = new List<string>();
        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                missing.Add(path);
                continue;
            }

            foreach (var (number, line) in CompleteLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    invalid.Add(new(path, number, "not JSON"));
                    continue;
                }

                using (document)
                {
                    var result = schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
                    if (!result.IsValid)
                    {
                        var first = result.Details?.FirstOrDefault(d => d.Errors is { Count: > 0 });
                        var reason = first?.Errors?.Values.FirstOrDefault() ?? "does not match the schema";
                        invalid.Add(new(path, number, $"{first?.InstanceLocation}: {reason}"));
                        continue;
                    }
                }

                events.Add(ToEvent(JsonNode.Parse(line)!.AsObject()));
            }
        }

        return new EventLoad(events, invalid, missing);
    }

    private static GuardEvent ToEvent(JsonObject o) => new()
    {
        Time = DateTimeOffset.Parse(o["time"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture),
        RequestId = o["request_id"]!.GetValue<string>(),
        Deployment = o["deployment"]?.GetValue<string>(),
        Profile = o["profile"]!.GetValue<string>(),
        Mode = o["mode"]!.GetValue<string>(),
        Kind = o["kind"]!.GetValue<string>(),
        Detector = o["detector"]?.GetValue<string>(),
        Action = o["action"]?.GetValue<string>(),
        Count = o["count"]?.GetValue<int>() ?? 0,
        Outcome = o["outcome"]?.GetValue<string>(),
        DurationMs = o["duration_ms"]?.GetValue<double>() ?? 0,
        Findings = o["findings"] is JsonObject f ? f.ToDictionary(p => p.Key, p => p.Value!.GetValue<int>()) : [],
        GuardErrors = o["guard_errors"]?.GetValue<int>() ?? 0,
        FailOpenPassthroughs = o["fail_open_passthroughs"]?.GetValue<int>() ?? 0,
    };

    /// <summary>Lines with their numbers; a final line that does not end with a newline is skipped (still being written).</summary>
    internal static IEnumerable<(int Number, string Line)> CompleteLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var endsWithNewline = stream.Length == 0 || EndsWithNewline(stream);
        stream.Position = 0;
        using var reader = new StreamReader(stream);
        var number = 0;
        string? current = reader.ReadLine();
        while (current is not null)
        {
            number++;
            var next = reader.ReadLine();
            if (next is null && !endsWithNewline)
            {
                yield break;
            }

            yield return (number, current);
            current = next;
        }
    }

    private static bool EndsWithNewline(FileStream stream)
    {
        stream.Position = stream.Length - 1;
        return stream.ReadByte() == '\n';
    }
}

/// <summary>One line of the ClaimsAssist audit log (answers are already redacted by the application).</summary>
public sealed class AuditRecord
{
    public DateTimeOffset Timestamp { get; init; }

    public string ConversationId { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string Question { get; init; } = string.Empty;

    public string Answer { get; init; } = string.Empty;

    public List<string> ToolCalls { get; init; } = [];

    public long InputTokens { get; init; }

    public long OutputTokens { get; init; }

    public long LatencyMs { get; init; }
}

public sealed record AuditLoad(List<AuditRecord> Records, List<InvalidLine> Invalid, List<string> MissingFiles);

public static class AuditReader
{
    public static AuditLoad Read(IEnumerable<string> paths)
    {
        var records = new List<AuditRecord>();
        var invalid = new List<InvalidLine>();
        var missing = new List<string>();
        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                missing.Add(path);
                continue;
            }

            foreach (var (number, line) in EventReader.CompleteLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var record = JsonSerializer.Deserialize<AuditRecord>(line, JsonDefaults.Options);
                    if (record is null || record.Timestamp == default)
                    {
                        invalid.Add(new(path, number, "no timestamp"));
                        continue;
                    }

                    records.Add(record);
                }
                catch (JsonException)
                {
                    invalid.Add(new(path, number, "not an audit record"));
                }
            }
        }

        return new AuditLoad(records, invalid, missing);
    }
}
