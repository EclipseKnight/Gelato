namespace Gelato.Services;

/// <summary>
/// The possible-duplicates report: read-only, nothing is merged or deleted from it. An admin
/// decides, and adds a title to the exclude list if it must not come back.
/// </summary>
public static class DuplicateReport
{
    public sealed record Item(
        Guid Id,
        string Name,
        string Kind,
        IReadOnlyDictionary<string, string> ProviderIds,
        int RegularEpisodes
    );

    public enum Confidence
    {
        /// <summary>Same native catalogue id, or all its episodes belong to another series.</summary>
        Likely,

        /// <summary>Shares some episodes with another series (related entries, e.g. a sequel).</summary>
        Related,

        /// <summary>Same TVDB or TMDB id only: never acted on, stages and remakes share these.</summary>
        Maybe,
    }

    public sealed record Finding(Confidence Confidence, string Reason, IReadOnlyList<Item> Items);

    public static List<Finding> Build(IReadOnlyList<Item> items, IReadOnlyList<EpisodeConflicts.Entry> conflicts)
    {
        var findings = new List<Finding>();
        var byId = items.ToDictionary(i => i.Id);

        // Same native id.
        foreach (var provider in CatalogIdentity.NativeProviders)
        {
            foreach (var g in GroupBy(items, provider))
                findings.Add(new(Confidence.Likely, $"same {provider} id {g.Key}", g.Value));
        }

        // Series whose episodes belong to another series (the prevention rule: a series whose
        // episodes all belong to another series is the same show).
        foreach (var c in conflicts.OrderBy(c => c.SeriesName, StringComparer.OrdinalIgnoreCase))
        {
            if (!byId.TryGetValue(c.SeriesId, out var copy) || !byId.TryGetValue(c.OwnerId, out var owner))
                continue;
            // The counts are what one sync saw; the library decides. An owner that holds no
            // episodes is a stale entry (the episodes went the other way since).
            if (owner.RegularEpisodes == 0)
                continue;
            var all = copy.RegularEpisodes == 0;
            findings.Add(
                all
                    ? new(Confidence.Likely, $"lists episodes that all belong to {owner.Name} and owns none: the same show", [copy, owner])
                    : new(Confidence.Related, $"{c.Shared} of {c.Listed} listed episodes belong to {owner.Name}", [copy, owner])
            );
        }

        // An IMDb id change on a title with no native id (InuYasha, One Piece): two items of a
        // kind with the same TVDB and the same TMDB id, different IMDb ids, neither with a native
        // id. Stages and remakes share one of these ids, rarely both.
        foreach (var g in items
            .Where(i => CatalogIdentity.NativeId(i.ProviderIds) is null)
            .Select(i => (Item: i, Tvdb: Get(i, "Tvdb"), Tmdb: Get(i, "Tmdb")))
            .Where(x => x.Tvdb is not null && x.Tmdb is not null)
            .GroupBy(x => (x.Item.Kind, x.Tvdb, x.Tmdb))
            .Where(g => g.Count() > 1))
        {
            var group = g.Select(x => x.Item).ToList();
            var imdbs = group.Select(i => Get(i, "Imdb")).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (imdbs < 2 || findings.Any(f => f.Confidence == Confidence.Likely && group.All(f.Items.Contains)))
                continue;
            findings.Add(new(Confidence.Likely, $"same TVDB {g.Key.Tvdb} and TMDB {g.Key.Tmdb} ids, different IMDb ids, no native id: likely an IMDb id change", group));
        }

        // Same TVDB / TMDB id, per kind.
        foreach (var provider in new[] { "Tvdb", "Tmdb" })
        {
            foreach (var g in GroupBy(items, provider))
            {
                if (findings.Any(f => f.Confidence != Confidence.Maybe && g.Value.All(f.Items.Contains)))
                    continue;
                findings.Add(new(Confidence.Maybe, $"same {provider} id {g.Key}", g.Value));
            }
        }

        return findings
            .OrderBy(f => f.Confidence)
            .ThenBy(f => f.Items[0].Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<KeyValuePair<string, List<Item>>> GroupBy(IReadOnlyList<Item> items, string provider) =>
        items
            .Select(i => (Item: i, Value: Get(i.ProviderIds, provider)))
            .Where(x => !string.IsNullOrWhiteSpace(x.Value))
            .GroupBy(x => $"{x.Item.Kind}|{x.Value}", StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => new KeyValuePair<string, List<Item>>(g.First().Value!, g.Select(x => x.Item).ToList()));

    private static string? Get(IReadOnlyDictionary<string, string> ids, string key) =>
        ids.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>The report as text, one finding per paragraph.</summary>
    public static string ToText(IReadOnlyList<Finding> findings, DateTime generatedUtc)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Gelato possible duplicates, {generatedUtc:yyyy-MM-dd HH:mm} UTC: {findings.Count(f => f.Confidence == Confidence.Likely)} likely, {findings.Count(f => f.Confidence == Confidence.Related)} related, {findings.Count(f => f.Confidence == Confidence.Maybe)} maybe.");
        sb.AppendLine("Read-only: nothing is merged or deleted. Add a title to the exclude list (ExcludedIds) to stop it being created again.");
        foreach (var f in findings)
        {
            sb.AppendLine();
            sb.AppendLine($"[{f.Confidence}] {f.Reason}");
            foreach (var i in f.Items)
            {
                var ids = string.Join(", ", i.ProviderIds.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).Select(kv => $"{kv.Key}={kv.Value}"));
                sb.AppendLine($"  - {i.Name} ({i.Kind}, {i.Id:N}, {i.RegularEpisodes} episodes) {ids}");
            }
        }
        return sb.ToString();
    }

    private static string? Get(Item item, string provider) =>
        item.ProviderIds.FirstOrDefault(kv => string.Equals(kv.Key, provider, StringComparison.OrdinalIgnoreCase)).Value is { Length: > 0 } v
            ? v
            : null;
}
