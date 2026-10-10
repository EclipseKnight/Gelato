using Gelato.Services;
using Xunit;

namespace Gelato.Tests;

public class AirDateTests
{
    private static readonly DateTime Now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Future_date_has_not_aired() =>
        Assert.True(AirDate.NotYetAired(Now.AddHours(1), Now));

    [Fact]
    public void Past_or_present_date_has_aired()
    {
        Assert.False(AirDate.NotYetAired(Now.AddMinutes(-1), Now));
        Assert.False(AirDate.NotYetAired(Now, Now));
    }

    [Fact]
    public void Today_at_midnight_has_aired() =>
        Assert.False(AirDate.NotYetAired(new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Unspecified), Now));

    [Fact]
    public void No_date_is_looked_up() => Assert.False(AirDate.NotYetAired(null, Now));

    [Fact]
    public void Unspecified_kind_is_read_as_utc() =>
        Assert.True(AirDate.NotYetAired(new DateTime(2026, 10, 9, 13, 0, 0, DateTimeKind.Unspecified), Now));
}
