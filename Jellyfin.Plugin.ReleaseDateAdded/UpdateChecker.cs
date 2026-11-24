using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Updates;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ReleaseDateAdded;

/// <summary>
/// Checks the plugin repository for a newer version of this plugin a few minutes after startup and then
/// every 6 hours, and installs it. Jellyfin then shows "restart required" on the dashboard and an
/// "updated" entry in the activity log, so a new version is easy to see.
/// </summary>
public sealed class UpdateChecker : IHostedService, IDisposable
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly IInstallationManager _installationManager;
    private readonly ILogger<UpdateChecker> _logger;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public UpdateChecker(IInstallationManager installationManager, ILogger<UpdateChecker> logger)
    {
        _installationManager = installationManager;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
        }
    }

    public void Dispose() => _cts?.Dispose();

    /// <summary>Installs a newer version of this plugin if one is available.</summary>
    /// <returns>True if an update was installed.</returns>
    public async Task<bool> CheckAndInstallAsync(CancellationToken cancellationToken)
    {
        if (!ReleaseDateService.Config.AutoInstallUpdates)
        {
            return false;
        }

        try
        {
            var myId = Guid.Parse(Plugin.PluginGuid);
            var updates = await _installationManager.GetAvailablePluginUpdates(cancellationToken).ConfigureAwait(false);
            var update = updates.FirstOrDefault(u => u.Id == myId);
            if (update is null)
            {
                return false;
            }

            _logger.LogInformation("{Prefix} installing update {Version}.", Plugin.LogPrefix, update.Version);
            await _installationManager.InstallPackage(update, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("{Prefix} version {Version} is installed – restart Jellyfin to activate it.", Plugin.LogPrefix, update.Version);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Prefix} could not check for or install updates.", Plugin.LogPrefix);
            return false;
        }
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(FirstCheck, cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                await CheckAndInstallAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(Interval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }
}
