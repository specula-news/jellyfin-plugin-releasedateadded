using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.ReleaseDateAdded;

/// <summary>
/// Manuell uppgift som lägger tillbaka Jellyfins ursprungliga "Date added" och pausar pluginet.
/// </summary>
public class RestoreTask : IScheduledTask
{
    private readonly ReleaseDateService _service;

    public RestoreTask(ReleaseDateService service)
    {
        _service = service;
    }

    public string Name => "Air Date Fixer: återställ ursprungliga 'Date added'";

    public string Key => "ReleaseDateAddedRestore";

    public string Description => "Lägger tillbaka de datum Jellyfin hade innan pluginet ändrade dem och pausar pluginet. Kör innan du avinstallerar om du vill ha tillbaka gamla sorteringen.";

    public string Category => "Library";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _service.RestoreAsync(progress, cancellationToken);

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
}
