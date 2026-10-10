using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Model.Entities;

namespace Gelato.Services;

/// <summary>
/// Reads the status words add-ons send and turns them into Jellyfin's series status. Add-ons do
/// not agree on the words: Cinemeta says "Continuing" or "Ended", TMDB-based ones "Returning
/// Series" or "Canceled", anime ones "current", "finished" or "upcoming".
/// </summary>
public static class SeriesStatusMap
{
    private static readonly Dictionary<string, StremioStatus> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["continuing"] = StremioStatus.Continuing,
        ["returning series"] = StremioStatus.Continuing,
        ["returning"] = StremioStatus.Continuing,
        ["current"] = StremioStatus.Continuing,
        ["currently airing"] = StremioStatus.Continuing,
        ["airing"] = StremioStatus.Continuing,
        ["ongoing"] = StremioStatus.Continuing,
        ["in production"] = StremioStatus.Continuing,
        ["releasing"] = StremioStatus.Continuing,
        ["ended"] = StremioStatus.Ended,
        ["finished"] = StremioStatus.Ended,
        ["finished airing"] = StremioStatus.Ended,
        ["completed"] = StremioStatus.Ended,
        ["complete"] = StremioStatus.Ended,
        ["canceled"] = StremioStatus.Ended,
        ["cancelled"] = StremioStatus.Ended,
        ["upcoming"] = StremioStatus.Upcoming,
        ["planned"] = StremioStatus.Upcoming,
        ["not yet aired"] = StremioStatus.Upcoming,
        ["not_yet_released"] = StremioStatus.Upcoming,
        ["tba"] = StremioStatus.Upcoming,
        ["unreleased"] = StremioStatus.Upcoming,
        ["pilot"] = StremioStatus.Upcoming,
    };

    public static StremioStatus Parse(string? word) =>
        word is not null && Words.TryGetValue(word.Trim(), out var s) ? s : StremioStatus.Unknown;

    public static SeriesStatus? ToJellyfin(StremioStatus? status) =>
        status switch
        {
            StremioStatus.Continuing => SeriesStatus.Continuing,
            StremioStatus.Ended => SeriesStatus.Ended,
            StremioStatus.Upcoming => SeriesStatus.Unreleased,
            _ => null,
        };

    /// <summary>
    /// The status to store, or null to leave the stored one alone: nothing known from the add-on,
    /// or nothing changed.
    /// </summary>
    public static SeriesStatus? Changed(SeriesStatus? stored, StremioStatus? fromAddon) =>
        ToJellyfin(fromAddon) is { } next && next != stored ? next : null;

    /// <summary>Whether the weekly pass over series that are not continuing is due.</summary>
    public static bool FullPassDue(DateTime? last, DateTime now, int days) =>
        days > 0 && (last is null || now - last.Value >= TimeSpan.FromDays(days));
}

public sealed class StremioStatusConverter : JsonConverter<StremioStatus>
{
    public override StremioStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return SeriesStatusMap.Parse(reader.GetString());
        reader.TrySkip();
        return StremioStatus.Unknown;
    }

    public override void Write(Utf8JsonWriter writer, StremioStatus value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
