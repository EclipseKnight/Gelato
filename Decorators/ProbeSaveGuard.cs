using MediaBrowser.Controller.Entities;

namespace Gelato.Decorators;

/// <summary>
/// Decides what is saved after a stream was probed for playback. The probe takes seconds, and a
/// stream sync of the movie/episode may run meanwhile. It deletes the rows no user has any more
/// (saving one would bring it back as an orphan) and saves the rest anew, with new URLs, users
/// and order (saving the probed copy as it was would undo that).
/// </summary>
/// <remarks>
/// Called under the movie/episode's write lock (<see cref="GelatoManager.RunExclusiveAsync"/>),
/// which every sync and deletion holds, so nothing changes the row while this decides.
/// A sync never edits the instance a probe holds: it loads its rows from the database, saves
/// them, and registers them in Jellyfin's item cache in place of the old ones. So when the cached
/// row is still the probed instance, no sync wrote it. The movie/episode is the exception: a sync
/// edits its cached instance's version links in place, so a probed copy of the movie is never
/// saved, only its run time is carried over to the cached one.
/// Upstream (#257) took the sync's data from the cached row. Here it comes from the database:
/// the cached row may be in the middle of another playback's probe, which points its path at a
/// temporary file until the probe ends.
/// </remarks>
public static class ProbeSaveGuard
{
    /// <param name="probed">The instance that was probed (or got its run time).</param>
    /// <param name="cached">The item as Jellyfin's cache has it now, or null when it was deleted.</param>
    /// <param name="stored">
    /// The item as the database has it now, or null when it was deleted. An episode deleted with
    /// its series stays in the cache, so the cache alone does not tell.
    /// </param>
    /// <returns>The instance to save, or null when nothing may be saved.</returns>
    public static BaseItem? ItemToSave(BaseItem probed, BaseItem? cached, BaseItem? stored)
    {
        if (cached is null || stored is null)
            return null;

        if (ReferenceEquals(cached, probed))
            return probed;

        if (probed.HasStreamTag())
        {
            TakeSyncedData(stored, probed);
            return probed;
        }

        // A movie/episode gets nothing but its run time from a probe.
        cached.RunTimeTicks ??= probed.RunTimeTicks;
        return cached;
    }

    /// <summary>
    /// Puts what a sync writes on a stream row onto the instance that was probed: its users,
    /// order, names and file (the Gelato data), its URL and its movie/episode. What the probe set
    /// (tracks, run time, size) stays.
    /// </summary>
    public static void TakeSyncedData(BaseItem synced, BaseItem probed)
    {
        probed.ExternalId = synced.ExternalId;
        probed.Path = synced.Path;
        probed.ProviderIds = synced.ProviderIds;
        probed.Tags = synced.Tags;
        probed.LockedFields = synced.LockedFields;
        probed.ParentId = synced.ParentId;
        probed.DateLastRefreshed = synced.DateLastRefreshed;
        // Also when the database has no owner (a split cleared it): the probe must not put the
        // old one back.
        if (probed is Video row && synced is Video storedRow)
            row.SetPrimaryVersionId(storedRow.PrimaryVersionId);
    }
}
