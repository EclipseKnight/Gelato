using System.Text.Json;
using Gelato.Services;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Gelato.ScheduledTasks;

/// <summary>
/// Writes the possible-duplicates report (read-only) to the data folder:
/// <c>gelato/duplicates-report.txt</c> and <c>.json</c>, also served at GET /gelato/duplicates.
/// </summary>
public sealed class DuplicatesReportTask(
    ILogger<DuplicatesReportTask> log,
    ILibraryManager libraryManager,
    IApplicationPaths appPaths
) : IScheduledTask
{
    public string Name => "Possible duplicates report";
    public string Key => "GelatoDuplicatesReport";
    public string Description =>
        "Lists Gelato items that may be the same title: same native id, a series whose episodes belong to another series, or the same TVDB/TMDB id. Read-only; nothing is merged or deleted.";
    public string Category => "Gelato";

    public static string Folder(IApplicationPaths paths) => Path.Combine(paths.DataPath, "gelato");

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
        [new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromDays(7).Ticks }];

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var folder = Folder(appPaths);
        EpisodeConflicts.Init(folder);
        var gelato = new Dictionary<string, string> { ["Stremio"] = string.Empty };

        var items = libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Series, BaseItemKind.Movie],
                    HasAnyProviderId = gelato,
                    Recursive = true,
                }
            )
            .Where(i => !i.IsStream())
            .ToList();
        progress.Report(30);

        var episodeCounts = libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Episode],
                    HasAnyProviderId = gelato,
                    Recursive = true,
                }
            )
            .OfType<Episode>()
            .Where(e => e.ParentIndexNumber is > 0 && !e.IsStream())
            .GroupBy(e => e.SeriesId)
            .ToDictionary(g => g.Key, g => g.Count());
        progress.Report(60);

        var records = items
            .Select(i => new DuplicateReport.Item(
                i.Id,
                i.Name ?? "",
                i.GetBaseItemKind().ToString(),
                new Dictionary<string, string>(i.ProviderIds, StringComparer.OrdinalIgnoreCase),
                episodeCounts.GetValueOrDefault(i.Id)
            ))
            .ToList();

        var findings = DuplicateReport.Build(records, EpisodeConflicts.Snapshot());
        var now = DateTime.UtcNow;
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "duplicates-report.txt"), DuplicateReport.ToText(findings, now));
        File.WriteAllText(
            Path.Combine(folder, "duplicates-report.json"),
            JsonSerializer.Serialize(new { generatedUtc = now, findings }, new JsonSerializerOptions { WriteIndented = true })
        );

        log.LogInformation(
            "Possible duplicates: {Likely} likely, {Related} related, {Maybe} maybe (report in {Folder})",
            findings.Count(f => f.Confidence == DuplicateReport.Confidence.Likely),
            findings.Count(f => f.Confidence == DuplicateReport.Confidence.Related),
            findings.Count(f => f.Confidence == DuplicateReport.Confidence.Maybe),
            folder
        );
        progress.Report(100);
        return Task.CompletedTask;
    }
}
