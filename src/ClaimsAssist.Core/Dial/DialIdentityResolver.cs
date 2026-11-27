using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ClaimsAssist.Core.Security;

namespace ClaimsAssist.Core.Dial;

/// <summary>Settings for running ClaimsAssist as an EPAM DIAL application (ClaimsAssist:Dial).</summary>
public sealed class DialSettings
{
    /// <summary>DIAL Core base URL, for example http://localhost:8080. Identity is looked up here and nowhere else.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>The DIAL deployment id ClaimsAssist answers to.</summary>
    public string DeploymentId { get; set; } = "claimsassist";

    /// <summary>DIAL role -> persona id (data/personas.json). Roles not in the map get no access.</summary>
    public Dictionary<string, string> RolePersonas { get; set; } = new(StringComparer.Ordinal);
}

public enum DialIdentityStatus
{
    Ok,
    Unauthorized,
    Forbidden,
    Unavailable,
}

public sealed record DialIdentityResult(DialIdentityStatus Status, UserContext? User, string Reason);

/// <summary>
/// Who is calling: asks DIAL Core (<c>GET /v1/user/info</c>) with the per-request key DIAL sent, and maps the caller's
/// DIAL role to a persona. Client headers are never consulted: the DIAL spike showed that DIAL Core forwards client
/// headers to applications unchanged, including a spoofed X-DIAL-Deployment-Id (dial/spike/README.md, fact 19).
/// </summary>
public sealed class DialIdentityResolver(HttpClient http, DialSettings settings, PersonaDirectory personas)
{
    public async Task<DialIdentityResult> ResolveAsync(string? apiKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new(DialIdentityStatus.Unauthorized, null, "The request carries no DIAL key.");
        }

        UserInfo? info;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(settings.Url.TrimEnd('/') + "/"), "v1/user/info"));
            request.Headers.TryAddWithoutValidation("Api-Key", apiKey);
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new(DialIdentityStatus.Unauthorized, null, "DIAL did not accept the key.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new(DialIdentityStatus.Unavailable, null, $"DIAL user info answered {(int)response.StatusCode}.");
            }

            info = await response.Content.ReadFromJsonAsync<UserInfo>(cancellationToken);
        }
        // A timeout or a broken answer means DIAL is unavailable; a request the client cancelled is not answered at all.
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
                                   && !cancellationToken.IsCancellationRequested)
        {
            return new(DialIdentityStatus.Unavailable, null, "DIAL user info could not be read.");
        }

        var mapped = (info?.Roles ?? [])
            .Where(role => settings.RolePersonas.ContainsKey(role))
            .Select(role => settings.RolePersonas[role])
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (mapped.Count == 0)
        {
            return new(DialIdentityStatus.Forbidden, null, "This DIAL role has no access to ClaimsAssist.");
        }

        if (mapped.Count > 1)
        {
            return new(DialIdentityStatus.Forbidden, null, "This DIAL identity maps to more than one ClaimsAssist persona.");
        }

        var user = personas.Find(mapped[0]);
        return user is null
            ? new(DialIdentityStatus.Forbidden, null, "The persona configured for this DIAL role does not exist.")
            : new(DialIdentityStatus.Ok, user, string.Empty);
    }

    private sealed class UserInfo
    {
        [JsonPropertyName("roles")]
        public List<string>? Roles { get; set; }

        [JsonPropertyName("project")]
        public string? Project { get; set; }
    }
}
