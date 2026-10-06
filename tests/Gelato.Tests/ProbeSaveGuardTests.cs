using Gelato.Decorators;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using Xunit;

namespace Gelato.Tests;

/// <summary>
/// A probed stream row is saved only if it is still the row a sync left: a sync may have deleted
/// it (saving would bring it back as an orphan) or rewritten it (saving would undo the sync).
/// </summary>
public class ProbeSaveGuardTests
{
    private static Movie Row(
        DateTime refreshed,
        string path = "http://addon/a",
        string users = "[\"u1\"]"
    ) =>
        new()
        {
            Id = Guid.Parse("6f1c1e2e-0d43-4a8e-9f5e-0c2b8f4f1a10"),
            Path = path,
            DateLastRefreshed = refreshed,
            ExternalId = $"{{\"userIds\":{users},\"index\":1}}",
        };

    private static readonly DateTime Synced = new(2026, 10, 6, 1, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_deleted_row_is_not_saved()
    {
        var probed = Row(Synced);
        var before = ProbeSaveGuard.Stamp(probed);

        Assert.False(ProbeSaveGuard.CanSave(probed, before, current: null));
    }

    [Fact]
    public void The_same_row_is_saved()
    {
        var probed = Row(Synced);
        var before = ProbeSaveGuard.Stamp(probed);

        // The probe may change the instance's own fields; that is what is being saved.
        probed.RunTimeTicks = TimeSpan.FromHours(2).Ticks;
        Assert.True(ProbeSaveGuard.CanSave(probed, before, current: probed));
    }

    [Fact]
    public void A_row_reloaded_unchanged_is_saved()
    {
        var probed = Row(Synced);
        var before = ProbeSaveGuard.Stamp(probed);

        Assert.True(ProbeSaveGuard.CanSave(probed, before, current: Row(Synced)));
    }

    [Fact]
    public void A_row_a_sync_saved_again_is_not_saved()
    {
        var probed = Row(Synced);
        var before = ProbeSaveGuard.Stamp(probed);

        Assert.False(ProbeSaveGuard.CanSave(probed, before, current: Row(Synced.AddMinutes(5))));
    }

    [Fact]
    public void A_row_whose_users_or_url_changed_is_not_saved()
    {
        var probed = Row(Synced);
        var before = ProbeSaveGuard.Stamp(probed);

        Assert.False(ProbeSaveGuard.CanSave(probed, before, current: Row(Synced, users: "[]")));
        Assert.False(
            ProbeSaveGuard.CanSave(probed, before, current: Row(Synced, path: "http://addon/b"))
        );
    }

    private static Movie MovieWith(params Guid[] versions) =>
        new()
        {
            Id = Guid.Parse("0b6a3f0e-9a55-4d0e-8e71-2a1c9b7f3d22"),
            Path = "gelato://movie/tt0898266",
            DateLastRefreshed = Synced,
            LinkedAlternateVersions = versions
                .Select(id => new LinkedChild
                {
                    ItemId = id,
                    Type = LinkedChildType.LinkedAlternateVersion,
                })
                .ToArray(),
        };

    private static readonly Guid VersionA = Guid.Parse("a1a1a1a1-0000-0000-0000-000000000001");
    private static readonly Guid VersionB = Guid.Parse("b2b2b2b2-0000-0000-0000-000000000002");

    [Fact]
    public void A_movie_reloaded_with_other_versions_is_not_saved()
    {
        // The movie owns its versions' links. Saving the probed copy would put the old ones back.
        var probed = MovieWith(VersionA, VersionB);
        var before = ProbeSaveGuard.Stamp(probed);

        Assert.False(ProbeSaveGuard.CanSave(probed, before, current: MovieWith(VersionB)));
    }

    [Fact]
    public void A_movie_reloaded_with_the_same_versions_is_saved()
    {
        var probed = MovieWith(VersionA, VersionB);
        var before = ProbeSaveGuard.Stamp(probed);

        Assert.True(ProbeSaveGuard.CanSave(probed, before, current: MovieWith(VersionA, VersionB)));
    }
}
