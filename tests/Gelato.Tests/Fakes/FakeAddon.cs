using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Gelato.Tests.Fakes;

/// <summary>
/// A small Stremio add-on for tests. It answers each request with the fixture file at the same
/// path under the current fixture folder (<c>/catalog/series/anime.json</c> is
/// <c>&lt;folder&gt;/catalog/series/anime.json</c>), and 404 when there is none. A <c>:</c> in the path is <c>_</c> in the file name.
/// Point it at another folder to play the next "import" of a scenario.
/// </summary>
public sealed class FakeAddon : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private volatile string _root;

    public FakeAddon(string fixtureFolder)
    {
        _root = fixtureFolder;
        var port = FreePort();
        BaseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    public string BaseUrl { get; }

    /// <summary>Paths asked for, in order, with the User-Agent sent.</summary>
    public ConcurrentQueue<(string Path, string? UserAgent)> Requests { get; } = new();

    /// <summary>Waits this long before answering paths that contain the key.</summary>
    public ConcurrentDictionary<string, TimeSpan> Delays { get; } = new();

    /// <summary>Answers paths that contain the key with this status and no body.</summary>
    public ConcurrentDictionary<string, HttpStatusCode> Failures { get; } = new();

    public void UseFixtures(string fixtureFolder) => _root = fixtureFolder;

    public static string Fixture(params string[] parts) =>
        Path.Combine([AppContext.BaseDirectory, "Fixtures", .. parts]);

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return;
            }
            _ = Task.Run(() => AnswerAsync(ctx));
        }
    }

    private async Task AnswerAsync(HttpListenerContext ctx)
    {
        var path = Uri.UnescapeDataString(ctx.Request.Url!.AbsolutePath);
        Requests.Enqueue((path, ctx.Request.UserAgent));
        try
        {
            foreach (var (key, delay) in Delays)
                if (path.Contains(key, StringComparison.Ordinal))
                    await Task.Delay(delay, _stop.Token).ConfigureAwait(false);

            foreach (var (key, status) in Failures)
                if (path.Contains(key, StringComparison.Ordinal))
                {
                    ctx.Response.StatusCode = (int)status;
                    ctx.Response.Close();
                    return;
                }

            var file = Path.Combine(_root, path.TrimStart('/').Replace(':', '_'));
            if (!File.Exists(file))
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }
            var body = Encoding.UTF8.GetBytes(await File.ReadAllTextAsync(file).ConfigureAwait(false));
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = body.Length;
            await ctx.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
            ctx.Response.Close();
        }
        catch (Exception)
        {
            try { ctx.Response.Abort(); } catch (Exception) { }
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch (Exception) { }
        _stop.Dispose();
    }
}

/// <summary>Hands out plain HttpClients, as Jellyfin's factory does for Gelato.</summary>
public sealed class PlainHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}
