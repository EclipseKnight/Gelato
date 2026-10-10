using Gelato;
using Xunit;

namespace Gelato.Tests;

public class FreshMetaTests
{
    private static readonly DateTime Now = new(2026, 10, 10, 6, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(30, false, true)]
    [InlineData(30, true, true)]
    [InlineData(120, false, true)]
    [InlineData(120, true, false)]
    public void FreshAsksAgainOnlyForOlderCopies(int ageSeconds, bool fresh, bool expected) =>
        Assert.Equal(
            expected,
            GelatoStremioProvider.CachedMetaUsable(Now.AddMinutes(3), Now.AddSeconds(-ageSeconds), Now, fresh)
        );

    [Fact]
    public void ExpiredIsNeverUsed() =>
        Assert.False(GelatoStremioProvider.CachedMetaUsable(Now.AddSeconds(-1), Now, Now, false));
}
