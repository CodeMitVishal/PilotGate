using System.Text;
using System.Text.RegularExpressions;
using ClaimsAssist.Core.Knowledge;

namespace ClaimsAssist.Core.Guardrails;

/// <summary>
/// Deterministic layers against indirect prompt injection, added after the v1 gate run showed a hidden
/// HTML comment in a broker FAQ and a note in a contractor invoice steering the model.
/// 1. Sanitize: strip content a human reader never sees but a model does (HTML comments, invisible characters).
/// 2. Quarantine: remove passages that address an AI system with instructions; a whole-text pass catches an
///    instruction split across lines.
/// 3. Links: remove answer sentences that carry links to hosts outside an allow-list.
/// Classification runs on <see cref="TextNormalizer"/>'s folded view, so look-alike letters, full-width forms and
/// zero-width characters do not hide an instruction. DIAL Guard (dial/guard) implements the same rules; the shared
/// corpus dial/schema/guard-corpus.v1.json pins both (tests: GuardCorpusTests).
/// Production adds Azure AI Content Safety Prompt Shields (document attack detection) in front of the model.
/// </summary>
public static partial class UntrustedContentGuard
{
    public const string Placeholder = "[removed: text addressed to an AI assistant]";

    /// <summary>The agent's own tools: naming one in untrusted text marks a planted command. A fact of the code, not a
    /// policy. The link allow-list, by contrast, is configuration (ClaimsAssist:AllowedLinkHosts) and passed in.</summary>
    public static readonly IReadOnlyList<string> ToolNames = ["propose_settlement", "get_claim", "search_knowledge"];

    public sealed record Cleaned(string Text, int Removed);

    public static Cleaned Sanitize(string text)
    {
        var removed = 0;
        var result = HtmlComment().Replace(text, _ =>
        {
            removed++;
            return string.Empty;
        });
        var sb = new StringBuilder(result.Length);
        foreach (var rune in result.EnumerateRunes())
        {
            if (TextNormalizer.Invisible.Contains(rune.Value))
            {
                removed++;
                continue;
            }

            sb.Append(rune.ToString());
        }

        return new Cleaned(sb.ToString().Trim(), removed);
    }

    public static Cleaned Quarantine(string text)
    {
        var removed = 0;
        string Check(Match m)
        {
            if (!IsInstruction(m.Value))
            {
                return m.Value;
            }

            removed++;
            return m.Value.StartsWith('[') || m.Value.StartsWith('(') ? Placeholder : " " + Placeholder;
        }

        var result = Bracketed().Replace(text, Check);
        result = Sentence().Replace(result, Check);

        // An instruction split across lines survives the sentence pass. Probe the whole text with line breaks
        // collapsed (sentence ends stay hard boundaries, and our own placeholders are blanked so they cannot match).
        var probe = result.Replace(Placeholder, " ").Replace('!', '.').Replace('?', '.');
        if (IsInstruction(probe))
        {
            removed++;
            result = Placeholder;
        }

        return new Cleaned(result, removed);
    }

    /// <summary>Sanitize, then quarantine, then trim: the full treatment for untrusted text.</summary>
    public static Cleaned CleanUntrusted(string text)
    {
        var sanitized = Sanitize(text);
        var quarantined = Quarantine(sanitized.Text);
        return new Cleaned(quarantined.Text.Trim(), sanitized.Removed + quarantined.Removed);
    }

    /// <summary>Ingestion step: sanitize and quarantine every chunk, and record how many segments were removed.</summary>
    public static IReadOnlyList<KnowledgeChunk> CleanForIngestion(IEnumerable<KnowledgeChunk> chunks) =>
        chunks.Select(chunk =>
        {
            var cleaned = CleanUntrusted(chunk.Text);
            return cleaned.Removed == 0 ? chunk : chunk with { Text = cleaned.Text, RemovedSegments = cleaned.Removed };
        }).ToList();

    /// <summary>Removes every sentence with a link to a host outside <paramref name="allowedHosts"/> (an empty list
    /// allows no links at all).</summary>
    public static Cleaned RemoveUnapprovedLinks(string answer, IReadOnlyCollection<string> allowedHosts)
    {
        var removed = 0;
        var result = SentenceWithLink().Replace(answer, m =>
        {
            if (Url().Matches(m.Value).All(u => IsAllowedLink(u.Value, allowedHosts)))
            {
                return m.Value;
            }

            removed++;
            return string.Empty;
        });
        return new Cleaned(result.Trim(), removed);
    }

    /// <summary>Host check for a bare URL: allowed hosts and their subdomains only. Invisible characters, raw IPs,
    /// look-alike hosts (compared IDNA-encoded) and unparseable URLs are refused.</summary>
    public static bool IsAllowedLink(string url, IReadOnlyCollection<string> allowedHosts)
    {
        if (TextNormalizer.HasInvisible(url))
        {
            return false;
        }

        var candidate = url.TrimEnd('.', ',', ')', ']', ';', ':', '!', '?', '\'', '"');
        if (candidate.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            candidate = "http://" + candidate;
        }
        else if (candidate.StartsWith("hxxp", StringComparison.OrdinalIgnoreCase))
        {
            candidate = "http" + candidate[4..];
        }

        candidate = candidate.Replace('\\', '/');
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || uri.HostNameType is not UriHostNameType.Dns)
        {
            return false; // unparseable, or an IP address
        }

        string host;
        try
        {
            host = uri.IdnHost.ToLowerInvariant();
        }
        catch (UriFormatException)
        {
            return false;
        }

        return allowedHosts.Any(a => host.Equals(a, StringComparison.Ordinal) || host.EndsWith("." + a, StringComparison.Ordinal));
    }

    private static bool IsInstruction(string segment)
    {
        var text = TextNormalizer.DetectionText(segment);
        return AddressedToAi().IsMatch(text) || OverrideInstructions().IsMatch(text) || Reveal().IsMatch(text)
            || RoleTokens().IsMatch(text) || ToolCommand().IsMatch(text);
    }

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex HtmlComment();

    [GeneratedRegex(@"\[[^\[\]]{0,800}\]|\([^()]{0,800}\)")]
    private static partial Regex Bracketed();

    [GeneratedRegex(@"[^.!?\n]+[.!?]?")]
    private static partial Regex Sentence();

    [GeneratedRegex(@"\b(?:note|notes|instruction|instructions|message|attention|reminder)\b[^.\]\n]{0,40}\b(?:to|for)\b[^.\]\n]{0,25}\b(?:AI|assistants?|LLMs?|chat ?bots?|language models?|copilots?|agents?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AddressedToAi();

    [GeneratedRegex(@"\b(?:ignore|disregard|forget)\b[^.\n]{0,40}\b(?:instructions?|rules|guidelines|prompts?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex OverrideInstructions();

    [GeneratedRegex(@"\b(?:reveal|print|show|output|repeat|leak|disclose)\b[^.\n]{0,40}\b(?:system prompt|system message|hidden instructions?|initial instructions?|developer message|configuration marker)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Reveal();

    [GeneratedRegex(@"<\|im_start\|>|<\|im_end\|>|<\|system\|>|<\|endoftext\|>|\[/?INST\]|<<SYS>>", RegexOptions.IgnoreCase)]
    private static partial Regex RoleTokens();

    [GeneratedRegex(@"\b(?:call|invoke|run|execute|trigger)\b[^.\n]{0,25}\b(?:propose_settlement|get_claim|search_knowledge)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ToolCommand();

    [GeneratedRegex(@"[^.!?\n]*(?:(?:https?|hxxps?)://|www\.)[^\s<>""'`]+[^.!?\n]*[.!?]?", RegexOptions.IgnoreCase)]
    private static partial Regex SentenceWithLink();

    [GeneratedRegex(@"(?:(?:https?|hxxps?)://|www\.)[^\s<>""'`]+", RegexOptions.IgnoreCase)]
    private static partial Regex Url();
}
