using Gelato.Services;
using Xunit;

namespace Gelato.Tests;

public class FiledSeasonTests
{
    [Theory]
    [InlineData("gelato://stub/mal:61967:1", "tt7441658", true)]
    [InlineData("gelato://stub/kitsu:9:3", "kitsu:1", true)]
    [InlineData("gelato://stub/kitsu:1:3", "kitsu:1", false)]
    [InlineData("gelato://stub/tt0000001:2:1", "tt7441658", false)]
    [InlineData("/media/show/s01e01.mkv", "tt7441658", false)]
    [InlineData(null, "tt7441658", false)]
    public void FiledFromOtherEntry(string? path, string syncing, bool expected) =>
        Assert.Equal(expected, SplitSeason.IsFiledFromOtherEntry(path, syncing));
}
