using System.Text.Json;
using Gelato.Services;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Gelato.Tests;

public class SeriesStatusTests
{
    [Theory]
    [InlineData("Continuing", StremioStatus.Continuing)]
    [InlineData("Returning Series", StremioStatus.Continuing)]
    [InlineData("current", StremioStatus.Continuing)]
    [InlineData("Currently Airing", StremioStatus.Continuing)]
    [InlineData("Ended", StremioStatus.Ended)]
    [InlineData("finished", StremioStatus.Ended)]
    [InlineData("Canceled", StremioStatus.Ended)]
    [InlineData("upcoming", StremioStatus.Upcoming)]
    [InlineData("tba", StremioStatus.Upcoming)]
    [InlineData(" Ended ", StremioStatus.Ended)]
    [InlineData("something else", StremioStatus.Unknown)]
    [InlineData("", StremioStatus.Unknown)]
    [InlineData(null, StremioStatus.Unknown)]
    public void Reads_the_words_addons_send(string? word, StremioStatus expected) =>
        Assert.Equal(expected, SeriesStatusMap.Parse(word));

    [Theory]
    [InlineData("{\"id\":\"tt1\",\"status\":\"current\"}", StremioStatus.Continuing)]
    [InlineData("{\"id\":\"tt1\",\"status\":\"Returning Series\"}", StremioStatus.Continuing)]
    [InlineData("{\"id\":\"tt1\",\"status\":\"finished\"}", StremioStatus.Ended)]
    [InlineData("{\"id\":\"tt1\",\"status\":42}", StremioStatus.Unknown)]
    [InlineData("{\"id\":\"tt1\",\"status\":\"odd\"}", StremioStatus.Unknown)]
    public void Meta_status_field_is_parsed(string json, StremioStatus expected)
    {
        var meta = JsonSerializer.Deserialize<StremioMeta>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(expected, meta.Status);
    }

    [Fact]
    public void Changed_only_when_the_addon_states_something_new()
    {
        Assert.Equal(SeriesStatus.Continuing, SeriesStatusMap.Changed(SeriesStatus.Ended, StremioStatus.Continuing));
        Assert.Equal(SeriesStatus.Ended, SeriesStatusMap.Changed(null, StremioStatus.Ended));
        Assert.Null(SeriesStatusMap.Changed(SeriesStatus.Ended, StremioStatus.Ended));
        Assert.Null(SeriesStatusMap.Changed(SeriesStatus.Continuing, StremioStatus.Unknown));
        Assert.Null(SeriesStatusMap.Changed(SeriesStatus.Continuing, null));
    }

    [Theory]
    [InlineData(null, "2014-", StremioStatus.Continuing)]
    [InlineData(null, "2014-2019", StremioStatus.Ended)]
    [InlineData("Ended", "2014-", StremioStatus.Ended)]
    [InlineData(null, "2014", null)]
    [InlineData(null, null, null)]
    public void Stated_status_ignores_guesses(string? status, string? releaseInfo, StremioStatus? expected)
    {
        var meta = new StremioMeta { Id = "tt1", ReleaseInfo = releaseInfo };
        meta.Status = status is null ? null : SeriesStatusMap.Parse(status);
        Assert.Equal(expected, meta.GetStatedStatus());
    }

    [Fact]
    public void Weekly_pass_is_due_after_the_interval()
    {
        var now = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(SeriesStatusMap.FullPassDue(null, now, 7));
        Assert.True(SeriesStatusMap.FullPassDue(now.AddDays(-7), now, 7));
        Assert.False(SeriesStatusMap.FullPassDue(now.AddDays(-6), now, 7));
        Assert.False(SeriesStatusMap.FullPassDue(null, now, 0));
    }
}
