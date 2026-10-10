using System.Text;
using Gelato.Services;
using Xunit;

namespace Gelato.Tests;

public class SplitSeasonTests
{
    private const string List = """
    [
      { "type": "TV", "anidb_id": 19433, "anilist_id": 195604, "kitsu_id": 50024, "mal_id": 61967,
        "imdb_id": ["tt7441658"], "tvdb_id": 331753, "season": { "tvdb": 2, "tmdb": 2 } },
      { "type": "OVA", "mal_id": 33950, "tvdb_id": 331753, "season": { "tvdb": 0 } },
      { "type": "TV", "mal_id": 1, "tvdb_id": 0, "season": { "tvdb": 1 } },
      { "type": "TV", "mal_id": 2, "tvdb_id": 5 },
      { "type": "TV", "mal_id": "3", "tvdb_id": "77", "season": { "tvdb": "1" } },
      "odd",
      { "type": "TV", "mal_id": 4, "tvdb_id": 9, "season": "x" }
    ]
    """;

    private static AnimeSeasonMap Map() => AnimeSeasonMap.Parse(new MemoryStream(Encoding.UTF8.GetBytes(List)));

    [Theory]
    [InlineData("mal:61967")]
    [InlineData("kitsu:50024")]
    [InlineData("anilist:195604")]
    [InlineData("anidb:19433")]
    [InlineData("kitsu:50024:3")]
    [InlineData("MAL:61967")]
    public void Finds_the_season_by_any_native_id(string id) =>
        Assert.Equal(new AnimeSeasonMap.Link(331753, 2), Map().Find(id));

    [Fact]
    public void Leaves_out_entries_without_tvdb_id_or_season()
    {
        var map = Map();
        Assert.Null(map.Find("mal:1"));
        Assert.Null(map.Find("mal:2"));
        Assert.Null(map.Find("mal:4"));
        Assert.Null(map.Find("tt7441658"));
        Assert.Null(map.Find(null));
        Assert.Equal(new AnimeSeasonMap.Link(331753, 0), map.Find("mal:33950"));
        Assert.Equal(new AnimeSeasonMap.Link(77, 1), map.Find("mal:3"));
    }

    [Fact]
    public void Keeps_the_imdb_ids_of_the_show()
    {
        Assert.Equal(["tt7441658"], Map().Find("mal:61967")!.Imdb);
        Assert.Empty(Map().Find("mal:3")!.Imdb);
    }

    [Fact]
    public void Not_an_array_is_empty() =>
        Assert.Equal(0, AnimeSeasonMap.Parse(new MemoryStream(Encoding.UTF8.GetBytes("{}"))).Count);

    private static readonly AnimeSeasonMap.Link S2 = new(331753, 2);

    [Fact]
    public void Files_a_new_season_into_the_one_parent() =>
        Assert.Equal(SplitSeason.Decision.File, SplitSeason.Decide(S2, 1, [1], 0, 0));

    [Fact]
    public void Files_again_when_the_season_only_holds_what_the_entry_filed() =>
        Assert.Equal(SplitSeason.Decision.File, SplitSeason.Decide(S2, 1, [1], 12, 12));

    [Fact]
    public void Parent_with_its_own_episodes_in_that_season_gets_nothing_moved() =>
        Assert.Equal(SplitSeason.Decision.ParentHasSeason, SplitSeason.Decide(S2, 1, [1], 13, 0));

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Needs_exactly_one_parent(int parents) =>
        Assert.Equal(SplitSeason.Decision.OwnShow, SplitSeason.Decide(S2, parents, [1], 0, 0));

    [Fact]
    public void No_mapping_or_specials_link_is_its_own_show()
    {
        Assert.Equal(SplitSeason.Decision.OwnShow, SplitSeason.Decide(null, 1, [1], 0, 0));
        Assert.Equal(SplitSeason.Decision.OwnShow, SplitSeason.Decide(new(331753, 0), 1, [1], 0, 0));
    }

    [Fact]
    public void Entry_with_several_seasons_or_none_is_its_own_show()
    {
        Assert.Equal(SplitSeason.Decision.OwnShow, SplitSeason.Decide(S2, 1, [1, 2], 0, 0));
        Assert.Equal(SplitSeason.Decision.OwnShow, SplitSeason.Decide(S2, 1, [], 0, 0));
    }
}
