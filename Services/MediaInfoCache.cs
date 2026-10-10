using System.Collections.Concurrent;
using System.Text.Json;
using MediaBrowser.Model.Entities;

namespace Gelato.Services;

/// <summary>
/// Probe results kept on this server by file identity (torrent info hash and file index), so a
/// file probed once fills in its tracks, runtime and size for every row and user that gets the
/// same file, without asking an outside service. Capped; the least recently used go first.
/// </summary>
public static class MediaInfoCache
{
    public const int MaxEntries = 5000;

    public sealed record Entry(
        long RunTimeTicks,
        string? Container,
        long? Size,
        int? TotalBitrate,
        int Width,
        int Height,
        int? DefaultVideoStreamIndex,
        bool HasSubtitles,
        List<MediaStream> Streams
    )
    {
        public DateTime LastUsedUtc { get; set; }
    }

    private static readonly ConcurrentDictionary<string, Entry> Entries = new();
    private static readonly object FileLock = new();
    private static string? _file;

    /// <summary>The cache key, or null when the file can't be told apart from others.</summary>
    public static string? Key(string? infoHash, int? fileIdx) =>
        string.IsNullOrWhiteSpace(infoHash) ? null : $"{infoHash.Trim().ToLowerInvariant()}:{fileIdx?.ToString() ?? "-"}";

    public static void Init(string folder)
    {
        lock (FileLock)
        {
            if (_file is not null)
                return;
            Directory.CreateDirectory(folder);
            _file = Path.Combine(folder, "media-info-cache.json");
            try
            {
                if (File.Exists(_file))
                    foreach (var (k, e) in JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_file)) ?? [])
                        Entries[k] = e;
            }
            catch (JsonException)
            {
                // A broken file starts the cache over.
            }
        }
    }

    public static Entry? Get(string? key)
    {
        if (key is null || !Entries.TryGetValue(key, out var e))
            return null;
        e.LastUsedUtc = DateTime.UtcNow;
        return e;
    }

    public static void Put(string key, Entry entry)
    {
        entry.LastUsedUtc = DateTime.UtcNow;
        Entries[key] = entry;
        Trim(MaxEntries);
        Save();
    }

    public static int Count => Entries.Count;

    /// <summary>Drops the least recently used entries above <paramref name="max"/>.</summary>
    public static void Trim(int max)
    {
        var over = Entries.Count - max;
        if (over <= 0)
            return;
        foreach (var k in Entries.OrderBy(p => p.Value.LastUsedUtc).Take(over).Select(p => p.Key).ToList())
            Entries.TryRemove(k, out _);
    }

    /// <summary>For tests.</summary>
    public static void Clear() => Entries.Clear();

    private static void Save()
    {
        lock (FileLock)
        {
            if (_file is null)
                return;
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Entries.ToDictionary(p => p.Key, p => p.Value)));
            File.Move(tmp, _file, true);
        }
    }
}
