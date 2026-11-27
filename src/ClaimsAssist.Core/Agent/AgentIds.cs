using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace ClaimsAssist.Core.Agent;

/// <summary>
/// Conversation and approval ids. Letters only, without i, l and o: unambiguous when a handler types
/// "approve apr_..." in a chat, and no PII detector (phone, card, IBAN, date) can ever match part of an id, so
/// DIAL Guard never redacts one. Hex ids did: about 1.8% of them contained a digit run that reads as a phone number.
/// </summary>
public static partial class AgentIds
{
    public const string Alphabet = "abcdefghjkmnpqrstuvwxyz";

    public const int Length = 12;

    public static string New(string prefix) => prefix + "_" + RandomNumberGenerator.GetString(Alphabet, Length);

    public static bool IsApprovalId(string value) => ApprovalId().IsMatch(value);

    [GeneratedRegex("^apr_[abcdefghjkmnpqrstuvwxyz]{12}$")]
    private static partial Regex ApprovalId();
}
