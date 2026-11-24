using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.ReleaseDateAdded;

/// <summary>
/// Schemalagd uppgift som går igenom hela biblioteket. Kör den manuellt en gång efter installation
/// för att rätta befintliga objekt. Körs sedan varje natt som säkerhetsnät.
/// </summary>
public class ReleaseDateTask : IScheduledTask
{
    /// <summary>Nyckel som används av konfigurationssidans "Kör nu"-knapp.</summary>
    public const string TaskKey = "ReleaseDateAddedFullScan";

    private readonly ReleaseDateService _service;

    public ReleaseDateTask(ReleaseDateService service)
    {
        _service = service;
    }

    public string Name => "Air Date Fixer: sätt 'Date added' från sändningsdatum";

    public string Key => TaskKey;

    public string Description => "Fyller i saknade sändningsdatum (IMDb/TVmaze), uppskattar resten och sätter 'Date added' = sändnings-/releasedatum så att Recently Added sorteras efter release.";

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
