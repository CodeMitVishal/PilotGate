using System.ClientModel.Primitives;
using Azure.Core;
using OpenAI;

namespace ClaimsAssist.Core.Ai;

public static class AzureOpenAIFactory
{
    public const string CognitiveServicesScope = "https://cognitiveservices.azure.com/.default";

    /// <summary>
    /// OpenAI SDK client for the Azure OpenAI v1 endpoint, authenticated with a Microsoft Entra ID token.
    /// The account has local (key) auth disabled, so there is no API key anywhere: locally the token comes
    /// from the Azure CLI login, in GitHub Actions from OIDC federation, and in Azure from a managed identity.
    /// </summary>
    /// <param name="networkTimeout">How long one model request may hang before it is abandoned and retried (the SDK
    /// retries timeouts, 429 and 5xx with backoff). The SDK default is 100 seconds; gate run 20261127-190104 had a
    /// 104-second answer, consistent with one hung request waiting out that default.</param>
    public static OpenAIClient Create(string endpoint, TokenCredential credential, TimeSpan? networkTimeout = null)
    {
#pragma warning disable OPENAI001 // Authentication-policy constructor is marked experimental in the OpenAI SDK.
        var policy = new BearerTokenPolicy(credential, CognitiveServicesScope);
        var options = new OpenAIClientOptions { Endpoint = new Uri(endpoint.TrimEnd('/') + "/openai/v1/") };
        if (networkTimeout is { } timeout)
        {
            options.NetworkTimeout = timeout;
        }

        return new OpenAIClient(policy, options);
#pragma warning restore OPENAI001
    }
}

public sealed class ClaimsAssistSettings
{
    public string Endpoint { get; set; } = string.Empty;

    public string ChatDeployment { get; set; } = "gpt-4.1-mini";

    public string EmbeddingDeployment { get; set; } = "text-embedding-3-small";

    /// <summary>Seconds one model request may hang before it is abandoned and retried (5 to 120). Answers normally take
    /// 1 to 4 seconds; the SDK default of 100 seconds let one hung connection break the latency budget.</summary>
    public int ModelTimeoutSeconds { get; set; } = 20;

    public string DataPath { get; set; } = "data";

    public string AuditLogPath { get; set; } = "logs/claimsassist-audit.jsonl";

    /// <summary>The public development key. Dial mode refuses it: anyone with the repository could recompute the
    /// audit pseudonyms and so re-identify the handlers.</summary>
    public const string DevelopmentPseudonymKey = "dev-only-not-a-secret";

    public const int MinimumPseudonymKeyLength = 32;

    public string PseudonymKey { get; set; } = DevelopmentPseudonymKey;

    /// <summary>File (append to <see cref="AuditLogPath"/>) or Stdout (one line per record on standard output, which a
    /// container platform ships to its log workspace; the replica's disk is lost on every restart).</summary>
    public string AuditSink { get; set; } = "File";

    /// <summary>Dial (EPAM DIAL application; identity from DIAL Core) or DevPersonas (development HTTP API with the
    /// X-Dev-Persona header, allowed only on loopback addresses). One process never offers both. The default is the
    /// mode that proves identity: a missing setting must never open the development API.</summary>
    public string AuthMode { get; set; } = "Dial";

    public ClaimsAssist.Core.Dial.DialSettings Dial { get; set; } = new();

    /// <summary>Hosts (and their subdomains) that answers may link to; every other link is removed. Empty allows none.
    /// The same list configures DIAL Guard in Azure (infra/dial/main.bicep), so both enforce one policy.</summary>
    public List<string> AllowedLinkHosts { get; set; } = [];

    /// <summary>Groups that may decide on another handler's pending approval (four-eyes escalation). Empty means only
    /// the requesting handler decides.</summary>
    public List<string> ApproverGroups { get; set; } = [];

    /// <summary>Where pending approvals wait: Memory (this process only; development and a single replica) or Redis
    /// (shared by every replica and kept across restarts; required for more than one replica).</summary>
    public string ApprovalStore { get; set; } = "Memory";

    /// <summary>Minutes a pending approval waits before it expires (1 to 1440). A pending approval holds claim data, so
    /// this is also its retention limit.</summary>
    public int ApprovalTtlMinutes { get; set; } = 30;

    /// <summary>Most approvals the in-memory store holds (1 to 1,000,000). When full, a new proposal is refused; a live
    /// approval is never evicted to make room.</summary>
    public int ApprovalCapacity { get; set; } = 10_000;

    public ClaimsAssist.Core.Agent.RedisSettings Redis { get; set; } = new();

    public bool AuditToStdout => string.Equals(AuditSink, "Stdout", StringComparison.OrdinalIgnoreCase);

    public bool RedisApprovals => string.Equals(ApprovalStore, "Redis", StringComparison.OrdinalIgnoreCase);

    /// <summary>Settings this process must not start with; empty when it may start. Dial mode is the production
    /// shape, so it needs a real pseudonymisation key.</summary>
    public IReadOnlyList<string> StartupProblems()
    {
        var problems = new List<string>();
        if (!AuditToStdout && !string.Equals(AuditSink, "File", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add("ClaimsAssist:AuditSink must be File or Stdout.");
        }

        if (ModelTimeoutSeconds is < 5 or > 120)
        {
            problems.Add("ClaimsAssist:ModelTimeoutSeconds must be between 5 and 120.");
        }

        foreach (var host in AllowedLinkHosts.Where(h => !IsHostName(h)))
        {
            problems.Add($"ClaimsAssist:AllowedLinkHosts entry \"{host}\" is not a lower-case host name (no scheme, port or path).");
        }

        if (!RedisApprovals && !string.Equals(ApprovalStore, "Memory", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add("ClaimsAssist:ApprovalStore must be Memory or Redis.");
        }

        if (ApprovalTtlMinutes is < 1 or > 1440)
        {
            problems.Add("ClaimsAssist:ApprovalTtlMinutes must be between 1 and 1440.");
        }

        if (ApprovalCapacity is < 1 or > 1_000_000)
        {
            problems.Add("ClaimsAssist:ApprovalCapacity must be between 1 and 1000000.");
        }

        if (RedisApprovals)
        {
            problems.AddRange(Redis.Problems());
        }

        var dial = string.Equals(AuthMode, "Dial", StringComparison.OrdinalIgnoreCase);
        if (dial && (PseudonymKey == DevelopmentPseudonymKey || PseudonymKey.Trim().Length < MinimumPseudonymKeyLength))
        {
            problems.Add(
                $"Dial mode needs ClaimsAssist:PseudonymKey (environment variable ClaimsAssist__PseudonymKey): a random secret of at least {MinimumPseudonymKeyLength} characters. The development key is public, so audit pseudonyms made with it could be reversed.");
        }

        return problems;
    }

    private static bool IsHostName(string host) =>
        host.Length is > 0 and <= 253
        && host == host.Trim().ToLowerInvariant()
        && !host.StartsWith('.') && !host.EndsWith('.')
        && Uri.CheckHostName(host) == UriHostNameType.Dns
        && host.Contains('.');
}
