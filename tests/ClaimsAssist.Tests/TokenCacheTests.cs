using Azure.Core;
using ClaimsAssist.Core.Ai;

namespace ClaimsAssist.Tests;

public class TokenCacheTests
{
    private sealed class CountingCredential(TimeSpan lifetime) : TokenCredential
    {
        public int Calls { get; private set; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Calls++;
            return new AccessToken($"token-{Calls}", DateTimeOffset.UtcNow.Add(lifetime));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private static readonly TokenRequestContext Cognitive = new(["https://cognitiveservices.azure.com/.default"]);

    [Fact]
    public async Task A_valid_token_is_reused_instead_of_asking_the_slow_credential_again()
    {
        var inner = new CountingCredential(TimeSpan.FromHours(1));
        var cached = new CachedTokenCredential(inner);

        var first = await cached.GetTokenAsync(Cognitive, CancellationToken.None);
        var second = await cached.GetTokenAsync(Cognitive, CancellationToken.None);
        var third = cached.GetToken(Cognitive, CancellationToken.None);

        Assert.Equal(1, inner.Calls);
        Assert.Equal(first.Token, second.Token);
        Assert.Equal(first.Token, third.Token);
    }

    [Fact]
    public async Task A_token_close_to_expiry_is_refreshed()
    {
        var inner = new CountingCredential(TimeSpan.FromMinutes(3));
        var cached = new CachedTokenCredential(inner);

        await cached.GetTokenAsync(Cognitive, CancellationToken.None);
        await cached.GetTokenAsync(Cognitive, CancellationToken.None);

        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task Different_scopes_get_their_own_tokens()
    {
        var inner = new CountingCredential(TimeSpan.FromHours(1));
        var cached = new CachedTokenCredential(inner);

        await cached.GetTokenAsync(Cognitive, CancellationToken.None);
        await cached.GetTokenAsync(new TokenRequestContext(["https://management.azure.com/.default"]), CancellationToken.None);

        Assert.Equal(2, inner.Calls);
    }
}
