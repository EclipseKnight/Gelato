using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Gelato.Providers;
using Gelato.Services;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Gelato.Decorators;

public sealed class MediaSourceManagerDecorator(
    IMediaSourceManager inner,
    ILibraryManager libraryManager,
    ILogger<MediaSourceManagerDecorator> log,
    IHttpContextAccessor http,
    GelatoItemRepository repo,
    IDirectoryService directoryService,
    IServerConfigurationManager config,
    //Lazy<ISubtitleManager> subtitleManager,
    Lazy<GelatoManager> manager,
    Lazy<SubtitleProvider> subtitleProvider,
    IMediaSegmentManager mediaSegmentManager,
    IEnumerable<ICustomMetadataProvider<Video>> videoProbeProviders
) : IMediaSourceManager
{
    private static readonly Regex YearRegex = new(@"\b(19|20)\d{2}\b", RegexOptions.Compiled);

    private readonly IMediaSourceManager _inner =
        inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly ILogger<MediaSourceManagerDecorator> _log =
        log ?? throw new ArgumentNullException(nameof(log));
    private readonly IHttpContextAccessor _http =
        http ?? throw new ArgumentNullException(nameof(http));
    private readonly KeyLock _lock = new();
    private readonly IMediaSegmentManager _mediaSegmentManager =
        mediaSegmentManager ?? throw new ArgumentNullException(nameof(mediaSegmentManager));
    private readonly ILibraryManager _libraryManager =
        libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
    private readonly IServerConfigurationManager _config =
        config ?? throw new ArgumentNullException(nameof(config));
    private readonly Lazy<GelatoManager> _manager = manager;
    private readonly Lazy<SubtitleProvider> _subtitleProvider = subtitleProvider;

    //  private readonly Lazy<ISubtitleManager> _subtitleManager = subtitleManager ?? throw new ArgumentNullException(nameof(subtitleManager));
    private readonly ICustomMetadataProvider<Video>? _probeProvider =
        videoProbeProviders.FirstOrDefault(p => p.Name == "Probe Provider");

    public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(
        BaseItem item,
        bool enablePathSubstitution,
        User? user = null
    )
    {
        var manager = _manager.Value;
        _log.LogDebug("GetStaticMediaSources {Id}", item.Id);
        var ctx = _http.HttpContext;
        Guid userId;
        if (user != null)
        {
            userId = user.Id;
        }
        else
        {
            ctx.TryGetUserId(out userId);
        }

        var cfg = GelatoPlugin.Instance!.GetConfig(userId);
        if (
            (!cfg.EnableMixed && !item.IsGelato())
            || item.GetBaseItemKind() is not (BaseItemKind.Movie or BaseItemKind.Episode)
        )
        {
            return _inner.GetStaticMediaSources(item, enablePathSubstitution, user);
        }

        var uri = StremioUri.FromBaseItem(item);
        var actionName =
            ctx?.Items.TryGetValue("actionName", out var ao) == true ? ao as string : null;

        var allowSync = ctx.IsInsertableAction() && userId != Guid.Empty;
        var video = item as Video;
        var cacheKey = Guid.TryParse(video?.PrimaryVersionId, out var id)
            ? id.ToString()
            : item.Id.ToString();

        if (userId != Guid.Empty)
        {
            cacheKey = $"{userId.ToString()}:{cacheKey}";
        }

        if (!allowSync)
        {
            _log.LogDebug(
                "GetStaticMediaSources not a sync-eligible call. action={Action} uri={Uri}",
                actionName,
                uri?.ToString()
            );
        }
        else if (uri is not null && !manager.HasStreamSync(cacheKey))
        {
            // Bug in web UI that calls the detail page twice. So that's why there's a lock.
            _lock
                .RunSingleFlightAsync(
                    item.Id,
                    async ct =>
                    {
                        _log.LogDebug("GetStaticMediaSources refreshing streams for {Id}", item.Id);

                        // Prewarm subtitle cache in the background if Gelato Subtitles
                        // is enabled for this library.
                        var libraryOptions = _libraryManager.GetLibraryOptions(item);
                        var subtitlePrewarmEnabled =
                            libraryOptions.SubtitleDownloadLanguages?.Length > 0
                            && !libraryOptions.DisabledSubtitleFetchers.Contains(
                                "Gelato Subtitles",
                                StringComparer.OrdinalIgnoreCase
                            );

                        if (subtitlePrewarmEnabled)
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await _subtitleProvider
                                        .Value.GetSubtitlesAsync(
                                            uri.ExternalId,
                                            uri.MediaType,
                                            CancellationToken.None
                                        )
                                        .ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    _log.LogWarning(ex, "Subtitle prewarm failed for {Uri}", uri);
                                }
                            });
                        }

                        try
                        {
                            var count = await manager
                                .SyncStreams(item, userId, ct)
                                .ConfigureAwait(false);
                            if (count > 0)
                            {
                                manager.SetStreamSync(cacheKey);
                            }
                        }
                        catch (Exception ex)
                        {
                            _log.LogError(ex, "Failed to sync streams");
                        }
                    }
                )
                .GetAwaiter()
                .GetResult();

            // refresh item
            libraryManager.GetItemById(item.Id);
        }

        var sources = _inner.GetStaticMediaSources(item, enablePathSubstitution, user).ToList();

        // we dont use jellyfins alternate versions crap. So we have to load it ourselves

        InternalItemsQuery query;

        if (item.GetBaseItemKind() == BaseItemKind.Episode)
        {
            var episode = (Episode)item;
            query = new InternalItemsQuery
            {
                IncludeItemTypes = [item.GetBaseItemKind()],
                ParentId = episode.SeasonId,
                Recursive = false,
                GroupByPresentationUniqueKey = false,
                GroupBySeriesPresentationUniqueKey = false,
                CollapseBoxSetItems = false,
                IsDeadPerson = true,
                Tags = [GelatoManager.StreamTag],
                IndexNumber = episode.IndexNumber,
            };

            var episodeStremioId = item.GetProviderId("Stremio");
            if (!string.IsNullOrWhiteSpace(episodeStremioId))
            {
                // Prevent stale/mis-matched stream rows from unrelated identities
                // when season/index overlap.
                query.HasAnyProviderId = new Dictionary<string, string>
                {
                    { "Stremio", episodeStremioId },
                };
            }
        }
        else
        {
            query = new InternalItemsQuery
            {
                IncludeItemTypes = [item.GetBaseItemKind()],
                Recursive = false,
                GroupByPresentationUniqueKey = false,
                GroupBySeriesPresentationUniqueKey = false,
                CollapseBoxSetItems = false,
                IsDeadPerson = true,
                Tags = [GelatoManager.StreamTag],
            };

            var movieStremioId = item.GetProviderId("Stremio");
            if (!string.IsNullOrWhiteSpace(movieStremioId))
            {
                query.HasAnyProviderId = new Dictionary<string, string>
                {
                    { "Stremio", movieStremioId },
                };
            }
        }

        var episodeContext = item as Episode;
        var gelatoStreamItems = repo.GetItemList(query)
            .OfType<Video>()
            .Where(x =>
                x.IsGelato()
                && (
                    userId == Guid.Empty
                    || (x.GelatoData<List<Guid>>("userIds")?.Contains(userId) ?? false)
                )
            )
            .ToList();

        if (episodeContext is not null)
        {
            var beforeCount = gelatoStreamItems.Count;
            gelatoStreamItems = gelatoStreamItems
                .Where(x => MatchesEpisodeContext(x, episodeContext))
                .ToList();

            var removed = beforeCount - gelatoStreamItems.Count;
            if (removed > 0)
            {
                _log.LogWarning(
                    "GetStaticMediaSources: filtered {Count} mismatched episode stream rows for item {ItemId}",
                    removed,
                    item.Id
                );
            }
        }

        var gelatoSources = gelatoStreamItems
            .OrderBy(x => x.GelatoData<int?>("index") ?? int.MaxValue)
            .Select(s =>
            {
                var k = GetVersionInfo(s, MediaSourceType.Grouping, ctx, user);

                if (user is not null)
                {
                    _inner.SetDefaultAudioAndSubtitleStreamIndices(item, k, user);
                }

                return k;
            })
            .ToList();

        _log.LogDebug(
            "Found {s} streams. UserId={Action} GelatoId={Uri}",
            gelatoSources.Count,
            userId,
            item.GetProviderId("Stremio")
        );

        sources.AddRange(gelatoSources);

        if (sources.Count > 1)
        {
            // remove primary from list when there are streams
            sources = sources
                .Where(k =>
                    !(k.Path?.StartsWith("gelato", StringComparison.OrdinalIgnoreCase) ?? false)
                )
                .Where(k =>
                    !(k.Path?.StartsWith("stremio", StringComparison.OrdinalIgnoreCase) ?? false)
                )
                .ToList();
        }

        // failsafe. mediasources cannot be null
        if (sources.Count == 0)
        {
            sources.Add(GetVersionInfo(item, MediaSourceType.Default, ctx, user));
        }

        // If playback started from a specific stream row, keep that stream as the
        // first/default source so selector choices don't collapse back to index 1.
        if (item is Video selectedVideo && selectedVideo.IsStream() && sources.Count > 1)
        {
            var selectedId = item.Id.ToString("N");
            var selectedIndex = sources.FindIndex(s =>
                string.Equals(s.Id, selectedId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(s.ETag, selectedId, StringComparison.OrdinalIgnoreCase)
            );
            if (selectedIndex > 0)
            {
                var selectedSource = sources[selectedIndex];
                sources.RemoveAt(selectedIndex);
                sources.Insert(0, selectedSource);
            }
        }

        if (sources.Count > 0)
            sources[0].Type = MediaSourceType.Default;

        // Preserve legacy expectation for primary-item playback requests where
        // MediaSourceId is omitted and clients use ItemId as the default source id.
        if (sources.Count > 0 && item.IsPrimaryVersion())
        {
            sources[0].Id = item.Id.ToString("N");
        }

        return sources;
    }

    public void AddParts(IEnumerable<IMediaSourceProvider> providers)
    {
        _inner.AddParts(providers);
    }

    public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId)
    {
        return _inner.GetMediaStreams(itemId);
    }

    public IReadOnlyList<MediaStream> GetMediaStreams(MediaStreamQuery query)
    {
        return _inner.GetMediaStreams(query).ToList();
    }

    public IReadOnlyList<MediaAttachment> GetMediaAttachments(Guid itemId) =>
        _inner.GetMediaAttachments(itemId);

    public IReadOnlyList<MediaAttachment> GetMediaAttachments(MediaAttachmentQuery query) =>
        _inner.GetMediaAttachments(query);

    public async Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSources(
        BaseItem item,
        User user,
        bool allowMediaProbe,
        bool enablePathSubstitution,
        CancellationToken ct
    )
    {
        if (item.GetBaseItemKind() is not (BaseItemKind.Movie or BaseItemKind.Episode))
        {
            return await _inner
                .GetPlaybackMediaSources(item, user, allowMediaProbe, enablePathSubstitution, ct)
                .ConfigureAwait(false);
        }

        var manager = _manager.Value;
        var ctx = _http.HttpContext;

        var sources = GetStaticMediaSources(item, enablePathSubstitution, user);

        string? mediaSourceId =
            ctx?.Items.TryGetValue("MediaSourceId", out var idObj) == true && idObj is string idStr
                ? idStr
                : null;

        // Some clients pass MediaSourceId in querystring but not HttpContext.Items.
        if (
            string.IsNullOrWhiteSpace(mediaSourceId)
            && ctx?.Request.Query.TryGetValue("MediaSourceId", out var qMediaSourceId) == true
        )
        {
            mediaSourceId = qMediaSourceId.ToString();
        }
        if (
            string.IsNullOrWhiteSpace(mediaSourceId)
            && ctx?.Request.Query.TryGetValue("mediaSourceId", out var qMediaSourceIdLower) == true
        )
        {
            mediaSourceId = qMediaSourceIdLower.ToString();
        }

        // Some clients open playback from a specific stream row without sending
        // MediaSourceId. In that case, pin selection to the clicked stream item id
        // instead of falling back to index 0 (often the 4K/default row).
        if (string.IsNullOrWhiteSpace(mediaSourceId) && item is Video streamItem && streamItem.IsStream())
        {
            mediaSourceId = item.Id.ToString("N");
        }

        if (string.IsNullOrWhiteSpace(mediaSourceId) && item.IsPrimaryVersion() && sources.Count > 0)
        {
            mediaSourceId = sources[0].Id;
        }

        _log.LogDebug(
            "GetPlaybackMediaSources {ItemId} mediaSourceId={MediaSourceId}",
            item.Id,
            mediaSourceId
        );

        var selected = SelectByIdOrFirst(sources, mediaSourceId);
        if (selected is null)
        {
            if (item is Video selectedStream && selectedStream.IsStream())
            {
                var pinned = GetVersionInfo(item, MediaSourceType.Default, ctx, user);
                return [pinned];
            }

            return sources;
        }

        var owner = ResolveOwnerFor(selected, item);
        if (owner.IsPrimaryVersion() && owner.Id != item.Id)
        {
            sources = GetStaticMediaSources(owner, enablePathSubstitution, user);
            selected = SelectByIdOrFirst(sources, mediaSourceId);
            if (selected is null)
                return sources;
        }

        if (NeedsProbe(selected))
        {
            var libraryOptions = _libraryManager.GetLibraryOptions(owner);

            var segmentTask = _mediaSegmentManager.RunSegmentPluginProviders(
                owner,
                libraryOptions,
                false,
                ct
            );
            var metadataTask = ProbeStreamAsync((Video)owner, selected.Path, ct);
            //  var subtitleTask = DownloadSubtitles((Video)owner, ct);

            await Task.WhenAll(metadataTask, segmentTask).ConfigureAwait(false);

            await owner
                .UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct)
                .ConfigureAwait(false);

            var refreshed = GetStaticMediaSources(item, enablePathSubstitution, user);
            selected = SelectByIdOrFirst(refreshed, mediaSourceId);

            if (selected is null)
                return refreshed;
        }

        if (item.RunTimeTicks is null && selected.RunTimeTicks is not null)
        {
            item.RunTimeTicks = selected.RunTimeTicks;
            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct)
                .ConfigureAwait(false);
        }

        return [selected];

        static MediaSourceInfo? SelectByIdOrFirst(IReadOnlyList<MediaSourceInfo> list, string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return list.FirstOrDefault();

            var target = id.Trim();

            // Prefer exact id match first; this keeps behavior stable even if clients
            // send non-GUID source ids.
            var exact = list.FirstOrDefault(s =>
                (
                    !string.IsNullOrWhiteSpace(s.Id)
                    && string.Equals(s.Id, target, StringComparison.OrdinalIgnoreCase)
                )
                || (
                    !string.IsNullOrWhiteSpace(s.ETag)
                    && string.Equals(s.ETag, target, StringComparison.OrdinalIgnoreCase)
                )
            );
            if (exact is not null)
                return exact;

            // Fallback to GUID equivalence for "N" vs dashed id formats.
            if (!Guid.TryParse(target, out var targetGuid))
                return list.FirstOrDefault();

            return list.FirstOrDefault(s =>
                    (
                        !string.IsNullOrWhiteSpace(s.Id)
                        && Guid.TryParse(s.Id, out var g)
                        && g == targetGuid
                    )
                    || (
                        !string.IsNullOrWhiteSpace(s.ETag)
                        && Guid.TryParse(s.ETag, out var eg)
                        && eg == targetGuid
                    )
                ) ?? list.FirstOrDefault();
        }

        static bool NeedsProbe(MediaSourceInfo s) =>
            (s.MediaStreams?.All(ms => ms.Type != MediaStreamType.Video) ?? true)
            || (s.RunTimeTicks ?? 0) < TimeSpan.FromMinutes(2).Ticks;

        BaseItem ResolveOwnerFor(MediaSourceInfo s, BaseItem fallback) =>
            Guid.TryParse(s.ETag, out var g) ? libraryManager.GetItemById(g) ?? fallback : fallback;
    }

    private static bool MatchesEpisodeContext(Video streamRow, Episode targetEpisode)
    {
        if (streamRow is not Episode streamEpisode)
        {
            return false;
        }

        if (streamEpisode.IndexNumber != targetEpisode.IndexNumber)
        {
            return false;
        }

        if (streamEpisode.ParentIndexNumber != targetEpisode.ParentIndexNumber)
        {
            return false;
        }

        if (
            streamEpisode.SeasonId != Guid.Empty
            && targetEpisode.SeasonId != Guid.Empty
            && streamEpisode.SeasonId != targetEpisode.SeasonId
        )
        {
            return false;
        }

        if (
            streamEpisode.SeriesId != Guid.Empty
            && targetEpisode.SeriesId != Guid.Empty
            && streamEpisode.SeriesId != targetEpisode.SeriesId
        )
        {
            return false;
        }

        // Conservative guard: only enforce year when both sides provide one.
        if (
            streamEpisode.ProductionYear is int streamYear
            && targetEpisode.ProductionYear is int targetYear
            && streamYear != targetYear
        )
        {
            return false;
        }

        if (
            targetEpisode.ProductionYear is int expectedYear
            && HasConflictingYearHint(streamRow, expectedYear)
        )
        {
            return false;
        }

        return true;
    }

    private static bool HasConflictingYearHint(Video streamRow, int expectedYear)
    {
        var text = string.Join(
            " ",
            new[]
            {
                streamRow.GelatoData<string>("filename"),
                streamRow.GelatoData<string>("name"),
                streamRow.GelatoData<string>("description"),
                streamRow.Name,
            }.Where(v => !string.IsNullOrWhiteSpace(v))
        );

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var years = YearRegex
            .Matches(text)
            .Select(m => int.TryParse(m.Value, out var y) ? y : (int?)null)
            .Where(y => y.HasValue)
            .Select(y => y!.Value)
            .Distinct()
            .ToList();

        if (years.Count == 0)
        {
            return false;
        }

        return !years.Contains(expectedYear);
    }

    public Task<MediaSourceInfo> GetMediaSource(
        BaseItem item,
        string mediaSourceId,
        string? liveStreamId,
        bool enablePathSubstitution,
        CancellationToken cancellationToken
    ) =>
        _inner.GetMediaSource(
            item,
            mediaSourceId,
            liveStreamId,
            enablePathSubstitution,
            cancellationToken
        );

    public async Task<LiveStreamResponse> OpenLiveStream(
        LiveStreamRequest request,
        CancellationToken cancellationToken
    ) => await _inner.OpenLiveStream(request, cancellationToken);

    public async Task<Tuple<LiveStreamResponse, IDirectStreamProvider>> OpenLiveStreamInternal(
        LiveStreamRequest request,
        CancellationToken cancellationToken
    ) => await _inner.OpenLiveStreamInternal(request, cancellationToken);

    public Task<MediaSourceInfo> GetLiveStream(string id, CancellationToken cancellationToken) =>
        _inner.GetLiveStream(id, cancellationToken);

    public Task<
        Tuple<MediaSourceInfo, IDirectStreamProvider>
    > GetLiveStreamWithDirectStreamProvider(string id, CancellationToken cancellationToken) =>
        _inner.GetLiveStreamWithDirectStreamProvider(id, cancellationToken);

    public ILiveStream GetLiveStreamInfo(string id) => _inner.GetLiveStreamInfo(id);

    public ILiveStream GetLiveStreamInfoByUniqueId(string uniqueId) =>
        _inner.GetLiveStreamInfoByUniqueId(uniqueId);

    public async Task<IReadOnlyList<MediaSourceInfo>> GetRecordingStreamMediaSources(
        ActiveRecordingInfo info,
        CancellationToken cancellationToken
    ) => await _inner.GetRecordingStreamMediaSources(info, cancellationToken);

    public Task CloseLiveStream(string id) => _inner.CloseLiveStream(id);

    public async Task<MediaSourceInfo> GetLiveStreamMediaInfo(
        string id,
        CancellationToken cancellationToken
    ) => await _inner.GetLiveStreamMediaInfo(id, cancellationToken);

    public bool SupportsDirectStream(string path, MediaProtocol protocol) =>
        _inner.SupportsDirectStream(path, protocol);

    public MediaProtocol GetPathProtocol(string path) => _inner.GetPathProtocol(path);

    public void SetDefaultAudioAndSubtitleStreamIndices(
        BaseItem item,
        MediaSourceInfo source,
        User user
    ) => _inner.SetDefaultAudioAndSubtitleStreamIndices(item, source, user);

    public Task AddMediaInfoWithProbe(
        MediaSourceInfo mediaSource,
        bool isAudio,
        string cacheKey,
        bool addProbeDelay,
        bool isLiveStream,
        CancellationToken cancellationToken
    ) =>
        _inner.AddMediaInfoWithProbe(
            mediaSource,
            isAudio,
            cacheKey,
            addProbeDelay,
            isLiveStream,
            cancellationToken
        );

    private MediaSourceInfo GetVersionInfo(
        BaseItem item,
        MediaSourceType type,
        HttpContext ctx,
        User? user = null
    )
    {
        ArgumentNullException.ThrowIfNull(item);

        var streamName = item.GelatoData<string>("name");
        var streamDesc = item.GelatoData<string>("description");
        var bingeGroup = item.GelatoData<string>("bingeGroup");
        var richName = !string.IsNullOrEmpty(streamDesc)
            ? $"{streamName}\n{streamDesc}"
            : streamName;

        var info = new MediaSourceInfo
        {
            Id = item.Id.ToString("N", CultureInfo.InvariantCulture),
            ETag = item.Id.ToString("N", CultureInfo.InvariantCulture),
            Protocol = MediaProtocol.Http,
            MediaStreams = _inner.GetMediaStreams(item.Id),
            MediaAttachments = _inner.GetMediaAttachments(item.Id),
            Name = richName,
            Path = item.Path,
            RunTimeTicks = item.RunTimeTicks,
            Container = item.Container,
            Size = item.Size,
            Type = type,
            SupportsDirectStream = true,
            SupportsDirectPlay = true,
            // just always say yes
            HasSegments = true,
            //HasSegments = MediaSegmentManager.HasSegments(item.Id)
        };

        // Set custom HTTP header for binge group routing/load balancing in streaming requests for Anfiteatro client to serve binge group aware content.
        if (!string.IsNullOrEmpty(bingeGroup))
        {
            info.RequiredHttpHeaders = new Dictionary<string, string>
            {
                { "X-Gelato-BingeGroup", bingeGroup },
            };
        }

        if (user is not null)
        {
            info.SupportsTranscoding = user.HasPermission(
                PermissionKind.EnableVideoPlaybackTranscoding
            );
            info.SupportsDirectStream = user.HasPermission(PermissionKind.EnablePlaybackRemuxing);
        }
        if (string.IsNullOrEmpty(info.Path))
        {
            info.Type = MediaSourceType.Placeholder;
        }

        if (item is Video video)
        {
            info.IsoType = video.IsoType;
            info.VideoType = video.VideoType;
            info.Video3DFormat = video.Video3DFormat;
            info.Timestamp = video.Timestamp;
            info.IsRemote = true;

            if (video.IsShortcut)
            {
                info.IsRemote = true;
                info.Path = video.ShortcutPath;
            }
        }

        // massive cheat. clients will direct play remote files directly. But we always want to proxy it.
        // just fake a real file.
        if (ctx.GetActionName() == "GetPostedPlaybackInfo")
        {
            info.IsRemote = false;
            info.Protocol = MediaProtocol.File;
        }

        info.Bitrate = item.TotalBitrate;
        info.InferTotalBitrate();

        return info;
    }

    private async Task ProbeStreamAsync(Video owner, string streamUrl, CancellationToken ct)
    {
        var gelatoFilename = owner.GelatoData<string>("filename");
        var strmBaseName = !string.IsNullOrEmpty(gelatoFilename)
            ? Path.GetFileNameWithoutExtension(gelatoFilename)
            : $"{owner.Id:N}";
        var tmpStrm = Path.Combine(Path.GetTempPath(), $"{strmBaseName}.strm");
        await File.WriteAllTextAsync(tmpStrm, streamUrl, ct).ConfigureAwait(false);

        var origPath = owner.Path;
        var origShortcut = owner.IsShortcut;
        owner.Path = tmpStrm;
        owner.IsShortcut = true;
        owner.DateModified = new FileInfo(tmpStrm).LastWriteTimeUtc;

        try
        {
            _log.LogInformation("Probing stream for {Id} via {Url}", owner.Id, streamUrl);
            await owner.RefreshMetadata(
                new MetadataRefreshOptions(directoryService)
                {
                    EnableRemoteContentProbe = true,
                    MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                },
                ct
            );
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Stream probe failed for {Id}", owner.Id);
        }
        finally
        {
            owner.Path = origPath;
            owner.IsShortcut = origShortcut;
            try
            {
                File.Delete(tmpStrm);
            }
            catch
            { /* best effort */
            }
        }
    }
}
