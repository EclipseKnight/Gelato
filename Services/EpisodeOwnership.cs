namespace Gelato.Services;

/// <summary>
/// Who an episode row belongs to when a tree sync wants to create an episode at a path that
/// already has one. Episode ids are a hash of the path, and the path comes from the addon's
/// episode id, so two catalog entries listing the same episode ids share rows.
/// </summary>
public static class EpisodeOwnership
{
    public enum Decision
    {
        /// <summary>No row at that path, or its series is gone: create the episode here.</summary>
        Create,

        /// <summary>The row already belongs to this series: update it.</summary>
        UpdateOwn,

        /// <summary>The row belongs to another series that still exists: leave it there.</summary>
        OwnedByAnother,
    }

    /// <param name="existingSeriesId">The series of the row at the path, or null when there is none.</param>
    /// <param name="seriesId">The series being synced.</param>
    /// <param name="existingSeriesExists">Whether the row's series is still in the library.</param>
    public static Decision Decide(Guid? existingSeriesId, Guid seriesId, bool existingSeriesExists)
    {
        if (existingSeriesId is not { } owner)
            return Decision.Create;
        if (owner == seriesId)
            return Decision.UpdateOwn;
        return existingSeriesExists ? Decision.OwnedByAnother : Decision.Create;
    }
}
