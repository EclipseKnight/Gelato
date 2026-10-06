using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Gelato.Providers;
using Gelato.RemuxDb;
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
    IUserDataManager userDataManager,
    IDirectoryService directoryService,
    IServerConfigurationManager config,
    //Lazy<ISubtitleManager> subtitleManager,
    Lazy<GelatoManager> manager,
    Lazy<SubtitleProvider> subtitleProvider,
    IMediaSegmentManager mediaSegmentManager,
    Lazy<IProviderManager> providerManager,
    RemuxDbService remuxDb
) : IMediaSourceManager
{
    private readonly IMediaSourceManager _inner =
        inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly ILogger<MediaSourceManagerDecorator> _log =
        log ?? throw new ArgumentNullException(nameof(log));
    private readonly IHttpContextAccessor _http =
        http ?? throw new ArgumentNullException(nameof(http));
    private readonly KeyLock _lock = new();

    /// <summary>
    /// Set for the code a stream sync runs, so a call it makes on a stream row (from an event
    /// handler of one of its saves, say) never waits for that same sync.
    /// </summary>
    private static readonly AsyncLocal<bool> InStreamSync = new();
    private readonly IMediaSegmentManager _mediaSegmentManager =
        mediaSegmentManager ?? throw new ArgumentNullException(nameof(mediaSegmentManager));
    private readonly ILibraryManager _libraryManager =
        libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
    private readonly IServerConfigurationManager _config =
        config ?? throw new ArgumentNullException(nameof(config));
    private readonly Lazy<GelatoManager> _manager = manager;
    private readonly Lazy<SubtitleProvider> _subtitleProvider = subtitleProvider;

    //  private readonly Lazy<ISubtitleManager> _subtitleManager = subtitleManager ?? throw new ArgumentNullException(nameof(subtitleManager));
    // Lazy: ProviderManager depends on ISubtitleManager, which depends on
    // IMediaSourceManager - this decorator.
    private readonly Lazy<IProviderManager> _providerManager = providerManager;

    // Jellyfin builds its metadata providers by type scanning and hands them to
    // IProviderManager; none are registered in the container. So the probe
    // provider has to be looked up there - injecting
    // IEnumerable<ICustomMetadataProvider<Video>> always resolves to an empty list.
    private ICustomMetadataProvider<Video>? FindProbeProvider(Video owner) =>
        _providerManager
            .Value.GetMetadataProviders<Video>(
                owner,
                _libraryManager.GetLibraryOptions(owner),
                includeDisabled: true
            )
            .OfType<ICustomMetadataProvider<Video>>()
            .FirstOrDefault(p => p.Name == "Probe Provider");

    public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(
        BaseItem item,
        bool enablePathSubstitution,
        User? user = null
    )
    {
        var manager = _manager.Value;
        _log.LogDebug("GetStaticMediaSources {Id}", item.Id);
        var userId =
            user?.Id
            ?? _http.ReadRequest(ctx => ctx.TryGetUserId(out var id) ? id : Guid.Empty, Guid.Empty);

        var cfg = GelatoPlugin.Instance!.GetConfig(userId);
        if (
            (!cfg.EnableMixed && !item.IsGelatoPlaybackItem())
            || item.GetBaseItemKind() is not (BaseItemKind.Movie or BaseItemKind.Episode)
        )
        {
            var own = _inner.GetStaticMediaSources(item, enablePathSubstitution, user);

            // A local movie keeps the stream rows linked while mixed mode was on. Jellyfin would
            // list them with their stream URLs and without the per-user filter.
            if (item is Video { LinkedAlternateVersions.Length: > 0 } localVideo)
            {
                var linkedStreams = GetStreamRowIds(GetStreamRows(localVideo));
                if (linkedStreams.Count > 0)
                {
                    return own.Where(s => !linkedStreams.Contains(s.Id)).ToList();
                }
            }

            return own;
        }

        // A stream row is one version of its movie/episode. Jellyfin 12's web client loads it as
        // an item when the version dropdown changes and rebuilds the dropdown from its sources.
        var isStreamRow = item.HasStreamTag();

        var uri = StremioUri.FromBaseItem(item);
        var actionName = _http.ReadRequest(
            ctx => ctx.Items.TryGetValue("actionName", out var ao) ? ao as string : null,
            null
        );

        var allowSync =
            _http.ReadRequest(ctx => ctx.IsInsertableAction(), false) && userId != Guid.Empty;
        var isItemRead = _http.ReadRequest(ctx => ctx.IsItemReadAction(), false);
        var video = item as Video;
        var syncItemId = video?.PrimaryVersionId ?? item.Id;
        // With the creation date: an item deleted and inserted again gets the same id (its path
        // is hashed), but its rows went with it, so it must sync anew within StreamTTL.
        var cacheKey = SyncCacheKey(item, userId);

        if (!allowSync)
        {
            _log.LogDebug(
                "GetStaticMediaSources not a sync-eligible call. action={Action} uri={Uri}",
                actionName,
                uri?.ToString()
            );
        }
        else if (uri is not null && !isStreamRow)
        {
            var syncAction = StreamSyncPolicy.Decide(
                manager.GetStreamSync(cacheKey, syncItemId),
                cfg.RefreshStreamsInBackground,
                isItemRead,
                manager.WasStreamSyncReset(syncItemId),
                () => CountUserStreamRows(video, userId)
            );

            // Each sync job runs once per movie/episode at a time: the web UI asks for the detail
            // page twice, and a background refresh may still be running when playback asks.
            Func<CancellationToken, Task> sync = ct =>
                SyncStreamsAsync(manager, item, uri, userId, cacheKey, ct);

            switch (syncAction)
            {
                case StreamSyncAction.UseRememberedNoStreams:
                    // Counted by log watchers: one line is one AIOStreams request saved.
                    _log.LogInformation(
                        "SyncStreams skipped, no streams remembered GelatoId={GelatoId} userId={UserId}",
                        uri.ExternalId,
                        userId
                    );
                    break;

                case StreamSyncAction.ServeKnownRowsAndRefresh:
                    var started = RunInBackground(item.Id, sync);
                    // Counted by log watchers: one line is one call that did not wait for a sync.
                    // Refresh says whether this call started the sync or joined one already
                    // running, so the count of syncs started can be told apart.
                    _log.LogInformation(
                        "SyncStreams refreshing in background GelatoId={GelatoId} userId={UserId} refresh={Refresh}",
                        uri.ExternalId,
                        userId,
                        started ? "started" : "joined"
                    );
                    break;

                case StreamSyncAction.SyncNow:
                    _lock.RunSingleFlightAsync(item.Id, sync).GetAwaiter().GetResult();

                    // refresh item
                    libraryManager.GetItemById(item.Id);
                    break;
            }
        }
        else if (
            StreamSyncPolicy.WaitsForRunningSync(isStreamRow, isItemRead, InStreamSync.Value)
            && video?.PrimaryVersionId is { } rowPrimaryId
        )
        {
            // A call that plays, probes or downloads this row waits for a sync of its movie that
            // is running (a background refresh, most likely), so it plays the URL the sync left
            // and does not probe a row the sync is deleting (the probe's save is guarded anyway,
            // see ProbeSaveGuard). It never starts a sync: the movie's own reads do. The movie's
            // syncs run under its id.
            var running = _lock.JoinIfRunningAsync(rowPrimaryId);
            if (!running.IsCompleted)
            {
                _log.LogDebug(
                    "Stream row {Id} waits for the running sync of {PrimaryId}",
                    item.Id,
                    rowPrimaryId
                );
                running.GetAwaiter().GetResult();
            }
        }

        var itemId = item.Id.ToString("N", CultureInfo.InvariantCulture);

        // A version, a stream row or a file merged in by hand, lists the versions of its movie.
        var primary = video?.PrimaryVersionId is { } primaryVersionId
            ? _libraryManager.GetItemById(primaryVersionId) as Video
            : video;

        // A sync deleted the row (no user has its stream any more): the one this call waited for
        // above, or one that ran since the client loaded it. PlaybackInfo also reads the sources
        // again after its probe, without waiting. The row's source would name an item that is
        // gone, so a call that plays it answers as the movie would: with the versions left, the
        // first one first, which is what gets played. Looked up only for such calls on a row.
        var answerAsMovie = StreamSyncPolicy.AnswersAsMovie(
            isStreamRow && allowSync,
            isItemRead,
            rowExists: !isStreamRow
                || !allowSync
                || isItemRead
                || _libraryManager.GetItemById(item.Id) is not null,
            movieExists: primary is not null
        );
        if (answerAsMovie)
        {
            _log.LogInformation(
                "Stream row {Id} was removed by a stream sync, answering with the versions of {PrimaryId}",
                item.Id,
                primary!.Id
            );
        }

        // The item whose sources these are: the row asked for, or its movie in its place.
        var servesRow = isStreamRow && !answerAsMovie;
        var answering = answerAsMovie ? primary! : item;
        var linkedVersions = primary is null
            ? []
            : _libraryManager.GetLinkedAlternateVersions(primary).ToList();
        if (
            linkedVersions.Count == 0
            && primary is not null
            && !servesRow
            && primary.IsGelatoPlaybackItem()
            && manager.RelinkOwnedRows(primary)
        )
        {
            linkedVersions = _libraryManager.GetLinkedAlternateVersions(primary).ToList();
        }
        var streamRows = linkedVersions
            .Where(v => v.HasStreamTag())
            .OrderBy(v => v.GelatoData<int?>("index") ?? int.MaxValue)
            .ToList();
        var streamRowIds = GetStreamRowIds(streamRows);

        // Jellyfin lists the linked stream rows itself, named after the item; they are added
        // below with their stream names, and only the ones this user has. A stream row's own
        // Jellyfin source is the row itself. A Gelato movie/episode has no media of its own, so
        // unless versions were merged in by hand, Jellyfin's list is skipped: building it costs
        // several queries per stream.
        // A stream row of a local movie lists the movie's own file too.
        var mediaOwner = isStreamRow ? primary : item;
        var hasOwnMedia =
            mediaOwner is not null
            && (!mediaOwner.IsGelatoPlaybackItem() || linkedVersions.Any(v => !v.HasStreamTag()));
        var sources = !hasOwnMedia
            ? []
            : _inner
                .GetStaticMediaSources(mediaOwner!, enablePathSubstitution, user)
                .Where(s => !streamRowIds.Contains(s.Id))
                .ToList();

        var versions = streamRows
            .Where(x =>
                userId == Guid.Empty
                || (x.GelatoData<List<Guid>>("userIds")?.Contains(userId) ?? false)
            )
            .Select(row =>
            {
                var source = GetVersionInfo(row, MediaSourceType.Grouping, user);

                if (user is not null)
                {
                    _inner.SetDefaultAudioAndSubtitleStreamIndices(answering, source, user);
                }

                return (Row: row, Source: source);
            })
            .ToList();

        _log.LogDebug(
            "Found {Count} streams. UserId={UserId} ItemId={ItemId}",
            versions.Count,
            userId,
            item.Id
        );

        sources.AddRange(versions.Select(v => v.Source));

        if (servesRow)
        {
            // The requested version goes first: it becomes the Default source.
            var own =
                sources.FirstOrDefault(s => s.Id == itemId)
                ?? GetVersionInfo(item, MediaSourceType.Grouping, user);
            sources.Remove(own);
            sources.Insert(0, own);
        }

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
            sources.Add(GetVersionInfo(answering, MediaSourceType.Default, user));
        }

        // A Gelato movie/episode has no media of its own, so its first stream takes its id and the
        // movie is one of its versions: clients that play the source with the item's id get the
        // first stream, and its watch state stays on the movie. Version pages list it with the
        // same id, the first stream's own page included, so picking it there opens the movie and
        // playing it there reports progress on the movie.
        var primaryId = primary?.Id.ToString("N", CultureInfo.InvariantCulture);
        if (
            primaryId is not null
            && sources.All(s => s.Id != primaryId)
            && versions.FirstOrDefault().Source is { } first
        )
        {
            first.Id = primaryId;
        }

        if (!servesRow && primary is not null && user is not null)
        {
            MoveResumedVersionFirst(sources, primary, versions, user);
        }

        foreach (var source in sources)
        {
            if (source.Type == MediaSourceType.Default)
                source.Type = MediaSourceType.Grouping;
        }
        sources[0].Type = MediaSourceType.Default;

        // A page opened or another version picked: the web client loads the version's row as an
        // item when the dropdown changes, so this is also the pick of a version. A read marked as
        // a prefetch (a card hovered or focused) gets its sources but starts no pre-probe.
        if (
            user is not null
            && _http.ReadRequest(
                ctx => ctx.GetActionName() is "GetItem" or "GetItemLegacy" && RequestsMediaSources(ctx),
                false
            )
        )
        {
            SchedulePreProbe(
                item,
                sources[0],
                user,
                isPrefetch: _http.ReadRequest(PrefetchHint.IsPrefetch, false)
            );
        }

        return sources;
    }

    /// <summary>
    /// Whether an item request wants the item's sources, as a page does: Jellyfin Web's details
    /// page sends no Fields and gets every field. A script that asks for some fields of the item
    /// (KefinTweaks' home sections ask for People) has not opened its page.
    /// </summary>
    private static bool RequestsMediaSources(HttpContext ctx) =>
        !ctx.Request.Query.TryGetValue("fields", out var fields)
        || fields.Any(value =>
            value
                ?.Split(',', StringSplitOptions.TrimEntries)
                .Contains("MediaSources", StringComparer.OrdinalIgnoreCase) ?? false
        );

    private static string SyncCacheKey(BaseItem item, Guid userId)
    {
        var syncItemId = (item as Video)?.PrimaryVersionId ?? item.Id;
        var key = $"{syncItemId}:{item.DateCreated.Ticks}";
        return userId != Guid.Empty ? $"{userId}:{key}" : key;
    }

    /// <summary>
    /// The stream rows linked to a movie/episode as its versions. Read from the database: the
    /// instance at hand may be a copy whose links are out of date.
    /// </summary>
    private List<Video> GetStreamRows(Video? primary) =>
        primary is null
            ? []
            : _libraryManager
                .GetLinkedAlternateVersions(primary)
                .Where(v => v.HasStreamTag())
                .ToList();

    private static HashSet<string> GetStreamRowIds(IEnumerable<Video> rows) =>
        rows.Select(r => r.Id.ToString("N", CultureInfo.InvariantCulture)).ToHashSet();

    /// <summary>The stream rows of the movie/episode this user has from an earlier sync.</summary>
    private int CountUserStreamRows(Video? primary, Guid userId) =>
        GetStreamRows(primary)
            .Count(r => r.GelatoData<List<Guid>>("userIds")?.Contains(userId) ?? false);

    /// <summary>
    /// Asks AIOStreams for the movie/episode's streams, saves them as its rows and marks the user's
    /// sync (<see cref="GelatoManager.SetStreamSync"/>), also when none were found. A failed sync
    /// is not marked, so the next call tries again.
    /// </summary>
    private async Task SyncStreamsAsync(
        GelatoManager manager,
        BaseItem item,
        StremioUri uri,
        Guid userId,
        string cacheKey,
        CancellationToken ct
    )
    {
        _log.LogDebug("GetStaticMediaSources refreshing streams for {Id}", item.Id);

        // Flows to everything this sync calls, and only to that: an async method restores the
        // caller's value when it returns.
        InStreamSync.Value = true;

        // Everything inside a try: run in the background, nothing would see an exception.
        try
        {
            PrewarmSubtitles(item, uri);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Subtitle prewarm check failed for {Id}", item.Id);
        }

        try
        {
            await StreamSyncPolicy
                .SyncAndMarkAsync(
                    token => manager.SyncStreams(item, userId, token),
                    isMergedVersion: item is Video { PrimaryVersionId: not null },
                    count => manager.SetStreamSync(cacheKey, count),
                    ct
                )
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to sync streams for {Id}", item.Id);
        }
    }

    /// <summary>
    /// Fetches the title's subtitles into the cache in the background if Gelato Subtitles is
    /// enabled for its library.
    /// </summary>
    private void PrewarmSubtitles(BaseItem item, StremioUri uri)
    {
        var libraryOptions = _libraryManager.GetLibraryOptions(item);
        var subtitlePrewarmEnabled =
            libraryOptions.SubtitleDownloadLanguages?.Length > 0
            && !libraryOptions.DisabledSubtitleFetchers.Contains(
                "Gelato Subtitles",
                StringComparer.OrdinalIgnoreCase
            );

        if (!subtitlePrewarmEnabled)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await _subtitleProvider
                    .Value.GetSubtitlesAsync(uri.ExternalId, uri.MediaType, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Subtitle prewarm failed for {Uri}", uri);
            }
        });
    }

    /// <summary>
    /// Starts a stream sync that the request does not wait for. If one is already running for the
    /// movie/episode, that one is left to finish and no second one starts. Returns true when this
    /// call started it.
    /// </summary>
    private bool RunInBackground(Guid itemId, Func<CancellationToken, Task> sync)
    {
        // Its own token, never the request's: the request ends as soon as it has answered (and
        // Jellyfin cancels the token when the client hangs up), and a sync stopped halfway leaves
        // rows saved but not linked. Nothing cancels it; the HTTP client's timeout bounds the
        // AIOStreams request, like the waiting sync's.
        // Without the request's context too: the sync outlives the request, and it should run
        // the same whether the request has ended or not, as it does from a scheduled task.
        return _lock.StartSingleFlightInBackground(itemId, sync);
    }

    /// <summary>
    /// Puts the stream the user is part way through first, so clients preselect it. The resume
    /// point itself is shared: StreamUserDataSync copies it to the movie.
    /// </summary>
    private void MoveResumedVersionFirst(
        List<MediaSourceInfo> sources,
        Video primary,
        List<(Video Row, MediaSourceInfo Source)> versions,
        User user
    )
    {
        if (sources.Count < 2 || versions.Count == 0)
            return;

        // The source with the movie's id plays with the movie's own watch state.
        var primaryId = primary.Id.ToString("N", CultureInfo.InvariantCulture);
        var streams = versions.Where(v => v.Source.Id != primaryId).ToList();
        var userData = userDataManager.GetUserDataBatch(
            [primary, .. streams.Select(v => v.Row)],
            user
        );
        if (userData.GetValueOrDefault(primary.Id) is not { PlaybackPositionTicks: > 0 } movieData)
            return;

        var resumed = VersionPlaybackSelector.SelectMostRecentlyPlayed(
            streams,
            v => userData.GetValueOrDefault(v.Row.Id),
            data => data.PlaybackPositionTicks > 0
        );

        // The movie holds a copy of the stream's state, dated CopyOffset after the stream; it is
        // newer only when the stream with the movie's id was played since.
        if (
            resumed.Source is not { } source
            || (userData[resumed.Row.Id].LastPlayedDate ?? DateTime.MinValue)
                + StreamUserDataSync.CopyOffset
                < (movieData.LastPlayedDate ?? DateTime.MinValue)
            || ReferenceEquals(sources[0], source)
        )
        {
            return;
        }

        sources.Remove(source);
        sources.Insert(0, source);
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

    /// <summary>
    /// How long a source prepared for streaming answers the requests that follow it, counted
    /// from the end of its preparation.
    /// </summary>
    private static readonly TimeSpan PreparedTtl = TimeSpan.FromSeconds(10);

    // Done completes with the time the preparation ended, however it ended.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<
        string,
        (Task<IReadOnlyList<MediaSourceInfo>> Sources, Task<DateTime> Done)
    > _prepared = new();

    private static bool PreparedExpired(Task<DateTime> done, DateTime now) =>
        done.IsCompletedSuccessfully && done.Result + PreparedTtl <= now;

    /// <remarks>
    /// Starting a playback asks for its source several times within a second: PlaybackInfo, then
    /// Jellyfin's HLS controller for the master playlist, the variant playlist and the first
    /// segment (the pre-probe the player's reload of the item schedules stays out, see
    /// <see cref="PreparedForPlaybackWindow"/>). Each preparation reads the item's versions and
    /// streams and may look up segments, so the streaming requests share the first one's answer
    /// while it is prepared and for <see cref="PreparedTtl"/> after that: the first probe of a
    /// remote stream can take up to a minute, and the requests that follow come only once it is
    /// over. PlaybackInfo is prepared on its own: its answer is stubbed.
    /// </remarks>
    public async Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSources(
        BaseItem item,
        User user,
        bool allowMediaProbe,
        bool enablePathSubstitution,
        CancellationToken ct
    )
    {
        if (
            item.GetBaseItemKind() is not (BaseItemKind.Movie or BaseItemKind.Episode)
            || _http.ReadRequest(ctx => ctx.IsPlaybackInfoAction(), false)
        )
        {
            return await GetPlaybackMediaSourcesCore(
                    item,
                    user,
                    allowMediaProbe,
                    enablePathSubstitution,
                    null,
                    false,
                    ct
                )
                .ConfigureAwait(false);
        }

        var sourceId = _http.ReadRequest(
            ctx => ctx.Items.TryGetValue("MediaSourceId", out var idObj) ? idObj as string : null,
            null
        );
        var key = string.Join(
            ':',
            item.Id.ToString("N", CultureInfo.InvariantCulture),
            UserKey(user),
            sourceId?.ToLowerInvariant(),
            allowMediaProbe,
            enablePathSubstitution
        );

        var now = DateTime.UtcNow;
        if (_prepared.TryGetValue(key, out var prepared) && !PreparedExpired(prepared.Done, now))
        {
            try
            {
                var sources = await prepared.Sources.WaitAsync(ct).ConfigureAwait(false);
                var done = await prepared.Done.ConfigureAwait(false);

                // 0 for a request that waited for the preparation to end.
                _log.LogDebug(
                    "GetPlaybackMediaSources {ItemId} mediaSourceId={MediaSourceId}: prepared {Ago} ms ago",
                    item.Id,
                    sourceId,
                    (int)Math.Max(0, (now - done).TotalMilliseconds)
                );
                return sources;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // The request that prepared it failed or was cancelled: this one prepares it again.
            }
        }

        foreach (var (k, entry) in _prepared)
        {
            if (PreparedExpired(entry.Done, now))
                _prepared.TryRemove(k, out _);
        }

        var task = GetPlaybackMediaSourcesCore(
            item,
            user,
            allowMediaProbe,
            enablePathSubstitution,
            null,
            false,
            ct
        );
        _prepared[key] = (
            task,
            task.ContinueWith(
                _ => DateTime.UtcNow,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            )
        );
        return await task.ConfigureAwait(false);
    }

    // Jellyfin's streaming requests pass no user, the declared type notwithstanding.
    private static string? UserKey(User? user) =>
        user?.Id.ToString("N", CultureInfo.InvariantCulture);

    /// <param name="requestedSourceId">The source to prepare; null reads it from the request.</param>
    /// <param name="preProbe">
    /// Prepares the source ahead of playback: nothing is stubbed, and a row on RemuxDB's media
    /// info gets its segment lookup but not the delayed probe, which would resolve a stream
    /// nobody may play.
    /// </param>
    private async Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSourcesCore(
        BaseItem item,
        User user,
        bool allowMediaProbe,
        bool enablePathSubstitution,
        string? requestedSourceId,
        bool preProbe,
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
        // Before the sources are read, which may wait for a sync (see StreamWriteKey).
        var itemWriteKey = StreamWriteKey(item, item);
        var sources = GetStaticMediaSources(item, enablePathSubstitution, user);

        requestedSourceId ??= _http.ReadRequest(
            ctx => ctx.Items.TryGetValue("MediaSourceId", out var idObj) ? idObj as string : null,
            null
        );
        Guid? mediaSourceId =
            Guid.TryParse(requestedSourceId, out var fromCtx)
                ? fromCtx
                : (
                    item.IsPrimaryVersion()
                    && sources.Count > 0
                    && Guid.TryParse(sources[0].Id, out var fromSource)
                        ? fromSource
                        : null
                );

        _log.LogDebug(
            "GetPlaybackMediaSources {ItemId} mediaSourceId={MediaSourceId} action={Action} preProbe={PreProbe}",
            item.Id,
            mediaSourceId,
            _http.ReadRequest(ctx => ctx.GetActionName(), null),
            preProbe
        );

        var selected = SelectByIdOrFirst(sources, mediaSourceId);
        if (selected is null)
            return sources;

        var owner = ResolveOwnerFor(selected, item);
        if (!owner.IsGelatoPlaybackItem())
        {
            // A local movie's linked stream rows are in Jellyfin's list too, with their real URLs.
            // They are played through their own source id, which the branch below handles. A file
            // merged into a Gelato movie is asked for itself: Jellyfin would probe the movie's
            // placeholder path otherwise.
            var streamRowIds = GetStreamRowIds(GetStreamRows(item as Video));
            var playbackSources = await _inner
                .GetPlaybackMediaSources(
                    item.IsGelatoPlaybackItem() ? owner : item,
                    user,
                    allowMediaProbe,
                    enablePathSubstitution,
                    ct
                )
                .ConfigureAwait(false);
            return playbackSources.Where(s => !streamRowIds.Contains(s.Id)).ToList();
        }

        if (owner.IsPrimaryVersion() && owner.Id != item.Id)
        {
            sources = GetStaticMediaSources(owner, enablePathSubstitution, user);
            selected = SelectByIdOrFirst(sources, mediaSourceId);
            if (selected is null)
                return sources;
        }

        // Ahead of playback only the source that was asked for is prepared. When the title's rows
        // changed since the page was opened (a split, a sync), the list falls back to another
        // source or to the title's placeholder, and probing that would save the movie/episode
        // in the middle of the change.
        if (
            preProbe
            && (
                IsPlaceholder(selected)
                || !string.Equals(selected.Id, requestedSourceId, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            return sources;
        }

        // A row probed before is done ahead of playback: one whose probe found a file too short
        // or without video still looks unprobed, and would be probed again on every visit.
        if (preProbe && owner.GelatoData<string>("mediaInfo") == RemuxDbService.SourceProbe)
            return sources;

        // A pre-probe of this source is under way: its result is what plays, not a second probe.
        if (!preProbe && _preProbing.TryGetValue(PreProbeKey(selected), out var running))
        {
            await running.WaitAsync(ct).ConfigureAwait(false);
            sources = GetStaticMediaSources(
                owner.IsPrimaryVersion() ? owner : item,
                enablePathSubstitution,
                user
            );
            selected = SelectByIdOrFirst(sources, mediaSourceId) ?? selected;
        }

        // RemuxDB's media info plays as it is and is probed afterwards, unless it lacks what the
        // playback decides on.
        var fromRemuxDb = owner.GelatoData<string>("mediaInfo") == RemuxDbService.SourceRemuxDb;
        var needsProbe =
            NeedsProbe(selected)
            || (fromRemuxDb && RemuxDbMapper.LacksPlaybackInfo(selected.MediaStreams));

        // A pre-probe that starts while this playback probes sees it and stays out.
        var probeKey = PreProbeKey(selected);
        using var claim = needsProbe && !preProbe ? ClaimProbe(probeKey) : null;
        if (needsProbe)
        {
            var writeKey = StreamWriteKey(owner, item);
            var libraryOptions = _libraryManager.GetLibraryOptions(owner);
            var remuxDbStreams = fromRemuxDb ? remuxDb.GetStreams(owner.Id) : null;

            var segmentTask = _mediaSegmentManager.RunSegmentPluginProviders(
                owner,
                libraryOptions,
                false,
                ct
            );
            var metadataTask = ProbeStreamAsync((Video)owner, selected.Path, ct);
            //  var subtitleTask = DownloadSubtitles((Video)owner, ct);

            await Task.WhenAll(metadataTask, segmentTask).ConfigureAwait(false);

            await SaveProbedAsync(
                    owner,
                    writeKey,
                    () =>
                    {
                        if (
                            owner is Video probedRow
                            && probedRow.HasStreamTag()
                            && !remuxDb.OnProbed(probedRow, libraryOptions)
                            && remuxDbStreams is not null
                        )
                        {
                            // A dead link: keep RemuxDB's, and try again on the next playback.
                            remuxDb.RestoreStreams(owner.Id, remuxDbStreams);
                        }
                    },
                    ct
                )
                .ConfigureAwait(false);

            var refreshed = GetStaticMediaSources(item, enablePathSubstitution, user);
            selected = SelectByIdOrFirst(refreshed, mediaSourceId);

            // Still unprobed after its probe: a dead link, left alone for a while.
            if (preProbe && (selected is null || NeedsProbe(selected)))
                MarkPreProbeFailed(probeKey);

            if (selected is null)
                return refreshed;
        }
        else if (fromRemuxDb)
        {
            await EnsureSegmentsAsync(owner, ct).ConfigureAwait(false);
            if (!preProbe)
                ProbeLater(owner.Id);
        }

        if (item.RunTimeTicks is null && selected.RunTimeTicks is not null)
        {
            item.RunTimeTicks = selected.RunTimeTicks;
            await SaveProbedAsync(item, itemWriteKey, null, ct).ConfigureAwait(false);
        }

        if (!preProbe)
            MarkPreparedForPlayback(PreProbeKey(selected));

        // Stub path after probing is done so the real URL is never sent to clients.
        // Force File protocol so clients proxy through Jellyfin instead of direct-playing.
        // Both playback info actions, not the POST alone: native clients use the GET.
        if (!preProbe && _http.ReadRequest(ctx => ctx.IsPlaybackInfoAction(), false))
        {
            selected.Stub();
        }

        return [selected];

        static bool NeedsProbe(MediaSourceInfo s) =>
            (s.MediaStreams?.All(ms => ms.Type != MediaStreamType.Video) ?? true)
            || (s.RunTimeTicks ?? 0) < TimeSpan.FromMinutes(2).Ticks;

        // Gelato's sources name their item in the ETag (the first stream carries the movie's id).
        // Jellyfin's own, like a file merged in as a version, use the item's id.
        BaseItem ResolveOwnerFor(MediaSourceInfo s, BaseItem fallback) =>
            (Guid.TryParse(s.ETag, out var etag) ? libraryManager.GetItemById(etag) : null)
            ?? (Guid.TryParse(s.Id, out var id) ? libraryManager.GetItemById(id) : null)
            ?? fallback;
    }

    /// <summary>
    /// The key a movie/episode's stream syncs and deletions hold (<see
    /// cref="GelatoManager.RunExclusiveAsync"/>): the movie's id, also for one of its rows.
    /// Taken before the probe: a sync that deletes the row, or a split of the versions, clears
    /// the row's owner on the way, and the save would then queue behind nothing.
    /// </summary>
    private static Guid StreamWriteKey(BaseItem row, BaseItem requested) =>
        (row as Video)?.PrimaryVersionId ?? (requested as Video)?.PrimaryVersionId ?? requested.Id;

    /// <summary>
    /// Saves an item after its probe (or its run time), as the only writer of its movie/episode's
    /// rows, queued behind a running sync or deletion. Not when it was deleted while the probe
    /// ran: saving it would bring it back. A row a sync saved anew meanwhile takes what the sync
    /// wrote before it is saved (<see cref="ProbeSaveGuard"/>), so the save never undoes the sync.
    /// </summary>
    private Task SaveProbedAsync(
        BaseItem probed,
        Guid writeKey,
        Action? beforeSave,
        CancellationToken ct
    ) =>
        _manager.Value.RunExclusiveAsync(
            writeKey,
            async token =>
            {
                // Asked of the database as well: an episode deleted with its series stays in the
                // library's cache.
                var cached = _libraryManager.GetItemById(probed.Id);
                var stored = cached is null ? null : _libraryManager.RetrieveItem(probed.Id);
                var saved = ProbeSaveGuard.ItemToSave(probed, cached, stored);
                if (saved is null)
                {
                    // Counted by log watchers: each line is a probe that would have brought back
                    // a row a sync deleted.
                    _log.LogInformation(
                        "Probe result of {Id} not saved: a stream sync deleted it meanwhile",
                        probed.Id
                    );
                    return;
                }

                if (!ReferenceEquals(cached, probed))
                {
                    _log.LogDebug(
                        "Probe result of {Id} saved with the data a stream sync wrote meanwhile",
                        probed.Id
                    );
                }

                beforeSave?.Invoke();
                await saved
                    .UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, token)
                    .ConfigureAwait(false);
            },
            ct
        );

    private static MediaSourceInfo? SelectByIdOrFirst(IReadOnlyList<MediaSourceInfo> list, Guid? id)
    {
        if (!id.HasValue)
            return list.FirstOrDefault();

        var target = id.Value;

        return list.FirstOrDefault(s =>
                !string.IsNullOrEmpty(s.Id) && Guid.TryParse(s.Id, out var g) && g == target
            ) ?? list.FirstOrDefault();
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
            MediaStreams = GetMediaStreamsWithExternalSubs(item),
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

        info.Bitrate = item.TotalBitrate;
        info.InferTotalBitrate();

        return info;
    }

    // Jellyfin's MediaInfoResolver.GetExternalStreamsAsync bails immediately when !video.IsFileProtocol
    // (stream items have http:// paths). This means external subtitle files saved to the internal
    // metadata folder are never discovered during library refresh and never written to the DB.
    // We work around this by scanning the metadata folder ourselves at playback time and merging
    // any matching subtitle files into the DB streams on the fly.
    private IReadOnlyList<MediaStream> GetMediaStreamsWithExternalSubs(BaseItem item)
    {
        var streams = _inner.GetMediaStreams(item.Id).ToList();

        var existingPaths = new HashSet<string>(
            streams.Where(s => s.Path != null).Select(s => s.Path!),
            StringComparer.OrdinalIgnoreCase
        );

        var nextIndex = streams.Count > 0 ? streams.Max(s => s.Index) + 1 : 0;

        foreach (var (file, langCode, codec) in item.GetGelatoSubtitleFiles())
        {
            if (existingPaths.Contains(file))
                continue;

            streams.Add(
                new MediaStream
                {
                    Type = MediaStreamType.Subtitle,
                    IsExternal = true,
                    IsExternalUrl = false,
                    SupportsExternalStream = true,
                    Path = file,
                    Language = langCode,
                    Codec = codec,
                    Index = nextIndex++,
                    IsDefault = false,
                    IsForced = false,
                    IsHearingImpaired = false,
                }
            );

            existingPaths.Add(file);
        }

        return streams;
    }

    // Sources being pre-probed, by the row they play: a playback of the same source waits for the
    // result instead of probing it a second time. The source's id is not the key: a movie's first
    // stream has the movie's id, whichever row that is at the moment.
    private static string PreProbeKey(MediaSourceInfo source) => source.ETag ?? source.Id;

    private IDisposable? ClaimProbe(string key)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _preProbing.TryAdd(key, done.Task) ? new ProbeClaim(this, key, done) : null;
    }

    private sealed class ProbeClaim(
        MediaSourceManagerDecorator owner,
        string key,
        TaskCompletionSource done
    ) : IDisposable
    {
        public void Dispose()
        {
            owner._preProbing.TryRemove(key, out _);
            done.TrySetResult();
        }
    }

    /// <summary>How long a page or version stays open before it is pre-probed.</summary>
    private static readonly TimeSpan PreProbeDelay = TimeSpan.FromSeconds(1);

    /// <summary>How long a pre-probe may run. A probe takes 2 to 5 seconds.</summary>
    private static readonly TimeSpan PreProbeTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long a source whose pre-probe failed is left alone. A dead link would otherwise hold a
    /// slot for <see cref="PreProbeTimeout"/> on every visit. Playback still probes it.
    /// </summary>
    private static readonly TimeSpan PreProbeRetry = TimeSpan.FromMinutes(30);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<
        string,
        DateTime
    > _preProbeFailed = new();

    private bool PreProbeFailedRecently(string key)
    {
        if (!_preProbeFailed.TryGetValue(key, out var failedAt))
            return false;

        if (DateTime.UtcNow - failedAt < PreProbeRetry)
            return true;

        _preProbeFailed.TryRemove(new KeyValuePair<string, DateTime>(key, failedAt));
        return false;
    }

    /// <summary>
    /// How long a source a playback prepared is not pre-probed. The player loads the item again
    /// when it starts, which schedules a pre-probe of the source it is already playing.
    /// </summary>
    private static readonly TimeSpan PreparedForPlaybackWindow = TimeSpan.FromMinutes(1);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<
        string,
        DateTime
    > _preparedForPlayback = new();

    private void MarkPreparedForPlayback(string key)
    {
        var now = DateTime.UtcNow;
        foreach (var (staleKey, preparedAt) in _preparedForPlayback)
        {
            if (now - preparedAt >= PreparedForPlaybackWindow)
                _preparedForPlayback.TryRemove(staleKey, out _);
        }

        _preparedForPlayback[key] = now;
    }

    private bool PreparedForPlaybackRecently(string key) =>
        _preparedForPlayback.TryGetValue(key, out var preparedAt)
        && DateTime.UtcNow - preparedAt < PreparedForPlaybackWindow;

    private void MarkPreProbeFailed(string key)
    {
        // Sources nobody opens again are dropped once the map grows.
        if (_preProbeFailed.Count > 1000)
        {
            foreach (var (staleKey, failedAt) in _preProbeFailed)
            {
                if (DateTime.UtcNow - failedAt >= PreProbeRetry)
                    _preProbeFailed.TryRemove(staleKey, out _);
            }
        }

        _preProbeFailed[key] = DateTime.UtcNow;
    }

    // Pre-probes running at once. A playback is never held up by this: it probes on its own.
    private readonly SemaphoreSlim _preProbeSlots = new(2);

    // The pre-probe waiting out its delay, per user and title: the next version of the title
    // replaces it, so flicking through the versions probes the one that is stopped on.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<
        string,
        CancellationTokenSource
    > _pendingPreProbe = new();

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task> _preProbing =
        new();

    private void SchedulePreProbe(BaseItem item, MediaSourceInfo source, User user, bool isPrefetch)
    {
        if (
            !GelatoPlugin.Instance!.GetConfig(user.Id).PreProbe
            || item.GetBaseItemKind() is not (BaseItemKind.Movie or BaseItemKind.Episode)
            || !item.IsGelatoPlaybackItem()
            || IsPlaceholder(source)
            || _preProbing.ContainsKey(PreProbeKey(source))
            || PreProbeFailedRecently(PreProbeKey(source))
        )
        {
            return;
        }

        // Checked last, so the line is logged only for a pre-probe that would have run. Returning
        // before the pending one is replaced also leaves a real page open's pre-probe alone.
        if (isPrefetch)
        {
            _log.LogDebug(
                "Pre-probe of {ItemId} source {SourceId} skipped: the request is a prefetch",
                item.Id,
                source.Id
            );
            return;
        }

        // Not in the request's context: the request is over when this runs, and the source list it
        // builds must not see it as a page visit again.
        var pending = new CancellationTokenSource();
        var pendingKey = $"{user.Id}:{(item as Video)?.PrimaryVersionId ?? item.Id}";
        _pendingPreProbe.AddOrUpdate(
            pendingKey,
            pending,
            (_, previous) =>
            {
                previous.Cancel();
                return pending;
            }
        );

        using (ExecutionContext.SuppressFlow())
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(PreProbeDelay, pending.Token).ConfigureAwait(false);
                    _pendingPreProbe.TryRemove(
                        new KeyValuePair<string, CancellationTokenSource>(pendingKey, pending)
                    );
                    await PreProbeAsync(item, source, user).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Another page or version took its place.
                }
                finally
                {
                    pending.Dispose();
                }
            });
        }
    }

    /// <summary>
    /// Prepares the source for playback as a playback request would: probes it when it needs one
    /// and looks its segments up. Does nothing for a source already being prepared.
    /// </summary>
    private async Task PreProbeAsync(BaseItem item, MediaSourceInfo source, User user)
    {
        var key = PreProbeKey(source);
        if (PreProbeFailedRecently(key) || PreparedForPlaybackRecently(key))
            return;

        // A sync of the title is running, most likely the background refresh the page visit
        // started (RefreshStreamsInBackground): the pre-probe waits for the rows it leaves, so it
        // neither resolves a URL the sync is replacing nor probes a row it is deleting. Before a
        // slot is taken: a sync waits for the addon's slowest source.
        try
        {
            await _lock
                .JoinIfRunningAsync((item as Video)?.PrimaryVersionId ?? item.Id)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Pre-probe of {Id}: the running sync failed", item.Id);
        }

        // Registered only once running: a playback waits for a pre-probe under way, not for one
        // queued behind the others.
        await _preProbeSlots.WaitAsync().ConfigureAwait(false);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_preProbing.TryAdd(key, done.Task))
        {
            _preProbeSlots.Release();
            return;
        }

        // A stream that takes the connection and never answers would hold its slot, and the
        // playbacks waiting for it, for good.
        using var timeout = new CancellationTokenSource(PreProbeTimeout);
        try
        {
            // Deleted while this waited: the instance at hand would be probed and saved back. An
            // item inserted again since has the same id, so the library's instance is the one.
            if (_libraryManager.GetItemById(item.Id) is not { } current)
                return;

            await GetPlaybackMediaSourcesCore(
                    current,
                    user,
                    true,
                    false,
                    source.Id,
                    true,
                    timeout.Token
                )
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            MarkPreProbeFailed(key);
            _log.LogWarning(
                "Pre-probe of {Id} source {Source} gave up after {Seconds}s",
                item.Id,
                source.Id,
                PreProbeTimeout.TotalSeconds
            );
        }
        catch (Exception ex)
        {
            MarkPreProbeFailed(key);
            _log.LogWarning(ex, "Pre-probe failed for {Id} source {Source}", item.Id, source.Id);
        }
        finally
        {
            _preProbing.TryRemove(key, out _);
            done.TrySetResult();
            _preProbeSlots.Release();
        }
    }

    /// <summary>
    /// Pre-probes the source an episode plays by default, syncing its streams first when they
    /// are not: the next episode of one that is nearing its end.
    /// </summary>
    public async Task PreProbeDefaultAsync(BaseItem item, User user)
    {
        if (
            !GelatoPlugin.Instance!.GetConfig(user.Id).PreProbe
            || item.GetBaseItemKind() is not (BaseItemKind.Movie or BaseItemKind.Episode)
            || !item.IsGelatoPlaybackItem()
        )
        {
            return;
        }

        var manager = _manager.Value;
        var syncKey = SyncCacheKey(item, user.Id);
        var syncItemId = (item as Video)?.PrimaryVersionId ?? item.Id;
        // Synced the way a page visit syncs, and marked the same way: a zero is remembered for
        // NoStreamsTTL, so the next episode's page does not ask again. A title remembered
        // without streams lists its placeholder, which is not pre-probed below.
        if (
            manager.GetStreamSync(syncKey, syncItemId) == StreamSyncState.Due
            && StremioUri.FromBaseItem(item) is { } uri
        )
        {
            await _lock
                .RunSingleFlightAsync(
                    item.Id,
                    ct => SyncStreamsAsync(manager, item, uri, user.Id, syncKey, ct)
                )
                .ConfigureAwait(false);
        }

        var sources = GetStaticMediaSources(item, false, user);
        if (sources.Count > 0 && !IsPlaceholder(sources[0]))
            await PreProbeAsync(item, sources[0], user).ConfigureAwait(false);
    }

    /// <summary>
    /// The movie/episode's own placeholder, listed when the user has no streams: there is nothing
    /// to probe, and probing it would save the movie on every visit. Asked before the source
    /// goes into a response, which stubs its path.
    /// </summary>
    private static bool IsPlaceholder(MediaSourceInfo source) =>
        string.IsNullOrEmpty(source.Path)
        || source.Path.StartsWith("gelato", StringComparison.OrdinalIgnoreCase)
        || source.Path.StartsWith("stremio", StringComparison.OrdinalIgnoreCase);

    /// <summary>How long playback waits for the segment providers of a row that plays on RemuxDB's media info.</summary>
    private static readonly TimeSpan SegmentWait = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// Fetches the row's segments (intro) before playback starts, so the client has them for its
    /// skip button on the first play. Playback waits at most <see cref="SegmentWait"/>; a slower
    /// provider finishes in the background.
    /// </summary>
    private async Task EnsureSegmentsAsync(BaseItem owner, CancellationToken ct)
    {
        if (_mediaSegmentManager.HasSegments(owner.Id))
            return;

        var run = Task.Run(async () =>
        {
            try
            {
                await _mediaSegmentManager
                    .RunSegmentPluginProviders(
                        owner,
                        _libraryManager.GetLibraryOptions(owner),
                        false,
                        CancellationToken.None
                    )
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Segment lookup failed for {Id}", owner.Id);
            }
        });

        await Task.WhenAny(run, Task.Delay(SegmentWait, ct)).ConfigureAwait(false);
    }

    /// <summary>How long after playback starts a stream with RemuxDB's media info is probed.</summary>
    private static readonly TimeSpan ProbeLaterDelay = TimeSpan.FromSeconds(30);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _probingLater =
        new();

    /// <summary>
    /// Probes a row that played with RemuxDB's media info once, in the background. It adds what
    /// RemuxDB does not record (attachments, such as the fonts of ASS subtitles) and corrects a
    /// wrong match. The segment providers run before playback (<see cref="EnsureSegmentsAsync"/>).
    /// Late enough not to hold
    /// up the playback's own requests to the stream.
    /// </summary>
    private void ProbeLater(Guid rowId)
    {
        if (!_probingLater.TryAdd(rowId, 0))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ProbeLaterDelay).ConfigureAwait(false);

                // A copy from the database: the probe points the item at a temporary file while it
                // runs, and the cached instance serves the playback meanwhile.
                if (
                    _libraryManager.RetrieveItem(rowId) is not Video row
                    || row.GelatoData<string>("mediaInfo") != RemuxDbService.SourceRemuxDb
                    || string.IsNullOrEmpty(row.Path)
                )
                {
                    return;
                }

                var libraryOptions = _libraryManager.GetLibraryOptions(row);
                var before = remuxDb.GetStreams(rowId);
                await ProbeStreamAsync(row, row.Path, CancellationToken.None)
                    .ConfigureAwait(false);

                // As a writer of the movie's rows: a sync meanwhile may have deleted this one, and
                // saving it would bring it back, or saved it anew, and saving this copy as it was
                // read would undo that (ProbeSaveGuard). The key is the copy's owner, read before
                // the probe.
                await _manager
                    .Value.RunExclusiveAsync(
                        row.PrimaryVersionId ?? rowId,
                        async ct =>
                        {
                            var cached = _libraryManager.GetItemById(rowId);
                            var stored = cached is null
                                ? null
                                : _libraryManager.RetrieveItem(rowId);
                            if (ProbeSaveGuard.ItemToSave(row, cached, stored) is null)
                            {
                                _log.LogInformation(
                                    "Probe result of {Id} not saved: a stream sync deleted it meanwhile",
                                    rowId
                                );
                                return;
                            }

                            if (!remuxDb.OnProbed(row, libraryOptions))
                            {
                                // A dead link: keep RemuxDB's, and try again on the next playback.
                                remuxDb.RestoreStreams(rowId, before);
                                return;
                            }

                            remuxDb.CompareWithProbe(rowId, before, remuxDb.GetStreams(rowId));
                            await row.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct)
                                .ConfigureAwait(false);
                        },
                        CancellationToken.None
                    )
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Background probe failed for {Id}", rowId);
            }
            finally
            {
                _probingLater.TryRemove(rowId, out _);
            }
        });
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
            _log.LogInformation(
                "Probing stream for {Id} via {Url}",
                owner.Id,
                Redact.Url(streamUrl)
            );

            var options = new MetadataRefreshOptions(directoryService)
            {
                EnableRemoteContentProbe = true,
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
            };

            var probeProvider = FindProbeProvider(owner);
            if (probeProvider is not null)
            {
                // Call the ffprobe provider directly instead of going through
                // RefreshMetadata.
                //
                // RefreshMetadata runs the whole metadata pipeline, and with
                // FullRefresh that includes ExecuteRemoteProviders - so every
                // stream probe also re-queried OMDb/TMDb for the item. On a
                // library browsed through Gelato that is a remote metadata
                // lookup per probe, and it is where the recurring
                // "Error in The Open Movie Database" JsonException spam comes
                // from: OMDb returns malformed JSON for some season payloads
                // and the probe drags that call along every time.
                //
                // The probe provider on its own does exactly what is wanted
                // here - read the container's streams - and nothing else. The
                // caller already persists the result with
                // UpdateToRepositoryAsync and runs segment providers itself, so
                // no other part of the pipeline is needed. It also keeps image
                // fetchers away from the item while its path points at the
                // temporary .strm file.
                await probeProvider.FetchAsync(owner, options, ct).ConfigureAwait(false);
            }
            else
            {
                // No probe provider resolved - fall back to the old path rather
                // than silently skipping the probe. Logged at information: this
                // path used to be taken on every probe without anyone noticing.
                _log.LogInformation(
                    "No probe provider available for {Id}, falling back to RefreshMetadata",
                    owner.Id
                );
                await owner.RefreshMetadata(options, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The playback went away or the pre-probe ran out of time: the caller decides what
            // that is, and saving after it would fail on the same token anyway.
            throw;
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
