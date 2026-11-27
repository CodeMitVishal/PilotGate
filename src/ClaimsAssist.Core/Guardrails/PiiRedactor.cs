using System.Text;
using System.Text.RegularExpressions;
using ClaimsAssist.Core.Claims;

namespace ClaimsAssist.Core.Guardrails;

/// <summary>
/// Pattern- and dictionary-based PII redaction. Used twice: on every answer (output guard) and on
/// everything written to the audit log, so customer data never lands in logs (GDPR Art. 5(1)(c)).
/// Patterns run on <see cref="TextNormalizer"/>'s folded view and map back to the raw text, so a zero-width character
/// or a look-alike letter inside a value does not hide it. The shared corpus (dial/schema/guard-corpus.v1.json) pins
/// the results against DIAL Guard; DIAL Guard additionally finds values glued to other text.
/// </summary>
public sealed partial class PiiRedactor
{
    private readonly Regex? _knownNames;

    public PiiRedactor(IEnumerable<string> knownNames)
    {
        var names = knownNames.Select(n => TextNormalizer.Folded(n).Text).Where(n => n.Length > 2).Distinct()
            .Select(Regex.Escape).ToList();
        if (names.Count > 0)
        {
            _knownNames = new Regex(@"\b(?:" + string.Join("|", names) + @")\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }
    }

    public static PiiRedactor ForClaims(ClaimStore claims) =>
        new(claims.All.Select(c => c.Policyholder));

    /// <summary>Redacts IBANs, e-mail addresses, phone numbers and German-format dates; optionally known customer names.</summary>
    public (string Text, int Redactions) Redact(string? text, bool includeNames)
    {
        if (string.IsNullOrEmpty(text))
        {
            return (text ?? string.Empty, 0);
        }

        var count = 0;
        string Apply(Regex regex, string input, Func<string, string> replacement)
        {
            var view = TextNormalizer.Folded(input);
            var sb = new StringBuilder(input.Length);
            var pos = 0;
            foreach (Match m in regex.Matches(view.Text))
            {
                if (m.Length == 0)
                {
                    continue;
                }

                var start = view.RawStart(m.Index);
                var end = view.RawEnd(m.Index + m.Length);
                if (start < pos)
                {
                    continue;
                }

                sb.Append(input, pos, start - pos).Append(replacement(m.Value));
                count++;
                pos = end;
            }

            return sb.Append(input, pos, input.Length - pos).ToString();
        }

        var result = Apply(Iban(), text, v => "[IBAN ending " + v.Replace(" ", string.Empty)[^4..] + "]");
        result = Apply(Email(), result, _ => "[EMAIL]");
        result = Apply(Phone(), result, _ => "[PHONE]");
        result = Apply(InternationalPhone(), result, _ => "[PHONE]");
        result = Apply(GermanDate(), result, _ => "[DATE]");
        if (includeNames && _knownNames is not null)
        {
            result = Apply(_knownNames, result, _ => "[NAME]");
        }

        return (result, count);
    }

    [GeneratedRegex(@"\b[A-Z]{2}\d{2}(?: ?[A-Z0-9]{4}){3,7}(?: ?[A-Z0-9]{1,3})?\b")]
    private static partial Regex Iban();

    [GeneratedRegex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b")]
    private static partial Regex Email();

    // German numbers: +49, the 0049 dialling prefix, or the national 0. Never inside a word or a digit run.
    [GeneratedRegex(@"(?<![\w+])(?:\+49 ?|0049 ?|0)[1-9]\d{1,4}[ /-]?\d{3,9}\b")]
    private static partial Regex Phone();

    // Other international numbers, with + or the 00 dialling prefix (the same rule as DIAL Guard's phone_intl); never
    // inside a word or a digit run. Like the guard, it also redacts a decimal such as 0.0012345678 (safe side).
    [GeneratedRegex(@"(?<![\w+])(?:\+|00)[1-9](?:[ .-]?\d){6,14}\b")]
    private static partial Regex InternationalPhone();

    [GeneratedRegex(@"\b\d{2}\.\d{2}\.\d{4}\b")]
    private static partial Regex GermanDate();
}
