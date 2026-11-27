using System.ClientModel.Primitives;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using Microsoft.Extensions.AI.Evaluation.Reporting.Storage;
using OpenAI;

namespace PilotGate;

public sealed record JudgeScores(double? Groundedness, string? GroundednessReason, double? Completeness, string? CompletenessReason);

/// <summary>
/// LLM-as-judge scoring with Microsoft.Extensions.AI.Evaluation (Groundedness and Completeness, 1 to 5).
/// The judge is a stronger model than the agent to limit self-preference bias. Responses are cached on disk,
/// so re-running the gate on unchanged answers costs nothing.
/// </summary>
public sealed class LlmJudge
{
    private readonly ReportingConfiguration _reporting;
    private readonly TokenCountingChatClient _counter;

    private LlmJudge(ReportingConfiguration reporting, TokenCountingChatClient counter, string model)
    {
        _reporting = reporting;
        _counter = counter;
        Model = model;
    }

    public string Model { get; }

    public long InputTokens => _counter.InputTokens;

    public long OutputTokens => _counter.OutputTokens;

    public long Calls => _counter.Calls;

    public static LlmJudge Create(string endpoint, string deployment, string cacheRoot, string executionName)
    {
#pragma warning disable OPENAI001 // Authentication-policy constructor is marked experimental in the OpenAI SDK.
        // Same chain as the agent: no Visual Studio or PowerShell credentials (they could pick up the developer's own
        // identity on a self-hosted CI agent), and up to a minute for a slow Azure CLI; tokens are cached.
        var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            CredentialProcessTimeout = TimeSpan.FromSeconds(60),
            ExcludeVisualStudioCredential = true,
            ExcludeVisualStudioCodeCredential = true,
            ExcludeAzurePowerShellCredential = true,
            ExcludeAzureDeveloperCliCredential = true,
            ExcludeInteractiveBrowserCredential = true,
        });
        var policy = new BearerTokenPolicy(new CachedTokenCredential(credential), "https://cognitiveservices.azure.com/.default");
        var openAi = new OpenAIClient(policy, new OpenAIClientOptions { Endpoint = new Uri(endpoint.TrimEnd('/') + "/openai/v1/") });
#pragma warning restore OPENAI001

        // The counter sits inside the response cache, so it counts only calls that are actually billed.
        var counter = new TokenCountingChatClient(openAi.GetChatClient(deployment).AsIChatClient());
        var reporting = DiskBasedReportingConfiguration.Create(
            storageRootPath: cacheRoot,
            evaluators: [new GroundednessEvaluator(), new CompletenessEvaluator()],
            chatConfiguration: new ChatConfiguration(counter),
            enableResponseCaching: true,
            executionName: executionName);
        return new LlmJudge(reporting, counter, deployment);
    }

    public async Task<JudgeScores> ScoreAsync(
        string scenarioId,
        string question,
        string answer,
        string groundingContext,
        string groundTruth,
        CancellationToken cancellationToken)
    {
        await using var run = await _reporting.CreateScenarioRunAsync(scenarioId, cancellationToken: cancellationToken);
        var result = await run.EvaluateAsync(
            [new ChatMessage(ChatRole.User, question)],
            new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)),
            additionalContext: [new GroundednessEvaluatorContext(groundingContext), new CompletenessEvaluatorContext(groundTruth)],
            cancellationToken: cancellationToken);

        var groundedness = result.Get<NumericMetric>(GroundednessEvaluator.GroundednessMetricName);
        var completeness = result.Get<NumericMetric>(CompletenessEvaluator.CompletenessMetricName);
        return new JudgeScores(groundedness.Value, groundedness.Reason, completeness.Value, completeness.Reason);
    }
}

/// <summary>
/// Reuses Entra access tokens until five minutes before expiry. Deliberately a copy of the agent's class:
/// the gate must not depend on the agent's code. Without it, every judge call started an <c>az</c> process.
/// </summary>
public sealed class CachedTokenCredential(Azure.Core.TokenCredential inner) : Azure.Core.TokenCredential
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Azure.Core.AccessToken> _tokens = new();
    private readonly SemaphoreSlim _refresh = new(1, 1);

    public override Azure.Core.AccessToken GetToken(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        var key = string.Join(' ', requestContext.Scopes) + "|" + requestContext.TenantId;
        if (_tokens.TryGetValue(key, out var token) && token.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return token;
        }

        _refresh.Wait(cancellationToken);
        try
        {
            if (!_tokens.TryGetValue(key, out token) || token.ExpiresOn <= DateTimeOffset.UtcNow.AddMinutes(5))
            {
                token = inner.GetToken(requestContext, cancellationToken);
                _tokens[key] = token;
            }

            return token;
        }
        finally
        {
            _refresh.Release();
        }
    }

    public override async ValueTask<Azure.Core.AccessToken> GetTokenAsync(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        var key = string.Join(' ', requestContext.Scopes) + "|" + requestContext.TenantId;
        if (_tokens.TryGetValue(key, out var token) && token.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return token;
        }

        await _refresh.WaitAsync(cancellationToken);
        try
        {
            if (!_tokens.TryGetValue(key, out token) || token.ExpiresOn <= DateTimeOffset.UtcNow.AddMinutes(5))
            {
                token = await inner.GetTokenAsync(requestContext, cancellationToken);
                _tokens[key] = token;
            }

            return token;
        }
        finally
        {
            _refresh.Release();
        }
    }
}

/// <summary>Counts the tokens of judge calls that reach the model, for the run-cost line in the evidence pack.</summary>
public sealed class TokenCountingChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    private long _input;
    private long _output;
    private long _calls;

    public long InputTokens => Interlocked.Read(ref _input);

    public long OutputTokens => Interlocked.Read(ref _output);

    public long Calls => Interlocked.Read(ref _calls);

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        Interlocked.Add(ref _input, response.Usage?.InputTokenCount ?? 0);
        Interlocked.Add(ref _output, response.Usage?.OutputTokenCount ?? 0);
        Interlocked.Increment(ref _calls);
        return response;
    }
}
