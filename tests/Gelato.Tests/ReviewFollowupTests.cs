using System.Net;
using System.Net.Sockets;
using System.Text;
using Gelato;
using Gelato.Services;
using Microsoft.Extensions.Logging.Abstractions;
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
    [InlineData("http://nas.local/list.json", false)]
    [InlineData("http://lists:8080/list.json", false)]
    [InlineData("https://nas.local/list.json", true)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("not a url", false)]
    public void MappingListMustBeHttpsOrLocal(string url, bool expected) =>
        Assert.Equal(expected, AnimeSeasonMapStore.IsAllowedUrl(url));

    [Fact]
    public async Task Mapping_list_download_times_out_when_the_body_stalls()
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var stall = new TaskCompletionSource();
        _ = Task.Run(async () =>
        {
            using var client = await server.AcceptTcpClientAsync();
            var stream = client.GetStream();
            await stream.ReadAsync(new byte[4096]);
            // Headers promise a body, then nothing more is sent.
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 1000\r\n\r\n["));
            await stall.Task;
        });

        var folder = Path.Combine(Path.GetTempPath(), "gelato-map-" + Guid.NewGuid().ToString("N"));
        var old = AnimeSeasonMapStore.DownloadTimeout;
        AnimeSeasonMapStore.DownloadTimeout = TimeSpan.FromSeconds(1);
        try
        {
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var map = await AnimeSeasonMapStore.GetAsync(
                $"http://127.0.0.1:{port}/{Guid.NewGuid():N}.json", folder, NullLogger.Instance, guard.Token);
            Assert.Null(map);
            Assert.False(guard.IsCancellationRequested);
        }
        finally
        {
            AnimeSeasonMapStore.DownloadTimeout = old;
            stall.TrySetResult();
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task Mapping_list_timeout_falls_back_to_the_last_saved_copy()
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var stall = new TaskCompletionSource();
        _ = Task.Run(async () =>
        {
            using var client = await server.AcceptTcpClientAsync();
            var stream = client.GetStream();
            await stream.ReadAsync(new byte[4096]);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 1000\r\n\r\n["));
            await stall.Task;
        });

        // A saved copy from another URL, so the store has to try the download first.
        var folder = Path.Combine(Path.GetTempPath(), "gelato-map-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "anime-list-full.json");
        await File.WriteAllTextAsync(file, """[{ "mal_id": 61967, "tvdb_id": 331753, "season": { "tvdb": 2 } }]""");
        await File.WriteAllTextAsync(file + ".url", "http://127.0.0.1:1/old.json");
        var old = AnimeSeasonMapStore.DownloadTimeout;
        AnimeSeasonMapStore.DownloadTimeout = TimeSpan.FromSeconds(1);
        try
        {
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var map = await AnimeSeasonMapStore.GetAsync(
                $"http://127.0.0.1:{port}/{Guid.NewGuid():N}.json", folder, NullLogger.Instance, guard.Token);
            Assert.NotNull(map);
            var link = map.Find("mal:61967");
            Assert.NotNull(link);
            Assert.Equal((331753, 2), (link.TvdbId, link.Season));
            Assert.False(guard.IsCancellationRequested);
            Assert.Equal("http://127.0.0.1:1/old.json", await File.ReadAllTextAsync(file + ".url"));
        }
        finally
        {
            AnimeSeasonMapStore.DownloadTimeout = old;
            stall.TrySetResult();
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
        }
    }
}
