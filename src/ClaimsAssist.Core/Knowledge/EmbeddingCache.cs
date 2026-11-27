using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaimsAssist.Core.Knowledge;

/// <summary>
/// Disk cache for chunk embeddings, keyed by model and content hash, so restarts and CI runs
/// only pay to embed chunks that actually changed.
///
/// Crash-safe: a save writes a temporary file and renames it over the cache in one step, so a process killed mid-save
/// leaves the previous cache intact. A cache that cannot be read anyway (truncated, hand-edited) is ignored and rebuilt:
/// it only saves cost, so it must never stop the service from starting.
/// </summary>
public sealed class EmbeddingCache
{
    private readonly string? _path;
    private readonly Dictionary<string, float[]> _entries;

    public EmbeddingCache(string? path)
    {
        _path = path;
        _entries = [];
        if (path is null || !File.Exists(path))
        {
            return;
        }

        try
        {
            _entries = JsonSerializer.Deserialize<Dictionary<string, float[]>>(File.ReadAllText(path)) ?? [];
        }
        catch (JsonException ex)
        {
            UnreadableFileIgnored = ex.Message;
        }
    }

    /// <summary>Why an existing cache file was ignored, or null when it was read (or there was none).</summary>
    public string? UnreadableFileIgnored { get; }

    public int Count => _entries.Count;

    public static string Key(string model, string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(model + "\n" + text)));

    public bool TryGet(string key, out float[] vector) => _entries.TryGetValue(key, out vector!);

    public void Set(string key, float[] vector) => _entries[key] = vector;

    /// <summary>Why the last save failed (read-only disk, disk full, a locked file), or null. A failed save only means
    /// the next start embeds again, so it is reported, never thrown.</summary>
    public string? SaveError { get; private set; }

    public bool Save()
    {
        if (_path is null)
        {
            return true;
        }

        var temporary = Path.GetFullPath(_path) + ".tmp-" + Guid.NewGuid().ToString("n");
        try
        {
            var full = Path.GetFullPath(_path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(_entries));
            File.Move(temporary, full, overwrite: true); // a rename: readers see the old file or the new one, never half
            SaveError = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveError = ex.Message;
            return false;
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: a leftover temporary file is harmless and the next save uses a new name.
            }
        }
    }
}
