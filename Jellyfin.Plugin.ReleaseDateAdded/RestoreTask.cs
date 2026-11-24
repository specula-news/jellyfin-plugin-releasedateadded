using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.ReleaseDateAdded;

/// <summary>
/// Manual task that puts back Jellyfin's original "Date added" and pauses the plugin.
/// </summary>
public class RestoreTask : IScheduledTask
{
    private readonly ReleaseDateService _service;

    public RestoreTask(ReleaseDateService service)
    {
        _service = service;
    }

    public string Name => "IMDb Air Date Fixer: restore original 'Date added'";

    public string Key => "ReleaseDateAddedRestore";

    public string Description => "Puts back the dates Jellyfin had before the plugin changed them and pauses the plugin. Run it before uninstalling if you want the old ordering back.";

    public string Category => "Library";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _service.RestoreAsync(progress, cancellationToken);

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
}
