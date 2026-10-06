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
}
