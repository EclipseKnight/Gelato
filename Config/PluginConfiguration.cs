using System.Text.Json.Serialization;
using System.Xml.Serialization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Logging;

namespace Gelato.Config;

public class PluginConfiguration : BasePluginConfiguration
{
    public string MoviePath { get; set; } = Path.Combine(Path.GetTempPath(), "gelato", "movies");
    public string SeriesPath { get; set; } = Path.Combine(Path.GetTempPath(), "gelato", "series");
    public int StreamTTL { get; set; } = 3600;

    /// <summary>
    /// Seconds a sync that found no streams is remembered for that user and title, so calls in
    /// between answer at once instead of asking AIOStreams again (and waiting for its slowest
    /// addon). Kept short so streams that appear later are found soon. 0 turns it off; it is
    /// never longer than <see cref="StreamTTL"/>.
    /// </summary>
    public int NoStreamsTTL { get; set; } = 600;

    /// <summary>
    /// When a user's streams for a title are due again (<see cref="StreamTTL"/> passed) and that
    /// user still has the rows from the last sync, a details read answers with those rows at once
    /// and the sync runs in the background. Playback calls still wait for the sync, also when they
    /// name one stream row (a version opened as an item), so what is played and probed is always
    /// the fresh list.
    /// </summary>
    public bool RefreshStreamsInBackground { get; set; } = true;

    public int CatalogMaxItems { get; set; } = 100;
    public string Url { get; set; } = "";
    public bool EnableMixed { get; set; } = false;
    public bool ExtendLocalSeriesTrees { get; set; } = false;

    /// <summary>
    /// Days between tree sync runs that also check series which are not continuing (ended,
    /// unreleased, no status), so shows that come back are found. 0 turns the pass off.
    /// </summary>
    public int FullSeriesSyncDays { get; set; } = 7;

    /// <summary>
    /// File an anime catalogue entry that is a season of a show in the library ("Black Clover
    /// Season 2") as that season of the show, instead of importing it as a show of its own.
    /// </summary>
    public bool FileSplitSeasons { get; set; } = true;

    /// <summary>
    /// Titles that are never created (one id each: tt…, kitsu:…, mal:…, tmdb:…, tvdb:…, with an
    /// optional "# note"). Filled in by hand; a deleted title is not added automatically.
    /// </summary>
    public string[] ExcludedIds { get; set; } = [];

    /// <summary>The anime id mapping list (Fribb/anime-lists format) used for that.</summary>
    public string AnimeMappingUrl { get; set; } =
        "https://raw.githubusercontent.com/Fribb/anime-lists/master/anime-list-full.json";

    /// <summary>When the last such pass finished (UTC). Set by the tree sync.</summary>
    /// <summary>Read once as a fallback; the time now lives in gelato/last-full-series-sync.txt.</summary>
    public DateTime? LastFullSeriesSync { get; set; }
    public bool FilterUnreleased { get; set; } = false;
    public int FilterUnreleasedBufferDays { get; set; } = 0;
    public bool DisableSourceCount { get; set; } = true;
    public string FFmpegAnalyzeDuration { get; set; } = "5M";
    public string FFmpegProbeSize { get; set; } = "40M";
    public bool CreateCollections { get; set; } = false;
    public int MaxCollectionItems { get; set; } = 100;
    public bool DisableSearch { get; set; } = false;
    public bool EnableJavaScriptInjection { get; set; } = false;
    public bool LazyImages { get; set; } = false;
    public List<CatalogConfig> Catalogs { get; set; } = [];
    public List<UserConfig> UserConfigs { get; set; } = [];

    /// <summary>
    /// Fill in a stream's tracks, runtime and size from RemuxDB when its streams are synced,
    /// so they show before playback and playback skips its probe.
    /// </summary>
    public bool RemuxDbEnabled { get; set; } = true;

    /// <summary>
    /// Keep probe results on this server by file, so a file probed once shows its tracks for
    /// every stream row and user that gets the same file. Nothing is sent anywhere.
    /// </summary>
    public bool MediaInfoCacheEnabled { get; set; } = true;

    /// <summary>
    /// Probe a stream before it is played: when its item is opened or another version of it is
    /// picked, and the next episode while an episode nears its end. Playback of a stream
    /// that needs a probe then starts without waiting for it.
    /// </summary>
    public bool PreProbe { get; set; } = true;

    /// <summary>
    /// Submit a stream's probe to RemuxDB when it played a file RemuxDB did not know. Anonymous,
    /// and only for streams whose torrent is known.
    /// </summary>
    public bool RemuxDbContribute { get; set; } = true;

    public string RemuxDbUrl { get; set; } = RemuxDb.RemuxDbClient.DefaultUrl;

    /// <summary>
    /// Random id RemuxDB requires of every client, created on first use. Tied to nothing else.
    /// </summary>
    public string RemuxDbClientId { get; set; } = "";

    /// <summary>
    /// The Jellyfin version Gelato last started against, so it can tell when the server has been
    /// upgraded underneath it. Empty until the first start that records one.
    /// </summary>
    public string LastSeenServerVersion { get; set; } = "";

    public string GetBaseUrl()
    {
        if (string.IsNullOrWhiteSpace(Url))
            throw new InvalidOperationException("Gelato Url not configured.");

        var u = Url.Trim().TrimEnd('/');

        if (u.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase))
            u = u[..^"/manifest.json".Length];

        return u;
    }

    [JsonIgnore]
    [XmlIgnore]
    public GelatoStremioProvider? Stremio;

    [JsonIgnore]
    [XmlIgnore]
    public Folder? MovieFolder;

    [JsonIgnore]
    [XmlIgnore]
    public Folder? SeriesFolder;

    public PluginConfiguration GetEffectiveConfig(Guid userId)
    {
        var userConfig = UserConfigs.FirstOrDefault(u => u.UserId == userId);
        return userConfig is null ? this : userConfig.ApplyOverrides(this);
    }

    /// <summary>
    /// Every folder Gelato seeds a stub file into: the base movie and series paths plus each
    /// per-user override. A path configured more than once is returned once.
    /// </summary>
    public IReadOnlyList<GelatoLibraryPath> GetLibraryPaths()
    {
        var seen = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal
        );
        var paths = new List<GelatoLibraryPath>();

        void Add(string label, string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            string key;
            try
            {
                key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            }
            catch (Exception)
            {
                key = path;
            }

            if (seen.Add(key))
                paths.Add(new GelatoLibraryPath(label, path));
        }

        Add("Movies", MoviePath);
        Add("Series", SeriesPath);
        foreach (var user in UserConfigs)
        {
            Add("Movies (user override)", user.MoviePath);
            Add("Series (user override)", user.SeriesPath);
        }

        return paths;
    }
}

/// <summary>A folder Gelato uses as a library location.</summary>
public sealed record GelatoLibraryPath(string Label, string Path);

public class UserConfig
{
    public Guid UserId { get; set; }
    public string Url { get; set; } = "";
    public string MoviePath { get; set; } = "";
    public string SeriesPath { get; set; } = "";
    public bool DisableSearch { get; set; } = false;

    /// <summary>
    /// Apply user overrides to base configuration - replaces all overridable fields
    /// </summary>
    public PluginConfiguration ApplyOverrides(PluginConfiguration baseConfig)
    {
        return new PluginConfiguration
        {
            // User overridable fields - all required, no fallback to baseConfig
            Url = Url,
            MoviePath = MoviePath,
            SeriesPath = SeriesPath,
            DisableSearch = DisableSearch,

            // All other fields from base config
            StreamTTL = baseConfig.StreamTTL,
            NoStreamsTTL = baseConfig.NoStreamsTTL,
            RefreshStreamsInBackground = baseConfig.RefreshStreamsInBackground,
            CatalogMaxItems = baseConfig.CatalogMaxItems,
            EnableMixed = baseConfig.EnableMixed,
            ExtendLocalSeriesTrees = baseConfig.ExtendLocalSeriesTrees,
            FilterUnreleased = baseConfig.FilterUnreleased,
            FilterUnreleasedBufferDays = baseConfig.FilterUnreleasedBufferDays,
            DisableSourceCount = baseConfig.DisableSourceCount,
            FFmpegAnalyzeDuration = baseConfig.FFmpegAnalyzeDuration,
            FFmpegProbeSize = baseConfig.FFmpegProbeSize,
            CreateCollections = baseConfig.CreateCollections,
            MaxCollectionItems = baseConfig.MaxCollectionItems,
            RemuxDbEnabled = baseConfig.RemuxDbEnabled,
            MediaInfoCacheEnabled = baseConfig.MediaInfoCacheEnabled,
            PreProbe = baseConfig.PreProbe,
            RemuxDbContribute = baseConfig.RemuxDbContribute,
            RemuxDbUrl = baseConfig.RemuxDbUrl,
            RemuxDbClientId = baseConfig.RemuxDbClientId,
            UserConfigs = baseConfig.UserConfigs,
        };
    }
}

public class GelatoStremioProviderFactory(IHttpClientFactory http, ILoggerFactory log)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<
        string,
        GelatoStremioProvider
    > _cache = new(StringComparer.OrdinalIgnoreCase);

    public GelatoStremioProvider Create(Guid userId)
    {
        var cfg = GelatoPlugin.Instance!.Configuration.GetEffectiveConfig(userId);
        return Create(cfg);
    }

    public GelatoStremioProvider Create(PluginConfiguration cfg)
    {
        var baseUrl = cfg.GetBaseUrl();
        return _cache.GetOrAdd(
            baseUrl,
            url => new GelatoStremioProvider(url, http, log.CreateLogger<GelatoStremioProvider>())
        );
    }

    public void ClearCache() => _cache.Clear();
}

public class CatalogConfig
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "movie";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = false;

    /// <summary>0 means "use global CatalogMaxItems".</summary>
    public int MaxItems { get; set; } = 0;
    public bool CreateCollection { get; set; } = false;
    public string Url { get; set; } = "";
}
