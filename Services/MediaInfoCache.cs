using System.Collections.Concurrent;
using System.Text.Json;
using MediaBrowser.Model.Entities;

namespace Gelato.Services;

/// <summary>
/// Probe results kept on this server by file identity (<see cref="Key"/>), so a
/// file probed once fills in its tracks, runtime and size for every row and user that gets the
/// same file, without asking an outside service. Capped; the least recently used go first.
/// </summary>
public static class MediaInfoCache
{
    /// <summary>
    /// Entries average about 22 KB on the server (a stream serializes to about 1.5 KB, 14 streams
    /// per file on average, up to 56), so 2000 keeps the file around 45 MB.
    /// </summary>
    public const int MaxEntries = 2000;

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
    private static DateTime? _lastSave;
    private static bool _dirty; // under FileLock
    private static bool _unreadable;
    private static Timer? _flushTimer;

    /// <summary>
    /// The file holds every entry (a few KB to 90 KB each), so it is written at most this often
    /// rather than on every probe. A skipped write is made by a timer this long after it, and on
    /// shutdown (<see cref="Flush"/>); it is only a cache.
    /// </summary>
    public static readonly TimeSpan SaveEvery = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The cache key: info hash and file index, or info hash and size when the add-on gave no file
    /// index (the files of a season pack differ in size). Null when the file can't be told apart
    /// from others: a hash alone would give every file of a pack one key and each other's tracks.
    /// </summary>
    public static string? Key(string? infoHash, int? fileIdx, long? size = null)
    {
        if (string.IsNullOrWhiteSpace(infoHash))
            return null;
        var hash = infoHash.Trim().ToLowerInvariant();
        if (fileIdx is not null)
            return $"{hash}:{fileIdx}";
        return size is > 0 ? $"{hash}:s{size}" : null;
    }

    public static void Init(string folder)
    {
        lock (FileLock)
        {
            if (_file is not null || _unreadable)
                return;
            var file = Path.Combine(folder, "media-info-cache.json");
            try
            {
                Directory.CreateDirectory(folder);
                if (File.Exists(file))
                    foreach (var (k, e) in JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(file)) ?? [])
                    {
                        // "hash:-" keys (no file index) gave every file of a pack one entry.
                        if (!k.EndsWith(":-", StringComparison.Ordinal))
                            Entries[k] = e;
                    }
            }
            catch (JsonException)
            {
                // A broken file starts the cache over.
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Unreadable: keep the cache in memory only until a restart, and leave the file
                // alone rather than overwrite it with a nearly empty one. Not retried on every key.
                _unreadable = true;
                return;
            }
            _file = file;
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
        var now = DateTime.UtcNow;
        if (SaveDue(_lastSave, now))
        {
            _lastSave = now;
            Save();
        }
        else
        {
            // Under the lock, so a write that already took its snapshot can't clear this flag.
            lock (FileLock)
            {
                _dirty = true;
                _flushTimer ??= new Timer(_ => FlushFromTimer(), null, SaveEvery, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private static void FlushFromTimer()
    {
        try
        {
            lock (FileLock)
                _flushTimer = null;
            Flush();
        }
        catch (Exception)
        {
            // A timer thread must never throw (it would end the server); the next write retries.
        }
    }

    /// <summary>Writes entries a throttled <see cref="Put"/> left unsaved (timer and shutdown).</summary>
    public static void Flush()
    {
        lock (FileLock)
        {
            if (!_dirty)
                return;
        }
        _lastSave = DateTime.UtcNow;
        Save();
    }

    public static bool SaveDue(DateTime? last, DateTime now) => last is null || now - last.Value >= SaveEvery;

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

    /// <summary>For tests: whether a write is still owed.</summary>
    public static bool Dirty
    {
        get
        {
            lock (FileLock)
                return _dirty;
        }
    }

    /// <summary>The file this cache writes (null until <see cref="Init"/>, or when unreadable). For tests.</summary>
    public static string? File_ => _file;

    /// <summary>For tests: forget the file so <see cref="Init"/> runs again.</summary>
    public static void Reset()
    {
        lock (FileLock)
        {
            _file = null;
            _unreadable = false;
            _lastSave = null;
            _dirty = false;
            _flushTimer?.Dispose();
            _flushTimer = null;
        }
        Entries.Clear();
    }

    /// <summary>Writes the file. A failed write (disk full, permissions) never fails the probe that called it.</summary>
    private static void Save()
    {
        lock (FileLock)
        {
            if (_file is null)
                return;
            _dirty = false; // before the snapshot: a Put after it sets it again (it waits for this lock)
            try
            {
                var tmp = _file + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Entries.ToDictionary(p => p.Key, p => p.Value)));
                File.Move(tmp, _file, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _dirty = true; // tried again on the next write
            }
        }
    }
}
