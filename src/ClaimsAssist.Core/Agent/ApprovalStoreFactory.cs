using System.Net;
using Azure.Core;
using ClaimsAssist.Core.Ai;
using Microsoft.Azure.StackExchangeRedis;
using StackExchange.Redis;

namespace ClaimsAssist.Core.Agent;

/// <summary>
/// The Redis approval store's connection. Sign-in is Microsoft Entra ID: the app's managed identity on Azure Managed
/// Redis, with access keys disabled. A password is never configured. Without Entra ID, only a Redis on a loopback
/// address is allowed (a local test container).
/// </summary>
public sealed class RedisSettings
{
    /// <summary>StackExchange.Redis configuration without a password, for example "name.region.redis.azure.net:10000"
    /// (Azure Managed Redis; TLS is on by default for Azure hosts) or "127.0.0.1:6379" (a local container).</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Sign in with the app's Microsoft Entra ID identity. False only for a local Redis without authentication.</summary>
    public bool UseEntraId { get; set; } = true;

    /// <summary>Client id of the user-assigned managed identity that may use the approval database, and nothing else
    /// (infra/dial/main.bicep creates it for ClaimsAssist only, so no other app can read or write approvals). Empty: the
    /// app's own credential chain (the Azure CLI login on a developer machine).</summary>
    public string ManagedIdentityClientId { get; set; } = string.Empty;

    /// <summary>Prefix of every key, so several apps or environments can share one Redis.</summary>
    public string KeyPrefix { get; set; } = "claimsassist:";

    public IReadOnlyList<string> Problems()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            return ["ClaimsAssist:Redis:ConnectionString is required when ClaimsAssist:ApprovalStore is Redis."];
        }

        ConfigurationOptions options;
        try
        {
            options = ConfigurationOptions.Parse(ConnectionString);
        }
        catch (ArgumentException ex)
        {
            return [$"ClaimsAssist:Redis:ConnectionString is not a valid Redis configuration ({ex.Message})."];
        }

        var problems = new List<string>();
        if (!string.IsNullOrEmpty(options.Password))
        {
            problems.Add("ClaimsAssist:Redis:ConnectionString must not contain a password: ClaimsAssist signs in to Redis with Microsoft Entra ID, and access keys stay disabled.");
        }

        if (options.EndPoints.Count == 0)
        {
            problems.Add("ClaimsAssist:Redis:ConnectionString names no Redis endpoint.");
        }
        else if (UseEntraId && !options.Ssl)
        {
            problems.Add("ClaimsAssist:Redis needs TLS (ssl=true) with Entra ID sign-in, so tokens never cross the network in plain text.");
        }
        else if (!UseEntraId && !options.EndPoints.All(IsLoopback))
        {
            problems.Add("ClaimsAssist:Redis:UseEntraId may be false only for a Redis on a loopback address (a local test container).");
        }

        if (KeyPrefix.Length is 0 or > 64 || KeyPrefix.Any(char.IsWhiteSpace))
        {
            problems.Add("ClaimsAssist:Redis:KeyPrefix must be 1 to 64 characters without spaces.");
        }

        return problems;
    }

    private static bool IsLoopback(EndPoint endPoint) => endPoint switch
    {
        IPEndPoint ip => IPAddress.IsLoopback(ip.Address),
        DnsEndPoint dns => string.Equals(dns.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(dns.Host, out var address) && IPAddress.IsLoopback(address)),
        _ => false,
    };
}

public static class ApprovalStoreFactory
{
    /// <summary>The configured approval store. Redis connects at startup, so a wrong address or a missing access policy
    /// stops the start instead of failing the first approval; after that, the client reconnects on its own.</summary>
    public static async Task<IApprovalStore> CreateAsync(ClaimsAssistSettings settings, TokenCredential credential, TimeProvider? time = null)
    {
        if (!settings.RedisApprovals)
        {
            return new InMemoryApprovalStore(settings.ApprovalCapacity, time);
        }

        var options = ConfigurationOptions.Parse(settings.Redis.ConnectionString);
        options.AbortOnConnectFail = true;
        if (settings.Redis.UseEntraId && !string.IsNullOrWhiteSpace(settings.Redis.ManagedIdentityClientId))
        {
            await options.ConfigureForAzureWithUserAssignedManagedIdentityAsync(settings.Redis.ManagedIdentityClientId.Trim());
        }
        else if (settings.Redis.UseEntraId)
        {
            await options.ConfigureForAzureWithTokenCredentialAsync(credential);
        }

        var redis = await ConnectionMultiplexer.ConnectAsync(options);
        return new RedisApprovalStore(redis, settings.Redis.KeyPrefix, time);
    }
}
