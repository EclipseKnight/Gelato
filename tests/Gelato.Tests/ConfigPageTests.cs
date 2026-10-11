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

    [Fact]
    public void Exclude_list_removes_by_value()
    {
        var page = Page();
        Assert.Contains("data-unexclude=\"${esc(e)}\"", page);
        Assert.Contains("list.filter((e) => e !== b.dataset.unexclude)", page);
        Assert.DoesNotContain("Number(b.dataset.unexclude)", page);
    }

    [Fact]
    public void Report_run_checks_the_task_result()
    {
        var page = Page();
        Assert.Contains("LastExecutionResult?.Status", page);
        Assert.Contains("The report is still running.", page);
    }

    [Fact]
    public void Report_run_does_not_misreport_its_outcome()
    {
        var page = Page();
        // A second click while the task runs must not queue another run.
        Assert.Contains("if (task.State !== 'Idle')", page);
        // A missing result is not a failure.
        Assert.DoesNotContain("|| 'Unknown'", page);
        // The failure text only mentions an earlier report when one was loaded.
        Assert.Contains("const loaded = await loadReport();", page);
        Assert.Contains("there is no earlier report.", page);
    }

    [Fact]
    public void Report_run_ignores_the_previous_result()
    {
        var page = Page();
        // Only a result that ended after the run was started counts, not the one from before.
        Assert.Contains("const lastEnd = task.LastExecutionResult?.EndTimeUtc ?? null;", page);
        Assert.Contains("(t.LastExecutionResult?.EndTimeUtc ?? null) !== lastEnd", page);
        Assert.True(page.IndexOf("const lastEnd", StringComparison.Ordinal)
            < page.IndexOf("ScheduledTasks/Running/", StringComparison.Ordinal));
    }
}
