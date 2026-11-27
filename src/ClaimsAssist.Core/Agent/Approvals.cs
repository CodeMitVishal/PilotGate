using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaimsAssist.Core.Security;

namespace ClaimsAssist.Core.Agent;

/// <summary>A write the model asked for and a human must decide on: the tool and its exact arguments.</summary>
public sealed record ApprovedCall(string CallId, string Tool, string ArgumentsJson);

/// <summary>The requester as resolved when the approval was requested. Used only when someone other than the requester
/// decides (a team lead): the write then runs with the requester's authority, as it did before approvals were stored.</summary>
public sealed record RequesterSnapshot(string UserId, string DisplayName, string Role, IReadOnlyList<string> Groups, decimal SettlementAuthorityEur)
{
    public static RequesterSnapshot Of(UserContext user) =>
        new(user.UserId, user.DisplayName, user.Role, user.Groups.Order(StringComparer.Ordinal).ToList(), user.SettlementAuthorityEur);

    public UserContext ToUserContext() =>
        new(UserId, DisplayName, Role, Groups.ToHashSet(StringComparer.OrdinalIgnoreCase), SettlementAuthorityEur);
}

/// <summary>
/// A pending approval as plain data, so any replica can decide it and it survives a restart. It holds claim data,
/// so its time to live is also its retention limit. The decision runs the stored call itself: the model is not asked
/// again, so what the approver saw is exactly what runs.
/// </summary>
public sealed record PendingApprovalRecord(
    string ApprovalId,
    string ConversationId,
    string Question,
    RequesterSnapshot Requester,
    IReadOnlyList<ApprovedCall> Calls,
    TurnTrace Trace,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ExpiresUtc);

/// <summary>
/// Where pending approvals wait. Contract (pinned by ApprovalStoreTests for every implementation): reading does not
/// consume; <see cref="TryTakeAsync"/> is atomic, so of any number of concurrent decisions exactly one gets the record;
/// expired records are gone.
/// </summary>
public interface IApprovalStore
{
    /// <summary>False when the store is full (it never evicts a live approval to make room).</summary>
    Task<bool> TryAddAsync(PendingApprovalRecord record, CancellationToken cancellationToken = default);

    Task<PendingApprovalRecord?> GetAsync(string approvalId, CancellationToken cancellationToken = default);

    /// <summary>Atomically removes and returns the record, or null if another decision took it first or it expired.</summary>
    Task<PendingApprovalRecord?> TryTakeAsync(string approvalId, CancellationToken cancellationToken = default);
}

/// <summary>One serializer for every store, so a record that works in memory also works in Redis.</summary>
public static class ApprovalJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        // TurnTrace exposes its lists as get-only properties: fill them instead of replacing them.
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
    };

    public static string Serialize(PendingApprovalRecord record) => JsonSerializer.Serialize(record, Options);

    public static PendingApprovalRecord Deserialize(string json) =>
        JsonSerializer.Deserialize<PendingApprovalRecord>(json, Options) ?? throw new JsonException("Empty approval record.");
}

/// <summary>
/// Pending approvals in this process (development, single replica). Records are kept serialized, exactly as a shared
/// store would keep them; they expire after their time to live; and the store refuses new approvals at capacity
/// instead of evicting live ones. Not shared between replicas: use <see cref="RedisApprovalStore"/> there.
/// </summary>
public sealed class InMemoryApprovalStore(int capacity = 10_000, TimeProvider? time = null) : IApprovalStore
{
    private readonly ConcurrentDictionary<string, (string Json, DateTimeOffset ExpiresUtc)> _records = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();

    public int Count => _records.Count;

    public Task<bool> TryAddAsync(PendingApprovalRecord record, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            foreach (var expired in _records.Where(r => r.Value.ExpiresUtc <= now).Select(r => r.Key).ToList())
            {
                _records.TryRemove(expired, out _);
            }

            if (record.ExpiresUtc <= now || _records.Count >= capacity)
            {
                return Task.FromResult(false);
            }

            return Task.FromResult(_records.TryAdd(record.ApprovalId, (ApprovalJson.Serialize(record), record.ExpiresUtc)));
        }
    }

    public Task<PendingApprovalRecord?> GetAsync(string approvalId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_records.TryGetValue(approvalId, out var entry) && entry.ExpiresUtc > _time.GetUtcNow()
            ? ApprovalJson.Deserialize(entry.Json)
            : null);

    public Task<PendingApprovalRecord?> TryTakeAsync(string approvalId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_records.TryRemove(approvalId, out var entry) && entry.ExpiresUtc > _time.GetUtcNow()
            ? ApprovalJson.Deserialize(entry.Json)
            : null);
}
