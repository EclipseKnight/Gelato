using System.Text.Json;

namespace Gelato.Services;

/// <summary>
/// The season-to-show links of the anime id mapping list (Fribb/anime-lists,
/// anime-list-full.json): which TVDB series and season an anime catalogue entry is. The anime
/// catalogues list a new season as its own entry ("Black Clover Season 2", mal:61967); the list
/// says it is season 2 of TVDB 331753, the show the library already has.
/// </summary>
public sealed class AnimeSeasonMap
{
    public sealed record Link(int TvdbId, int Season)
    {
        /// <summary>The IMDb ids the list gives for the show, used when no series carries the TVDB id.</summary>
        public IReadOnlyList<string> Imdb { get; init; } = [];

        public bool Equals(Link? other) => other is not null && TvdbId == other.TvdbId && Season == other.Season;

        public override int GetHashCode() => HashCode.Combine(TvdbId, Season);
    }

    private static readonly (string Field, string Prefix)[] Ids =
    [
        ("mal_id", "mal"),
        ("kitsu_id", "kitsu"),
        ("anilist_id", "anilist"),
        ("anidb_id", "anidb"),
    ];

    private readonly Dictionary<string, Link> _links;

    private AnimeSeasonMap(Dictionary<string, Link> links) => _links = links;

    public int Count => _links.Count;

    /// <summary>Reads the list. Entries without a TVDB id or a TVDB season are left out.</summary>
    public static AnimeSeasonMap Parse(Stream json)
    {
        var links = new Dictionary<string, Link>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return new AnimeSeasonMap(links);
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object)
                continue;
            if (Int(e, "tvdb_id") is not { } tvdb || tvdb <= 0)
                continue;
            if (
                !e.TryGetProperty("season", out var season)
                || season.ValueKind != JsonValueKind.Object
                || Int(season, "tvdb") is not { } number
            )
                continue;
            var link = new Link(tvdb, number) { Imdb = Imdbs(e) };
            foreach (var (field, prefix) in Ids)
            {
                if (Int(e, field) is { } id && id > 0)
                    links.TryAdd($"{prefix}:{id}", link);
            }
        }
        return new AnimeSeasonMap(links);
    }

    /// <summary>The link for a catalogue id such as <c>mal:61967</c> or <c>kitsu:50024</c>.</summary>
    public Link? Find(string? stremioId)
    {
        if (string.IsNullOrWhiteSpace(stremioId))
            return null;
        var parts = stremioId.Split(':');
        if (parts.Length < 2)
            return null;
        return _links.TryGetValue($"{parts[0]}:{parts[1]}", out var link) ? link : null;
    }

    private static List<string> Imdbs(JsonElement e)
    {
        if (!e.TryGetProperty("imdb_id", out var v))
            return [];
        var all = v.ValueKind switch
        {
            JsonValueKind.Array => v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()),
            JsonValueKind.String => [v.GetString()],
            _ => [],
        };
        return all.Where(x => x is not null && x.StartsWith("tt", StringComparison.OrdinalIgnoreCase)).Select(x => x!).Distinct().ToList();
    }

    private static int? Int(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v))
            return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n))
            return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s))
            return s;
        return null;
    }
}

/// <summary>What to do with an anime catalogue entry the mapping names as a season of a show.</summary>
public static class SplitSeason
{
    /// <summary>
    /// True when a Gelato episode path comes from another anime catalogue entry than
    /// <paramref name="syncingId"/>, i.e. it was filed into this show as a split season.
    /// Native ids never change, so a show's own re-keyed IMDb episodes do not count.
    /// </summary>
    public static bool IsFiledFromOtherEntry(string? path, string syncingId)
    {
        const string Stub = "gelato://stub/";
        if (path is null || !path.StartsWith(Stub, StringComparison.OrdinalIgnoreCase))
            return false;
        var id = path[Stub.Length..];
        var native =
            id.StartsWith("mal:", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("kitsu:", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("anilist:", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("anidb:", StringComparison.OrdinalIgnoreCase);
        return native && !id.StartsWith(syncingId + ":", StringComparison.OrdinalIgnoreCase);
    }

    public enum Decision
    {
        /// <summary>Import as its own show, as before.</summary>
        OwnShow,

        /// <summary>The parent has the season already: no copy, nothing filed.</summary>
        ParentHasSeason,

        /// <summary>File the entry's episodes as the season of the parent.</summary>
        File,
    }

    /// <param name="link">The mapping link of the entry, if any.</param>
    /// <param name="parents">Library series with the link's TVDB id (not the entry itself).</param>
    /// <param name="entrySeasons">Season numbers in the entry's own episode list (0 left out).</param>
    /// <param name="parentSeasonEpisodes">Episodes the parent has in that season.</param>
    /// <param name="parentSeasonFiledFromEntry">How many of those were filed from this entry.</param>
    public static Decision Decide(
        AnimeSeasonMap.Link? link,
        int parents,
        IReadOnlyCollection<int> entrySeasons,
        int parentSeasonEpisodes,
        int parentSeasonFiledFromEntry
    )
    {
        // Only with a confident mapping: one show with that TVDB id, a real season, and an entry
        // that is one season itself.
        if (link is null || link.Season < 1 || parents != 1)
            return Decision.OwnShow;
        if (entrySeasons.Count != 1)
            return Decision.OwnShow;
        // Episodes the parent got from elsewhere are never moved or renumbered; the entry would
        // be the same season twice, so no copy either.
        if (parentSeasonEpisodes > parentSeasonFiledFromEntry)
            return Decision.ParentHasSeason;
        return Decision.File;
    }
}
