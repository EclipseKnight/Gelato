namespace Gelato.Services;

/// <summary>
/// Whether an episode has aired yet. An episode that has not has no streams anywhere, so asking
/// AIOStreams (and waiting for its slowest addon) only costs time.
/// </summary>
public static class AirDate
{
    /// <summary>
    /// True when the episode's air date is known and still to come. An episode without a date is
    /// looked up as before: no date is not the same as not aired.
    /// </summary>
    public static bool NotYetAired(DateTime? premiere, DateTime utcNow) =>
        premiere is { } date && ToUtc(date) > utcNow;

    private static DateTime ToUtc(DateTime d) =>
        d.Kind switch
        {
            DateTimeKind.Utc => d,
            DateTimeKind.Local => d.ToUniversalTime(),
            _ => DateTime.SpecifyKind(d, DateTimeKind.Utc),
        };
}
