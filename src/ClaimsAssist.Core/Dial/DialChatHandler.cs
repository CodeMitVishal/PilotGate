using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClaimsAssist.Core.Agent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaimsAssist.Core.Dial;

/// <summary>An HTTP answer for the DIAL endpoint: a JSON body, or a list of server-sent events.</summary>
public sealed record DialResult(int StatusCode, string? Json, IReadOnlyList<string>? Events)
{
    public static DialResult Error(int status, string message, string type) =>
        new(status, new JsonObject
        {
            ["error"] = new JsonObject { ["message"] = message, ["type"] = type, ["code"] = status.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        }.ToJsonString(), null);
}

/// <summary>
/// ClaimsAssist as an EPAM DIAL application: the OpenAI chat completions protocol, streaming and not, in the shape the
/// DIAL spike captured (dial/guard/tests/fixtures/dial-core-0.50.8/client/*_direct.json).
///
/// Trust model (docs/suite/PLAN.md):
/// * identity comes only from DIAL Core (<see cref="DialIdentityResolver"/>), never from request headers;
/// * each request answers the last user message; the history and any client state DIAL resends are not trusted;
/// * approvals stay on the server with their owner: "approve apr_..." or "reject apr_..." decides one, and the agent
///   checks that the caller owns it (or is a team lead) and runs its deterministic guards again.
/// The agent answers in one piece, so a streaming request receives the finished answer as a short event stream.
/// A failing turn returns HTTP 500 with a reference that is also logged, never exception text. Not 502 or 503: DIAL Core
/// treats those as an unreachable upstream and replaces the body with its own "No route", so the user would lose the
/// reference (observed with DIAL Core 0.50.8; a 500 body reaches the client unchanged, spike fact 13).
/// </summary>
public sealed partial class DialChatHandler(
    IClaimsAgent agent,
    DialIdentityResolver identity,
    DialSettings settings,
    TimeProvider? time = null,
    ILogger? logger = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<DialResult> HandleAsync(string? apiKey, string? conversationHeader, string body, CancellationToken cancellationToken)
    {
        JsonNode? request;
        try
        {
            request = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return DialResult.Error(400, "The request body is not valid JSON.", "invalid_request_error");
        }

        if (request is not JsonObject root || root["messages"] is not JsonArray messages)
        {
            return DialResult.Error(400, "The request has no messages.", "invalid_request_error");
        }

        var question = LastUserText(messages);
        if (string.IsNullOrWhiteSpace(question))
        {
            return DialResult.Error(400, "The request has no user message with text.", "invalid_request_error");
        }

        var stream = root["stream"] is JsonValue s && s.TryGetValue<bool>(out var b) && b;

        var who = await identity.ResolveAsync(apiKey, cancellationToken);
        switch (who.Status)
        {
            case DialIdentityStatus.Unauthorized:
                return DialResult.Error(401, who.Reason, "unauthorized");
            case DialIdentityStatus.Forbidden:
                return DialResult.Error(403, who.Reason, "forbidden");
            case DialIdentityStatus.Unavailable:
                return DialResult.Error(500, "ClaimsAssist could not confirm the caller with DIAL. Please try again shortly.", "identity_unavailable");
        }

        var user = who.User!;
        var conversationId = conversationHeader is not null && ConversationId().IsMatch(conversationHeader) ? conversationHeader : null;

        ChatTurnResult? result;
        var command = ApprovalCommand().Match(question.Trim());
        try
        {
            if (command.Success)
            {
                var approvalId = command.Groups["id"].Value;
                var approve = command.Groups["verb"].Value.Equals("approve", StringComparison.OrdinalIgnoreCase);
                var reason = command.Groups["reason"].Value.Trim();
                try
                {
                    result = await agent.ResolveApprovalAsync(approvalId, approve, reason.Length > 0 ? reason : null, user, cancellationToken);
                }
                catch (UnauthorizedAccessException ex)
                {
                    return DialResult.Error(403, ex.Message, "forbidden");
                }

                if (result is null)
                {
                    return Answer($"There is no pending approval {approvalId}. It may have been decided already, or it expired.", "stop", 0, 0, stream);
                }
            }
            else
            {
                result = await agent.AskAsync(question, user, conversationId, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Failure(ex);
        }

        return Answer(Format(result), result.Status == "blocked" ? "content_filter" : "stop",
            result.Trace.InputTokens, result.Trace.OutputTokens, stream);
    }

    private DialResult Failure(Exception ex)
    {
        var reference = Guid.NewGuid().ToString("n")[..12];
        (logger ?? NullLogger.Instance).LogError(ex, "ClaimsAssist could not answer a DIAL request (reference {Reference})", reference);
        return IsUpstream(ex)
            ? DialResult.Error(500, $"ClaimsAssist could not reach its language model. Please try again shortly. Reference: {reference}", "model_unavailable")
            : DialResult.Error(500, $"ClaimsAssist could not answer this request. Reference: {reference}", "internal_error");
    }

    /// <summary>A failure of the model service or of the sign-in to it, as opposed to a fault in ClaimsAssist.</summary>
    private static bool IsUpstream(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is System.ClientModel.ClientResultException or Azure.RequestFailedException or Azure.Identity.AuthenticationFailedException
                or Azure.Identity.CredentialUnavailableException or HttpRequestException or TimeoutException or OperationCanceledException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The answer text a DIAL user sees: answer, sources, approval instructions, AI disclosure.</summary>
    public static string Format(ChatTurnResult result)
    {
        var text = new StringBuilder(result.Answer.Trim());
        if (result.Citations.Count > 0)
        {
            text.Append("\n\nSources: ").Append(string.Join(", ", result.Citations.Select(c => c.Ref).Distinct()));
        }

        foreach (var approval in result.PendingApprovals)
        {
            text.Append("\n\nApproval needed: ").Append(approval.Tool).Append(' ').Append(approval.ArgumentsJson)
                .Append(". Reply \"approve ").Append(approval.ApprovalId).Append("\" to run it, or \"reject ")
                .Append(approval.ApprovalId).Append("\" to stop it.");
        }

        text.Append("\n\n_").Append(result.AiDisclosure).Append('_');
        return text.ToString();
    }

    private DialResult Answer(string content, string finishReason, long promptTokens, long completionTokens, bool stream)
    {
        var id = "chatcmpl-" + Guid.NewGuid().ToString("n");
        var created = _time.GetUtcNow().ToUnixTimeSeconds();
        var usage = new JsonObject
        {
            ["prompt_tokens"] = promptTokens,
            ["completion_tokens"] = completionTokens,
            ["total_tokens"] = promptTokens + completionTokens,
        };

        if (!stream)
        {
            var body = new JsonObject
            {
                ["id"] = id,
                ["object"] = "chat.completion",
                ["created"] = created,
                ["model"] = settings.DeploymentId,
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["index"] = 0,
                    ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content },
                    ["finish_reason"] = finishReason,
                }),
                ["usage"] = usage,
            };
            return new DialResult(200, body.ToJsonString(), null);
        }

        string Chunk(JsonObject delta, string? finish, JsonObject? withUsage = null)
        {
            var chunk = new JsonObject
            {
                ["id"] = id,
                ["object"] = "chat.completion.chunk",
                ["created"] = created,
                ["model"] = settings.DeploymentId,
                ["choices"] = withUsage is null
                    ? new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finish })
                    : new JsonArray(),
            };
            if (withUsage is not null)
            {
                chunk["usage"] = withUsage;
            }

            return "data: " + chunk.ToJsonString() + "\n\n";
        }

        var events = new List<string> { Chunk(new JsonObject { ["role"] = "assistant" }, null) };
        foreach (var piece in Pieces(content))
        {
            events.Add(Chunk(new JsonObject { ["content"] = piece }, null));
        }

        events.Add(Chunk(new JsonObject(), finishReason));
        events.Add(Chunk(new JsonObject(), null, usage));
        events.Add("data: [DONE]\n\n");
        return new DialResult(200, null, events);
    }

    /// <summary>Splits the finished answer into pieces of about 64 characters at spaces.</summary>
    private static IEnumerable<string> Pieces(string content)
    {
        var start = 0;
        while (start < content.Length)
        {
            var end = Math.Min(content.Length, start + 64);
            if (end < content.Length)
            {
                var space = content.IndexOf(' ', end);
                end = space < 0 ? content.Length : space + 1;
            }

            yield return content[start..end];
            start = end;
        }
    }

    private static string? LastUserText(JsonArray messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is not JsonObject message || Str(message["role"]) != "user")
            {
                continue;
            }

            return message["content"] switch
            {
                JsonValue v when v.TryGetValue<string>(out var text) => text,
                JsonArray parts => string.Join("\n", parts.OfType<JsonObject>()
                    .Where(p => Str(p["type"]) == "text")
                    .Select(p => Str(p["text"]) ?? string.Empty)),
                _ => null,
            };
        }

        return null;
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    [GeneratedRegex(@"^(?<verb>approve|reject)\s+(?<id>apr_[abcdefghjkmnpqrstuvwxyz]{12})\b(?<reason>.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ApprovalCommand();

    [GeneratedRegex(@"^[A-Za-z0-9._:-]{1,128}$")]
    private static partial Regex ConversationId();
}
