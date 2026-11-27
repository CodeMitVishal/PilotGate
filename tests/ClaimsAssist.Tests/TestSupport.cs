using System.Text.RegularExpressions;
using ClaimsAssist.Core.Claims;
using ClaimsAssist.Core.Guardrails;
using ClaimsAssist.Core.Knowledge;
using ClaimsAssist.Core.Security;
using Microsoft.Extensions.AI;

namespace ClaimsAssist.Tests;

/// <summary>Deterministic bag-of-words embeddings (FNV hashing), so retrieval tests run offline and repeatably.</summary>
internal sealed partial class HashingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private const int Dimensions = 512;

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(v => new Embedding<float>(Vectorize(v))).ToList()));

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    private static float[] Vectorize(string text)
    {
        var vector = new float[Dimensions];
        foreach (var token in Word().Split(text.ToLowerInvariant()).Where(t => t.Length > 1))
        {
            var hash = 2166136261u;
            foreach (var ch in token)
            {
                hash = (hash ^ ch) * 16777619u;
            }

            vector[hash % Dimensions] += 1;
        }

        return vector;
    }

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex Word();
}

internal static class TestData
{
    public static string RepoRoot { get; } = FindRoot();

    public static string DataPath => Path.Combine(RepoRoot, "data");

    public static IReadOnlyList<KnowledgeChunk> CleanChunks() =>
        UntrustedContentGuard.CleanForIngestion(MarkdownKnowledgeLoader.LoadDirectory(Path.Combine(DataPath, "kb")));

    public static ClaimStore Claims() => ClaimStore.Load(Path.Combine(DataPath, "claims.json"));

    public static PersonaDirectory Personas() => PersonaDirectory.Load(Path.Combine(DataPath, "personas.json"));

    public static UserContext Persona(string id) => Personas().Find(id) ?? throw new InvalidOperationException(id);

    public static Task<HybridRetriever> RetrieverAsync() =>
        HybridRetriever.CreateAsync(CleanChunks(), new HashingEmbeddingGenerator(), "test-hashing", new EmbeddingCache(null));

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PilotGate.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root (PilotGate.slnx) not found.");
    }
}

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
