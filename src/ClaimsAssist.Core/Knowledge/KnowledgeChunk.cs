using System.Text;
using System.Text.RegularExpressions;

namespace ClaimsAssist.Core.Knowledge;

/// <summary>
/// One retrievable unit: a section of a source document plus its access-control list.
/// <see cref="RemovedSegments"/> counts hidden or instruction-like segments removed at ingestion.
/// </summary>
public sealed record KnowledgeChunk(
    string ChunkId,
    string DocId,
    string DocTitle,
    string Section,
    string Text,
    IReadOnlySet<string> AllowedGroups,
    string Source,
    int RemovedSegments = 0)
{
    /// <summary>The product or line of business a rule belongs to, so limits are never carried over to another product.</summary>
    public string AppliesTo { get; init; } = "all lines of business";
}

/// <summary>
/// Loads Markdown documents with front matter (id, title, allowedGroups, source) and splits them
/// on "## " headings. Structure-aware chunking keeps every rule inside one chunk with a stable id.
/// </summary>
public static partial class MarkdownKnowledgeLoader
{
    public static IReadOnlyList<KnowledgeChunk> LoadDirectory(string directory)
    {
        var chunks = new List<KnowledgeChunk>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.md").Order(StringComparer.Ordinal))
        {
            chunks.AddRange(Parse(File.ReadAllText(file), Path.GetFileName(file)));
        }

        var duplicate = chunks.GroupBy(c => c.ChunkId).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new FormatException($"Duplicate chunk id '{duplicate.Key}'.");
        }

        return chunks;
    }

    public static IReadOnlyList<KnowledgeChunk> Parse(string markdown, string fileName)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            throw new FormatException($"{fileName}: missing front matter.");
        }

        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 1;
        for (; i < lines.Length && lines[i].Trim() != "---"; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon > 0)
            {
                meta[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
            }
        }

        if (i >= lines.Length)
        {
            throw new FormatException($"{fileName}: front matter is not closed.");
        }

        string Require(string key) =>
            meta.TryGetValue(key, out var value) && value.Length > 0
                ? value
                : throw new FormatException($"{fileName}: front matter needs '{key}'.");

        var docId = Require("id");
        var title = Require("title");
        var groups = Require("allowedGroups")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var source = meta.GetValueOrDefault("source", "internal");
        var appliesTo = meta.GetValueOrDefault("appliesTo", "all lines of business");

        var chunks = new List<KnowledgeChunk>();
        string? heading = null;
        var body = new List<string>();

        void Flush()
        {
            var text = string.Join('\n', body).Trim();
            if (heading is not null && text.Length > 0)
            {
                chunks.Add(new KnowledgeChunk($"{docId}#{Slug(heading)}", docId, title, heading, text, groups, source) { AppliesTo = appliesTo });
            }
        }

        for (i++; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                heading = lines[i][3..].Trim();
                body.Clear();
            }
            else
            {
                body.Add(lines[i]);
            }
        }

        Flush();
        return chunks;
    }

    public static string Slug(string heading)
    {
        var sb = new StringBuilder();
        foreach (var ch in heading.ToLowerInvariant())
        {
            sb.Append(ch switch
            {
                'ä' => "ae",
                'ö' => "oe",
                'ü' => "ue",
                'ß' => "ss",
                _ when char.IsAsciiLetterOrDigit(ch) => ch.ToString(),
                _ => "-",
            });
        }

        return MultipleDashes().Replace(sb.ToString(), "-").Trim('-');
    }

    [GeneratedRegex("-{2,}")]
    private static partial Regex MultipleDashes();
}
