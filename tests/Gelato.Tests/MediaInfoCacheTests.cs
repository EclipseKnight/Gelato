using Gelato.Services;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Gelato.Tests;

public class MediaInfoCacheTests
{
    private static MediaInfoCache.Entry E(long ticks) =>
        new(ticks, "mkv", 1, 1, 1920, 1080, 0, false, [new MediaStream { Type = MediaStreamType.Video, Index = 0 }]);

    [Theory]
    [InlineData("ABC", 2, "abc:2")]
    [InlineData("abc", null, "abc:-")]
    [InlineData(null, 2, null)]
    [InlineData(" ", 1, null)]
    public void KeyIsInfoHashAndFile(string? hash, int? idx, string? expected) =>
        Assert.Equal(expected, MediaInfoCache.Key(hash, idx));

    [Fact]
    public void TrimDropsLeastRecentlyUsed()
    {
        MediaInfoCache.Clear();
        MediaInfoCache.Put("a:-", E(1));
        Thread.Sleep(5);
        MediaInfoCache.Put("b:-", E(2));
        Thread.Sleep(5);
        MediaInfoCache.Get("a:-");
        MediaInfoCache.Trim(1);
        Assert.NotNull(MediaInfoCache.Get("a:-"));
        Assert.Null(MediaInfoCache.Get("b:-"));
        MediaInfoCache.Clear();
    }

    [Fact]
    public void EntryRoundTripsThroughJson()
    {
        var e = E(42) with { Streams = [new MediaStream { Type = MediaStreamType.Audio, Index = 1, Codec = "eac3", Language = "jpn", Channels = 6 }] };
        var back = System.Text.Json.JsonSerializer.Deserialize<MediaInfoCache.Entry>(System.Text.Json.JsonSerializer.Serialize(e))!;
        Assert.Equal(42, back.RunTimeTicks);
        var a = Assert.Single(back.Streams);
        Assert.Equal((MediaStreamType.Audio, 1, "eac3", "jpn", (int?)6), (a.Type, a.Index, a.Codec, a.Language, a.Channels));
    }
}
