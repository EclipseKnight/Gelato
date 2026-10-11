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

    // Per TVDB show and id kind, the lowest id the list names as season 1 ("500:kitsu" -> 12).
    private readonly Dictionary<string, int> _firstSeason;

    private AnimeSeasonMap(Dictionary<string, Link> links, Dictionary<string, int> firstSeason)
    {
        _links = links;
        _firstSeason = firstSeason;
    }

    public int Count => _links.Count;

    /// <summary>Reads the list. Entries without a TVDB id or a TVDB season are left out.</summary>
    public static AnimeSeasonMap Parse(Stream json)
    {
        var links = new Dictionary<string, Link>(StringComparer.OrdinalIgnoreCase);
        var firstSeason = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return new AnimeSeasonMap(links, firstSeason);
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
                if (Int(e, field) is not { } id || id <= 0)
                    continue;
                links.TryAdd($"{prefix}:{id}", link);
                var show = $"{tvdb}:{prefix}";
                if (number == 1 && (!firstSeason.TryGetValue(show, out var first) || id < first))
                    firstSeason[show] = id;
            }
        }
        return new AnimeSeasonMap(links, firstSeason);
    }

    /// <summary>
    /// Whether the list names a catalogue id as a part of a show other than the show itself: a
    /// later season, specials, or a second season 1 entry (a split first season, "Part 2"). Of
    /// several season 1 entries, the lowest id of that kind is the show. False for ids the list
    /// doesn't have.
    /// </summary>
    public bool IsSplitEntry(string? stremioId)
    {
        if (Find(stremioId) is not { } link)
            return false;
        if (link.Season != 1)
            return true;
        var parts = stremioId!.Split(':');
        return int.TryParse(parts[1], out var id)
            && _firstSeason.TryGetValue($"{link.TvdbId}:{parts[0]}", out var first)
            && id != first;
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

    /// <summary>
    /// The order deferred entries are placed in: by catalogue id length, then text (numeric order
    /// for ids of one kind; the earlier part usually has the lower id), so within one import run
    /// the result doesn't depend on the order the entries were found in.
    /// </summary>
    public static IEnumerable<StremioMeta> FilingOrder(IEnumerable<StremioMeta> entries) =>
        entries.OrderBy(m => m.Id.Length).ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a library series can be the show a later season is filed under: not one the list
    /// names as a part kept as its own show (a later season, an OVA, a second season 1 entry),
    /// which carries the show's TVDB and IMDb ids too.
    /// </summary>
    public static bool CanBeParent(AnimeSeasonMap map, string? seriesStremioId) =>
        !map.IsSplitEntry(seriesStremioId);

    public enum Decision
    {
        /// <summary>Import as its own show, as before.</summary>
        OwnShow,

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
        // Only with a confident mapping: one show with that TVDB id, a later season (season 1
        // is the show itself), and an entry that is one season itself.
        if (link is null || link.Season < 2 || parents != 1)
            return Decision.OwnShow;
        if (entrySeasons.Count != 1)
            return Decision.OwnShow;
        // Episodes the parent got from elsewhere are never moved or renumbered, and the entry is
        // not dropped either (a second cour of the same season): it stays a show of its own.
        if (parentSeasonEpisodes > parentSeasonFiledFromEntry)
            return Decision.OwnShow;
        return Decision.File;
    }
}
