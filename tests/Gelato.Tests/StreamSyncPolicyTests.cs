using Gelato.Decorators;
using Gelato.Services;
using Xunit;

namespace Gelato.Tests;

public class StreamSyncPolicyTests
{
    private static StreamSyncAction Decide(
        StreamSyncState state,
        bool refreshInBackground = true,
        bool isItemRead = true,
        bool wasReset = false,
        int knownRows = 3
    ) => StreamSyncPolicy.Decide(state, refreshInBackground, isItemRead, wasReset, () => knownRows);

    [Fact]
    public void Synced_recently_answers_from_the_rows()
    {
        Assert.Equal(StreamSyncAction.UseSynced, Decide(StreamSyncState.Synced));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Remembered_no_streams_answers_at_once_for_reads_and_playback(bool isItemRead)
    {
        Assert.Equal(
            StreamSyncAction.UseRememberedNoStreams,
            Decide(StreamSyncState.NoStreams, isItemRead: isItemRead, knownRows: 0)
        );
    }

    [Fact]
    public void Due_with_known_rows_on_a_details_read_serves_them_and_refreshes()
    {
        Assert.Equal(StreamSyncAction.ServeKnownRowsAndRefresh, Decide(StreamSyncState.Due));
    }

    [Fact]
    public void A_first_sync_waits()
    {
        Assert.Equal(StreamSyncAction.SyncNow, Decide(StreamSyncState.Due, knownRows: 0));
    }

    [Fact]
    public void Playback_waits_for_the_sync_even_with_known_rows()
    {
        Assert.Equal(StreamSyncAction.SyncNow, Decide(StreamSyncState.Due, isItemRead: false));
    }

    [Fact]
    public void The_setting_turns_background_refresh_off()
    {
        Assert.Equal(
            StreamSyncAction.SyncNow,
            Decide(StreamSyncState.Due, refreshInBackground: false)
        );
    }

    [Fact]
    public void A_reset_item_waits_for_a_real_sync()
    {
        Assert.Equal(StreamSyncAction.SyncNow, Decide(StreamSyncState.Due, wasReset: true));
    }

    [Fact]
    public void Rows_are_only_counted_when_they_decide_the_outcome()
    {
        var counted = 0;
        int Count()
        {
            counted++;
            return 2;
        }

        StreamSyncPolicy.Decide(StreamSyncState.Synced, true, true, false, Count);
        StreamSyncPolicy.Decide(StreamSyncState.NoStreams, true, true, false, Count);
        StreamSyncPolicy.Decide(StreamSyncState.Due, true, false, false, Count);
        StreamSyncPolicy.Decide(StreamSyncState.Due, false, true, false, Count);
        Assert.Equal(0, counted);

        StreamSyncPolicy.Decide(StreamSyncState.Due, true, true, false, Count);
        Assert.Equal(1, counted);
    }
}

/// <summary>Which calls on a stream row wait for a running sync of its movie/episode.</summary>
public class StreamRowWaitTests
{
    [Fact]
    public void Playback_on_a_stream_row_waits_for_a_running_sync()
    {
        Assert.True(
            StreamSyncPolicy.WaitsForRunningSync(
                isStreamRow: true,
                isItemRead: false,
                insideSync: false
            )
        );
    }

    [Fact]
    public void A_details_read_of_a_stream_row_does_not_wait()
    {
        Assert.False(
            StreamSyncPolicy.WaitsForRunningSync(
                isStreamRow: true,
                isItemRead: true,
                insideSync: false
            )
        );
    }

    [Fact]
    public void A_movie_or_episode_does_not_join_here()
    {
        // It goes through Decide, which starts or joins the sync itself.
        Assert.False(
            StreamSyncPolicy.WaitsForRunningSync(
                isStreamRow: false,
                isItemRead: false,
                insideSync: false
            )
        );
    }

    [Fact]
    public void A_call_made_by_the_sync_itself_never_waits_for_it()
    {
        // It would wait for itself forever.
        Assert.False(
            StreamSyncPolicy.WaitsForRunningSync(
                isStreamRow: true,
                isItemRead: false,
                insideSync: true
            )
        );
    }
}

/// <summary>When a finished sync is marked (StreamTTL or NoStreamsTTL), and when it is not.</summary>
public class SyncAndMarkTests
{
    private readonly List<int> _marks = [];

    private Task Run(
        Func<CancellationToken, Task<int>> sync,
        bool isMergedVersion = false,
        CancellationToken ct = default
    ) => StreamSyncPolicy.SyncAndMarkAsync(sync, isMergedVersion, _marks.Add, ct);

    [Theory]
    [InlineData(4)]
    [InlineData(0)]
    public async Task A_sync_that_finishes_is_marked_with_its_count(int count)
    {
        await Run(_ => Task.FromResult(count));
        Assert.Equal([count], _marks);
    }

    [Fact]
    public async Task A_merged_version_is_never_marked_as_zero()
    {
        // Its key is the movie it was merged into: a zero would hold back that movie's sync.
        await Run(_ => Task.FromResult(0), isMergedVersion: true);
        Assert.Empty(_marks);
    }

    [Fact]
    public async Task A_sync_that_throws_is_not_marked()
    {
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Run(_ => throw new HttpRequestException("AIOStreams answered 502"))
        );
        Assert.Empty(_marks);
    }

    [Fact]
    public async Task A_cancelled_sync_is_not_marked()
    {
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Run(
                async token =>
                {
                    await cts.CancelAsync();
                    token.ThrowIfCancellationRequested();
                    return 3;
                },
                ct: cts.Token
            )
        );
        Assert.Empty(_marks);
    }
}

/// <summary>
/// A call that plays a stream row a sync has deleted (typically the sync it just waited for)
/// answers as the movie would: its first remaining version, not a source whose item is gone.
/// </summary>
public class StreamRowGoneTests
{
    private static bool AnswersAsMovie(
        bool isStreamRow = true,
        bool isItemRead = false,
        bool rowExists = false,
        bool movieExists = true
    ) => StreamSyncPolicy.AnswersAsMovie(isStreamRow, isItemRead, rowExists, movieExists);

    [Fact]
    public void Playing_a_row_the_sync_deleted_answers_as_its_movie()
    {
        Assert.True(AnswersAsMovie());
    }

    [Fact]
    public void A_row_the_sync_kept_answers_as_itself()
    {
        Assert.False(AnswersAsMovie(rowExists: true));
    }

    [Fact]
    public void A_details_read_is_not_affected()
    {
        // It only lists the versions; the client gets the 404 for the gone row as before.
        Assert.False(AnswersAsMovie(isItemRead: true));
    }

    [Fact]
    public void Without_its_movie_the_row_answers_as_itself()
    {
        Assert.False(AnswersAsMovie(movieExists: false));
    }

    [Fact]
    public void A_movie_or_episode_is_never_affected()
    {
        Assert.False(AnswersAsMovie(isStreamRow: false));
    }
}
