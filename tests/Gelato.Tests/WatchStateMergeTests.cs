using Gelato.Services;
using Xunit;

namespace Gelato.Tests;

public class WatchStateMergeTests
{
    private static readonly DateTime T = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Later_parked_play_wins()
    {
        Assert.True(WatchStateMerge.ParkedWins(T.AddDays(1), T));
        Assert.True(WatchStateMerge.ParkedWins(T, null));
    }

    [Fact]
    public void Held_row_stays_when_newer_equal_or_parked_never_played()
    {
        Assert.False(WatchStateMerge.ParkedWins(T, T.AddDays(1)));
        Assert.False(WatchStateMerge.ParkedWins(T, T));
        Assert.False(WatchStateMerge.ParkedWins(null, T));
        Assert.False(WatchStateMerge.ParkedWins(null, null));
    }
}
