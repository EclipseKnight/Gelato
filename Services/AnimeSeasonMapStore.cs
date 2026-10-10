using Microsoft.Extensions.Logging;

namespace Gelato.Services;

/// <summary>
/// Keeps the anime mapping list: downloaded to the plugin's data folder, refreshed once a day,
/// the last good copy used when a download fails.
/// </summary>
public static class AnimeSeasonMapStore
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);

    /// <summary>The full Fribb list is a few MB; anything far larger is not it.</summary>
    public const long MaxBytes = 64L * 1024 * 1024;
    private static AnimeSeasonMap? _map;
    private static string? _url;
    private static DateTime _loaded;

    public static async Task<AnimeSeasonMap?> GetAsync(string? url, string folder, ILogger log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        if (_map is not null && _url == url && DateTime.UtcNow - _loaded < MaxAge)
            return _map;

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_map is not null && _url == url && DateTime.UtcNow - _loaded < MaxAge)
                return _map;

            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "anime-list-full.json");
            // The URL the saved copy came from, so a changed URL is fetched even after a restart.
            var urlFile = file + ".url";
            var savedUrl = File.Exists(urlFile) ? File.ReadAllText(urlFile).Trim() : null;
            var fresh = File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < MaxAge
                && _url is null && savedUrl == url;
            var failed = false;
            if (!fresh)
            {
                try
                {
                    if (!IsAllowedUrl(url))
                        throw new InvalidOperationException("the mapping list URL must be https (http only on a local address)");
                    using var response = await Http
                        .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                        .ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength > MaxBytes)
                        throw new InvalidOperationException("the mapping list is too large");
                    var bytes = await ReadCappedAsync(response.Content, ct).ConfigureAwait(false);
                    using (var check = new MemoryStream(bytes))
                        _ = AnimeSeasonMap.Parse(check);
                    var tmp = file + ".tmp";
                    await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
                    File.Move(tmp, file, true);
                    await File.WriteAllTextAsync(urlFile, url, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    failed = true;
                    log.LogWarning("Anime mapping list could not be downloaded ({Message}); using the last copy if there is one", ex.Message);
                }
            }

            if (!File.Exists(file))
                return null;
            await using var stream = File.OpenRead(file);
            _map = AnimeSeasonMap.Parse(stream);
            _url = url;
            // After a failed download the last copy is used, but asked for again within the hour.
            _loaded = failed ? DateTime.UtcNow - MaxAge + RetryAfter : DateTime.UtcNow;
            log.LogInformation("Anime mapping list loaded: {Count} season links", _map.Count);
            return _map;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>https, or plain http to a loopback or private address (a list served on the LAN).</summary>
    public static bool IsAllowedUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u))
            return false;
        if (u.Scheme == Uri.UriSchemeHttps)
            return true;
        if (u.Scheme != Uri.UriSchemeHttp)
            return false;
        if (u.IsLoopback)
            return true;
        if (!System.Net.IPAddress.TryParse(u.Host, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }

    private static async Task<byte[]> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
                throw new InvalidOperationException("the mapping list is too large");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
