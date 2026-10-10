namespace Gelato.Services;

/// <summary>
/// Titles that must never be created: one id per entry (<c>tt1345529</c>, <c>kitsu:12</c>,
/// <c>mal:61967</c>, <c>tmdb:123</c>, <c>tvdb:456</c>), optionally followed by <c># note</c>.
/// Filled in by hand only. A deleted title is not added: deleting a title to have it re-added is a
/// normal way to refresh it.
/// </summary>
public static class ExcludeList
{
    private static readonly (string Provider, string Prefix)[] Prefixed =
    [
        ("Kitsu", "kitsu"),
        ("Mal", "mal"),
        ("Anilist", "anilist"),
        ("AniDB", "anidb"),
        ("Tmdb", "tmdb"),
        ("Tvdb", "tvdb"),
    ];

    /// <summary>The ids in the list, without notes, blanks and comment lines.</summary>
    public static HashSet<string> Parse(IEnumerable<string>? entries)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in entries ?? [])
        {
            foreach (var line in (raw ?? "").Split('\n', ',', ';'))
            {
                var id = line.Split('#')[0].Trim();
                if (id.Length > 0)
                    set.Add(id);
            }
        }
        return set;
    }

    /// <summary>Every id a title can be listed under.</summary>
    public static IEnumerable<string> Keys(string? stremioId, IReadOnlyDictionary<string, string> providerIds)
    {
        if (!string.IsNullOrWhiteSpace(stremioId))
            yield return stremioId.Trim();
        foreach (var (key, value) in providerIds)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;
            if (string.Equals(key, "Imdb", StringComparison.OrdinalIgnoreCase))
                yield return value.Trim();
            foreach (var (provider, prefix) in Prefixed)
            {
                if (string.Equals(key, provider, StringComparison.OrdinalIgnoreCase))
                    yield return $"{prefix}:{value.Trim()}";
            }
        }
    }

    /// <summary>The list entry this title matches, or null.</summary>
    public static string? Match(
        HashSet<string> excluded,
        string? stremioId,
        IReadOnlyDictionary<string, string> providerIds
    )
    {
        if (excluded.Count == 0)
            return null;
        foreach (var key in Keys(stremioId, providerIds))
        {
            if (excluded.TryGetValue(key, out var entry))
                return entry;
        }
        return null;
    }
}
