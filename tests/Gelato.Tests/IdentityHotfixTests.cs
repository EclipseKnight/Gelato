using Gelato.Decorators;
using Gelato.Services;
using Xunit;

namespace Gelato.Tests;

public class IdentityHotfixTests
{
    [Fact]
    public void Imdb_match_is_the_same_title_without_a_native_id_or_with_the_same_one()
    {
        var native = new KeyValuePair<string, string>("Kitsu", "12");
        Assert.True(CatalogIdentity.ImdbMatchIsSame(new Dictionary<string, string> { ["Imdb"] = "tt1" }, native));
        Assert.True(CatalogIdentity.ImdbMatchIsSame(new Dictionary<string, string> { ["Imdb"] = "tt1", ["kitsu"] = "12" }, native));
        Assert.False(CatalogIdentity.ImdbMatchIsSame(new Dictionary<string, string> { ["Imdb"] = "tt1", ["Kitsu"] = "13" }, native));
        Assert.False(CatalogIdentity.ImdbMatchIsSame(new Dictionary<string, string> { ["Imdb"] = "tt1", ["Mal"] = "12" }, native));
    }

    [Fact]
    public async Task A_skipped_lookup_is_not_remembered()
    {
        var marked = false;
        await StreamSyncPolicy.SyncAndMarkAsync(_ => Task.FromResult(-1), false, _ => marked = true, CancellationToken.None);
        Assert.False(marked);
        await StreamSyncPolicy.SyncAndMarkAsync(_ => Task.FromResult(0), false, _ => marked = true, CancellationToken.None);
        Assert.True(marked);
    }
}
