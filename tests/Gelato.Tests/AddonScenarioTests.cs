using System.Net;
using System.Text.Json;
using Gelato.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gelato.Tests;

/// <summary>
/// Gelato's add-on client against the fake add-on and its scenario fixtures. These check what
/// Gelato reads from each scenario; the library-level checks (counts, owners, watch state) run
/// against a test Jellyfin.
/// </summary>
public sealed class AddonScenarioTests : IDisposable
{
    private readonly FakeAddon _addon = new(FakeAddon.Fixture("onepiece", "import1"));

    private GelatoStremioProvider NewProvider() =>
        new(_addon.BaseUrl, new PlainHttpClientFactory(), NullLogger<GelatoStremioProvider>.Instance);

    public void Dispose() => _addon.Dispose();

    private void Use(params string[] parts) => _addon.UseFixtures(FakeAddon.Fixture(parts));

    [Fact]
    public async Task Show_whose_imdb_id_changes_keeps_its_native_id()
    {
        var first = (await NewProvider().GetCatalogMetasAsync("anime", "series")).Single();
        Use("onepiece", "import2");
        var second = (await NewProvider().GetCatalogMetasAsync("anime", "series")).Single();

        Assert.Equal("kitsu:12", first.Id);
        Assert.Equal(first.Id, second.Id);
        Assert.NotEqual(first.ImdbId, second.ImdbId);
        Assert.Equal(first.ImdbId, first.GetProviderIds()["Imdb"]);
        Assert.Equal(second.ImdbId, second.GetProviderIds()["Imdb"]);
    }

    [Fact]
    public async Task Stages_sharing_a_tvdb_id_are_separate_entries()
    {
        Use("initiald");
        var metas = await NewProvider().GetCatalogMetasAsync("anime", "series");

        Assert.Equal(5, metas.Count);
        Assert.Equal(5, metas.Select(m => m.Id).Distinct().Count());
        Assert.Single(metas.Select(m => m.GetProviderIds()["Tvdb"]).Distinct());
    }

    [Fact]
    public async Task Different_films_can_share_a_tmdb_id()
    {
        Use("sharedtmdb");
        var metas = await NewProvider().GetCatalogMetasAsync("anime", "movie");

        Assert.Equal(2, metas.Count);
        Assert.Single(metas.Select(m => m.GetProviderIds()["Tmdb"]).Distinct());
        Assert.Equal(2, metas.Select(m => m.GetProviderIds()["Imdb"]).Distinct().Count());
    }

    [Fact]
    public async Task Season_listed_as_its_own_entry_points_at_the_same_show()
    {
        Use("blackclover");
        var p = NewProvider();
        var parent = await p.GetMetaAsync("tt7441658", StremioMediaType.Series);
        var season2 = await p.GetMetaAsync("mal:61967", StremioMediaType.Series);

        Assert.NotNull(parent);
        Assert.NotNull(season2);
        Assert.Equal(parent!.TvdbIdExtra, season2!.TvdbIdExtra);
        Assert.All(parent.Videos!, v => Assert.Equal(1, v.Season));
        Assert.All(season2.Videos!, v => Assert.Equal(1, v.Season));
    }

    [Fact]
    public async Task Ended_show_that_comes_back_reads_as_continuing()
    {
        Use("comeback", "import1");
        var before = await NewProvider().GetMetaAsync("tt7441658", StremioMediaType.Series);
        Use("comeback", "import2");
        var after = await NewProvider().GetMetaAsync("tt7441658", StremioMediaType.Series);

        Assert.Equal(StremioStatus.Ended, before!.Status);
        Assert.Equal(StremioStatus.Continuing, after!.Status);
        Assert.True(after.Videos!.Count > before.Videos!.Count);
    }

    [Fact]
    public async Task Two_entries_claiming_the_same_episodes()
    {
        Use("sharedepisodes");
        var p = NewProvider();
        var a = await p.GetMetaAsync("kitsu:12", StremioMediaType.Series);
        var b = await p.GetMetaAsync("tt11737520", StremioMediaType.Series);

        var ids = a!.Videos!.Select(v => v.Id).Intersect(b!.Videos!.Select(v => v.Id)).ToList();
        Assert.Equal(3, ids.Count);
    }

    [Fact]
    public async Task Zero_streams_is_an_empty_list()
    {
        Use("streams");
        var streams = await NewProvider()
            .GetStreamsAsync(new StremioUri(StremioMediaType.Movie, "tt0000002"));
        Assert.Empty(streams);
    }

    [Fact]
    public async Task Streams_are_read()
    {
        Use("streams");
        var streams = await NewProvider()
            .GetStreamsAsync(new StremioUri(StremioMediaType.Movie, "tt0000001"));
        Assert.Equal(2, streams.Count);
    }

    [Fact]
    public async Task Slow_answer_is_waited_for()
    {
        Use("streams");
        _addon.Delays["/stream/"] = TimeSpan.FromMilliseconds(800);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var streams = await NewProvider()
            .GetStreamsAsync(new StremioUri(StremioMediaType.Movie, "tt0000001"));
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(700));
        Assert.Equal(2, streams.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Failed_stream_answer_throws_with_its_status(HttpStatusCode status)
    {
        Use("streams");
        _addon.Failures["/stream/"] = status;
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            NewProvider().GetStreamsAsync(new StremioUri(StremioMediaType.Movie, "tt0000001"))
        );
        Assert.Equal(status, ex.StatusCode);
    }

    [Fact]
    public async Task Broken_json_throws()
    {
        Use("streams");
        await Assert.ThrowsAnyAsync<JsonException>(() =>
            NewProvider().GetStreamsAsync(new StremioUri(StremioMediaType.Movie, "tt0000003"))
        );
    }

    [Fact]
    public async Task Missing_manifest_is_not_ready()
    {
        Use("initiald");
        Assert.False(await NewProvider().IsReady());
    }

    [Fact]
    public async Task AioStreams_gets_its_own_user_agent_for_streams()
    {
        Use("streams");
        await NewProvider().GetStreamsAsync(new StremioUri(StremioMediaType.Movie, "tt0000001"));
        var stream = _addon.Requests.Single(r => r.Path.StartsWith("/stream/", StringComparison.Ordinal));
        Assert.Contains("AIOStreams/1.0", stream.UserAgent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Meta_is_cached_between_reads()
    {
        Use("blackclover");
        var p = NewProvider();
        await p.GetMetaAsync("tt7441658", StremioMediaType.Series);
        await p.GetMetaAsync("tt7441658", StremioMediaType.Series);
        Assert.Single(_addon.Requests, r => r.Path.StartsWith("/meta/", StringComparison.Ordinal));
    }
}
