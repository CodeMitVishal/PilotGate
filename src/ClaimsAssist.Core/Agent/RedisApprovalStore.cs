using StackExchange.Redis;

namespace ClaimsAssist.Core.Agent;

/// <summary>
/// Pending approvals in Redis, shared by every replica and kept across restarts. Each record is one key with the
/// record's time to live. A decision takes the record with GETDEL (Redis 6.2 or later), which is atomic: of any number
/// of concurrent decisions, on any replicas, exactly one gets it. If the process stops after that, the approval is lost
/// rather than run twice, which is the safe failure for a payout.
/// </summary>
public sealed class RedisApprovalStore(IConnectionMultiplexer redis, string keyPrefix = "claimsassist:", TimeProvider? time = null) : IApprovalStore
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private RedisKey Key(string approvalId) => keyPrefix + "approval:" + approvalId;

    public async Task<bool> TryAddAsync(PendingApprovalRecord record, CancellationToken cancellationToken = default)
    {
        var ttl = record.ExpiresUtc - _time.GetUtcNow();
        if (ttl <= TimeSpan.Zero)
        {
            return false;
        }

        return await redis.GetDatabase().StringSetAsync(Key(record.ApprovalId), ApprovalJson.Serialize(record), ttl, When.NotExists);
    }

    public async Task<PendingApprovalRecord?> GetAsync(string approvalId, CancellationToken cancellationToken = default)
    {
        var value = await redis.GetDatabase().StringGetAsync(Key(approvalId));
        return value.HasValue ? ApprovalJson.Deserialize(value.ToString()) : null;
    }

    public async Task<PendingApprovalRecord?> TryTakeAsync(string approvalId, CancellationToken cancellationToken = default)
    {
        var value = await redis.GetDatabase().StringGetDeleteAsync(Key(approvalId));
        return value.HasValue ? ApprovalJson.Deserialize(value.ToString()) : null;
    }
}
