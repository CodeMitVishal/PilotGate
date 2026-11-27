using System.Net;

namespace ClaimsAssist.Core.Security;

/// <summary>
/// Development identities (the X-Dev-Persona header) carry no proof: whoever can reach the API can claim any persona,
/// the fraud investigator and the team lead included. They are therefore allowed only while every address the server
/// listens on is a loopback address, so only processes on the same machine can call it. The API checks the addresses
/// the server actually bound (not the configuration), and answers 503 until the check has passed.
/// </summary>
public static class DevIdentityBinding
{
    /// <summary>The bound addresses that are not loopback, such as http://[::]:8080, http://0.0.0.0:80 or a host name.</summary>
    public static IReadOnlyList<string> NonLoopback(IEnumerable<string> addresses) =>
        addresses.Where(a => !IsLoopback(a)).ToList();

    private static bool IsLoopback(string address)
    {
        if (!Uri.TryCreate(address.Replace("://+", "://wildcard.invalid", StringComparison.Ordinal)
                .Replace("://*", "://wildcard.invalid", StringComparison.Ordinal), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip);
    }
}
