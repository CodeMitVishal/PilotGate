using System.Globalization;
using System.Text.RegularExpressions;

namespace PilotGate;

/// <summary>
/// The gate's own check of figure grounding: every number, date and claim id in a shown answer must appear in a cited
/// knowledge chunk, a tool result of the turn (from the trace) or the question. It is an independent implementation of
/// the agent's rule, on purpose: the gate never shares code with the agent it judges (AGENTS.md), so a defect in the
/// agent's check cannot hide itself here. The shared corpus tests/grounding-corpus.v1.json keeps both on one rule.
/// Needs the trace: for a black-box target it is not measurable.
/// </summary>
public static partial class FigureGrounding
{
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

    public sealed record Unsupported(string Sentence, IReadOnlyList<string> Missing);

    public sealed record Outcome(int Statements, IReadOnlyList<Unsupported> Unsupported);

    /// <summary>Checks one answered turn against its trace.</summary>
    public static Outcome Check(AgentTurn turn, string question) =>
        Check(turn.Answer, SourcesFrom(turn.Trace.GroundingContext, turn.Citations.Select(c => c.Ref)), question, turn.Trace.OpenedClaimIds);

    /// <summary>Exactly what the model saw: each cited document whole (attributes such as title and section, and its
    /// text), plus every tool result that is not a search (claim files, settlement outcomes).</summary>
    public static IReadOnlyList<string> SourcesFrom(IEnumerable<string> toolResults, IEnumerable<string> citedRefs)
    {
        var cited = citedRefs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sources = new List<string>();
        foreach (var item in toolResults)
        {
            var documents = Document().Matches(item);
            if (documents.Count == 0)
            {
                sources.Add(item);
                continue;
            }

            sources.AddRange(documents.Where(d => cited.Contains(d.Groups[1].Value)).Select(d => d.Value));
        }

        return sources;
    }

    public static Outcome Check(string answer, IEnumerable<string> sources, string question, IEnumerable<string> openedClaimIds)
    {
        var support = Atoms(question, isSource: true);
        foreach (var source in sources)
        {
            support.UnionWith(Atoms(source, isSource: true));
        }

        support.UnionWith(openedClaimIds.Select(id => "claim:" + id.ToUpperInvariant()));
        var statements = 0;
        var unsupported = new List<Unsupported>();
        foreach (var sentence in Sentences(answer))
        {
            var atoms = Atoms(sentence, isSource: false);
            if (atoms.Count == 0)
            {
                continue;
            }

            statements++;
            var missing = atoms.Where(a => !support.Contains(a)).Order(StringComparer.Ordinal).ToList();
            if (missing.Count > 0)
            {
                unsupported.Add(new Unsupported(sentence, missing));
            }
        }

        return new Outcome(statements, unsupported);
    }

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
            atoms.Add(m.Groups[1].Success
                ? Date(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value)
                : Date(m.Groups[6].Value, m.Groups[5].Value, m.Groups[4].Value));
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
            if (Value(m.Value) is { } value)
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

    private static IEnumerable<string> Sentences(string answer)
    {
        var parts = SentenceSplit().Split(answer);
        for (var i = 0; i < parts.Length; i += 2)
        {
            if (!string.IsNullOrWhiteSpace(parts[i]))
            {
                yield return parts[i].Trim();
            }
        }
    }

    private static string? Value(string token)
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
        string[] english = ["january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december"];
        string[] german = ["januar", "februar", "märz", "april", "mai", "juni", "juli", "august", "september", "oktober", "november", "dezember"];
        var months = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < 12; i++)
        {
            months[english[i]] = i + 1;
            months[german[i]] = i + 1;
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

    [GeneratedRegex(@"\b(?:one|a single|eins?|eine[mnrs]?)\s+(?:calendar\s+|full\s+|ganze[mnrs]?\s+)?(?:months?|years?|weeks?|days?|hours?|monate?n?s?|jahre?n?s?|wochen?|tage?n?s?|stunden?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OneWithUnit();
}
