using MediaBrowser.Controller.Entities;

namespace Gelato.Decorators;

/// <summary>
/// Decides whether a stream row probed for playback may be saved. The probe takes seconds, and a
/// stream sync of the movie/episode may run meanwhile. It deletes the rows no user has any more
/// (saving one would bring it back as an orphan) and saves the rest anew, with new URLs, users
/// and order (saving the probed copy would undo that).
/// </summary>
/// <remarks>
/// A sync never edits the instance the probe holds: it loads its rows from the database, saves
/// them, and registers them in Jellyfin's item cache in place of the old ones. So the row as it
/// is now either is that same instance (untouched), or is compared by the fields a sync writes.
/// The movie/episode is the exception: a sync edits its cached instance's links in place, so the
/// same instance always carries the current links; a reloaded copy is compared like a row.
/// A row the probe could not save is probed again on its next playback.
/// </remarks>
public static class ProbeSaveGuard
{
    /// <summary>
    /// The fields of an item a stream sync writes. Taken before the probe. For a row: its refresh
    /// date, URL and Gelato data (users, order). For the movie/episode, which is probed when
    /// played by its own id: the links to its versions, which every sync that adds or drops a row
    /// rewrites.
    /// </summary>
    public readonly record struct RowStamp(
        DateTime DateLastRefreshed,
        string? Path,
        string? Data,
        string Versions
    );

    public static RowStamp Stamp(BaseItem item) =>
        new(item.DateLastRefreshed, item.Path, item.ExternalId, VersionIds(item));

    // Joined into one string: the record compares it by value, which an array is not.
    private static string VersionIds(BaseItem item) =>
        item is Video { LinkedAlternateVersions: { Length: > 0 } links }
            ? string.Join(',', links.Select(l => l.ItemId))
            : string.Empty;

    /// <param name="probed">The instance that was probed and would be saved.</param>
    /// <param name="before">Its <see cref="Stamp"/> from before the probe.</param>
    /// <param name="current">The row as Jellyfin has it now, or null when it was deleted.</param>
    public static bool CanSave(BaseItem probed, RowStamp before, BaseItem? current) =>
        current is not null && (ReferenceEquals(current, probed) || Stamp(current) == before);
}
