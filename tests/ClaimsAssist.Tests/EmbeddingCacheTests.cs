using ClaimsAssist.Core.Knowledge;

namespace ClaimsAssist.Tests;

/// <summary>The embedding cache only saves cost: it must never stop the service from starting (review finding P0-3).</summary>
public sealed class EmbeddingCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "embedding-cache-tests-" + Guid.NewGuid().ToString("n"));

    private string CachePath => Path.Combine(_dir, ".cache", "embeddings.test.json");

    [Theory]
    [InlineData("{\"abc\": [0.1, 0.2")] // a save killed mid-write
    [InlineData("")]
    [InlineData("not json at all")]
    public void An_unreadable_cache_is_ignored_and_rebuilt_instead_of_crashing_startup(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        File.WriteAllText(CachePath, content);

        var cache = new EmbeddingCache(CachePath);

        Assert.NotNull(cache.UnreadableFileIgnored);
        Assert.Equal(0, cache.Count);
        cache.Set("k", [1f, 2f]);
        cache.Save();
        var reloaded = new EmbeddingCache(CachePath);
        Assert.Null(reloaded.UnreadableFileIgnored);
        Assert.True(reloaded.TryGet("k", out var vector));
        Assert.Equal([1f, 2f], vector);
    }

    [Fact]
    public void A_save_replaces_the_file_in_one_step_and_leaves_no_temporary_files()
    {
        var cache = new EmbeddingCache(CachePath);
        cache.Set("a", [0.5f]);
        cache.Save();
        cache.Set("b", [0.25f]);
        cache.Save();

        Assert.Equal([Path.GetFileName(CachePath)], Directory.GetFiles(Path.GetDirectoryName(CachePath)!).Select(Path.GetFileName));
        Assert.Equal(2, new EmbeddingCache(CachePath).Count);
    }

    [Fact]
    public async Task A_cache_that_cannot_be_written_never_stops_startup()
    {
        // A file where the cache directory should be: creating the directory fails, as on a read-only disk.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, ".cache"), "a file, not a directory");
        var cache = new EmbeddingCache(CachePath);

        var retriever = await HybridRetriever.CreateAsync(TestData.CleanChunks(), new HashingEmbeddingGenerator(), "test-hashing", cache);

        Assert.NotEmpty(retriever.Chunks);
        Assert.NotNull(cache.SaveError);
        Assert.False(cache.Save());
    }

    [Fact]
    public void A_missing_cache_starts_empty_without_a_warning()
    {
        var cache = new EmbeddingCache(CachePath);
        Assert.Null(cache.UnreadableFileIgnored);
        Assert.Equal(0, cache.Count);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
