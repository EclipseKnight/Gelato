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
