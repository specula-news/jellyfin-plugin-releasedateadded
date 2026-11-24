using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ReleaseDateAdded.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ReleaseDateAdded;

/// <summary>
/// Resultat för ett enskilt objekt.
/// </summary>
public enum ApplyResult
{
    /// <summary>Objekttypen är avstängd, virtuell eller låst.</summary>
    Skipped,

    /// <summary>Redan rätt datum, eller inget releasedatum.</summary>
    Unchanged,

    /// <summary>Datumet uppdaterades.</summary>
    Changed,

    /// <summary>Testläge: datumet skulle ha uppdaterats.</summary>
    WouldChange
}

/// <summary>
/// Gemensam logik för realtidsbevakning och schemalagd uppgift.
/// </summary>
public sealed class ReleaseDateService
{
    private const int BatchSize = 500;

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ReleaseDateService> _logger;
    private readonly ConcurrentDictionary<Guid, byte> _ownWrites = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly OriginalDateStore _store;

    public ReleaseDateService(ILibraryManager libraryManager, ILogger<ReleaseDateService> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _store = new OriginalDateStore(logger);
    }

    /// <summary>Aktuell konfiguration.</summary>
    public static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>Sant medan pluginet självt sparar objektet (för att ignorera sitt eget ItemUpdated-event).</summary>
    public bool IsOwnWrite(Guid itemId) => _ownWrites.ContainsKey(itemId);

    /// <summary>Om objekttypen omfattas enligt inställningarna.</summary>
    public static bool IsSupported(BaseItem item, PluginConfiguration config) => item switch
    {
        Episode => config.EnableEpisodes,
        Movie => config.EnableMovies,
        MusicVideo => config.EnableMusicVideos,
        _ => false
    };

    /// <summary>Bearbetar en mängd objekt (används av realtidsbevakningen).</summary>
    public async Task ProcessItemsAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var config = Config;
            if (!config.Enabled)
            {
                return;
            }

            var seriesIds = new HashSet<Guid>();

            foreach (var id in itemIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = _libraryManager.GetItemById(id);
                if (item is null)
                {
                    continue;
                }

                var result = await ApplyAsync(item, config, cancellationToken).ConfigureAwait(false);
                CollectSeries(item, result, config, seriesIds);
            }

            _store.Flush();
            await UpdateSeriesAsync(seriesIds, config, false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Går igenom hela biblioteket (används av den schemalagda uppgiften).</summary>
    public async Task ProcessLibraryAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var config = Config;
            if (!config.Enabled)
            {
                _logger.LogInformation("Release Date Added: pluginet är pausat (se inställningarna), inget att göra.");
                progress.Report(100);
                return;
            }

            var kinds = new List<BaseItemKind>();
            if (config.EnableMovies)
            {
                kinds.Add(BaseItemKind.Movie);
            }

            if (config.EnableEpisodes)
            {
                kinds.Add(BaseItemKind.Episode);
            }

            if (config.EnableMusicVideos)
            {
                kinds.Add(BaseItemKind.MusicVideo);
            }

            if (kinds.Count == 0)
            {
                _logger.LogInformation("Release Date Added: inga objekttyper är aktiverade, inget att göra.");
                progress.Report(100);
                return;
            }

            var ids = _libraryManager.GetItemIds(new InternalItemsQuery
            {
                IncludeItemTypes = kinds.ToArray(),
                IsVirtualItem = false,
                Recursive = true,
                GroupByPresentationUniqueKey = false
            });

            _logger.LogInformation("Release Date Added: kontrollerar {Count} objekt.", ids.Count);

            var seriesIds = new HashSet<Guid>();
            int changed = 0, unchanged = 0, skipped = 0;

            for (var offset = 0; offset < ids.Count; offset += BatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var chunk = ids.Skip(offset).Take(BatchSize).ToArray();
                var items = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    ItemIds = chunk,
                    IsVirtualItem = false,
                    GroupByPresentationUniqueKey = false
                });

                foreach (var item in items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = await ApplyAsync(item, config, cancellationToken).ConfigureAwait(false);
                    switch (result)
                    {
                        case ApplyResult.Changed:
                        case ApplyResult.WouldChange:
                            changed++;
                            break;
                        case ApplyResult.Unchanged:
                            unchanged++;
                            break;
                        default:
                            skipped++;
                            break;
                    }

                    CollectSeries(item, result, config, seriesIds);
                }

                _store.Flush();
                progress.Report(Math.Min(95.0, (offset + chunk.Length) * 95.0 / Math.Max(1, ids.Count)));
            }

            await UpdateSeriesAsync(seriesIds, config, false, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Release Date Added: klart. {Changed} {Verb}, {Unchanged} redan rätt/saknar releasedatum, {Skipped} överhoppade.",
                changed,
                config.DryRun ? "skulle ändras (testläge)" : "ändrade",
                unchanged,
                skipped);

            progress.Report(100);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Sätter DateCreated = releasedatum för ett objekt om det behövs.</summary>
    public async Task<ApplyResult> ApplyAsync(BaseItem item, PluginConfiguration config, CancellationToken cancellationToken)
    {
        if (!IsSupported(item, config) || item.IsVirtualItem)
        {
            return ApplyResult.Skipped;
        }

        if (config.SkipLockedItems && item.IsLocked)
        {
            return ApplyResult.Skipped;
        }

        var oldDate = item.DateCreated;
        var newDate = DateAddedCalculator.CalculateNewDateAdded(oldDate, item.PremiereDate, DateTime.UtcNow, config.KeepTimeOfDay);
        if (!newDate.HasValue)
        {
            return ApplyResult.Unchanged;
        }

        if (config.DryRun)
        {
            _logger.LogInformation(
                "[Testläge] {Name}: Date added {Old:yyyy-MM-dd HH:mm} → {New:yyyy-MM-dd HH:mm} (release {Release:yyyy-MM-dd})",
                item.Name,
                oldDate,
                newDate.Value,
                item.PremiereDate);
            return ApplyResult.WouldChange;
        }

        item.DateCreated = newDate.Value;

        _ownWrites[item.Id] = 0;
        try
        {
            var reason = config.UpdateNfoFiles ? ItemUpdateType.MetadataEdit : ItemUpdateType.MetadataImport;
            await item.UpdateToRepositoryAsync(reason, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ownWrites.TryRemove(item.Id, out _);
        }

        _store.Record(item.Id, oldDate, newDate.Value);

        _logger.LogDebug(
            "{Name}: Date added {Old:yyyy-MM-dd HH:mm} → {New:yyyy-MM-dd HH:mm} (release {Release:yyyy-MM-dd})",
            item.Name,
            oldDate,
            newDate.Value,
            item.PremiereDate);

        return ApplyResult.Changed;
    }

    /// <summary>
    /// Lägger tillbaka Jellyfins ursprungliga datum och pausar pluginet (Enabled = false).
    /// </summary>
    public async Task RestoreAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var plugin = Plugin.Instance;
            if (plugin is not null && plugin.Configuration.Enabled)
            {
                plugin.Configuration.Enabled = false;
                plugin.SaveConfiguration();
                _logger.LogInformation("Release Date Added: pluginet pausas (Enabled = false) innan återställning.");
            }

            var config = Config;
            var entries = _store.Snapshot();
            var seriesIds = new HashSet<Guid>();
            int restored = 0, untouched = 0;

            for (var i = 0; i < entries.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (id, entry) = (entries[i].Key, entries[i].Value);
                var item = _libraryManager.GetItemById(id);

                if (item is not null && (item.DateCreated - entry.Applied).Duration() < DateAddedCalculator.Tolerance)
                {
                    item.DateCreated = DateTime.SpecifyKind(entry.Original, DateTimeKind.Utc);
                    _ownWrites[item.Id] = 0;
                    try
                    {
                        await item.UpdateToRepositoryAsync(config.UpdateNfoFiles ? ItemUpdateType.MetadataEdit : ItemUpdateType.MetadataImport, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        _ownWrites.TryRemove(item.Id, out _);
                    }

                    restored++;
                    if (item is Episode episode && episode.SeriesId != Guid.Empty)
                    {
                        seriesIds.Add(episode.SeriesId);
                    }
                }
                else
                {
                    // Borttaget, eller ändrat av någon annan sedan dess – låt det vara.
                    untouched++;
                }

                _store.Remove(id);
                if (i % 500 == 0)
                {
                    _store.Flush();
                    progress.Report(i * 95.0 / Math.Max(1, entries.Count));
                }
            }

            _store.Flush();
            await UpdateSeriesAsync(seriesIds, config, true, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Release Date Added: återställde {Restored} objekt ({Untouched} var borttagna eller ändrade av annat och lämnades). Pluginet är pausat.",
                restored,
                untouched);
            progress.Report(100);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void CollectSeries(BaseItem item, ApplyResult result, PluginConfiguration config, HashSet<Guid> seriesIds)
    {
        if (result != ApplyResult.Changed || !config.UpdateSeriesDateLastMediaAdded || item is not Episode episode)
        {
            return;
        }

        var seriesId = episode.SeriesId;
        if (seriesId == Guid.Empty)
        {
            seriesId = episode.Series?.Id ?? Guid.Empty;
        }

        if (seriesId != Guid.Empty)
        {
            seriesIds.Add(seriesId);
        }
    }

    /// <summary>
    /// Räknar om seriens DateLastMediaAdded (= senaste avsnittets DateCreated), på samma sätt som Jellyfin gör.
    /// Styr sorteringen "Date episode added" i TV-biblioteket.
    /// </summary>
    private async Task UpdateSeriesAsync(IEnumerable<Guid> seriesIds, PluginConfiguration config, bool force, CancellationToken cancellationToken)
    {
        if (!force && (config.DryRun || !config.UpdateSeriesDateLastMediaAdded))
        {
            return;
        }

        foreach (var seriesId in seriesIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_libraryManager.GetItemById(seriesId) is not Series series)
            {
                continue;
            }

            var children = _libraryManager.GetItemList(new InternalItemsQuery
            {
                AncestorIds = new[] { series.Id },
                IsFolder = false,
                IsVirtualItem = false,
                Recursive = true,
                GroupByPresentationUniqueKey = false
            });

            DateTime? latest = null;
            foreach (var child in children)
            {
                if (!latest.HasValue || child.DateCreated > latest.Value)
                {
                    latest = child.DateCreated;
                }
            }

            if (!latest.HasValue || series.DateLastMediaAdded == latest)
            {
                continue;
            }

            series.DateLastMediaAdded = latest;
            await series.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, cancellationToken).ConfigureAwait(false);
        }
    }
}
