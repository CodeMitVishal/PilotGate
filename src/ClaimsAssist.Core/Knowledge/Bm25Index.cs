using System.Text.RegularExpressions;

namespace ClaimsAssist.Core.Knowledge;

/// <summary>
/// Okapi BM25 keyword ranking. Keyword search catches exact terms that embeddings blur:
/// amounts, factors, product names and legal references such as "GOÄ" or "Art. 9".
/// </summary>
internal sealed partial class Bm25Index
{
    private const double K1 = 1.2;
    private const double B = 0.75;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "an", "and", "or", "of", "to", "in", "on", "for", "is", "are", "be", "it", "this", "that",
        "with", "what", "when", "which", "who", "how", "do", "does", "can", "we", "you", "my", "our", "at",
        "der", "die", "das", "und", "oder", "ist", "ein", "eine", "für", "von", "mit", "den", "dem", "des",
    };

    private readonly List<Dictionary<string, int>> _termFrequencies = [];
    private readonly List<int> _lengths = [];
    private readonly Dictionary<string, int> _documentFrequency = new(StringComparer.Ordinal);
    private readonly double _averageLength;

    public Bm25Index(IEnumerable<string> documents)
    {
        foreach (var document in documents)
        {
            var tokens = Tokenize(document);
            var frequencies = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var token in tokens)
            {
                frequencies[token] = frequencies.GetValueOrDefault(token) + 1;
            }

            foreach (var term in frequencies.Keys)
            {
                _documentFrequency[term] = _documentFrequency.GetValueOrDefault(term) + 1;
            }

            _termFrequencies.Add(frequencies);
            _lengths.Add(tokens.Count);
        }

        _averageLength = _lengths.Count == 0 ? 1 : Math.Max(1, _lengths.Average());
    }

    public double Score(int document, IReadOnlyList<string> queryTerms)
    {
        var score = 0.0;
        var n = _termFrequencies.Count;
        foreach (var term in queryTerms.Distinct())
        {
            if (!_termFrequencies[document].TryGetValue(term, out var frequency))
            {
                continue;
            }

            var df = _documentFrequency[term];
            var idf = Math.Log(1 + ((n - df + 0.5) / (df + 0.5)));
            var lengthNorm = 1 - B + (B * _lengths[document] / _averageLength);
            score += idf * frequency * (K1 + 1) / (frequency + (K1 * lengthNorm));
        }

        return score;
    }

    public static IReadOnlyList<string> Tokenize(string text) =>
        NonWord().Split(text.ToLowerInvariant())
            .Where(t => t.Length > 1 && !StopWords.Contains(t))
            .ToList();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonWord();
}
