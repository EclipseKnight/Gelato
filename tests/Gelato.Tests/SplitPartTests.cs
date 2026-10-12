using Gelato.Providers;
using Gelato.Services;
using Gelato.Tests.Fakes;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gelato.Tests;

/// <summary>
/// An OVA that carries its show's IMDb id is asked for by its own id, and a series keeps the
/// show's TVDB id, so a later season with no IMDb id in the mapping list finds it. Neither makes
/// Jellyfin list the series as another show.
/// </summary>
public sealed class SplitPartTests : IDisposable
{
    private readonly FakeAddon _addon = new(FakeAddon.Fixture("sharedimdb"));

    public void Dispose() => _addon.Dispose();

    private GelatoStremioProvider NewProvider() =>
        new(_addon.BaseUrl, new PlainHttpClientFactory(), NullLogger<GelatoStremioProvider>.Instance);

    private static readonly StremioMeta Ova = new()
    {
        Id = "kitsu:603",
        ImdbId = "tt0000601",
        Type = StremioMediaType.Series,
        Name = "Cine Show OVA",
    };

    [Fact]
    public async Task A_part_is_fetched_by_its_own_id_not_the_shows_imdb_id()
    {
        var p = NewProvider();
        // By the IMDb id first, the add-on answers with the show.
        Assert.Equal("Cine Show", (await p.GetMetaAsync(Ova))?.Name);
        Assert.Equal("Cine Show OVA", (await p.GetMetaAsync(Ova, ownIdFirst: true))?.Name);
    }

    [Fact]
    public async Task A_parts_refresh_asks_by_its_native_id_first()
    {
        // The OVA's Stremio id is the show's IMDb id; its native id says what it is.
        var ids = new Dictionary<string, string> { ["Imdb"] = "tt0000601", ["Stremio"] = "tt0000601", ["Kitsu"] = "603" };
        var p = NewProvider();
        Assert.Equal("Cine Show", (await p.GetMetaAsync(ids, StremioMediaType.Series))?.Name);
        Assert.Equal("Cine Show OVA", (await p.GetMetaAsync(ids, StremioMediaType.Series, nativeFirst: true))?.Name);
    }

    [Fact]
    public async Task Native_first_without_a_native_id_asks_as_before()
    {
        var ids = new Dictionary<string, string> { ["Imdb"] = "tt0000601" };
        Assert.Equal("Cine Show", (await NewProvider().GetMetaAsync(ids, StremioMediaType.Series, nativeFirst: true))?.Name);
    }

    [Fact]
    public void A_refreshed_series_keeps_the_tvdb_id_the_addon_reports()
    {
        var series = new Series();
        series.SetProviderId("Imdb", "tt0000810");
        GelatoSeriesProvider.KeepTvdbId(series, new StremioMeta { Id = "tt0000810", TvdbIdExtra = "900810" });
        Assert.Equal("900810", series.GetProviderId(SplitSeason.ShowTvdbKey));
        Assert.Null(series.GetProviderId("Tvdb"));

        // Its own TVDB id (a tvdb: catalogue id) is left as it is; none reported, none added.
        var keyed = new Series();
        keyed.SetProviderId("Tvdb", "77");
        GelatoSeriesProvider.KeepTvdbId(keyed, new StremioMeta { Id = "tvdb:77", TvdbIdExtra = "900810" });
        Assert.Equal("77", keyed.GetProviderId("Tvdb"));
        var none = new Series();
        GelatoSeriesProvider.KeepTvdbId(none, new StremioMeta { Id = "tt1" });
        Assert.Null(none.GetProviderId(SplitSeason.ShowTvdbKey));
    }

    private static Series NewSeries(params (string Provider, string Id)[] ids)
    {
        var series = new Series { Id = Guid.NewGuid() };
        foreach (var (provider, id) in ids)
            series.SetProviderId(provider, id);
        return series;
    }

    // Jellyfin lists series by their first user-data key (and keys episodes' watch state on it).
    private static string ListedAs(Series series) => series.GetUserDataKeys()[0];

    [Fact]
    public void Series_that_report_one_shows_tvdb_id_are_not_listed_as_one()
    {
        // A show and its second cour kept as its own show, both reported with the show's TVDB id.
        var show = NewSeries(("Imdb", "tt0000830"));
        var cour = NewSeries(("Mal", "80032"));
        GelatoSeriesProvider.KeepTvdbId(show, new StremioMeta { Id = "tt0000830", TvdbIdExtra = "900830" });
        GelatoSeriesProvider.KeepTvdbId(cour, new StremioMeta { Id = "mal:80032", TvdbIdExtra = "900830" });
        Assert.NotEqual(ListedAs(show), ListedAs(cour));
    }

    private const string List = """
    [
      { "type": "TV", "kitsu_id": 601, "tvdb_id": 900601, "season": { "tvdb": 1 }, "imdb_id": ["tt0000601"] },
      { "type": "OVA", "kitsu_id": 603, "tvdb_id": 900601, "season": { "tvdb": 0 }, "imdb_id": ["tt0000601"] }
    ]
    """;

    [Fact]
    public void A_part_kept_as_its_own_show_is_not_listed_as_the_show_whose_imdb_id_it_carries()
    {
        var map = AnimeSeasonMap.Parse(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(List)));
        var show = NewSeries(("Imdb", "tt0000601"));
        var ova = NewSeries(("Kitsu", "603"), ("Imdb", "tt0000601"), ("Stremio", "tt0000601"));
        // As the OVA is created without a key of its own, it is listed as the show.
        Assert.Equal(ListedAs(show), ListedAs(ova));

        ova.SetProviderId(MetadataProvider.Custom, SplitSeason.OwnKey(map, ova.ProviderIds)!);
        Assert.Equal("kitsu:603", ListedAs(ova));
        Assert.NotEqual(ListedAs(show), ListedAs(ova));

        // The show, by its IMDb id or its season 1 entry, and anything without the list: none.
        Assert.Null(SplitSeason.OwnKey(map, show.ProviderIds));
        Assert.Null(SplitSeason.OwnKey(map, new Dictionary<string, string> { ["Kitsu"] = "601", ["Imdb"] = "tt0000601" }));
        Assert.Null(SplitSeason.OwnKey(null, ova.ProviderIds));
    }
}
