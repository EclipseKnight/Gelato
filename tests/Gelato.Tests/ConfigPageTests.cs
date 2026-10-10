using Xunit;

namespace Gelato.Tests;

public class ConfigPageTests
{
    private static string Page()
    {
        var asm = typeof(Gelato.GelatoPlugin).Assembly;
        var name = asm.GetManifestResourceNames().Single(n => n.EndsWith("config.html", StringComparison.OrdinalIgnoreCase));
        using var s = asm.GetManifestResourceStream(name)!;
        return new StreamReader(s).ReadToEnd();
    }

    [Fact]
    public void Config_page_has_the_duplicates_section()
    {
        var page = Page();
        Assert.Contains("id=\"gelatoDuplicates\"", page);
        Assert.Contains("gelato/duplicates", page);
        Assert.Contains("GelatoDuplicatesReport", page);
        Assert.Contains("ExcludedIds", page);
    }
}
