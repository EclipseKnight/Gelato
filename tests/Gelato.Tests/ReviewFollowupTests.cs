using Gelato;
using Gelato.Services;
using Xunit;

namespace Gelato.Tests;

public class ReviewFollowupTests
{
    [Theory]
    [InlineData("2011\u20132014", StremioStatus.Ended)]
    [InlineData("2011\u2013", StremioStatus.Continuing)]
    [InlineData("2011-2014", StremioStatus.Ended)]
    public void YearRangeWithEnDash(string releaseInfo, StremioStatus expected)
    {
        var meta = new StremioMeta { Id = "tt1", ReleaseInfo = releaseInfo };
        Assert.Equal(expected, meta.GetStatedStatus());
        Assert.Equal(expected, meta.GetStatus());
    }

    [Theory]
    [InlineData("https://example.com/list.json", true)]
    [InlineData("http://example.com/list.json", false)]
    [InlineData("http://172.17.0.1:9000/list.json", true)]
    [InlineData("http://localhost/list.json", true)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("not a url", false)]
    public void MappingListMustBeHttpsOrLocal(string url, bool expected) =>
        Assert.Equal(expected, AnimeSeasonMapStore.IsAllowedUrl(url));
}
