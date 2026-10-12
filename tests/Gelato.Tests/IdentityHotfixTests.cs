using Gelato.Decorators;
using Gelato.Services;
using Xunit;

namespace Gelato.Tests;

public class IdentityHotfixTests
{
    private static AnimeSeasonMap Map() =>
        AnimeSeasonMap.Parse(
            new MemoryStream(
                System.Text.Encoding.UTF8.GetBytes(
                    """
                    [
                      { "kitsu_id": 12, "tvdb_id": 500, "imdb_id": ["tt1"], "season": { "tvdb": 1 } },
                      { "kitsu_id": 13, "tvdb_id": 500, "imdb_id": ["tt1"], "season": { "tvdb": 2 } },
                      { "kitsu_id": 14, "tvdb_id": 500, "imdb_id": ["tt1"], "season": { "tvdb": 0 } },
                      { "kitsu_id": 15, "tvdb_id": 500, "imdb_id": ["tt1"], "season": { "tvdb": 1 } },
                      { "kitsu_id": 9, "tvdb_id": 600, "imdb_id": ["tt2"], "season": { "tvdb": 1 } }
                    ]
                    """
                )
            )
        );

    [Fact]
    public void Imdb_match_is_the_same_title_without_a_native_id_or_with_the_same_one()
    {
        var native = new KeyValuePair<string, string>("Kitsu", "12");
        Assert.True(CatalogIdentity.ImdbMatchIsSame(new Dictionary<string, string> { ["Imdb"] = "tt1" }, native, false, null));
        Assert.True(CatalogIdentity.ImdbMatchIsSame(new Dictionary<string, string> { ["Imdb"] = "tt1", ["kitsu"] = "12" }, native, true, null));
        Assert.False(CatalogIdentity.ImdbMatchIsSame(new Dictionary<string, string> { ["Imdb"] = "tt1", ["Kitsu"] = "13" }, native, false, null));
        Assert.False(CatalogIdentity.ImdbMatchIsSame(new Dictionary<string, string> { ["Imdb"] = "tt1", ["Mal"] = "12" }, native, false, null));
    }

    [Theory]
    [InlineData("12", true)] // season 1: the show itself
    [InlineData("13", false)] // a later season
    [InlineData("14", false)] // specials / OVA
    [InlineData("15", false)] // a second season 1 entry (Part 2): the lower id is the show
    [InlineData("99", true)] // not in the list: as before
    public void A_split_entry_does_not_take_a_series_known_by_its_imdb_id(string kitsu, bool same)
    {
        var imdbOnly = new Dictionary<string, string> { ["Imdb"] = "tt1" };
        var native = new KeyValuePair<string, string>("Kitsu", kitsu);
        Assert.Equal(same, CatalogIdentity.ImdbMatchIsSame(imdbOnly, native, true, Map()));
        // No list (FileSplitSeasons off, or not saved yet): matched as before.
        Assert.True(CatalogIdentity.ImdbMatchIsSame(imdbOnly, native, true, null));
    }

    [Fact]
    public void Of_several_season_one_entries_only_the_lowest_id_of_a_kind_is_the_show()
    {
        var map = Map();
        Assert.False(map.IsSplitEntry("kitsu:12"));
        Assert.True(map.IsSplitEntry("kitsu:15"));
        Assert.False(map.IsSplitEntry("kitsu:9")); // another show
        Assert.False(map.IsSplitEntry("kitsu:99"));
    }

    [Fact]
    public void A_claim_is_taken_once_and_lost_by_any_other_entry()
    {
        var s1 = new KeyValuePair<string, string>("Kitsu", "12");
        var ova = new KeyValuePair<string, string>("Kitsu", "14");
        Assert.Equal(CatalogIdentity.ClaimResult.Take, CatalogIdentity.Claim(new Dictionary<string, string> { ["Imdb"] = "tt1" }, s1));
        var stamped = new Dictionary<string, string> { ["Imdb"] = "tt1", ["Kitsu"] = "12" };
        Assert.Equal(CatalogIdentity.ClaimResult.Ours, CatalogIdentity.Claim(stamped, s1));
        Assert.Equal(CatalogIdentity.ClaimResult.Taken, CatalogIdentity.Claim(stamped, ova));
    }

    [Fact]
    public void An_item_found_by_imdb_that_carries_another_entrys_native_id_is_taken()
    {
        var ova = new KeyValuePair<string, string>("Kitsu", "14");
        // The found copy already carries the season 1 entry's id: reached through the IMDb id,
        // so it goes through the gate, where the saved ids say another entry took it.
        var taken = new Dictionary<string, string> { ["Imdb"] = "tt1", ["Kitsu"] = "12" };
        Assert.Equal(CatalogIdentity.ClaimResult.Taken, CatalogIdentity.Claim(taken, ova));
        Assert.Equal(CatalogIdentity.ClaimResult.Take, CatalogIdentity.Claim(new Dictionary<string, string> { ["Imdb"] = "tt1" }, ova));
        // Found by its own native id: no claim to check.
        Assert.Equal(CatalogIdentity.ClaimResult.Ours, CatalogIdentity.Claim(new Dictionary<string, string> { ["Imdb"] = "tt1", ["kitsu"] = "14" }, ova));
    }

    [Fact]
    public void An_item_with_two_native_ids_found_by_the_second_one_is_ours()
    {
        var both = new Dictionary<string, string> { ["Imdb"] = "tt1", ["Kitsu"] = "12", ["AniDB"] = "77" };
        Assert.Equal(CatalogIdentity.ClaimResult.Ours, CatalogIdentity.Claim(both, new("AniDB", "77")));
        Assert.Equal(CatalogIdentity.ClaimResult.Taken, CatalogIdentity.Claim(both, new("AniDB", "78")));
    }

    [Fact]
    public void Only_tv_rows_count_toward_the_show()
    {
        var map = AnimeSeasonMap.Parse(
            new MemoryStream(
                System.Text.Encoding.UTF8.GetBytes(
                    """
                    [
                      { "type": "MOVIE", "kitsu_id": 3, "tvdb_id": 500, "season": { "tvdb": 1 } },
                      { "type": "ONA", "kitsu_id": 4, "tvdb_id": 500, "season": { "tvdb": 1 } },
                      { "type": "TV", "kitsu_id": 12, "tvdb_id": 500, "season": { "tvdb": 1 } },
                      { "type": "TV", "kitsu_id": 15, "tvdb_id": 500, "season": { "tvdb": 1 } },
                      { "type": "ONA", "kitsu_id": 21, "tvdb_id": 700, "season": { "tvdb": 1 } },
                      { "type": "ONA", "kitsu_id": 22, "tvdb_id": 700, "season": { "tvdb": 1 } }
                    ]
                    """
                )
            )
        );
        Assert.False(map.IsSplitEntry("kitsu:12"));
        Assert.True(map.IsSplitEntry("kitsu:3"));
        Assert.True(map.IsSplitEntry("kitsu:4"));
        Assert.True(map.IsSplitEntry("kitsu:15"));
        Assert.True(CatalogIdentity.ImdbMatchIsSame(new Dictionary<string, string> { ["Imdb"] = "tt1" }, new("Kitsu", "12"), true, map));
        // No TV row: the lowest id of any type is the show.
        Assert.False(map.IsSplitEntry("kitsu:21"));
        Assert.True(map.IsSplitEntry("kitsu:22"));
    }

    [Fact]
    public void The_saved_mapping_list_is_read_without_a_download()
    {
        var folder = Path.Combine(Path.GetTempPath(), "gelato-peek-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            const string Url = "https://example.invalid/peek-list.json";
            Assert.Null(AnimeSeasonMapStore.Peek(Url, folder));
            File.WriteAllText(Path.Combine(folder, "anime-list-full.json"), """[{ "kitsu_id": 12, "tvdb_id": 500, "season": { "tvdb": 1 } }]""");
            File.WriteAllText(Path.Combine(folder, "anime-list-full.json.url"), "https://example.invalid/other.json");
            Assert.Null(AnimeSeasonMapStore.Peek(Url, folder));
            File.WriteAllText(Path.Combine(folder, "anime-list-full.json.url"), Url);
            Assert.Equal(1, AnimeSeasonMapStore.Peek(Url, folder)?.Find("kitsu:12")?.Season);
        }
        finally
        {
            AnimeSeasonMapStore.Reset();
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task The_saved_mapping_list_is_not_read_while_a_download_replaces_it()
    {
        var folder = Path.Combine(Path.GetTempPath(), "gelato-peek-" + Guid.NewGuid().ToString("N"));
        var served = Path.Combine(folder, "served");
        Directory.CreateDirectory(served);
        File.WriteAllText(Path.Combine(served, "list.json"), """[{ "kitsu_id": 12, "tvdb_id": 500, "season": { "tvdb": 2 } }]""");
        using var addon = new Fakes.FakeAddon(served);
        addon.Delays["list.json"] = TimeSpan.FromSeconds(1);
        var url = addon.BaseUrl + "/list.json";
        try
        {
            // A day-old copy from the same URL: GetAsync downloads a new one.
            var file = Path.Combine(folder, "anime-list-full.json");
            File.WriteAllText(file, """[{ "kitsu_id": 12, "tvdb_id": 500, "season": { "tvdb": 1 } }]""");
            File.WriteAllText(file + ".url", url);
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-2));
            var get = AnimeSeasonMapStore.GetAsync(url, folder, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);
            while (addon.Requests.IsEmpty)
                await Task.Delay(10);
            Assert.Null(AnimeSeasonMapStore.Peek(url, folder));
            Assert.Equal(2, (await get)?.Find("kitsu:12")?.Season);
            Assert.Equal(2, AnimeSeasonMapStore.Peek(url, folder)?.Find("kitsu:12")?.Season);
        }
        finally
        {
            AnimeSeasonMapStore.Reset();
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task A_skipped_lookup_is_not_remembered()
    {
        var marked = false;
        await StreamSyncPolicy.SyncAndMarkAsync(_ => Task.FromResult(-1), false, _ => marked = true, CancellationToken.None);
        Assert.False(marked);
        await StreamSyncPolicy.SyncAndMarkAsync(_ => Task.FromResult(0), false, _ => marked = true, CancellationToken.None);
        Assert.True(marked);
    }
}
