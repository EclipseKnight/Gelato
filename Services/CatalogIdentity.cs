using System.Globalization;

namespace Gelato.Services;

/// <summary>
/// How Gelato tells catalogue items apart.
/// </summary>
/// <remarks>
/// <para>
/// An anime catalogue hands out its own ids (<c>kitsu:12</c>) and a translated IMDb id next to
/// them. The translation changes now and then, so keying on the IMDb id alone created a new
/// series each time and the copies fought over the same episodes. An item is identified by the
/// catalogue's native id when it has one, and by its IMDb (or other) ids otherwise. TVDB and TMDB
/// ids are never used to match: different stages and films share them.
/// </para>
/// <para>
/// When a known native id comes with a new IMDb id, the item takes the new one and keeps the old
/// one as an alias. Aliases are memory only ("this item was once called X"): never used to find,
/// create, merge or delete anything. An alias that becomes another item's main id is removed
/// from the old item. An item keeps at most <see cref="MaxAliases"/>, and one not seen for
/// <see cref="AliasTtl"/> ages out.
/// </para>
/// </remarks>
public static class CatalogIdentity
{
    /// <summary>Native catalogue id providers, as <see cref="GelatoManager.IntoBaseItem"/> names them.</summary>
    public static readonly string[] NativeProviders = ["Kitsu", "Mal", "Anilist", "AniDB"];

    public const string AliasKey = "GelatoImdbAlias";
    public const string AliasSeenKey = "GelatoImdbAliasSeen";
    public const int MaxAliases = 5;
    public static readonly TimeSpan AliasTtl = TimeSpan.FromDays(90);

    /// <summary>The native catalogue id among these provider ids, if any.</summary>
    public static KeyValuePair<string, string>? NativeId(IReadOnlyDictionary<string, string> ids)
    {
        foreach (var provider in NativeProviders)
        {
            foreach (var (key, value) in ids)
            {
                if (
                    string.Equals(key, provider, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(value)
                )
                    return new(provider, value);
            }
        }
        return null;
    }

    /// <summary>
    /// The provider ids to look an item up by: only its native id when it has one, else all of
    /// them except the alias entries.
    /// </summary>
    public static Dictionary<string, string> LookupIds(IReadOnlyDictionary<string, string> ids)
    {
        if (NativeId(ids) is { } native)
            return new(StringComparer.OrdinalIgnoreCase) { [native.Key] = native.Value };
        return ids.Where(kv => !IsAliasKey(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether an item found by the IMDb id alone (after the native id found nothing) is the same
    /// title: it has no native id yet (added from a catalogue without one, e.g. Cinemeta), or
    /// the same one. An item with another native id is another title that shares the IMDb id.
    /// </summary>
    public static bool ImdbMatchIsSame(IReadOnlyDictionary<string, string> candidate, KeyValuePair<string, string> native) =>
        NativeId(candidate) is not { } other
        || (string.Equals(other.Key, native.Key, StringComparison.OrdinalIgnoreCase)
            && string.Equals(other.Value, native.Value, StringComparison.OrdinalIgnoreCase));

    public static bool IsAliasKey(string key) =>
        key.StartsWith(AliasKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a found item should take the incoming IMDb id.</summary>
    public static bool NeedsRekey(string? existingImdb, string? incomingImdb) =>
        !string.IsNullOrWhiteSpace(existingImdb)
        && !string.IsNullOrWhiteSpace(incomingImdb)
        && !string.Equals(existingImdb, incomingImdb, StringComparison.OrdinalIgnoreCase);

    public readonly record struct Alias(string Id, DateTime Seen);

    /// <summary>The aliases stored in these provider ids, newest first.</summary>
    public static List<Alias> ReadAliases(IReadOnlyDictionary<string, string> ids)
    {
        var list = new List<Alias>();
        for (var i = 0; i < MaxAliases; i++)
        {
            if (!ids.TryGetValue(AliasKey + i, out var id) || string.IsNullOrWhiteSpace(id))
                continue;
            var seen =
                ids.TryGetValue(AliasSeenKey + i, out var s)
                && DateTime.TryParse(
                    s,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out var d
                )
                    ? d
                    : DateTime.MinValue;
            list.Add(new Alias(id, seen));
        }
        return list.OrderByDescending(a => a.Seen).ToList();
    }

    /// <summary>Replaces the alias entries in these provider ids.</summary>
    public static void WriteAliases(Dictionary<string, string> ids, IEnumerable<Alias> aliases)
    {
        foreach (var key in ids.Keys.Where(IsAliasKey).ToList())
            ids.Remove(key);
        var i = 0;
        foreach (var a in aliases.Take(MaxAliases))
        {
            ids[AliasKey + i] = a.Id;
            ids[AliasSeenKey + i] = a.Seen.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            i++;
        }
    }

    /// <summary>Adds (or refreshes) an alias, drops aged ones and keeps the newest few.</summary>
    public static List<Alias> AddAlias(IEnumerable<Alias> aliases, string id, DateTime now) =>
        Prune(aliases.Where(a => !Same(a.Id, id)).Prepend(new Alias(id, now)), now);

    public static List<Alias> RemoveAlias(IEnumerable<Alias> aliases, string id) =>
        aliases.Where(a => !Same(a.Id, id)).ToList();

    public static List<Alias> Prune(IEnumerable<Alias> aliases, DateTime now) =>
        aliases
            .Where(a => now - a.Seen <= AliasTtl)
            .OrderByDescending(a => a.Seen)
            .Take(MaxAliases)
            .ToList();

    /// <summary>The provider ids that find items holding this id as an alias.</summary>
    public static Dictionary<string, string> AliasLookup(string id)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < MaxAliases; i++)
            d[AliasKey + i] = id;
        return d;
    }

    private static bool Same(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
