using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PilotGate;

/// <summary>
/// Gates an application hosted on EPAM DIAL through DIAL Core's OpenAI-compatible API, exactly as a user reaches it:
/// one DIAL key per persona, DIAL's access control, any interceptors (DIAL Guard) in between. The application is a
/// black box: the turn is built from what the answer shows (text, a "Sources:" line, an approval command, the AI
/// disclosure, token usage) and is marked <see cref="AgentTurn.TraceAvailable"/> = false.
/// </summary>
public sealed partial class DialAgentClient(HttpClient http, DialTarget target, IReadOnlyDictionary<string, string> personaKeys) : IAgentClient
{
    private int _turn;

    public static IReadOnlyDictionary<string, string> LoadKeys(string path)
    {
        var keys = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
            ?? throw new InvalidOperationException($"No DIAL keys in {path}.");
        return keys;
    }

    public async Task<JsonElement?> HealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync("/health", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var health = new JsonObject
            {
                ["target"] = "dial",
                ["deployment"] = target.Deployment,
                ["blackBox"] = true,
                ["model"] = target.Model,
            };
            return JsonDocument.Parse(health.ToJsonString()).RootElement.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    public Task<AgentTurn> AskAsync(string persona, string message, CancellationToken cancellationToken) =>
        SendAsync(persona, message, cancellationToken);

    public Task<AgentTurn> DecideAsync(string persona, string approvalId, bool approve, CancellationToken cancellationToken) =>
        SendAsync(persona, approve ? $"approve {approvalId}" : $"reject {approvalId} Rejected by PilotGate", cancellationToken);

    private async Task<AgentTurn> SendAsync(string persona, string message, CancellationToken cancellationToken)
    {
        if (!personaKeys.TryGetValue(persona, out var key))
        {
            throw new InvalidOperationException($"No DIAL key for persona '{persona}' in {target.KeysFile}.");
        }

        var conversation = $"pilotgate-{Environment.ProcessId}-{Interlocked.Increment(ref _turn)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/openai/deployments/{Uri.EscapeDataString(target.Deployment)}/chat/completions")
        {
            Content = JsonContent.Create(new { messages = new[] { new { role = "user", content = message } }, stream = false }),
        };
        request.Headers.TryAddWithoutValidation("Api-Key", key);
        request.Headers.TryAddWithoutValidation("X-Conversation-Id", conversation);

        var started = Stopwatch.GetTimestamp();
        using var response = await http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // An application answer such as 403 is evidence about the target, not a broken transport: the scenario
            // records it and the run continues (only transport failures make the runner check health and stop).
            throw new InvalidOperationException($"DIAL answered {(int)response.StatusCode}: {(text.Length > 300 ? text[..300] : text)}");
        }

        var body = JsonNode.Parse(text) ?? throw new InvalidOperationException("Empty DIAL response.");
        var choice = body["choices"]?[0] ?? throw new InvalidOperationException("DIAL response without choices.");
        var content = choice["message"]?["content"]?.GetValue<string>() ?? string.Empty;
        var finish = choice["finish_reason"]?.GetValue<string>();
        return Parse(content, finish, body["usage"], target, conversation, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    /// <summary>Builds a turn from an answer's visible parts. Public for tests.</summary>
    public static AgentTurn Parse(string content, string? finishReason, JsonNode? usage, DialTarget target, string conversation, long latencyMs)
    {
        var approvals = Approval().Matches(content)
            .Select(m => new AgentPendingApproval { Tool = m.Groups["tool"].Value, ArgumentsJson = m.Groups["args"].Value.Trim(), ApprovalId = m.Groups["id"].Value })
            .ToList();
        var sources = Sources().Match(content);
        var citations = sources.Success
            ? sources.Groups["refs"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(r => new AgentCitation { Ref = r, DocId = r.Split('#')[0] }).ToList()
            : [];
        var disclosure = content.Split('\n').Select(l => l.Trim().Trim('_').Trim())
            .LastOrDefault(l => target.DisclosureMarker.Length > 0 && l.Contains(target.DisclosureMarker, StringComparison.Ordinal));
        var notFound = target.NotFoundPhrases.Any(p => content.Contains(p, StringComparison.OrdinalIgnoreCase));

        return new AgentTurn
        {
            ConversationId = conversation,
            Status = finishReason == "content_filter" ? "blocked" : approvals.Count > 0 ? "approval_required" : "answered",
            Answer = content,
            AnswerFound = !notFound,
            Citations = citations,
            PendingApprovals = approvals,
            AiDisclosure = disclosure,
            TraceAvailable = false,
            Trace = new AgentTrace
            {
                Model = target.Model,
                InputTokens = usage?["prompt_tokens"]?.GetValue<long>() ?? 0,
                OutputTokens = usage?["completion_tokens"]?.GetValue<long>() ?? 0,
                LatencyMs = latencyMs,
                ModelRuns = 1,
            },
        };
    }

    [GeneratedRegex("Approval needed: (?<tool>[A-Za-z0-9_.-]+) (?<args>.*?)\\. Reply \"approve (?<id>apr_[a-z]{12})\"")]
    private static partial Regex Approval();

    [GeneratedRegex(@"^Sources: (?<refs>.+)$", RegexOptions.Multiline)]
    private static partial Regex Sources();
}
