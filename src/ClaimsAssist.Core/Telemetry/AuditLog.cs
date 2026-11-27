using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClaimsAssist.Core.Guardrails;

namespace ClaimsAssist.Core.Telemetry;

public sealed record AuditRecord(
    DateTimeOffset Timestamp,
    string ConversationId,
    string User,
    string Status,
    string Question,
    string Answer,
    IReadOnlyList<string> RetrievedChunkIds,
    IReadOnlyList<string> ToolCalls,
    long InputTokens,
    long OutputTokens,
    long LatencyMs);

/// <summary>
/// Append-only JSON Lines audit trail. Handlers are pseudonymised (HMAC) and customer PII is redacted
/// before anything is written. Production path: the same records as OpenTelemetry spans in Application Insights.
/// </summary>
public sealed class AuditLog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string? _path;
    private readonly TextWriter? _writer;
    private readonly PiiRedactor _redactor;
    private readonly byte[] _pseudonymKey;
    private readonly Lock _gate = new();

    /// <summary>Appends one line per record to the file at <paramref name="path"/>.</summary>
    public AuditLog(string path, PiiRedactor redactor, string pseudonymKey)
        : this(redactor, pseudonymKey)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    }

    /// <summary>Writes one line per record to <paramref name="writer"/>: standard output in a container, which the
    /// platform ships to its log workspace. Each record is a single write, so lines never interleave.</summary>
    public AuditLog(TextWriter writer, PiiRedactor redactor, string pseudonymKey)
        : this(redactor, pseudonymKey)
    {
        _writer = writer;
    }

    private AuditLog(PiiRedactor redactor, string pseudonymKey)
    {
        _redactor = redactor;
        _pseudonymKey = Encoding.UTF8.GetBytes(pseudonymKey);
    }

    /// <summary>The audit file, or "stdout".</summary>
    public string FilePath => _path ?? "stdout";

    public string Pseudonymise(string userId) =>
        "u_" + Convert.ToHexString(HMACSHA256.HashData(_pseudonymKey, Encoding.UTF8.GetBytes(userId)))[..12].ToLowerInvariant();

    public void Write(AuditRecord record)
    {
        string Clean(string value) => _redactor.Redact(value, includeNames: true).Text;

        var safe = record with
        {
            Question = Clean(record.Question),
            Answer = Clean(record.Answer),
            ToolCalls = record.ToolCalls.Select(Clean).ToList(),
        };

        var line = JsonSerializer.Serialize(safe, Json) + "\n";
        lock (_gate)
        {
            if (_writer is not null)
            {
                _writer.Write(line);
                _writer.Flush();
            }
            else
            {
                File.AppendAllText(_path!, line);
            }
        }
    }
}
