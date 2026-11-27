using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ClaimsAssist.Core.Security;

namespace ClaimsAssist.Core.Claims;

public sealed record ClaimAttachment(string Name, string Source, string Text);

public sealed record ClaimRecord(
    string ClaimId,
    string LineOfBusiness,
    string Product,
    string Policyholder,
    string DateOfBirth,
    string Iban,
    string Email,
    string Phone,
    string LossDate,
    string LossType,
    decimal ReserveEur,
    decimal SumInsuredEur,
    string Status,
    string? Diagnosis,
    IReadOnlyList<ClaimAttachment> Attachments);

/// <summary>Claim records with line-of-business access control and data minimisation for the model.</summary>
public sealed class ClaimStore
{
    // Which groups may open a claim of a given line of business.
    private static readonly Dictionary<string, string[]> ReadersByLine = new(StringComparer.OrdinalIgnoreCase)
    {
        ["property"] = ["claims-property", "siu"],
        ["motor"] = ["claims-motor", "siu"],
        ["health"] = ["claims-health"],
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<string, ClaimRecord> _claims;

    private ClaimStore(Dictionary<string, ClaimRecord> claims) => _claims = claims;

    public IReadOnlyCollection<ClaimRecord> All => _claims.Values;

    public static ClaimStore Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var claims = doc.RootElement.GetProperty("claims").Deserialize<List<ClaimRecord>>(Json) ?? [];
        return new ClaimStore(claims.ToDictionary(c => c.ClaimId, StringComparer.OrdinalIgnoreCase));
    }

    public ClaimRecord? Find(string claimId) => _claims.GetValueOrDefault(claimId.Trim());

    public static bool CanOpen(UserContext user, ClaimRecord claim) =>
        ReadersByLine.TryGetValue(claim.LineOfBusiness, out var groups) && groups.Any(user.IsIn);

    public static string IbanTail(string iban) => iban.Replace(" ", string.Empty)[^4..];

    /// <summary>
    /// Renders a claim for the model with data minimisation: no date of birth, contact details or full IBAN,
    /// and the diagnosis only for the health team. External attachments are sanitized and quarantined;
    /// <paramref name="removedSegments"/> reports how many segments were removed.
    /// </summary>
    public static string RenderForModel(ClaimRecord claim, UserContext user, out int removedSegments)
    {
        removedSegments = 0;
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine($"<claim_file id=\"{claim.ClaimId}\">");
        sb.AppendLine($"Line of business: {claim.LineOfBusiness}; product: {claim.Product}; status: {claim.Status}");
        sb.AppendLine($"Policyholder: {claim.Policyholder}");
        sb.AppendLine($"Loss date: {claim.LossDate}; loss: {claim.LossType}");
        sb.AppendLine(string.Create(c, $"Reserve: {claim.ReserveEur:N0} EUR; sum insured: {claim.SumInsuredEur:N0} EUR"));
        sb.AppendLine($"Payout account: IBAN on file ending {IbanTail(claim.Iban)}");
        if (claim.Diagnosis is not null)
        {
            sb.AppendLine(user.IsIn("claims-health")
                ? $"Diagnosis (special category data, GDPR Art. 9): {claim.Diagnosis}"
                : "Diagnosis: withheld (health claims team only)");
        }

        foreach (var attachment in claim.Attachments)
        {
            var text = attachment.Text;
            if (attachment.Source.StartsWith("external", StringComparison.OrdinalIgnoreCase))
            {
                var sanitized = Guardrails.UntrustedContentGuard.Sanitize(text);
                var quarantined = Guardrails.UntrustedContentGuard.Quarantine(sanitized.Text);
                removedSegments += sanitized.Removed + quarantined.Removed;
                text = quarantined.Text;
            }

            sb.AppendLine($"<attachment name=\"{attachment.Name}\" source=\"{attachment.Source}\">{text}</attachment>");
        }

        sb.Append("</claim_file>");
        return sb.ToString();
    }
}

public sealed record SettlementOutcome(string Outcome, string Message);

public sealed record SettlementInstruction(
    string ClaimId,
    decimal AmountEur,
    string PayeeIban,
    string Rationale,
    string RequestedBy,
    DateTimeOffset At);

/// <summary>
/// The only write action. Deterministic guards run even after a human approved the call,
/// and the payee is never a parameter: money can only go to the IBAN on file.
/// </summary>
public sealed class SettlementService(ClaimStore claims)
{
    private readonly ConcurrentQueue<SettlementInstruction> _queue = new();

    public IReadOnlyCollection<SettlementInstruction> Queued => _queue.ToArray();

    public SettlementOutcome Propose(string claimId, decimal amountEur, string rationale, UserContext user)
    {
        var c = CultureInfo.InvariantCulture;
        var claim = claims.Find(claimId);
        if (claim is null)
        {
            return Rejected($"Claim {claimId} not found.");
        }

        if (!ClaimStore.CanOpen(user, claim))
        {
            return Rejected($"You are not authorised for claim {claim.ClaimId}.");
        }

        if (amountEur <= 0)
        {
            return Rejected("The amount must be positive.");
        }

        if (amountEur > claim.SumInsuredEur)
        {
            return Rejected(string.Create(c, $"The amount exceeds the sum insured of {claim.SumInsuredEur:N0} EUR."));
        }

        if (amountEur > user.SettlementAuthorityEur)
        {
            return Rejected(string.Create(c, $"The amount exceeds your settlement authority of {user.SettlementAuthorityEur:N0} EUR. Route the claim to your team lead."));
        }

        _queue.Enqueue(new SettlementInstruction(claim.ClaimId, amountEur, claim.Iban, rationale, user.UserId, DateTimeOffset.UtcNow));
        return new SettlementOutcome("QUEUED", string.Create(c, $"Settlement of {amountEur:N2} EUR queued for payout to the IBAN on file ending {ClaimStore.IbanTail(claim.Iban)}."));
    }

    private static SettlementOutcome Rejected(string message) => new("REJECTED", message);
}
