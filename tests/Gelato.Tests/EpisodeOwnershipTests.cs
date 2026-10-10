using Gelato.Services;
using Xunit;
using static Gelato.Services.EpisodeOwnership;

namespace Gelato.Tests;

public class EpisodeOwnershipTests
{
    private static readonly Guid This = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    [Fact]
    public void No_row_at_the_path_is_created() =>
        Assert.Equal(Decision.Create, Decide(null, This, false));

    [Fact]
    public void Own_row_is_updated() =>
        Assert.Equal(Decision.UpdateOwn, Decide(This, This, true));

    [Fact]
    public void Row_of_another_series_is_left_alone() =>
        Assert.Equal(Decision.OwnedByAnother, Decide(Other, This, true));

    [Fact]
    public void Row_whose_series_is_gone_is_taken_over() =>
        Assert.Equal(Decision.Create, Decide(Other, This, false));
}
