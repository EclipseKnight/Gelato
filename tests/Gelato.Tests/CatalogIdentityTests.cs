using Gelato.Services;
using Xunit;
using static Gelato.Services.CatalogIdentity;

namespace Gelato.Tests;

public class CatalogIdentityTests
{
    private static readonly DateTime Now = new(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);

    private static Dictionary<string, string> Ids(params (string, string)[] pairs) =>
        pairs.ToDictionary(p => p.Item1, p => p.Item2, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Native_id_is_the_identity()
    {
        var ids = Ids(("Kitsu", "12"), ("Imdb", "tt11737520"), ("Stremio", "tt11737520"));
        Assert.Equal(Ids(("Kitsu", "12")), LookupIds(ids));
    }

    [Fact]
    public void Without_a_native_id_the_other_ids_are_used_but_never_aliases()
    {
        var ids = Ids(("Imdb", "tt1"), ("Stremio", "tt1"), (AliasKey + "0", "tt0"), (AliasSeenKey + "0", "2026-10-01"));
        Assert.Equal(Ids(("Imdb", "tt1"), ("Stremio", "tt1")), LookupIds(ids));
    }

    [Theory]
    [InlineData("Mal", "61967")]
    [InlineData("Anilist", "21")]
    [InlineData("AniDB", "69")]
    public void Every_native_namespace_counts(string key, string value) =>
        Assert.Equal(new KeyValuePair<string, string>(key, value), NativeId(Ids((key, value), ("Imdb", "tt1"))));

    [Fact]
    public void Tvdb_and_tmdb_are_not_native() =>
        Assert.Null(NativeId(Ids(("Tvdb", "72025"), ("Tmdb", "5555"))));

    [Theory]
    [InlineData("tt1", "tt2", true)]
    [InlineData("tt1", "TT1", false)]
    [InlineData(null, "tt2", false)]
    [InlineData("tt1", null, false)]
    [InlineData("", "tt2", false)]
    public void Rekey_only_when_both_ids_are_known_and_differ(string? have, string? incoming, bool expected) =>
        Assert.Equal(expected, NeedsRekey(have, incoming));

    [Fact]
    public void Aliases_round_trip_newest_first()
    {
        var ids = Ids(("Kitsu", "12"));
        WriteAliases(ids, [new Alias("tt1", Now.AddDays(-2)), new Alias("tt2", Now)]);
        var read = ReadAliases(ids);
        Assert.Equal(["tt2", "tt1"], read.Select(a => a.Id));
        Assert.Equal(Now.Date, read[0].Seen);
        Assert.Equal("12", ids["Kitsu"]);
    }

    [Fact]
    public void Writing_fewer_aliases_clears_the_old_entries()
    {
        var ids = Ids();
        WriteAliases(ids, [new Alias("tt1", Now), new Alias("tt2", Now)]);
        WriteAliases(ids, [new Alias("tt3", Now)]);
        Assert.Equal(["tt3"], ReadAliases(ids).Select(a => a.Id));
        Assert.Equal(2, ids.Count);
    }

    [Fact]
    public void Adding_an_alias_again_refreshes_it()
    {
        var list = AddAlias([new Alias("tt1", Now.AddDays(-30))], "TT1", Now);
        Assert.Single(list);
        Assert.Equal(Now, list[0].Seen);
    }

    [Fact]
    public void Aliases_are_capped_to_the_newest()
    {
        var list = new List<Alias>();
        for (var i = 0; i < MaxAliases + 3; i++)
            list = AddAlias(list, $"tt{i}", Now.AddDays(i - 10));
        Assert.Equal(MaxAliases, list.Count);
        Assert.Equal($"tt{MaxAliases + 2}", list[0].Id);
        Assert.DoesNotContain(list, a => a.Id == "tt0");
    }

    [Fact]
    public void Aliases_not_seen_for_90_days_age_out()
    {
        var list = AddAlias([new Alias("old", Now - AliasTtl - TimeSpan.FromDays(1)), new Alias("recent", Now.AddDays(-10))], "new", Now);
        Assert.Equal(["new", "recent"], list.Select(a => a.Id));
    }

    [Fact]
    public void Removing_an_alias()
    {
        var list = RemoveAlias([new Alias("tt1", Now), new Alias("tt2", Now)], "TT1");
        Assert.Equal(["tt2"], list.Select(a => a.Id));
    }

    [Fact]
    public void Alias_lookup_covers_every_slot()
    {
        var d = AliasLookup("tt9");
        Assert.Equal(MaxAliases, d.Count);
        Assert.All(d, kv => Assert.Equal("tt9", kv.Value));
    }
}
