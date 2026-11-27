using System.Collections.Concurrent;
using Azure.Core;

namespace ClaimsAssist.Core.Ai;

public static class AgentCredential
{
    /// <summary>
    /// The credential chain for the agent: managed identity in Azure, the Azure CLI login on a developer machine
    /// and in Azure Pipelines (federated). Visual Studio and Azure PowerShell are excluded on purpose: on a
    /// self-hosted CI agent they could pick up the developer's own identity instead of the least-privileged
    /// CI identity. The CLI gets up to a minute, because it timed out on a busy machine; tokens are then cached.
    /// </summary>
    public static Azure.Core.TokenCredential Create() => new CachedTokenCredential(new Azure.Identity.DefaultAzureCredential(
        new Azure.Identity.DefaultAzureCredentialOptions
        {
            CredentialProcessTimeout = TimeSpan.FromSeconds(60),
            ExcludeVisualStudioCredential = true,
            ExcludeVisualStudioCodeCredential = true,
            ExcludeAzurePowerShellCredential = true,
            ExcludeAzureDeveloperCliCredential = true,
            ExcludeInteractiveBrowserCredential = true,
        }));
}

/// <summary>
/// Reuses access tokens until five minutes before they expire. The OpenAI SDK's bearer-token policy asks the
/// credential on every request, and the Azure CLI credential starts an <c>az</c> process each time: on a busy
/// machine that added about ten seconds to every turn (found by PilotGate's latency check in CI run 4).
/// </summary>
public sealed class CachedTokenCredential(TokenCredential inner) : TokenCredential
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, AccessToken> _tokens = new();
    private readonly SemaphoreSlim _refresh = new(1, 1);

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        var key = Key(requestContext);
        if (TryGetFresh(key, out var token))
        {
            return token;
        }

        _refresh.Wait(cancellationToken);
        try
        {
            if (TryGetFresh(key, out token))
            {
                return token;
            }

            token = inner.GetToken(requestContext, cancellationToken);
            _tokens[key] = token;
            return token;
        }
        finally
        {
            _refresh.Release();
        }
    }

    public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        var key = Key(requestContext);
        if (TryGetFresh(key, out var token))
        {
            return token;
        }

        await _refresh.WaitAsync(cancellationToken);
        try
        {
            if (TryGetFresh(key, out token))
            {
                return token;
            }

            token = await inner.GetTokenAsync(requestContext, cancellationToken);
            _tokens[key] = token;
            return token;
        }
        finally
        {
            _refresh.Release();
        }
    }

    private bool TryGetFresh(string key, out AccessToken token) =>
        _tokens.TryGetValue(key, out token) && token.ExpiresOn - RefreshMargin > DateTimeOffset.UtcNow;

    private static string Key(TokenRequestContext context) =>
        $"{string.Join(' ', context.Scopes)}|{context.TenantId}|{context.Claims}";
}
