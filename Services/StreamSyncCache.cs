using Microsoft.Extensions.Caching.Memory;

namespace Gelato.Services;

/// <summary>Whether a user's streams for a movie/episode have to be asked for again.</summary>
public enum StreamSyncState
{
    /// <summary>Never synced, the mark expired, or the movie/episode was reset since.</summary>
    Due,

    /// <summary>Synced within <c>StreamTTL</c>, and streams were found.</summary>
    Synced,

    /// <summary>
    /// The last sync found no streams: within <c>NoStreamsTTL</c>, or within
    /// <see cref="StreamSyncCache.FailedLookupTtl"/> when its lookup failed.
    /// </summary>
    NoStreams,
}

/// <summary>What a finished stream sync found.</summary>
/// <param name="Count">
/// The streams found. Negative when no lookup was made (an episode that has not aired).
/// </param>
/// <param name="LookupFailed">
/// The lookup can't be trusted to have found everything: an addon behind it failed or timed
/// out (see <see cref="Gelato.Decorators.StreamSyncPolicy.LookupFailed"/>). Its zero is not
/// "this title has no streams".
/// </param>
public readonly record struct StreamSyncResult(int Count, bool LookupFailed = false);

/// <summary>
/// The per-user marks that say a movie/episode's streams were synced recently, so the next call
/// does not ask AIOStreams again. Keyed by the caller (user, item and its creation date).
/// </summary>
/// <remarks>
/// A sync that found nothing is remembered too, for a shorter time. Without that a title with no
/// streams asked AIOStreams on every call, and waited for its slowest addon each time: on one
/// server over five days, a third of all syncs found nothing and most were repeated within the
/// hour. The shorter time lets a title whose streams appear later be picked up soon.
/// <para>
/// A sync whose lookup failed (every addon timed out, say) also answers with no streams, but
/// that says nothing about the title. It is remembered for <see cref="FailedLookupTtl"/> only,
/// so Play works again a minute later instead of after <c>NoStreamsTTL</c>.
/// </para>
/// </remarks>
public sealed class StreamSyncCache(
    IMemoryCache cache,
    Func<TimeSpan> streamTtl,
    Func<TimeSpan> noStreamsTtl
)
{
    // Orders syncs and resets. Not the wall clock: when it is set back between a sync and a
    // reset, the reset gets the older stamp and the sync before it counts as the newer one
    // (upstream #268). Process-wide, like the marks themselves.
    private static long _sequence;

    private static long NextSequence() => Interlocked.Increment(ref _sequence);

    private sealed record Mark(long Sequence, bool NoStreams);

    /// <summary>
    /// How long a sync that found nothing because its lookup failed is remembered. Fixed, not a
    /// setting: long enough that a burst of calls (a details page, then Play, then the player's
    /// own calls) doesn't ask again, short enough that a retry a minute later does. Never longer
    /// than <c>NoStreamsTTL</c>, so 0 there turns this off too.
    /// </summary>
    public static readonly TimeSpan FailedLookupTtl = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Records a finished sync that found <paramref name="streamCount"/> streams.
    /// <paramref name="lookupFailed"/> only matters when it found none.
    /// </summary>
    public void Set(string key, int streamCount, bool lookupFailed = false)
    {
        var lifetime = streamTtl();
        if (streamCount == 0)
        {
            // Never longer than a sync that found streams is kept. Zero turns this off.
            var noStreams = noStreamsTtl();
            if (noStreams <= TimeSpan.Zero)
                return;
            if (lookupFailed && FailedLookupTtl < noStreams)
                noStreams = FailedLookupTtl;
            lifetime = noStreams < lifetime ? noStreams : lifetime;
        }

        if (lifetime <= TimeSpan.Zero)
            return;

        cache.Set(MarkKey(key), new Mark(NextSequence(), NoStreams: streamCount == 0), lifetime);
    }

    /// <summary>
    /// The state of the mark behind the key. A mark older than the movie/episode's last
    /// <see cref="Reset"/> does not count.
    /// </summary>
    public StreamSyncState Get(string key, Guid itemId)
    {
        if (!cache.TryGetValue(MarkKey(key), out Mark? mark) || mark is null)
            return StreamSyncState.Due;

        if (cache.TryGetValue(ResetKey(itemId), out long resetAt) && mark.Sequence < resetAt)
            return StreamSyncState.Due;

        return mark.NoStreams ? StreamSyncState.NoStreams : StreamSyncState.Synced;
    }

    /// <summary>
    /// Makes the next visit of the movie/episode sync its streams again, for every user.
    /// </summary>
    public void Reset(Guid itemId)
    {
        var lifetime = streamTtl();
        if (lifetime <= TimeSpan.Zero)
            return;
        cache.Set(ResetKey(itemId), NextSequence(), lifetime);
    }

    /// <summary>Whether the movie/episode was reset within <c>StreamTTL</c>.</summary>
    public bool WasReset(Guid itemId) => cache.TryGetValue(ResetKey(itemId), out long _);

    private static string MarkKey(string key) => $"streamsync:{key}";

    private static string ResetKey(Guid itemId) => $"streamsync-reset:{itemId}";
}
