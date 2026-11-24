using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.ReleaseDateAdded;

/// <summary>
/// Scheduled task that processes the whole library. Run it once manually after installing
/// to fix existing items. It then runs every night as a safety net.
/// </summary>
public class ReleaseDateTask : IScheduledTask
{
    /// <summary>Key used by the settings page's "Run now" button.</summary>
    public const string TaskKey = "ReleaseDateAddedFullScan";

    private readonly ReleaseDateService _service;

    public ReleaseDateTask(ReleaseDateService service)
    {
        _service = service;
    }

    public string Name => "IMDb Air Date Fixer: set 'Date added' from air date";

    public string Key => TaskKey;

    public string Description => "Fills in missing air dates (IMDb/TVmaze), estimates the rest and sets 'Date added' to the air/release date so Recently Added is ordered by release.";

    public string Category => "Library";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _service.ProcessLibraryAsync(progress, cancellationToken);

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
            }
        };
    }
}
