using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ReleaseDateAdded;

/// <summary>
/// Listens to ItemAdded/ItemUpdated. When Jellyfin has fetched metadata (and with it the air date)
/// for a new episode/movie, the item is queued and gets its "Date added" set to the air/release date.
/// </summary>
public sealed class LibraryWatcher : IHostedService, IDisposable
{
    /// <summary>Delay so the library scan/metadata refresh can finish before the item is touched.</summary>
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(5);

    private readonly ILibraryManager _libraryManager;
    private readonly ReleaseDateService _service;
    private readonly ILogger<LibraryWatcher> _logger;
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    private CancellationTokenSource? _cts;
    private Task? _worker;

    public LibraryWatcher(ILibraryManager libraryManager, ReleaseDateService service, ILogger<LibraryWatcher> logger)
    {
        _libraryManager = libraryManager;
        _service = service;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _worker = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);

        _libraryManager.ItemAdded += OnItemChanged;
        _libraryManager.ItemUpdated += OnItemChanged;

        _logger.LogInformation("{Prefix} watching the library.", Plugin.LogPrefix);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemChanged;
        _libraryManager.ItemUpdated -= OnItemChanged;

        _queue.Writer.TryComplete();
        _cts?.Cancel();

        if (_worker is not null)
        {
            try
            {
                await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
        }
    }

    public void Dispose()
    {
        _cts?.Dispose();
    }

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        try
        {
            var item = e.Item;
            if (item is null)
            {
                return;
            }

            var config = ReleaseDateService.Config;
            if (!config.Enabled || !config.EnableRealtime || !ReleaseDateService.IsSupported(item, config) || item.IsVirtualItem)
            {
                return;
            }

            if (_service.IsOwnWrite(item.Id))
            {
                // Our own save – ignore it to avoid a loop.
                return;
            }

            _queue.Writer.TryWrite(item.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Prefix} error in the event handler.", Plugin.LogPrefix);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                await Task.Delay(DebounceDelay, cancellationToken).ConfigureAwait(false);

                var batch = new HashSet<Guid>();
                while (_queue.Reader.TryRead(out var id))
                {
                    batch.Add(id);
                }

                if (batch.Count == 0)
                {
                    continue;
                }

                try
                {
                    await _service.ProcessItemsAsync(batch, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "{Prefix} could not update {Count} items.", Plugin.LogPrefix, batch.Count);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }
}
