using System.Collections.Concurrent;
using Gelato.Services;
using Xunit;

namespace Gelato.Tests;

public class ClaimGateTests
{
    [Fact]
    public async Task Two_entries_finding_one_imdb_only_item_at_once_end_with_one_claim()
    {
        // The saved item has no native id yet. Both entries found it before either saved; the
        // save is slow, so without the gate both would read "no native id" and both take it.
        var saved = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Imdb"] = "tt0000601",
        };
        var gate = new ClaimGate();
        using var start = new ManualResetEventSlim();

        Task<CatalogIdentity.ClaimResult> Claim(string kitsu) =>
            Task.Run(async () =>
            {
                start.Wait();
                return await gate.ClaimAsync(
                    () => new Dictionary<string, string>(saved),
                    new("Kitsu", kitsu),
                    async () =>
                    {
                        await Task.Delay(50);
                        saved["Kitsu"] = kitsu;
                    },
                    CancellationToken.None
                );
            });

        var a = Claim("600");
        var b = Claim("603");
        start.Set();
        var results = await Task.WhenAll(a, b);

        Assert.Single(results, r => r == CatalogIdentity.ClaimResult.Take);
        Assert.Single(results, r => r == CatalogIdentity.ClaimResult.Taken);
        var winner = results[0] == CatalogIdentity.ClaimResult.Take ? "600" : "603";
        Assert.Equal(winner, saved["Kitsu"]);
    }

    [Fact]
    public async Task The_same_entry_claiming_again_finds_it_its_own()
    {
        var saved = new Dictionary<string, string> { ["Imdb"] = "tt1", ["Kitsu"] = "600" };
        var taken = false;
        var result = await new ClaimGate().ClaimAsync(
            () => saved,
            new("Kitsu", "600"),
            () =>
            {
                taken = true;
                return Task.CompletedTask;
            },
            CancellationToken.None
        );
        Assert.Equal(CatalogIdentity.ClaimResult.Ours, result);
        Assert.False(taken);
    }
}
