using Gelato.Services;
using Xunit;

namespace Gelato.Tests;

public class ExcludeListTests
{
    [Fact]
    public void Parses_ids_notes_and_blank_lines()
    {
        var set = ExcludeList.Parse(["tt1345529 # One Piece copy", "", "# a note", " kitsu:12 ", "mal:1, mal:2"]);
        Assert.Equal(new[] { "kitsu:12", "mal:1", "mal:2", "tt1345529" }, set.OrderBy(x => x));
    }

    [Fact]
    public void Matches_by_stremio_id_imdb_or_prefixed_provider_id()
    {
        var set = ExcludeList.Parse(["tt1345529", "mal:61967", "tmdb:99"]);
        Assert.Equal("tt1345529", ExcludeList.Match(set, "kitsu:5", new Dictionary<string, string> { ["Imdb"] = "tt1345529" }));
        Assert.Equal("mal:61967", ExcludeList.Match(set, "mal:61967", new Dictionary<string, string>()));
        Assert.Equal("mal:61967", ExcludeList.Match(set, "kitsu:50024", new Dictionary<string, string> { ["Mal"] = "61967" }));
        Assert.Equal("tmdb:99", ExcludeList.Match(set, "tt1", new Dictionary<string, string> { ["Tmdb"] = "99" }));
        Assert.Equal("tt1345529", ExcludeList.Match(set, "TT1345529", new Dictionary<string, string>()));
    }

    [Fact]
    public void No_match_and_empty_list()
    {
        var set = ExcludeList.Parse(["tt1345529"]);
        Assert.Null(ExcludeList.Match(set, "tt0388629", new Dictionary<string, string> { ["Tmdb"] = "1345529" }));
        Assert.Null(ExcludeList.Match(ExcludeList.Parse(null), "tt1345529", new Dictionary<string, string>()));
    }
}

public class DuplicateReportTests
{
    private static DuplicateReport.Item I(string name, int eps, params (string, string)[] ids) =>
        new(Guid.NewGuid(), name, "Series", ids.ToDictionary(x => x.Item1, x => x.Item2), eps);

    [Fact]
    public void Same_native_id_is_likely()
    {
        var a = I("A", 3, ("Kitsu", "12"));
        var b = I("A copy", 3, ("Kitsu", "12"));
        var f = Assert.Single(DuplicateReport.Build([a, b, I("C", 1, ("Kitsu", "13"))], []));
        Assert.Equal(DuplicateReport.Confidence.Likely, f.Confidence);
        Assert.Equal(new[] { a, b }, f.Items);
    }

    [Fact]
    public void Series_whose_episodes_all_belong_to_another_is_the_same_show()
    {
        var owner = I("One Piece", 1179, ("Imdb", "tt0388629"));
        var copy = I("One Piece", 0, ("Imdb", "tt1345529"));
        var c = new EpisodeConflicts.Entry(copy.Id, copy.Name, owner.Id, owner.Name, 1179, 1179, DateTime.UtcNow);
        var f = Assert.Single(DuplicateReport.Build([owner, copy], [c]));
        Assert.Equal(DuplicateReport.Confidence.Likely, f.Confidence);
        Assert.Contains("the same show", f.Reason);
        Assert.Equal(new[] { copy, owner }, f.Items);
    }

    [Fact]
    public void Some_shared_episodes_are_related_only()
    {
        var owner = I("Pokémon", 1200);
        var other = I("Pokémon Horizons", 50);
        var c = new EpisodeConflicts.Entry(other.Id, other.Name, owner.Id, owner.Name, 1004, 1100, DateTime.UtcNow);
        var f = Assert.Single(DuplicateReport.Build([owner, other], [c]));
        Assert.Equal(DuplicateReport.Confidence.Related, f.Confidence);
    }

    [Fact]
    public void Stale_entry_whose_owner_holds_no_episodes_is_left_out()
    {
        var a = I("One Piece", 3);
        var b = I("One Piece (copy)", 0);
        var stale = new EpisodeConflicts.Entry(a.Id, a.Name, b.Id, b.Name, 1, 3, DateTime.UtcNow);
        var real = new EpisodeConflicts.Entry(b.Id, b.Name, a.Id, a.Name, 1, 3, DateTime.UtcNow);
        var f = Assert.Single(DuplicateReport.Build([a, b], [stale, real]));
        Assert.Equal(DuplicateReport.Confidence.Likely, f.Confidence);
        Assert.Equal(new[] { b, a }, f.Items);
    }

    [Fact]
    public void Conflict_for_an_item_no_longer_in_the_library_is_left_out()
    {
        var owner = I("X", 3);
        var c = new EpisodeConflicts.Entry(Guid.NewGuid(), "gone", owner.Id, owner.Name, 3, 3, DateTime.UtcNow);
        Assert.Empty(DuplicateReport.Build([owner], [c]));
    }

    [Fact]
    public void Same_tvdb_or_tmdb_id_is_maybe_and_not_repeated_when_already_likely()
    {
        var a = I("Initial D First Stage", 26, ("Tvdb", "1"), ("Kitsu", "100"));
        var b = I("Initial D Second Stage", 13, ("Tvdb", "1"), ("Kitsu", "101"));
        var f = Assert.Single(DuplicateReport.Build([a, b], []));
        Assert.Equal(DuplicateReport.Confidence.Maybe, f.Confidence);

        var c = I("Same", 3, ("Tvdb", "2"), ("Kitsu", "7"));
        var d = I("Same copy", 3, ("Tvdb", "2"), ("Kitsu", "7"));
        Assert.Single(DuplicateReport.Build([c, d], []));
    }

    [Fact]
    public void Imdb_change_without_native_id_is_likely()
    {
        var old = I("InuYasha", 198, ("Tvdb", "71361"), ("Tmdb", "35610"), ("Imdb", "tt0290223"));
        var copy = I("InuYasha", 32, ("Tvdb", "71361"), ("Tmdb", "35610"), ("Imdb", "tt15436162"));
        var f = Assert.Single(DuplicateReport.Build([old, copy], []));
        Assert.Equal(DuplicateReport.Confidence.Likely, f.Confidence);
        Assert.Contains("IMDb id change", f.Reason);

        // Stages share the TVDB id but not the TMDB id: still only maybe.
        var s2 = I("InuYasha Final Act", 26, ("Tvdb", "71361"), ("Tmdb", "99"), ("Imdb", "tt1"));
        Assert.Equal(DuplicateReport.Confidence.Maybe, Assert.Single(DuplicateReport.Build([old, s2], [])).Confidence);
    }

    [Fact]
    public void Movies_and_series_with_one_tmdb_number_are_not_grouped()
    {
        var m = new DuplicateReport.Item(Guid.NewGuid(), "Film", "Movie", new Dictionary<string, string> { ["Tmdb"] = "5" }, 0);
        var s = I("Show", 3, ("Tmdb", "5"));
        Assert.Empty(DuplicateReport.Build([m, s], []));
    }

    [Fact]
    public void Text_names_every_item()
    {
        var a = I("A", 3, ("Kitsu", "12"));
        var b = I("B", 3, ("Kitsu", "12"));
        var text = DuplicateReport.ToText(DuplicateReport.Build([a, b], []), DateTime.UtcNow);
        Assert.Contains("1 likely", text);
        Assert.Contains("  - A (Series", text);
        Assert.Contains("  - B (Series", text);
    }
}
