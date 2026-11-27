using ClaimsAssist.Core.Security;
using Microsoft.Extensions.AI;

namespace ClaimsAssist.Core.Knowledge;

public sealed record RetrievedChunk(KnowledgeChunk Chunk, double Score);

/// <summary>
/// Hybrid retrieval (BM25 + embeddings, fused with reciprocal rank fusion) with security trimming.
/// Production equivalent: an Azure AI Search hybrid query with a filter on the allowed-groups field.
/// </summary>
public sealed class HybridRetriever
{
    private const int RrfK = 60;

    private readonly IReadOnlyList<KnowledgeChunk> _chunks;
    private readonly Bm25Index _bm25;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly ReadOnlyMemory<float>[] _vectors;

    private HybridRetriever(
        IReadOnlyList<KnowledgeChunk> chunks,
        Bm25Index bm25,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        ReadOnlyMemory<float>[] vectors)
    {
        _chunks = chunks;
        _bm25 = bm25;
        _embedder = embedder;
        _vectors = vectors;
    }

    public IReadOnlyList<KnowledgeChunk> Chunks => _chunks;

    public static async Task<HybridRetriever> CreateAsync(
        IReadOnlyList<KnowledgeChunk> chunks,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        string embeddingModel,
        EmbeddingCache cache,
        CancellationToken cancellationToken = default)
    {
        var texts = chunks.Select(c => $"{c.DocTitle}\n{c.Section}\n{c.Text}").ToList();
        var keys = texts.Select(t => EmbeddingCache.Key(embeddingModel, t)).ToList();
        var missing = Enumerable.Range(0, chunks.Count).Where(i => !cache.TryGet(keys[i], out _)).ToList();

        if (missing.Count > 0)
        {
            var generated = await embedder.GenerateAsync(missing.Select(i => texts[i]), cancellationToken: cancellationToken);
            for (var j = 0; j < missing.Count; j++)
            {
                cache.Set(keys[missing[j]], generated[j].Vector.ToArray());
            }

            cache.Save();
        }

        var vectors = keys.Select(k => cache.TryGet(k, out var v) ? new ReadOnlyMemory<float>(v) : ReadOnlyMemory<float>.Empty).ToArray();
        return new HybridRetriever(chunks, new Bm25Index(texts), embedder, vectors);
    }

    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        string query,
        UserContext user,
        int top = 4,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        // Security trimming happens BEFORE ranking, like a filter on an Azure AI Search query:
        // a chunk the caller may not read never becomes a candidate, so it can never reach the model.
        var candidates = Enumerable.Range(0, _chunks.Count).Where(i => user.CanRead(_chunks[i])).ToList();
        if (candidates.Count == 0)
        {
            return [];
        }

        var terms = Bm25Index.Tokenize(query);
        var lexical = candidates
            .Select(i => (Index: i, Score: _bm25.Score(i, terms)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ToList();

        var queryVector = await _embedder.GenerateVectorAsync(query, cancellationToken: cancellationToken);
        var semantic = candidates
            .Select(i => (Index: i, Score: Cosine(queryVector.Span, _vectors[i].Span)))
            .OrderByDescending(x => x.Score)
            .ToList();

        // Reciprocal rank fusion: robust to the different score scales of BM25 and cosine similarity.
        var fused = new Dictionary<int, double>();
        for (var rank = 0; rank < lexical.Count; rank++)
        {
            fused[lexical[rank].Index] = fused.GetValueOrDefault(lexical[rank].Index) + (1.0 / (RrfK + rank + 1));
        }

        for (var rank = 0; rank < semantic.Count; rank++)
        {
            fused[semantic[rank].Index] = fused.GetValueOrDefault(semantic[rank].Index) + (1.0 / (RrfK + rank + 1));
        }

        return fused
            .OrderByDescending(kv => kv.Value)
            .Take(top)
            .Select(kv => new RetrievedChunk(_chunks[kv.Key], Math.Round(kv.Value, 5)))
            .ToList();
    }

    private static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length == 0 || a.Length != b.Length)
        {
            return 0;
        }

        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        return dot / ((Math.Sqrt(normA) * Math.Sqrt(normB)) + 1e-12);
    }
}
