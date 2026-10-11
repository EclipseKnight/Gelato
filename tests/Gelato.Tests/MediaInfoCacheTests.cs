using Gelato.Services;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Gelato.Tests;

public class MediaInfoCacheTests
{
    private static MediaInfoCache.Entry E(long ticks) =>
        new(ticks, "mkv", 1, 1, 1920, 1080, 0, false, [new MediaStream { Type = MediaStreamType.Video, Index = 0 }]);

    [Theory]
    [InlineData("ABC", 2, 5L, "abc:2")]
    [InlineData("abc", null, 734003200L, "abc:s734003200")]
    [InlineData("abc", null, null, null)]
    [InlineData("abc", null, 0L, null)]
    [InlineData(null, 2, 5L, null)]
    [InlineData(" ", 1, null, null)]
    public void KeyIsInfoHashAndFileOrSize(string? hash, int? idx, long? size, string? expected) =>
        Assert.Equal(expected, MediaInfoCache.Key(hash, idx, size));

    [Fact]
    public void TrimDropsLeastRecentlyUsed()
    {
        MediaInfoCache.Clear();
        MediaInfoCache.Put("a:1", E(1));
        Thread.Sleep(5);
        MediaInfoCache.Put("b:2", E(2));
        Thread.Sleep(5);
        MediaInfoCache.Get("a:1");
        MediaInfoCache.Trim(1);
        Assert.NotNull(MediaInfoCache.Get("a:1"));
        Assert.Null(MediaInfoCache.Get("b:2"));
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

    [Fact]
    public void FileIsWrittenAtMostOnceAMinute()
    {
        var t = new DateTime(2026, 10, 11, 7, 0, 0, DateTimeKind.Utc);
        Assert.True(MediaInfoCache.SaveDue(null, t));
        Assert.False(MediaInfoCache.SaveDue(t, t.AddSeconds(59)));
        Assert.True(MediaInfoCache.SaveDue(t, t.AddMinutes(1)));
    }

    [Fact]
    public void FullCacheStaysUnderFiftyMegabytes()
    {
        // A typical file: 1 video, 3 audio and 10 subtitle tracks, filled in like a real probe.
        var streams = new List<MediaStream> { new() { Type = MediaStreamType.Video, Index = 0, Codec = "hevc", Width = 1920, Height = 1080, BitRate = 8_000_000, Profile = "Main 10", PixelFormat = "yuv420p10le", Title = "1080p HEVC" } };
        for (var i = 1; i <= 3; i++)
            streams.Add(new() { Type = MediaStreamType.Audio, Index = i, Codec = "eac3", Language = "jpn", Channels = 6, ChannelLayout = "5.1", SampleRate = 48000, Title = "Japanese 5.1" });
        for (var i = 4; i <= 13; i++)
            streams.Add(new() { Type = MediaStreamType.Subtitle, Index = i, Codec = "ass", Language = "eng", Title = "English (Full)" });
        var entry = E(1) with { Streams = streams };
        var bytes = System.Text.Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(entry));
        Assert.True((long)bytes * MediaInfoCache.MaxEntries <= 50L * 1024 * 1024, $"{bytes} bytes per entry");
    }

    [Fact]
    public void ThrottledEntriesReachTheFileOnFlush()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gelato-cache-" + Guid.NewGuid());
        try
        {
            MediaInfoCache.Reset();
            MediaInfoCache.Init(dir);
            MediaInfoCache.Put("a:1", E(1)); // first write goes straight out
            MediaInfoCache.Put("b:2", E(2)); // within the minute: kept in memory
            var file = Path.Combine(dir, "media-info-cache.json");
            Assert.DoesNotContain("b:2", File.ReadAllText(file));
            MediaInfoCache.Flush();
            Assert.Contains("b:2", File.ReadAllText(file));
        }
        finally
        {
            MediaInfoCache.Reset();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void AnUnwritableFolderNeverFailsAPut()
    {
        var blocker = Path.GetTempFileName(); // a file where the folder should be
        try
        {
            MediaInfoCache.Reset();
            MediaInfoCache.Init(Path.Combine(blocker, "sub"));
            Assert.Null(MediaInfoCache.File_);
            MediaInfoCache.Put("a:1", E(1));
            MediaInfoCache.Flush();
            Assert.NotNull(MediaInfoCache.Get("a:1"));
        }
        finally
        {
            MediaInfoCache.Reset();
            File.Delete(blocker);
        }
    }

    [Fact]
    public void AFailedWriteIsKeptOwedAndNeverThrows()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gelato-cache-" + Guid.NewGuid());
        try
        {
            MediaInfoCache.Reset();
            MediaInfoCache.Init(dir);
            Directory.CreateDirectory(Path.Combine(dir, "media-info-cache.json")); // the move onto it fails
            MediaInfoCache.Put("a:1", E(1));
            Assert.True(MediaInfoCache.Dirty);
            MediaInfoCache.Flush();
            Assert.True(MediaInfoCache.Dirty);
            Assert.NotNull(MediaInfoCache.Get("a:1"));
        }
        finally
        {
            MediaInfoCache.Reset();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void InitDropsLegacyKeysWithoutAFileIndex()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gelato-cache-" + Guid.NewGuid());
        try
        {
            MediaInfoCache.Reset();
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                Path.Combine(dir, "media-info-cache.json"),
                System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, MediaInfoCache.Entry> { ["abc:-"] = E(1), ["abc:2"] = E(2) })
            );
            MediaInfoCache.Init(dir);
            Assert.Null(MediaInfoCache.Get("abc:-"));
            Assert.NotNull(MediaInfoCache.Get("abc:2"));
            Assert.Equal(1, MediaInfoCache.Count);
        }
        finally
        {
            MediaInfoCache.Reset();
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, false, false, false)]
    public void CacheRowsAreNotSubmittedToRemuxDb(bool contribute, bool wasRemuxDb, bool fromCache, bool expected) =>
        Assert.Equal(expected, Gelato.RemuxDb.RemuxDbService.Submits(contribute, wasRemuxDb, fromCache));
}
