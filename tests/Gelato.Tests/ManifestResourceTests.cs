using System.Text.Json;
using Gelato.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gelato.Tests;

public sealed class ManifestResourceTests : IDisposable
{
    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };
    private readonly FakeAddon _addon = new(FakeAddon.Fixture("plainresources"));

    public void Dispose() => _addon.Dispose();

    private GelatoStremioProvider NewProvider() =>
        new(_addon.BaseUrl, new PlainHttpClientFactory(), NullLogger<GelatoStremioProvider>.Instance);

    [Fact]
    public void Plain_name_reads_as_a_resource_for_any_type_and_id()
    {
        var m = JsonSerializer.Deserialize<StremioManifest>(
            """{"id":"x","name":"x","version":"1","resources":["catalog","stream"]}""",
            Opts
        )!;

        Assert.Equal(["catalog", "stream"], m.Resources.Select(r => r.Name));
        Assert.All(m.Resources, r => Assert.Empty(r.Types));
        Assert.All(m.Resources, r => Assert.Empty(r.IdPrefixes));
    }

    [Fact]
    public void Object_form_still_reads_in_any_case()
    {
        var m = JsonSerializer.Deserialize<StremioManifest>(
            """{"id":"x","name":"x","version":"1","resources":[{"Name":"meta","types":["series"],"IDPREFIXES":["tt","kitsu"]}]}""",
            Opts
        )!;

        var r = Assert.Single(m.Resources);
        Assert.Equal("meta", r.Name);
        Assert.Equal(["series"], r.Types);
        Assert.Equal(["tt", "kitsu"], r.IdPrefixes);
    }

    [Fact]
    public void Odd_entries_are_skipped_not_fatal()
    {
        var m = JsonSerializer.Deserialize<StremioManifest>(
            """{"id":"x","name":"x","version":"1","resources":["meta",3,null,{"name":"stream","types":"movie"}]}""",
            Opts
        )!;

        Assert.Contains(m.Resources, r => r?.Name == "meta");
        Assert.Contains(m.Resources, r => r?.Name == "stream");
    }

    [Fact]
    public async Task Addon_with_plain_resources_is_ready()
    {
        var p = NewProvider();
        Assert.True(await p.IsReady());
        Assert.Equal(3, (await p.GetManifestAsync())!.Resources.Count);
    }

    [Fact]
    public async Task Addon_with_plain_resources_is_recognised_as_AioStreams()
    {
        await NewProvider().GetStreamsAsync(new StremioUri(StremioMediaType.Movie, "tt0000001"));
        var stream = _addon.Requests.Single(r => r.Path.StartsWith("/stream/", StringComparison.Ordinal));
        Assert.Contains("AIOStreams/1.0", stream.UserAgent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Meta_prefixes_come_from_the_object_entry()
    {
        var meta = await NewProvider().GetMetaAsync(
            new Dictionary<string, string> { ["Kitsu"] = "12" },
            StremioMediaType.Series
        );
        Assert.Equal("kitsu:12", meta?.Id);
    }
}
