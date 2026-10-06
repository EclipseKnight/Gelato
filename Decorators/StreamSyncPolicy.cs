using Gelato.Services;

namespace Gelato.Decorators;

/// <summary>What a sync-eligible call does about the user's streams for a movie/episode.</summary>
public enum StreamSyncAction
{
    /// <summary>Synced recently: answer from the rows there are.</summary>
    UseSynced,

    /// <summary>The last sync found nothing, recently: answer at once without asking again.</summary>
    UseRememberedNoStreams,

    /// <summary>Due, and the user has rows from an earlier sync: answer with them, sync behind.</summary>
    ServeKnownRowsAndRefresh,

    /// <summary>Due: sync now and answer when it is done.</summary>
    SyncNow,
}

/// <summary>
/// Decides between answering from what Gelato already has and asking AIOStreams first. Kept apart
/// from <see cref="MediaSourceManagerDecorator"/> so the rules can be read, and tested, alone.
/// </summary>
public static class StreamSyncPolicy
{
    /// <param name="state">The user's sync mark for the movie/episode.</param>
    /// <param name="refreshInBackground">The <c>RefreshStreamsInBackground</c> setting.</param>
    /// <param name="isItemRead">
    /// The call only reads the item (a details page). Playback calls are not: they probe and play
    /// a row, and a background sync may be rewriting or deleting that row at the same time.
    /// </param>
    /// <param name="wasReset">
    /// The movie/episode was reset lately (its versions were split or merged), so the rows it has
    /// now are not the ones a sync would leave.
    /// </param>
    /// <param name="countKnownRows">
    /// The user's stream rows for the movie/episode. Asked only when it decides the outcome: it
    /// costs a query.
    /// </param>
    public static StreamSyncAction Decide(
        StreamSyncState state,
        bool refreshInBackground,
        bool isItemRead,
        bool wasReset,
        Func<int> countKnownRows
    ) =>
        state switch
        {
            StreamSyncState.Synced => StreamSyncAction.UseSynced,
            StreamSyncState.NoStreams => StreamSyncAction.UseRememberedNoStreams,
            // A first sync has no rows to show, so it always waits.
            _ when refreshInBackground && isItemRead && !wasReset && countKnownRows() > 0 =>
                StreamSyncAction.ServeKnownRowsAndRefresh,
            _ => StreamSyncAction.SyncNow,
        };

    /// <summary>
    /// Whether a sync-eligible call on a stream row waits for a sync of the row's movie/episode
    /// that is already running. It never starts one.
    /// </summary>
    /// <remarks>
    /// Jellyfin 12's web client loads a version as an item, then plays it by the row's own id.
    /// Playback, stream, download and subtitle calls probe and save that row, and the sync may be
    /// rewriting or deleting it. A details read only lists the versions, as the movie's own read
    /// does while a refresh runs. A call made by the sync itself (an event handler of a save it
    /// makes, say) must never wait: it would wait for itself.
    /// </remarks>
    public static bool WaitsForRunningSync(bool isStreamRow, bool isItemRead, bool insideSync) =>
        isStreamRow && !isItemRead && !insideSync;

    /// <summary>
    /// Runs a sync and marks it with the number of streams found, so the next calls within
    /// StreamTTL (or NoStreamsTTL for none) answer without asking again. A sync that throws or is
    /// cancelled is not marked, so the next call tries again; the exception is the caller's.
    /// </summary>
    /// <param name="isMergedVersion">
    /// The item is a version merged into another movie/episode. Its streams belong to that one, so
    /// it is never synced and its zero says nothing; and it shares that one's key, so marking it
    /// would hold back that movie's own sync.
    /// </param>
    public static async Task SyncAndMarkAsync(
        Func<CancellationToken, Task<int>> sync,
        bool isMergedVersion,
        Action<int> mark,
        CancellationToken ct
    )
    {
        var count = await sync(ct).ConfigureAwait(false);
        if (count > 0 || !isMergedVersion)
        {
            mark(count);
        }
    }
}
