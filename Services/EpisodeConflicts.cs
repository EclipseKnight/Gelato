using System.Collections.Concurrent;
using System.Text.Json;

namespace Gelato.Services;

/// <summary>
/// What the tree sync saw when a series listed episodes another series owns: kept on disk so
/// the duplicates report can show a series whose episodes all belong to another one (the One
/// Piece copy: its whole meta was the other show's episodes).
/// </summary>
public static class EpisodeConflicts
{
    public sealed record Entry(
        Guid SeriesId,
        string SeriesName,
        Guid OwnerId,
        string OwnerName,
        int Shared,
        int Listed,
        DateTime SeenUtc
    );

    private static readonly ConcurrentDictionary<Guid, Entry> Entries = new();
    private static readonly object FileLock = new();
    private static string? _file;

    public static void Init(string folder)
    {
        lock (FileLock)
        {
            if (_file is not null)
                return;
            Directory.CreateDirectory(folder);
            _file = Path.Combine(folder, "episode-conflicts.json");
            try
            {
                if (File.Exists(_file))
                {
                    foreach (var e in JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_file)) ?? [])
                        Entries[e.SeriesId] = e;
                }
            }
            catch (JsonException)
            {
                // A broken file is rebuilt by the next tree sync.
            }
        }
    }

    /// <summary>Records the sync's outcome for a series; a sync without clashes clears it.</summary>
    public static void Record(Guid seriesId, string seriesName, Guid ownerId, string? ownerName, int shared, int listed)
    {
        bool changed;
        if (shared > 0)
        {
            var entry = new Entry(seriesId, seriesName, ownerId, ownerName ?? "", shared, listed, DateTime.UtcNow);
            // Rewrite the file only when what it says changes, not on every sync of the same clash.
            changed = !Entries.TryGetValue(seriesId, out var old)
                || old.OwnerId != ownerId || old.Shared != shared || old.Listed != listed || old.SeriesName != seriesName;
            Entries[seriesId] = changed ? entry : old!;
        }
        else
            changed = Entries.TryRemove(seriesId, out _);
        if (changed)
            Save();
    }

    public static IReadOnlyList<Entry> Snapshot() => Entries.Values.ToList();

    private static void Save()
    {
        lock (FileLock)
        {
            if (_file is null)
                return;
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Entries.Values.OrderBy(e => e.SeriesName).ToList()));
            File.Move(tmp, _file, true);
        }
    }
}
