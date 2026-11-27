using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaimsAssist.Core.Guardrails;

/// <summary>
/// Figure grounding, enforced by code. Every number, date and claim id shown to a handler must appear in the answer's
/// sources: the knowledge chunks it cites, the tool results of the turn (claim files, settlement outcomes), or the
/// handler's own question. A sentence with a figure that cannot be found is withheld; if nothing is left, the assistant
/// says the sources do not confirm the answer. Because this runs after the model, it holds whatever the model does.
///
/// What it does not do: judge meaning. A wrong figure that also appears in the sources passes (wind force 8 instead
/// of 7 where the source states 8), and statements without figures are left to the gate's semantic judge.
/// Measured on 129 real answers from five gate runs: 1 of 154 sentences with figures withheld (an inference the
/// source does not state), and 92% of figures changed in a mutation test caught; the rest changed into figures that
/// also appear in the cited sources.
/// Shared cases: tests/grounding-corpus.v1.json, which PilotGate's independent re-check runs too.
/// </summary>
public static partial class GroundingVerifier
{
    public const string NotConfirmed =
        "The approved sources do not confirm the figures needed to answer this. Check the source documents or ask your team lead.";

    private static readonly string[] EnglishMonths =
        ["january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december"];

    private static readonly string[] GermanMonths =
        ["januar", "februar", "märz", "april", "mai", "juni", "juli", "august", "september", "oktober", "november", "dezember"];

    private static readonly Dictionary<string, int> Months = BuildMonths();

    private static readonly Dictionary<string, int> NumberWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = 0, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8,
        ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["fourteen"] = 14, ["fifteen"] = 15, ["twenty"] = 20,
        ["thirty"] = 30, ["zwei"] = 2, ["drei"] = 3, ["vier"] = 4, ["fünf"] = 5, ["sechs"] = 6, ["sieben"] = 7, ["acht"] = 8,
        ["neun"] = 9, ["zehn"] = 10, ["zwölf"] = 12, ["vierzehn"] = 14, ["fünfzehn"] = 15, ["zwanzig"] = 20, ["dreißig"] = 30,
    };

    private static readonly Regex DateText = BuildDateText();
    private static readonly Regex NumberWord = new(
        @"\b(" + string.Join('|', NumberWords.Keys.OrderByDescending(k => k.Length)) + @")\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The outcome of a check: the text that may be shown, and what was withheld.</summary>
    public sealed record Result(string Text, int Statements, int Withheld, IReadOnlyList<string> Missing);

    /// <summary>The answer a handler sees after the check.</summary>
    public sealed record Shown(string Answer, bool AnswerFound, Result Check);

    /// <summary>Checks the answer and applies the policy: withheld sentences disappear, and an answer with nothing
    /// verifiable left becomes <see cref="NotConfirmed"/> with answerFound false.</summary>
    public static Shown ForHandler(string answer, bool answerFound, IEnumerable<string> sources, string question, IEnumerable<string> openedClaimIds)
    {
        var check = Verify(answer, sources, question, openedClaimIds);
        if (check.Withheld == 0)
        {
            return new Shown(answer, answerFound, check);
        }

        return string.IsNullOrWhiteSpace(check.Text) ? new Shown(NotConfirmed, false, check) : new Shown(check.Text, answerFound, check);
    }

    /// <summary>The sources an answer may draw figures from, taken from exactly what the model saw in this turn: each
    /// cited knowledge document whole (its title, section and other attributes, and its text), and every tool result
    /// that is not a search (claim files, settlement outcomes). Searched but uncited documents do not count.</summary>
    public static IReadOnlyList<string> SourcesFrom(IEnumerable<string> toolResults, IEnumerable<string> citedRefs)
    {
        var cited = citedRefs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sources = new List<string>();
        foreach (var result in toolResults)
        {
            var documents = Document().Matches(result);
            if (documents.Count == 0)
            {
                sources.Add(result);
                continue;
            }

            sources.AddRange(documents.Where(d => cited.Contains(d.Groups[1].Value)).Select(d => d.Value));
        }

        return sources;
    }

    public static Result Verify(string answer, IEnumerable<string> sources, string question, IEnumerable<string> openedClaimIds)
    {
        var support = Atoms(question, isSource: true);
        foreach (var source in sources)
        {
            support.UnionWith(Atoms(source, isSource: true));
        }

        foreach (var id in openedClaimIds)
        {
            support.Add("claim:" + id.ToUpperInvariant());
        }

        var parts = SentenceSplit().Split(answer);
        var kept = new StringBuilder(answer.Length);
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        int statements = 0, withheld = 0;
        for (var i = 0; i < parts.Length; i += 2)
        {
            var sentence = parts[i];
            var separator = i + 1 < parts.Length ? parts[i + 1] : string.Empty;
            if (string.IsNullOrWhiteSpace(sentence))
            {
                kept.Append(separator);
                continue;
            }

            var atoms = Atoms(sentence, isSource: false);
            if (atoms.Count > 0)
            {
                statements++;
            }

            var unsupported = atoms.Where(a => !support.Contains(a)).ToList();
            if (unsupported.Count > 0)
            {
                withheld++;
                missing.UnionWith(unsupported);
                continue;
            }

            kept.Append(sentence).Append(separator);
        }

        return new Result(kept.ToString().Trim(), statements, withheld, missing.ToList());
    }

    /// <summary>The figures in a text, normalised: "num:5000" (from 5,000, 5.000 or 5000; number words such as "ten"
    /// too), "date:2026-10-02" (from 2026-10-02, 02.10.2026 or 2 October 2026), "claim:CLM-2026-0142". Citations,
    /// redaction markers and chunk references are not figures; list markers count only in sources.</summary>
    public static HashSet<string> Atoms(string text, bool isSource)
    {
        var atoms = new HashSet<string>(StringComparer.Ordinal);
        var t = ChunkReference().Replace(Marker().Replace(Citation().Replace(text, " "), " "), " ");

        foreach (Match m in ClaimId().Matches(t))
        {
            atoms.Add("claim:" + m.Value.ToUpperInvariant());
        }

        t = ClaimId().Replace(t, " ");
        foreach (Match m in DateNumeric().Matches(t))
        {
            var (y, mo, d) = m.Groups[1].Success
                ? (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value)
                : (m.Groups[6].Value, m.Groups[5].Value, m.Groups[4].Value);
            atoms.Add(Date(y, mo, d));
        }

        t = DateNumeric().Replace(t, " ");
        foreach (Match m in DateText.Matches(t))
        {
            atoms.Add(m.Groups[1].Success
                ? Date(m.Groups[3].Value, Months[m.Groups[2].Value.ToLowerInvariant()].ToString(CultureInfo.InvariantCulture), m.Groups[1].Value)
                : Date(m.Groups[6].Value, Months[m.Groups[4].Value.ToLowerInvariant()].ToString(CultureInfo.InvariantCulture), m.Groups[5].Value));
        }

        t = DateText.Replace(t, " ");
        if (!isSource)
        {
            t = ListMarker().Replace(t, " ");
        }

        foreach (Match m in Number().Matches(t))
        {
            if (NumberValue(m.Value) is { } value)
            {
                atoms.Add("num:" + value);
            }
        }

        foreach (Match m in NumberWord.Matches(t))
        {
            atoms.Add("num:" + NumberWords[m.Groups[1].Value].ToString(CultureInfo.InvariantCulture));
        }

        if (OneWithUnit().IsMatch(t))
        {
            atoms.Add("num:1");
        }

        return atoms;
    }

    /// <summary>The sentences of an answer, as the check splits them (for the replay and mutation tests).</summary>
    internal static IReadOnlyList<string> Sentences(string answer)
    {
        var parts = SentenceSplit().Split(answer);
        var sentences = new List<string>();
        for (var i = 0; i < parts.Length; i += 2)
        {
            if (!string.IsNullOrWhiteSpace(parts[i]))
            {
                sentences.Add(parts[i].Trim());
            }
        }

        return sentences;
    }

    /// <summary>The sentence without citations, markers, chunk references and claim ids, and the first number in it
    /// (start, length, normalised value): what the mutation test changes.</summary>
    internal static (string Body, int Start, int Length, decimal Value)? FirstNumber(string sentence)
    {
        var body = ClaimId().Replace(ChunkReference().Replace(Marker().Replace(Citation().Replace(sentence, " "), " "), " "), " ");
        var m = Number().Match(body);
        return m.Success && NumberValue(m.Value) is { } value
            ? (body, m.Index, m.Length, decimal.Parse(value, CultureInfo.InvariantCulture))
            : null;
    }

    private static string? NumberValue(string token)
    {
        var t = token.Replace(' ', ' ');
        string text;
        var grouped = Grouped().Match(t);
        if (grouped.Success
            && grouped.Groups[2].Value.Where(c => !char.IsAsciiDigit(c)).Distinct().Count() == 1
            && (!grouped.Groups[3].Success || !grouped.Groups[2].Value.Contains(grouped.Groups[3].Value[0])))
        {
            text = grouped.Groups[1].Value + new string(grouped.Groups[2].Value.Where(char.IsAsciiDigit).ToArray())
                + (grouped.Groups[3].Success ? "." + grouped.Groups[3].Value[1..] : string.Empty);
        }
        else if (DecimalToken().IsMatch(t))
        {
            text = t.Replace(',', '.');
        }
        else if (IntegerToken().IsMatch(t))
        {
            text = t;
        }
        else
        {
            return null;
        }

        return decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            ? value.ToString("0.############################", CultureInfo.InvariantCulture)
            : null;
    }

    private static string Date(string year, string month, string day) =>
        string.Create(CultureInfo.InvariantCulture, $"date:{int.Parse(year, CultureInfo.InvariantCulture):0000}-{int.Parse(month, CultureInfo.InvariantCulture):00}-{int.Parse(day, CultureInfo.InvariantCulture):00}");

    private static Dictionary<string, int> BuildMonths()
    {
        var months = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < 12; i++)
        {
            months[EnglishMonths[i]] = i + 1;
            months[GermanMonths[i]] = i + 1;
        }

        return months;
    }

    private static Regex BuildDateText()
    {
        var alternatives = string.Join('|', Months.Keys.OrderByDescending(k => k.Length));
        return new Regex(
            $@"\b([0-9]{{1,2}})\.?\s+({alternatives})\s+([0-9]{{4}})\b|\b({alternatives})\s+([0-9]{{1,2}}),?\s+([0-9]{{4}})\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    [GeneratedRegex("<document chunk_id=\"([^\"]+)\"[^>]*>\\n.*?\\n</document>", RegexOptions.Singleline)]
    private static partial Regex Document();

    // A sentence ends at . ! ? followed by space and a capital, digit or bracket, never right after a digit (list
    // markers "1." and dates "2. Oktober"), or at a line break. The separators are kept, so the text reassembles.
    [GeneratedRegex(@"((?<=[^0-9\s][.!?])\s+(?=[A-Z0-9\[(""'])|\n+)")]
    private static partial Regex SentenceSplit();

    [GeneratedRegex(@"\[[A-Z][A-Z0-9-]+(?:#[A-Za-z0-9-]+)?(?:\s*,\s*[A-Z][A-Z0-9-]+(?:#[A-Za-z0-9-]+)?)*\]")]
    private static partial Regex Citation();

    [GeneratedRegex(@"\[(?:EMAIL|PHONE|DATE|NAME|REDACTED|CARD ending [0-9]{4}|IBAN ending [A-Z0-9]{1,4})\]")]
    private static partial Regex Marker();

    [GeneratedRegex(@"\b[A-Z]{2,}(?:-[A-Z0-9]+)+#[a-z0-9-]+\b")]
    private static partial Regex ChunkReference();

    [GeneratedRegex(@"\bCLM-[0-9]{4}-[0-9]{4}\b", RegexOptions.IgnoreCase)]
    private static partial Regex ClaimId();

    [GeneratedRegex(@"\b([0-9]{4})-([0-9]{2})-([0-9]{2})\b|\b([0-9]{1,2})\.([0-9]{1,2})\.([0-9]{4})\b")]
    private static partial Regex DateNumeric();

    [GeneratedRegex(@"(?<![\w.,])[0-9]{1,3}(?:([.,  ])[0-9]{3})(?:\1[0-9]{3})*(?:[.,][0-9]{1,2})?(?!\w)|(?<![\w.,])[0-9]+(?:[.,][0-9]+)?(?!\w)")]
    private static partial Regex Number();

    [GeneratedRegex(@"^([0-9]{1,3})((?:[., ][0-9]{3})+)([.,][0-9]{1,2})?$")]
    private static partial Regex Grouped();

    [GeneratedRegex(@"^[0-9]+[.,][0-9]+$")]
    private static partial Regex DecimalToken();

    [GeneratedRegex(@"^[0-9]+$")]
    private static partial Regex IntegerToken();

    [GeneratedRegex(@"^\s*[0-9]{1,2}[.)]\s", RegexOptions.Multiline)]
    private static partial Regex ListMarker();

    // "one" counts only before a unit: "ein"/"einem" are also the German indefinite article.
    [GeneratedRegex(@"\b(?:one|a single|eins?|eine[mnrs]?)\s+(?:calendar\s+|full\s+|ganze[mnrs]?\s+)?(?:months?|years?|weeks?|days?|hours?|monate?n?s?|jahre?n?s?|wochen?|tage?n?s?|stunden?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OneWithUnit();
}
