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
    private static bool _unreadable;

    public static void Init(string folder)
    {
        lock (FileLock)
        {
            if (_file is not null || _unreadable)
                return;
            var file = Path.Combine(folder, "episode-conflicts.json");
            try
            {
                Directory.CreateDirectory(folder);
                if (File.Exists(file))
                {
                    foreach (var e in JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(file)) ?? [])
                        Entries[e.SeriesId] = e;
                }
            }
            catch (JsonException)
            {
                // A broken file is rebuilt by the next tree sync.
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Unreadable: a report side file, so the sync goes on without it and the file is
                // left as it is. Not retried for every series until a restart.
                _unreadable = true;
                return;
            }
            _file = file;
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
            try
            {
                var tmp = _file + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Entries.Values.OrderBy(e => e.SeriesName).ToList()));
                File.Move(tmp, _file, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Only feeds the duplicates report: never fail the tree sync over it.
            }
        }
    }
}
