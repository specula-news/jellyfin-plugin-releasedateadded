using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ReleaseDateAdded.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ReleaseDateAdded;

/// <summary>Result for a single item.</summary>
public enum ApplyResult
{
    /// <summary>Item type disabled, virtual, an extra, or locked.</summary>
    Skipped,

    /// <summary>Date already correct, or no date could be determined.</summary>
    Unchanged,

    /// <summary>Date and/or metadata was updated.</summary>
    Changed,

    /// <summary>Dry run: would have been updated.</summary>
    WouldChange
}

/// <summary>Where the date came from.</summary>
public enum DateSource
{
    /// <summary>No date.</summary>
    None,

    /// <summary>Jellyfin's own metadata (TMDb/TVDb/NFO …).</summary>
    Metadata,

    /// <summary>Lookup via IMDb/TVDb ID on TVmaze/OMDb.</summary>
    Online,

    /// <summary>Estimated from other episodes or the release year.</summary>
    Estimated,

    /// <summary>No date anywhere: January 1st of the item's/series' year (or the fallback year).</summary>
    Fallback
}

/// <summary>
/// Shared logic for the real-time watcher and the scheduled task.
/// </summary>
public sealed class ReleaseDateService
{
    private const int MaxReportLines = 2000;

    private readonly ILibraryManager _libraryManager;
    private readonly OnlineEpisodeLookup _lookup;
    private readonly ILogger<ReleaseDateService> _logger;
    private readonly ConcurrentDictionary<Guid, byte> _ownWrites = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly OriginalDateStore _store;

    public ReleaseDateService(ILibraryManager libraryManager, OnlineEpisodeLookup lookup, ILogger<ReleaseDateService> logger)
    {
        _libraryManager = libraryManager;
        _lookup = lookup;
        _logger = logger;
        _store = new OriginalDateStore(logger);
    }

    /// <summary>Current configuration.</summary>
    public static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>True while the plugin itself saves the item (to ignore its own ItemUpdated event).</summary>
    public bool IsOwnWrite(Guid itemId) => _ownWrites.ContainsKey(itemId);

    /// <summary>Whether the item type is covered by the settings.</summary>
    public static bool IsSupported(BaseItem item, PluginConfiguration config) => item switch
    {
        Episode => config.EnableEpisodes,
        Movie => config.EnableMovies && !item.ExtraType.HasValue,
        MusicVideo => config.EnableMusicVideos,
        Video when item.GetType() == typeof(Video) => config.EnableOtherVideos && !item.ExtraType.HasValue && item.OwnerId == Guid.Empty,
        _ => false
    };

    /// <summary>Processes a set of items (used by the real-time watcher).</summary>
    public async Task ProcessItemsAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var ctx = new RunContext(Config);
            if (!ctx.Config.Enabled)
            {
                return;
            }

            foreach (var id in itemIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = _libraryManager.GetItemById(id);
                if (item is null)
                {
                    continue;
                }

                try
                {
                    await ApplyAsync(item, ctx, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "{Prefix} could not update {Name} ({Path})", Plugin.LogPrefix, item.Name, item.Path);
                }
            }

            await FinishAsync(ctx, false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Processes the whole library (used by the scheduled task).</summary>
    public async Task ProcessLibraryAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var ctx = new RunContext(Config);
            var config = ctx.Config;
            if (!config.Enabled)
            {
                _logger.LogInformation("{Prefix} the plugin is paused (see settings), nothing to do.", Plugin.LogPrefix);
                progress.Report(100);
                return;
            }

            var kinds = new List<BaseItemKind>();
            if (config.EnableEpisodes)
            {
                kinds.Add(BaseItemKind.Episode);
            }

            if (config.EnableMovies)
            {
                kinds.Add(BaseItemKind.Movie);
            }

            if (config.EnableMusicVideos)
            {
                kinds.Add(BaseItemKind.MusicVideo);
            }

            if (config.EnableOtherVideos)
            {
                kinds.Add(BaseItemKind.Video);
            }

            if (kinds.Count == 0)
            {
                _logger.LogInformation("{Prefix} no item types are enabled, nothing to do.", Plugin.LogPrefix);
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

            _logger.LogInformation("{Prefix} checking {Count} items.", Plugin.LogPrefix, ids.Count);

            for (var i = 0; i < ids.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // GetItemById returns Jellyfin's own (cached) instance, so the change is not overwritten by a stale copy.
                var item = _libraryManager.GetItemById(ids[i]);
                if (item is not null)
                {
                    try
                    {
                        await ApplyAsync(item, ctx, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        ctx.Errors++;
                        _logger.LogWarning(ex, "{Prefix} could not update {Name} ({Path})", Plugin.LogPrefix, item.Name, item.Path);
                    }
                }

                if (i % 250 == 0)
                {
                    _store.Flush();
                    progress.Report(Math.Min(95.0, i * 95.0 / Math.Max(1, ids.Count)));
                }
            }

            await FinishAsync(ctx, true, cancellationToken).ConfigureAwait(false);
            progress.Report(100);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Sets the correct "Date added" for an item and fills in missing episode metadata.</summary>
    internal async Task<ApplyResult> ApplyAsync(BaseItem item, RunContext ctx, CancellationToken cancellationToken)
    {
        var config = ctx.Config;
        if (!IsSupported(item, config) || item.IsVirtualItem)
        {
            ctx.Skipped++;
            return ApplyResult.Skipped;
        }

        if (config.SkipLockedItems && item.IsLocked)
        {
            ctx.Skipped++;
            return ApplyResult.Skipped;
        }

        var now = DateTime.UtcNow;
        var oldDate = item.DateCreated;
        DateTime? release = item.PremiereDate;
        var source = release.HasValue ? DateSource.Metadata : DateSource.None;
        string? reason = null;
        var filled = new List<string>();

        // 1. Episodes without an air date, or with a file name as title: look up via IMDb/TVDb ID.
        if (item is Episode episode && config.EnableOnlineLookup
            && (!release.HasValue || (config.FillMissingMetadata && MetadataHeuristics.LooksLikeFileName(episode.Name, episode.Path))))
        {
            var lookup = await LookupAsync(episode, ctx, cancellationToken).ConfigureAwait(false);
            if (lookup?.Episode is EpisodeInfo info)
            {
                if (!release.HasValue && info.AirDate.HasValue)
                {
                    release = info.AirDate;
                    source = DateSource.Online;
                    reason = info.Source;
                }

                if (config.FillMissingMetadata)
                {
                    FillEpisode(episode, info, filled, config.DryRun);
                }
            }
            else if (lookup?.Problem is not null)
            {
                reason = lookup.Problem;
            }
        }

        // 2. Still no date: estimate.
        if (!release.HasValue && config.EstimateMissingDates)
        {
            var (estimate, why) = Estimate(item, ctx, now);
            if (estimate.HasValue)
            {
                release = estimate;
                source = DateSource.Estimated;
                reason = why;
            }
        }

        // 3. No date anywhere: use the item's/series' year so it never ends up first.
        if (!release.HasValue)
        {
            ctx.NoDate++;
            var problem = reason;
            if (config.PushUndatedItemsDown)
            {
                var seriesYear = item is Episode undatedEpisode ? SeriesYear(GetSeries(undatedEpisode, ctx)) : null;
                var (fallback, why) = DateEstimator.Fallback(item.ProductionYear, seriesYear, config.FallbackYear, now);
                release = fallback;
                source = DateSource.Fallback;
                reason = why;
            }

            ctx.AddReport(Describe(item, ctx)
                + (problem is null ? " – no air date found" : " – " + problem)
                + (source == DateSource.Fallback ? $" → placed at {release:yyyy-MM-dd}" : " → treated as new"));
        }

        var newDate = release.HasValue
            ? DateAddedCalculator.CalculateNewDateAdded(oldDate, release, now, config.KeepTimeOfDay)
            : null;

        if (!newDate.HasValue && filled.Count == 0)
        {
            ctx.Unchanged++;
            return ApplyResult.Unchanged;
        }

        if (config.DryRun)
        {
            _logger.LogInformation(
                "[Dry run] {Item}: Date added {Old:yyyy-MM-dd HH:mm} → {New} ({Source}{Reason}){Filled}",
                Describe(item, ctx),
                oldDate,
                newDate.HasValue ? newDate.Value.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) : "unchanged",
                source,
                reason is null ? string.Empty : ": " + reason,
                filled.Count == 0 ? string.Empty : " – would fill in " + string.Join(", ", filled));
            if (newDate.HasValue)
            {
                ctx.Count(source);
            }

            if (filled.Count > 0)
            {
                ctx.MetadataFilled++;
            }

            return ApplyResult.WouldChange;
        }

        if (newDate.HasValue)
        {
            item.DateCreated = newDate.Value;
        }

        _ownWrites[item.Id] = 0;
        try
        {
            var updateReason = config.UpdateNfoFiles ? ItemUpdateType.MetadataEdit : ItemUpdateType.MetadataImport;
            await item.UpdateToRepositoryAsync(updateReason, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ownWrites.TryRemove(item.Id, out _);
        }

        if (newDate.HasValue)
        {
            _store.Record(item.Id, oldDate, newDate.Value);
            ctx.Count(source);
            if (item is Episode changedEpisode)
            {
                var seriesId = SeriesIdOf(changedEpisode);
                if (seriesId != Guid.Empty)
                {
                    ctx.SeriesToUpdate.Add(seriesId);
                }
            }
        }

        if (filled.Count > 0)
        {
            ctx.MetadataFilled++;
        }

        if (source is DateSource.Estimated or DateSource.Fallback || filled.Count > 0)
        {
            _logger.LogDebug(
                "{Item}: Date added {Old:yyyy-MM-dd} → {New} ({Source}: {Reason}) {Filled}",
                Describe(item, ctx),
                oldDate,
                newDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "unchanged",
                source,
                reason,
                string.Join(", ", filled));
        }

        return ApplyResult.Changed;
    }

    /// <summary>
    /// Puts back Jellyfin's original dates and pauses the plugin (Enabled = false).
    /// Filled-in metadata (titles/air dates) is kept.
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
                _logger.LogInformation("{Prefix} pausing the plugin (Enabled = false) before restoring.", Plugin.LogPrefix);
            }

            var config = Config;
            var ctx = new RunContext(config);
            var entries = _store.Snapshot();
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
                    if (item is Episode episode && SeriesIdOf(episode) is var sid && sid != Guid.Empty)
                    {
                        ctx.SeriesToUpdate.Add(sid);
                    }
                }
                else
                {
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
            await UpdateSeriesAsync(ctx.SeriesToUpdate, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "{Prefix} restored {Restored} items ({Untouched} were removed or changed by something else and left alone). The plugin is paused.",
                Plugin.LogPrefix,
                restored,
                untouched);
            progress.Report(100);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<LookupResult?> LookupAsync(Episode episode, RunContext ctx, CancellationToken cancellationToken)
    {
        if (episode.ParentIndexNumber is not int season || episode.IndexNumber is not int number || season <= 0)
        {
            return null;
        }

        var series = GetSeries(episode, ctx);
        if (series is null)
        {
            return null;
        }

        var key = new SeriesKey(
            series.Name,
            series.ProductionYear,
            MetadataHeuristics.GetProviderId(series.ProviderIds, "Imdb"),
            MetadataHeuristics.GetProviderId(series.ProviderIds, "Tvdb"),
            MetadataHeuristics.GetProviderId(series.ProviderIds, "TvMaze"));

        var result = await _lookup.FindEpisodeAsync(key, season, number, ctx.Config.OmdbApiKey, cancellationToken).ConfigureAwait(false);
        ctx.Lookups++;

        // Better matching from now on: save the series' IMDb/TVDb/TVmaze IDs if missing.
        if (ctx.Config.FillMissingMetadata && !ctx.Config.DryRun && result.TvMazeShowId.HasValue)
        {
            ctx.SeriesIdFills[series.Id] = result;
        }

        if (result.Episode is null && result.Problem is not null)
        {
            ctx.LookupMisses++;
        }

        return result;
    }

    private static void FillEpisode(Episode episode, EpisodeInfo info, List<string> filled, bool dryRun)
    {
        var locked = episode.LockedFields ?? Array.Empty<MetadataField>();

        if (!episode.PremiereDate.HasValue && info.AirDate.HasValue)
        {
            filled.Add("air date " + info.AirDate.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
            if (!dryRun)
            {
                episode.PremiereDate = info.AirDate;
                episode.ProductionYear ??= info.AirDate.Value.Year;
            }
        }

        if (!string.IsNullOrWhiteSpace(info.Name) && MetadataHeuristics.LooksLikeFileName(episode.Name, episode.Path) && !locked.Contains(MetadataField.Name))
        {
            filled.Add("title \"" + info.Name + "\"");
            if (!dryRun)
            {
                episode.Name = info.Name!;
            }
        }

        if (string.IsNullOrWhiteSpace(episode.Overview) && !string.IsNullOrWhiteSpace(info.Overview) && !locked.Contains(MetadataField.Overview))
        {
            filled.Add("overview");
            if (!dryRun)
            {
                episode.Overview = info.Overview!;
            }
        }

        if (episode.ProviderIds is not null)
        {
            if (info.ImdbId is not null && MetadataHeuristics.GetProviderId(episode.ProviderIds, "Imdb") is null)
            {
                filled.Add("IMDb ID " + info.ImdbId);
                if (!dryRun)
                {
                    episode.ProviderIds["Imdb"] = info.ImdbId;
                }
            }

            // The TVmaze ID is only saved together with other filled metadata (no separate write just for the ID).
            if (filled.Count > 0 && info.TvMazeId is not null && MetadataHeuristics.GetProviderId(episode.ProviderIds, "TvMaze") is null)
            {
                filled.Add("TVmaze ID");
                if (!dryRun)
                {
                    episode.ProviderIds["TvMaze"] = info.TvMazeId;
                }
            }
        }
    }

    private (DateTime? Date, string? Reason) Estimate(BaseItem item, RunContext ctx, DateTime now)
    {
        if (item is not Episode episode)
        {
            var fromYear = DateEstimator.FromYear(item.ProductionYear, now);
            return fromYear.HasValue ? (fromYear, $"release year {item.ProductionYear}") : (null, null);
        }

        var series = GetSeries(episode, ctx);
        if (series is null)
        {
            return (null, null);
        }

        if (!ctx.SeriesEpisodes.TryGetValue(series.Id, out var siblings))
        {
            // Virtual (missing) episodes often have dates from TMDb too – useful input.
            siblings = _libraryManager.GetItemList(new InternalItemsQuery
            {
                AncestorIds = new[] { series.Id },
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                Recursive = true,
                GroupByPresentationUniqueKey = false
            }).Select(e => new EpisodeDate(e.ParentIndexNumber, e.IndexNumber, e.PremiereDate)).ToList();
            ctx.SeriesEpisodes[series.Id] = siblings;
        }

        return DateEstimator.EstimateEpisode(
            episode.ParentIndexNumber,
            episode.IndexNumber,
            siblings,
            episode.Season?.PremiereDate,
            series.Status == SeriesStatus.Ended,
            series.EndDate,
            series.PremiereDate,
            series.ProductionYear,
            now);
    }

    private Series? GetSeries(Episode episode, RunContext ctx)
    {
        var id = SeriesIdOf(episode);
        if (id == Guid.Empty)
        {
            return null;
        }

        if (!ctx.Series.TryGetValue(id, out var series))
        {
            series = _libraryManager.GetItemById(id) as Series;
            ctx.Series[id] = series;
        }

        return series;
    }

    private static int? SeriesYear(Series? series)
        => series?.ProductionYear ?? series?.PremiereDate?.Year;

    private static Guid SeriesIdOf(Episode episode)
    {
        var id = episode.SeriesId;
        return id != Guid.Empty ? id : episode.Series?.Id ?? Guid.Empty;
    }

    private string Describe(BaseItem item, RunContext ctx)
    {
        if (item is Episode e)
        {
            var seriesName = GetSeries(e, ctx)?.Name ?? "?";
            return $"{seriesName} S{e.ParentIndexNumber ?? 0:00}E{e.IndexNumber ?? 0:00} \"{e.Name}\"";
        }

        return item.ProductionYear.HasValue ? $"{item.Name} ({item.ProductionYear})" : item.Name;
    }

    private async Task FinishAsync(RunContext ctx, bool fullRun, CancellationToken cancellationToken)
    {
        _store.Flush();

        if (!ctx.Config.DryRun)
        {
            await SaveSeriesIdsAsync(ctx, cancellationToken).ConfigureAwait(false);
            if (ctx.Config.UpdateSeriesDateLastMediaAdded)
            {
                await UpdateSeriesAsync(ctx.SeriesToUpdate, cancellationToken).ConfigureAwait(false);
            }
        }

        if (fullRun)
        {
            var reportPath = WriteReport(ctx);
            _logger.LogInformation(
                "{Prefix} done. {Changed} {Verb} (from metadata {Meta}, IMDb/TVmaze {Online}, estimated {Estimated}, fallback year {Fallback}), " +
                "{Filled} episodes got missing metadata, {Unchanged} already correct, {NoDate} without any air date, {Skipped} skipped, {Errors} errors. " +
                "{Lookups} lookups ({Misses} without match, {Requests} requests). Report: {Report}",
                Plugin.LogPrefix,
                ctx.ChangedTotal,
                ctx.Config.DryRun ? "would change (dry run)" : "changed",
                ctx.FromMetadata,
                ctx.FromOnline,
                ctx.FromEstimate,
                ctx.FromFallback,
                ctx.MetadataFilled,
                ctx.Unchanged,
                ctx.NoDate,
                ctx.Skipped,
                ctx.Errors,
                ctx.Lookups,
                ctx.LookupMisses,
                _lookup.RequestCount,
                reportPath ?? "-");

            foreach (var line in ctx.Report.Take(25))
            {
                _logger.LogInformation("{Prefix} {Line}", Plugin.LogPrefix, line);
            }
        }
    }

    private async Task SaveSeriesIdsAsync(RunContext ctx, CancellationToken cancellationToken)
    {
        foreach (var (seriesId, result) in ctx.SeriesIdFills)
        {
            if (!ctx.Series.TryGetValue(seriesId, out var series) || series?.ProviderIds is null)
            {
                continue;
            }

            var changed = new List<string>();
            void Set(string key, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value) && MetadataHeuristics.GetProviderId(series.ProviderIds, key) is null)
                {
                    series.ProviderIds[key] = value!;
                    changed.Add(key + "=" + value);
                }
            }

            Set("TvMaze", result.TvMazeShowId?.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Set("Imdb", result.ShowImdbId);
            Set("Tvdb", result.ShowTvdbId);

            if (changed.Count > 0)
            {
                _ownWrites[series.Id] = 0;
                try
                {
                    await series.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _ownWrites.TryRemove(series.Id, out _);
                }

                _logger.LogInformation(
                    "{Prefix} series \"{Name}\" got {Ids}{How}.",
                    Plugin.LogPrefix,
                    series.Name,
                    string.Join(", ", changed),
                    result.MatchedByName ? " (matched by name – run \"Refresh metadata\" on the series to let Jellyfin fetch the rest)" : string.Empty);
            }
        }
    }

    /// <summary>
    /// Recomputes the series' DateLastMediaAdded (= newest episode DateCreated), the same way Jellyfin does.
    /// </summary>
    private async Task UpdateSeriesAsync(IEnumerable<Guid> seriesIds, CancellationToken cancellationToken)
    {
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
            _ownWrites[series.Id] = 0;
            try
            {
                await series.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _ownWrites.TryRemove(series.Id, out _);
            }
        }
    }

    private string? WriteReport(RunContext ctx)
    {
        try
        {
            var configFile = Plugin.Instance?.ConfigurationFilePath;
            var folder = string.IsNullOrEmpty(configFile) ? Path.GetTempPath() : Path.GetDirectoryName(configFile)!;
            var path = Path.Combine(folder, "Jellyfin.Plugin.ReleaseDateAdded.report.txt");
            var sb = new StringBuilder();
            sb.AppendLine("IMDb Air Date Fixer – report " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture));
            sb.AppendLine("Items without an air/release date. They were placed at January 1st of their own year or the series' start year so they don't show up first.");
            sb.AppendLine("Common causes: the file is in the wrong series, season numbering differs from TMDb/TVmaze, or the series has no IMDb ID.");
            sb.AppendLine();
            foreach (var line in ctx.Report.OrderBy(l => l, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine(line);
            }

            if (ctx.ReportTruncated > 0)
            {
                sb.AppendLine($"… and {ctx.ReportTruncated} more.");
            }

            Directory.CreateDirectory(folder);
            File.WriteAllText(path, sb.ToString());
            return path;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Prefix} could not write the report.", Plugin.LogPrefix);
            return null;
        }
    }

    /// <summary>State for one run.</summary>
    internal sealed class RunContext
    {
        public RunContext(PluginConfiguration config)
        {
            Config = config;
        }

        public PluginConfiguration Config { get; }

        public Dictionary<Guid, Series?> Series { get; } = new();

        public Dictionary<Guid, List<EpisodeDate>> SeriesEpisodes { get; } = new();

        public HashSet<Guid> SeriesToUpdate { get; } = new();

        public Dictionary<Guid, LookupResult> SeriesIdFills { get; } = new();

        public List<string> Report { get; } = new();

        public int ReportTruncated { get; set; }

        public int Skipped { get; set; }

        public int Unchanged { get; set; }

        public int NoDate { get; set; }

        public int Errors { get; set; }

        public int Lookups { get; set; }

        public int LookupMisses { get; set; }

        public int MetadataFilled { get; set; }

        public int FromMetadata { get; set; }

        public int FromOnline { get; set; }

        public int FromEstimate { get; set; }

        public int FromFallback { get; set; }

        public int ChangedTotal => FromMetadata + FromOnline + FromEstimate + FromFallback;

        public void Count(DateSource source)
        {
            switch (source)
            {
                case DateSource.Metadata:
                    FromMetadata++;
                    break;
                case DateSource.Online:
                    FromOnline++;
                    break;
                case DateSource.Estimated:
                    FromEstimate++;
                    break;
                case DateSource.Fallback:
                    FromFallback++;
                    break;
            }
        }

        public void AddReport(string line)
        {
            if (Report.Count < MaxReportLines)
            {
                Report.Add(line);
            }
            else
            {
                ReportTruncated++;
            }
        }
    }
}
