using ClaimsAssist.Core.Agent;
using ClaimsAssist.Core.Ai;
using ClaimsAssist.Core.Claims;
using ClaimsAssist.Core.Guardrails;
using ClaimsAssist.Core.Security;
using ClaimsAssist.Core.Telemetry;
using Microsoft.Extensions.AI;
using StackExchange.Redis;

namespace ClaimsAssist.Tests;

/// <summary>
/// The approval store contract (review finding P0-2), for every implementation: records survive serialization whole,
/// reading does not consume, exactly one of many concurrent decisions gets a record, and expired records are gone.
/// The in-memory store always runs; the Redis store runs when CLAIMSASSIST_TEST_REDIS names a local Redis, for
/// example 127.0.0.1:6379 (docker run --rm -p 127.0.0.1:6379:6379 redis:7.2.4-alpine).
/// </summary>
public sealed class ApprovalStoreTests
{
    private static readonly string? RedisAddress = Environment.GetEnvironmentVariable("CLAIMSASSIST_TEST_REDIS");

    private static readonly Lazy<ConnectionMultiplexer> Redis = new(() => ConnectionMultiplexer.Connect(RedisAddress!));

    public static TheoryData<string> Stores()
    {
        var stores = new TheoryData<string> { "memory" };
        if (!string.IsNullOrWhiteSpace(RedisAddress))
        {
            stores.Add("redis");
        }

        return stores;
    }

    private sealed record Subject(IApprovalStore Store, ManualTimeProvider Time, Func<TimeSpan, Task> PassTime);

    private static Subject Create(string kind)
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        if (kind == "memory")
        {
            return new Subject(new InMemoryApprovalStore(time: time), time, by =>
            {
                time.Advance(by);
                return Task.CompletedTask;
            });
        }

        // Redis expires keys on its own clock, so time passes for real there; each test gets its own key prefix.
        return new Subject(new RedisApprovalStore(Redis.Value, $"test-{Guid.NewGuid():n}:", time), time, async by =>
        {
            time.Advance(by);
            await Task.Delay(by + TimeSpan.FromMilliseconds(250));
        });
    }

    internal static PendingApprovalRecord Sample(string id, DateTimeOffset now, TimeSpan ttl)
    {
        var trace = new TurnTrace
        {
            Model = "gpt-test",
            InputTokens = 1234,
            OutputTokens = 56,
            LatencyMs = 789,
            ModelRuns = 1,
            GroundingStatements = 2,
            SuspiciousContentDetected = true,
        };
        trace.Retrievals.Add(new RetrievalEvent("settlement authority Regulierungsvollmacht", [new RetrievedRef("KB-PROP-GUIDE#settlement-authority", "KB-PROP-GUIDE", 0.91)]));
        trace.ToolCalls.Add(new ToolEvent { CallId = "call_1", Name = "propose_settlement", ArgumentsJson = "{\"claimId\":\"CLM-2026-0142\",\"amountEur\":4200}", RequiresApproval = true });
        trace.OpenedClaimIds.Add("CLM-2026-0142");
        trace.QuarantinedSources.Add("CLM-2026-0142");
        trace.GroundingContext.Add("Claim CLM-2026-0142, sum insured 65,000 EUR.");
        trace.GroundingMissing.Add("num:6000");
        trace.WriteActions.Add(new WriteAction("propose_settlement", "CLM-2026-0142", 12.5m, "REJECTED", "for the round trip only"));
        var requester = new RequesterSnapshot("anna.property", "Anna Example", "Claims handler, property", ["all-claims", "claims-property"], 5000m);
        return new PendingApprovalRecord(id, "c_test", "Propose 4,200 EUR for CLM-2026-0142.", requester,
            [new ApprovedCall("call_1", "propose_settlement", "{\"claimId\":\"CLM-2026-0142\",\"amountEur\":4200}")], trace, now, now + ttl);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task A_record_comes_back_whole(string kind)
    {
        var s = Create(kind);
        var record = Sample("apr_roundtrip", s.Time.GetUtcNow(), TimeSpan.FromMinutes(5));

        Assert.True(await s.Store.TryAddAsync(record));
        var back = await s.Store.GetAsync(record.ApprovalId);

        Assert.NotNull(back);
        Assert.Equal(ApprovalJson.Serialize(record), ApprovalJson.Serialize(back));
        Assert.True(back.Trace.ToolCalls.Single().RequiresApproval);
        Assert.Equal(["CLM-2026-0142"], back.Trace.OpenedClaimIds);
        Assert.Equal(0.91, back.Trace.Retrievals.Single().Results.Single().Score);
        var requester = back.Requester.ToUserContext();
        Assert.True(requester.IsIn("CLAIMS-PROPERTY")); // group checks stay case-insensitive
        Assert.Equal(5000m, requester.SettlementAuthorityEur);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Reading_does_not_consume_and_taking_does(string kind)
    {
        var s = Create(kind);
        var record = Sample("apr_readtake", s.Time.GetUtcNow(), TimeSpan.FromMinutes(5));
        await s.Store.TryAddAsync(record);

        Assert.NotNull(await s.Store.GetAsync(record.ApprovalId));
        Assert.NotNull(await s.Store.GetAsync(record.ApprovalId));
        Assert.NotNull(await s.Store.TryTakeAsync(record.ApprovalId));
        Assert.Null(await s.Store.GetAsync(record.ApprovalId));
        Assert.Null(await s.Store.TryTakeAsync(record.ApprovalId));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Of_many_concurrent_decisions_exactly_one_gets_the_record(string kind)
    {
        var s = Create(kind);
        var record = Sample("apr_race", s.Time.GetUtcNow(), TimeSpan.FromMinutes(5));
        await s.Store.TryAddAsync(record);

        var takes = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => s.Store.TryTakeAsync(record.ApprovalId))));

        Assert.Single(takes, t => t is not null);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task An_expired_record_is_gone_and_an_expired_one_is_never_stored(string kind)
    {
        var s = Create(kind);
        var record = Sample("apr_expiry", s.Time.GetUtcNow(), TimeSpan.FromSeconds(1));
        Assert.True(await s.Store.TryAddAsync(record));

        await s.PassTime(TimeSpan.FromSeconds(1));

        Assert.Null(await s.Store.GetAsync(record.ApprovalId));
        Assert.Null(await s.Store.TryTakeAsync(record.ApprovalId));
        Assert.False(await s.Store.TryAddAsync(Sample("apr_stale", s.Time.GetUtcNow(), TimeSpan.Zero)));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task An_approval_id_is_stored_once(string kind)
    {
        var s = Create(kind);
        var first = Sample("apr_twice", s.Time.GetUtcNow(), TimeSpan.FromMinutes(5));

        Assert.True(await s.Store.TryAddAsync(first));
        Assert.False(await s.Store.TryAddAsync(first with { Question = "a different request" }));
        Assert.Equal(first.Question, (await s.Store.GetAsync(first.ApprovalId))!.Question);
    }

    [Fact]
    public async Task A_full_memory_store_refuses_new_approvals_instead_of_evicting_live_ones()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryApprovalStore(capacity: 2, time: time);
        var now = time.GetUtcNow();
        Assert.True(await store.TryAddAsync(Sample("apr_a", now, TimeSpan.FromMinutes(5))));
        Assert.True(await store.TryAddAsync(Sample("apr_b", now, TimeSpan.FromMinutes(10))));

        Assert.False(await store.TryAddAsync(Sample("apr_c", now, TimeSpan.FromMinutes(5))));
        Assert.NotNull(await store.GetAsync("apr_a"));

        await store.TryTakeAsync("apr_a");
        Assert.True(await store.TryAddAsync(Sample("apr_c", now, TimeSpan.FromMinutes(5))));

        time.Advance(TimeSpan.FromMinutes(6)); // apr_c expires; apr_b is still live
        Assert.True(await store.TryAddAsync(Sample("apr_d", time.GetUtcNow(), TimeSpan.FromMinutes(5))));
        Assert.Equal(2, store.Count);
    }

    [Theory]
    [InlineData("Memory", null, true, null)]
    [InlineData("Redis", "name.westeurope.redis.azure.net:10000", true, null)]
    [InlineData("Redis", "127.0.0.1:6379", false, null)]
    [InlineData("Redis", "localhost:6379", false, null)]
    [InlineData("Redis", "", true, "ConnectionString is required")]
    [InlineData("Redis", "name.westeurope.redis.azure.net:10000,password=secret", true, "must not contain a password")]
    [InlineData("Redis", "redis.internal:6379", false, "loopback")]
    [InlineData("Redis", "127.0.0.1:6379,ssl=false", true, "needs TLS")]
    [InlineData("Cosmos", null, true, "must be Memory or Redis")]
    public void The_approval_store_settings_are_checked_at_startup(string store, string? connection, bool entraId, string? problem)
    {
        var settings = new ClaimsAssistSettings
        {
            AuthMode = "DevPersonas",
            ApprovalStore = store,
            Redis = new RedisSettings { ConnectionString = connection ?? string.Empty, UseEntraId = entraId },
        };

        var problems = settings.StartupProblems();

        if (problem is null)
        {
            Assert.Empty(problems);
        }
        else
        {
            Assert.Contains(problems, p => p.Contains(problem, StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(0, 10_000)]
    [InlineData(1441, 10_000)]
    [InlineData(30, 0)]
    public void Approval_expiry_and_capacity_must_be_in_range(int ttlMinutes, int capacity)
    {
        var settings = new ClaimsAssistSettings { AuthMode = "DevPersonas", ApprovalTtlMinutes = ttlMinutes, ApprovalCapacity = capacity };
        Assert.NotEmpty(settings.StartupProblems());
    }
}

/// <summary>
/// Deciding an approval, end to end through the agent with a scripted model: the stored call runs through the same
/// settlement guards as the tool, on any replica, at most once, and only before it expires.
/// </summary>
public sealed class ApprovalFlowTests
{
    private const string Claim = "CLM-2026-0142"; // property; anna.property may open it, ben.motor may not

    private static readonly UserContext Anna = TestData.Persona("anna.property"); // settlement authority 5,000 EUR
    private static readonly UserContext Ben = TestData.Persona("ben.motor");

    private static readonly UserContext TeamLead = new(
        "tl.property", "Team lead, property", "Team lead, property",
        new HashSet<string>(["all-claims", "claims-property", "claims-teamlead"], StringComparer.OrdinalIgnoreCase), 25_000m);

    /// <summary>A model that proposes one settlement, as gpt-4.1-mini does for "propose 4,200 EUR for ...".</summary>
    private sealed class ProposingModel(decimal amountEur) : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            var call = new FunctionCallContent($"call_{Calls}", "propose_settlement", new Dictionary<string, object?>
            {
                ["claimId"] = Claim,
                ["amountEur"] = amountEur,
                ["rationale"] = "Storm damage to the conservatory roof, covered by the policy.",
            });
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed record Replica(ClaimsAgentService Agent, SettlementService Settlements, ProposingModel Model, StringWriter AuditOut);

    private static async Task<Replica> ReplicaAsync(IApprovalStore store, TimeProvider time, decimal amountEur = 4200m)
    {
        var claims = TestData.Claims();
        var settlements = new SettlementService(claims);
        var model = new ProposingModel(amountEur);
        var auditOut = new StringWriter();
        var redactor = PiiRedactor.ForClaims(claims);
        var agent = new ClaimsAgentService(
            model, await TestData.RetrieverAsync(), claims, settlements,
            new AuditLog(auditOut, redactor, "6f1c0e9a4b7d2f8e3a5c1b9d7e2f4a6c8b0d3e5f7a9c1e3b"), redactor, "test-model",
            approverGroups: ["claims-teamlead"], approvalStore: store, approvalTtl: TimeSpan.FromMinutes(30), time: time);
        return new Replica(agent, settlements, model, auditOut);
    }

    private static async Task<(Replica Replica, ChatTurnResult Request, ManualTimeProvider Time, IApprovalStore Store)> ProposeAsync(decimal amountEur = 4200m)
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryApprovalStore(time: time);
        var replica = await ReplicaAsync(store, time, amountEur);
        var request = await replica.Agent.AskAsync($"Propose a settlement of {amountEur} EUR for {Claim}.", Anna);
        Assert.Equal("approval_required", request.Status);
        return (replica, request, time, store);
    }

    [Fact]
    public async Task Approving_runs_the_stored_call_once_through_the_settlement_guards()
    {
        var (replica, request, _, _) = await ProposeAsync();
        var approvalId = request.PendingApprovals.Single().ApprovalId;
        Assert.Empty(replica.Settlements.Queued); // nothing runs before the decision

        var result = await replica.Agent.ResolveApprovalAsync(approvalId, approve: true, reason: null, Anna);

        Assert.NotNull(result);
        Assert.Equal("answered", result.Status);
        Assert.StartsWith("QUEUED: Settlement of 4,200.00 EUR", result.Answer, StringComparison.Ordinal);
        Assert.Equal(Claim, Assert.Single(result.Citations).Ref);
        Assert.Equal(ClaimsAgentService.AiDisclosure, result.AiDisclosure);
        var write = Assert.Single(result.Trace.WriteActions);
        Assert.Equal(("QUEUED", 4200m), (write.Outcome, write.AmountEur));
        Assert.Equal(1, result.Trace.ApprovalsGranted);
        var tool = Assert.Single(result.Trace.ToolCalls);
        Assert.True(tool.Approved);
        Assert.True(tool.Executed);
        Assert.Equal(0, result.Trace.GroundingWithheld);
        Assert.Single(replica.Settlements.Queued);
        Assert.Equal(1, replica.Model.Calls); // the decision does not ask the model again
        Assert.Contains("\"status\":\"answered\"", replica.AuditOut.ToString(), StringComparison.Ordinal);
        Assert.Null(await replica.Agent.ResolveApprovalAsync(approvalId, approve: true, reason: null, Anna)); // decided once
    }

    [Fact]
    public async Task Rejecting_writes_nothing_and_keeps_the_reason_visible()
    {
        var (replica, request, _, _) = await ProposeAsync();

        var result = await replica.Agent.ResolveApprovalAsync(request.PendingApprovals.Single().ApprovalId, approve: false,
            reason: "Photos of the roof first, the estimate says 3,900 EUR", Anna);

        Assert.NotNull(result);
        Assert.Equal("Not run: the approver rejected the propose_settlement request (Photos of the roof first, the estimate says 3,900 EUR).", result.Answer);
        Assert.DoesNotContain(Anna.DisplayName, replica.AuditOut.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, result.Trace.GroundingWithheld); // the approver's own figures are a source
        Assert.Empty(result.Trace.WriteActions);
        Assert.Equal(0, result.Trace.ApprovalsGranted);
        Assert.False(Assert.Single(result.Trace.ToolCalls).Approved);
        Assert.Empty(replica.Settlements.Queued);
        Assert.Contains("rejected by the approver", replica.AuditOut.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Another_handler_cannot_decide_and_does_not_use_up_the_approval()
    {
        var (replica, request, _, _) = await ProposeAsync();
        var approvalId = request.PendingApprovals.Single().ApprovalId;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => replica.Agent.ResolveApprovalAsync(approvalId, approve: true, reason: null, Ben));

        var result = await replica.Agent.ResolveApprovalAsync(approvalId, approve: true, reason: null, Anna);
        Assert.Equal("QUEUED", Assert.Single(result!.Trace.WriteActions).Outcome);
    }

    [Fact]
    public async Task Concurrent_approvals_of_one_request_pay_out_once()
    {
        var (replica, request, _, _) = await ProposeAsync();
        var approvalId = request.PendingApprovals.Single().ApprovalId;

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => replica.Agent.ResolveApprovalAsync(approvalId, approve: true, reason: null, Anna))));

        Assert.Single(results, r => r is not null);
        Assert.Single(replica.Settlements.Queued);
    }

    [Fact]
    public async Task A_team_lead_decision_keeps_the_requesters_authority()
    {
        var (replica, request, _, _) = await ProposeAsync(amountEur: 6000m); // above Anna's 5,000, within the lead's 25,000

        var result = await replica.Agent.ResolveApprovalAsync(request.PendingApprovals.Single().ApprovalId, approve: true, reason: null, TeamLead);

        var write = Assert.Single(result!.Trace.WriteActions);
        Assert.Equal("REJECTED", write.Outcome);
        Assert.Contains("settlement authority of 5,000 EUR", write.Message, StringComparison.Ordinal);
        Assert.Empty(replica.Settlements.Queued);

        // The request is audited under the requester, the decision under the team lead: two pseudonyms, one conversation.
        var lines = replica.AuditOut.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.NotEqual(Pseudonym(lines[0]), Pseudonym(lines[1]));
    }

    private static string Pseudonym(string auditLine)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(auditLine);
        return doc.RootElement.EnumerateObject().Single(p => p.Name.Contains("user", StringComparison.OrdinalIgnoreCase)).Value.GetString()!;
    }

    [Fact]
    public async Task The_requesters_own_decision_uses_their_identity_as_it_is_now()
    {
        var (replica, request, _, _) = await ProposeAsync();
        var revoked = Anna with { SettlementAuthorityEur = 0m }; // authority withdrawn while the approval waited

        var result = await replica.Agent.ResolveApprovalAsync(request.PendingApprovals.Single().ApprovalId, approve: true, reason: null, revoked);

        Assert.Equal("REJECTED", Assert.Single(result!.Trace.WriteActions).Outcome);
        Assert.Empty(replica.Settlements.Queued);
    }

    [Fact]
    public async Task An_expired_approval_cannot_be_decided()
    {
        var (replica, request, time, _) = await ProposeAsync();

        time.Advance(TimeSpan.FromMinutes(31));

        Assert.Null(await replica.Agent.ResolveApprovalAsync(request.PendingApprovals.Single().ApprovalId, approve: true, reason: null, Anna));
        Assert.Empty(replica.Settlements.Queued);
    }

    [Fact]
    public async Task Any_replica_sharing_the_store_can_decide()
    {
        var (first, request, time, store) = await ProposeAsync();
        var second = await ReplicaAsync(store, time); // another replica, or this one after a restart

        var result = await second.Agent.ResolveApprovalAsync(request.PendingApprovals.Single().ApprovalId, approve: true, reason: null, Anna);

        Assert.Equal("QUEUED", Assert.Single(result!.Trace.WriteActions).Outcome);
        Assert.Equal(Claim, Assert.Single(result.Citations).Ref);
        Assert.Single(second.Settlements.Queued);
        Assert.Empty(first.Settlements.Queued);
    }

    [Fact]
    public async Task When_too_many_approvals_wait_a_new_proposal_is_refused()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var replica = await ReplicaAsync(new InMemoryApprovalStore(capacity: 1, time: time), time);
        Assert.Equal("approval_required", (await replica.Agent.AskAsync($"Propose 4200 EUR for {Claim}.", Anna)).Status);

        var refused = await replica.Agent.AskAsync($"Propose 4200 EUR for {Claim}.", Anna);

        Assert.Equal("answered", refused.Status);
        Assert.Empty(refused.PendingApprovals);
        Assert.Contains("Too many approvals are waiting", refused.Answer, StringComparison.Ordinal);
    }
}
