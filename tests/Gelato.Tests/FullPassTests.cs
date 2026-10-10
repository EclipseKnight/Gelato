using Gelato.Services;
using Xunit;

namespace Gelato.Tests;

public class FullPassTests
{
    [Theory]
    [InlineData(0, 0, 0, true)]
    [InlineData(10, 0, 0, true)]
    [InlineData(10, 4, 0, true)]
    [InlineData(10, 3, 2, false)]
    [InlineData(10, 9, 0, false)]
    public void CountsAsDoneOnlyWhenMostSynced(int total, int failed, int noMeta, bool expected) =>
        Assert.Equal(expected, SeriesStatusMap.FullPassSucceeded(total, failed, noMeta));

    [Fact]
    public void StateFileRoundTripsAndFallsBack()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var old = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        Assert.Equal(old, FullPassState.Read(dir, old));
        var now = new DateTime(2026, 10, 10, 6, 0, 0, DateTimeKind.Utc);
        FullPassState.Write(dir, now);
        Assert.Equal(now, FullPassState.Read(dir, old));
        Directory.Delete(dir, true);
    }
}
