using ClaimsAssist.Core.Security;

namespace ClaimsAssist.Core.Agent;

/// <summary>The agent as the transports see it (development HTTP API, DIAL application endpoint).</summary>
public interface IClaimsAgent
{
    Task<ChatTurnResult> AskAsync(string question, UserContext user, string? conversationId = null, CancellationToken cancellationToken = default);

    /// <summary>Returns null when no approval with that id is pending. Throws <see cref="UnauthorizedAccessException"/>
    /// when the approver is neither the requesting handler nor a team lead.</summary>
    Task<ChatTurnResult?> ResolveApprovalAsync(string approvalId, bool approve, string? reason, UserContext approver, CancellationToken cancellationToken = default);
}
