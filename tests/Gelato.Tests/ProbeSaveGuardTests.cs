using Gelato.Decorators;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using Xunit;

namespace Gelato.Tests;

/// <summary>
/// What is saved after a probe: nothing for an item a sync deleted (saving would bring it back
/// as an orphan), and never the users, URL or order from before a sync that ran meanwhile.
/// </summary>
public class ProbeSaveGuardTests
{
    private static readonly Guid RowId = Guid.Parse("6f1c1e2e-0d43-4a8e-9f5e-0c2b8f4f1a10");
    private static readonly Guid MovieId = Guid.Parse("0b6a3f0e-9a55-4d0e-8e71-2a1c9b7f3d22");
    private static readonly DateTime Synced = new(2026, 10, 6, 1, 0, 0, DateTimeKind.Utc);

    private static Movie Row(
        DateTime refreshed,
        string path = "http://addon/a",
        string users = "[\"u1\"]",
        int index = 1,
        Guid? owner = null
    )
    {
        var row = new Movie
        {
            Id = RowId,
            Path = path,
            DateLastRefreshed = refreshed,
            ExternalId = $"{{\"userIds\":{users},\"index\":{index}}}",
            Tags = [GelatoManager.StreamTag],
        };
        row.SetPrimaryVersionId(owner ?? MovieId);
        return row;
    }

    [Fact]
    public void A_row_deleted_from_the_cache_is_not_saved()
    {
        var probed = Row(Synced);

        Assert.Null(ProbeSaveGuard.ItemToSave(probed, cached: null, stored: null));
    }

    [Fact]
    public void A_row_gone_from_the_database_is_not_saved_even_when_still_cached()
    {
        // An episode deleted with its series stays in the library's cache.
        var probed = Row(Synced);

        Assert.Null(ProbeSaveGuard.ItemToSave(probed, cached: probed, stored: null));
    }

    [Fact]
    public void The_same_row_is_saved_as_probed()
    {
        var probed = Row(Synced);
        probed.RunTimeTicks = TimeSpan.FromHours(2).Ticks;

        var saved = ProbeSaveGuard.ItemToSave(probed, cached: probed, stored: Row(Synced));

        Assert.Same(probed, saved);
        Assert.Equal(TimeSpan.FromHours(2).Ticks, saved!.RunTimeTicks);
    }

    [Fact]
    public void A_row_a_sync_saved_anew_keeps_the_syncs_data_and_the_probes_result()
    {
        var probed = Row(Synced);
        probed.RunTimeTicks = TimeSpan.FromHours(2).Ticks;

        // The sync added a user, moved the row to second place and refreshed its URL.
        var synced = Row(
            Synced.AddMinutes(5),
            path: "http://addon/b",
            users: "[\"u1\",\"u2\"]",
            index: 2
        );
        var saved = ProbeSaveGuard.ItemToSave(probed, cached: synced, stored: synced);

        Assert.Same(probed, saved);
        Assert.Equal("http://addon/b", saved!.Path);
        Assert.Equal(synced.ExternalId, saved.ExternalId);
        Assert.Equal(Synced.AddMinutes(5), saved.DateLastRefreshed);
        Assert.Equal(TimeSpan.FromHours(2).Ticks, saved.RunTimeTicks);
    }

    [Fact]
    public void The_syncs_data_comes_from_the_database_not_from_a_cached_row_being_probed()
    {
        // Another playback's probe points the cached row at a temporary file while it runs.
        var probed = Row(Synced);
        var cached = Row(Synced.AddMinutes(5), path: "/tmp/movie.strm");
        var stored = Row(Synced.AddMinutes(5), path: "http://addon/b");

        var saved = ProbeSaveGuard.ItemToSave(probed, cached, stored);

        Assert.Equal("http://addon/b", saved!.Path);
    }

    [Fact]
    public void A_row_whose_owner_a_split_cleared_is_not_given_it_back()
    {
        var probed = Row(Synced);
        var stored = Row(Synced);
        stored.SetPrimaryVersionId(null);

        var saved = (Video)ProbeSaveGuard.ItemToSave(probed, cached: stored, stored: stored)!;

        Assert.Null(saved.PrimaryVersionId);
    }

    private static readonly Guid VersionA = Guid.Parse("a1a1a1a1-0000-0000-0000-000000000001");
    private static readonly Guid VersionB = Guid.Parse("b2b2b2b2-0000-0000-0000-000000000002");

    private static Movie MovieWith(params Guid[] versions) =>
        new()
        {
            Id = MovieId,
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

    [Fact]
    public void A_movie_reloaded_with_other_versions_keeps_them_and_gets_only_the_run_time()
    {
        // The movie owns its versions' links. Saving the probed copy would put the old ones back.
        var probed = MovieWith(VersionA, VersionB);
        probed.RunTimeTicks = TimeSpan.FromHours(2).Ticks;
        var cached = MovieWith(VersionB);

        var saved = ProbeSaveGuard.ItemToSave(probed, cached, stored: MovieWith(VersionB));

        Assert.Same(cached, saved);
        Assert.Equal([VersionB], ((Video)saved!).LinkedAlternateVersions.Select(l => l.ItemId));
        Assert.Equal(TimeSpan.FromHours(2).Ticks, saved.RunTimeTicks);
    }

    [Fact]
    public void A_movie_reloaded_keeps_its_own_run_time()
    {
        var probed = MovieWith(VersionA);
        probed.RunTimeTicks = TimeSpan.FromHours(2).Ticks;
        var cached = MovieWith(VersionA);
        cached.RunTimeTicks = TimeSpan.FromMinutes(95).Ticks;

        var saved = ProbeSaveGuard.ItemToSave(probed, cached, stored: MovieWith(VersionA));

        Assert.Equal(TimeSpan.FromMinutes(95).Ticks, saved!.RunTimeTicks);
    }
}
