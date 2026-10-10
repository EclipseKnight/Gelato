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
            var fresh = File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < MaxAge && _url is null;
            if (!fresh)
            {
                try
                {
                    var bytes = await Http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
                    using (var check = new MemoryStream(bytes))
                        _ = AnimeSeasonMap.Parse(check);
                    var tmp = file + ".tmp";
                    await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
                    File.Move(tmp, file, true);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    log.LogWarning("Anime mapping list could not be downloaded ({Message}); using the last copy if there is one", ex.Message);
                }
            }

            if (!File.Exists(file))
                return null;
            await using var stream = File.OpenRead(file);
            _map = AnimeSeasonMap.Parse(stream);
            _url = url;
            _loaded = DateTime.UtcNow;
            log.LogInformation("Anime mapping list loaded: {Count} season links", _map.Count);
            return _map;
        }
        finally
        {
            Gate.Release();
        }
    }
}
