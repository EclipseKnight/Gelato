using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;

namespace Gelato.Services;

/// <summary>
/// Merges watch state parked on Jellyfin's detached-user-data placeholder into an item that
/// already holds a row for the same user and key. Jellyfin's reattach moves every parked row in
/// one update, so a single collision fails the whole item; this settles the collisions first,
/// newer play wins (as Jellyfin's own delete does), and leaves the rest to the normal reattach.
/// </summary>
public sealed class WatchStateMerge(IDbContextFactory<JellyfinDbContext> dbFactory)
{
    /// <summary>Jellyfin's <c>BaseItemRepository.PlaceholderId</c>, not on the plugin API.</summary>
    public static readonly Guid PlaceholderId = new("00000000-0000-0000-0000-000000000001");

    /// <summary>Whether the parked row should replace the one the item holds: it was played later.</summary>
    public static bool ParkedWins(DateTime? parkedLastPlayed, DateTime? heldLastPlayed) =>
        parkedLastPlayed is { } parked && (heldLastPlayed is not { } held || parked > held);

    /// <summary>
    /// Settles the parked rows for <paramref name="keys"/> that collide with rows
    /// <paramref name="itemId"/> already holds. Returns how many were settled.
    /// </summary>
    public async Task<int> SettleCollisionsAsync(Guid itemId, IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        if (keys.Count == 0)
            return 0;
        var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var parked = await db
                .UserData.Where(e => e.ItemId == PlaceholderId && keys.Contains(e.CustomDataKey))
                .ToListAsync(ct)
                .ConfigureAwait(false);
            if (parked.Count == 0)
                return 0;
            var held = await db
                .UserData.Where(e => e.ItemId == itemId && keys.Contains(e.CustomDataKey))
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var settled = 0;
            foreach (var p in parked)
            {
                var h = held.Find(x => x.UserId == p.UserId && x.CustomDataKey == p.CustomDataKey);
                if (h is null)
                    continue;
                if (ParkedWins(p.LastPlayedDate, h.LastPlayedDate))
                    CopyState(p, h);
                db.UserData.Remove(p);
                settled++;
            }
            if (settled > 0)
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return settled;
        }
    }

    private static void CopyState(UserData from, UserData to)
    {
        to.Played = from.Played;
        to.PlayCount = from.PlayCount;
        to.PlaybackPositionTicks = from.PlaybackPositionTicks;
        to.LastPlayedDate = from.LastPlayedDate;
        to.IsFavorite = from.IsFavorite;
        to.Rating = from.Rating;
        to.Likes = from.Likes;
        to.AudioStreamIndex = from.AudioStreamIndex;
        to.SubtitleStreamIndex = from.SubtitleStreamIndex;
    }
}
