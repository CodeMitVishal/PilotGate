using System.Collections.Frozen;
using System.Globalization;
using System.Text;

namespace ClaimsAssist.Core.Guardrails;

/// <summary>
/// Detection views of a text, the same rules as DIAL Guard (dial/guard/src/dial_guard/normalize.py). Detectors run on a
/// view in which characters a reader does not see are removed and look-alike letters are folded to ASCII, so
/// <c>ig​nore</c> or Cyrillic <c>іgnore</c> is still recognised. Every view character remembers the raw index it
/// came from, so a match maps back to the exact raw span to replace; the raw text itself is never rewritten here.
/// The shared corpus (dial/schema/guard-corpus.v1.json) pins both tables, so the two implementations cannot drift.
/// </summary>
public static class TextNormalizer
{
    /// <summary>Zero-width characters, LRM/RLM, word joiner, invisible operators, BOM, soft hyphen, Mongolian vowel
    /// separator and the bidi embedding, override and isolate controls.</summary>
    public static readonly FrozenSet<int> Invisible = Enumerable.Range(0x200B, 5)
        .Concat(Enumerable.Range(0x2060, 5))
        .Concat(Enumerable.Range(0x202A, 5))
        .Concat(Enumerable.Range(0x2066, 4))
        .Concat([0xFEFF, 0x00AD, 0x180E])
        .ToFrozenSet();

    /// <summary>Look-alike letters (Cyrillic, Greek, Armenian, Latin variants) folded to ASCII.</summary>
    public static readonly FrozenDictionary<int, char> Confusables = new Dictionary<int, char>
    {
        // Cyrillic lower case
        [0x0430] = 'a', [0x0435] = 'e', [0x043E] = 'o', [0x0440] = 'p', [0x0441] = 'c', [0x0443] = 'y', [0x0445] = 'x',
        [0x0456] = 'i', [0x0458] = 'j', [0x0455] = 's', [0x0501] = 'd', [0x051B] = 'q', [0x051D] = 'w', [0x04BB] = 'h',
        [0x04CF] = 'l', [0x043A] = 'k',
        // Cyrillic upper case
        [0x0410] = 'A', [0x0412] = 'B', [0x0415] = 'E', [0x041A] = 'K', [0x041C] = 'M', [0x041D] = 'H', [0x041E] = 'O',
        [0x0420] = 'P', [0x0421] = 'C', [0x0422] = 'T', [0x0425] = 'X', [0x0423] = 'Y', [0x0406] = 'I', [0x0408] = 'J',
        [0x0405] = 'S', [0x04C0] = 'I',
        // Greek lower case
        [0x03B1] = 'a', [0x03BF] = 'o', [0x03C1] = 'p', [0x03B9] = 'i', [0x03BA] = 'k', [0x03BD] = 'v', [0x03C5] = 'u',
        [0x03C7] = 'x',
        // Greek upper case
        [0x0391] = 'A', [0x0392] = 'B', [0x0395] = 'E', [0x0396] = 'Z', [0x0397] = 'H', [0x0399] = 'I', [0x039A] = 'K',
        [0x039C] = 'M', [0x039D] = 'N', [0x039F] = 'O', [0x03A1] = 'P', [0x03A4] = 'T', [0x03A5] = 'Y', [0x03A7] = 'X',
        // Armenian and Latin variants
        [0x0585] = 'o', [0x057D] = 'u', [0x0131] = 'i', [0x0237] = 'j',
    }.ToFrozenDictionary();

    /// <summary>A view: its text and, for every view character (UTF-16 unit), the raw index it came from.</summary>
    public sealed class View(string raw, string text, int[] rawIndex)
    {
        public string Text { get; } = text;

        public int RawStart(int viewIndex) => viewIndex < rawIndex.Length ? rawIndex[viewIndex] : raw.Length;

        /// <summary>Raw end (exclusive) of a view span ending at <paramref name="viewEnd"/>; a raw character that
        /// expanded into several view characters (or is a surrogate pair) is covered whole.</summary>
        public int RawEnd(int viewEnd)
        {
            if (viewEnd <= 0)
            {
                return 0;
            }

            var start = rawIndex[viewEnd - 1];
            return start + (char.IsHighSurrogate(raw[start]) && start + 1 < raw.Length ? 2 : 1);
        }
    }

    /// <summary>Folded view: invisible characters removed, compatibility forms decomposed (NFKD), combining marks
    /// dropped, look-alike letters folded to ASCII.</summary>
    public static View Folded(string raw)
    {
        var text = new StringBuilder(raw.Length);
        var index = new List<int>(raw.Length);
        var i = 0;
        foreach (var rune in raw.EnumerateRunes())
        {
            var at = i;
            i += rune.Utf16SequenceLength;
            if (Invisible.Contains(rune.Value))
            {
                continue;
            }

            foreach (var part in rune.ToString().Normalize(NormalizationForm.FormKD).EnumerateRunes())
            {
                var category = Rune.GetUnicodeCategory(part);
                if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark)
                {
                    continue;
                }

                if (Confusables.TryGetValue(part.Value, out var ascii))
                {
                    text.Append(ascii);
                    index.Add(at);
                }
                else
                {
                    var s = part.ToString();
                    text.Append(s);
                    for (var k = 0; k < s.Length; k++)
                    {
                        index.Add(at);
                    }
                }
            }
        }

        return new View(raw, text.ToString(), [.. index]);
    }

    /// <summary>Folded view as plain text with whitespace runs collapsed to one space (classifies short segments).</summary>
    public static string DetectionText(string raw)
    {
        var folded = Folded(raw).Text;
        var sb = new StringBuilder(folded.Length);
        var inSpace = false;
        foreach (var ch in folded)
        {
            if (char.IsWhiteSpace(ch))
            {
                inSpace = true;
                continue;
            }

            if (inSpace && sb.Length > 0)
            {
                sb.Append(' ');
            }

            inSpace = false;
            sb.Append(ch);
        }

        return sb.ToString();
    }

    /// <summary>True if the text contains a character the folded view drops as invisible.</summary>
    public static bool HasInvisible(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (Invisible.Contains(rune.Value))
            {
                return true;
            }
        }

        return false;
    }
}
