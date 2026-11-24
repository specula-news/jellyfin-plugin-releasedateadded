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

/// <summary>Resultat för ett enskilt objekt.</summary>
public enum ApplyResult
{
    /// <summary>Objekttypen är avstängd, virtuell, ett extramaterial eller låst.</summary>
    Skipped,

    /// <summary>Redan rätt datum, eller inget datum gick att få fram.</summary>
    Unchanged,

    /// <summary>Datum och/eller metadata uppdaterades.</summary>
    Changed,

    /// <summary>Testläge: skulle ha uppdaterats.</summary>
    WouldChange
}

/// <summary>Varifrån datumet kom.</summary>
public enum DateSource
{
    /// <summary>Inget datum.</summary>
    None,

    /// <summary>Jellyfins egen metadata (TMDb/TVDb/NFO …).</summary>
    Metadata,

    /// <summary>Uppslag via IMDb-/TVDb-ID hos TVmaze/OMDb.</summary>
    Online,

    /// <summary>Uppskattat från andra avsnitt eller utgivningsår.</summary>
    Estimated
}

/// <summary>
/// Gemensam logik för realtidsbevakning och schemalagd uppgift.
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

    /// <summary>Aktuell konfiguration.</summary>
    public static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>Sant medan pluginet självt sparar objektet (för att ignorera sitt eget ItemUpdated-event).</summary>
    public bool IsOwnWrite(Guid itemId) => _ownWrites.ContainsKey(itemId);

    /// <summary>Om objekttypen omfattas enligt inställningarna.</summary>
    public static bool IsSupported(BaseItem item, PluginConfiguration config) => item switch
    {
        Episode => config.EnableEpisodes,
        Movie => config.EnableMovies && !item.ExtraType.HasValue,
        MusicVideo => config.EnableMusicVideos,
        Video when item.GetType() == typeof(Video) => config.EnableOtherVideos && !item.ExtraType.HasValue && item.OwnerId == Guid.Empty,
        _ => false
    };

    /// <summary>Bearbetar en mängd objekt (används av realtidsbevakningen).</summary>
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
                    _logger.LogWarning(ex, "Air Date Fixer: kunde inte uppdatera {Name} ({Path})", item.Name, item.Path);
                }
            }

            await FinishAsync(ctx, false, cancellationToken).ConfigureAwait(false);
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
            var ctx = new RunContext(Config);
            var config = ctx.Config;
            if (!config.Enabled)
            {
                _logger.LogInformation("Air Date Fixer: pluginet är pausat (se inställningarna), inget att göra.");
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
                _logger.LogInformation("Air Date Fixer: inga objekttyper är aktiverade, inget att göra.");
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

            _logger.LogInformation("Air Date Fixer: kontrollerar {Count} objekt.", ids.Count);

            for (var i = 0; i < ids.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // GetItemById ger Jellyfins egen (cachade) instans, så att ändringen inte skrivs över av en gammal kopia.
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
                        _logger.LogWarning(ex, "Air Date Fixer: kunde inte uppdatera {Name} ({Path})", item.Name, item.Path);
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

    /// <summary>Sätter rätt "Date added" för ett objekt, och fyller i saknad avsnittsmetadata.</summary>
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

        // 1. Avsnitt utan sändningsdatum eller med filnamn som titel: slå upp via IMDb-/TVDb-ID.
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

        // 2. Fortfarande inget datum: uppskatta.
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

        if (!release.HasValue)
        {
            ctx.NoDate++;
            ctx.AddReport(Describe(item, ctx) + " – inget datum" + (reason is null ? string.Empty : ": " + reason));
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
                "[Testläge] {Item}: Date added {Old:yyyy-MM-dd HH:mm} → {New} ({Source}{Reason}){Filled}",
                Describe(item, ctx),
                oldDate,
                newDate.HasValue ? newDate.Value.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) : "oförändrat",
                source,
                reason is null ? string.Empty : ": " + reason,
                filled.Count == 0 ? string.Empty : " – skulle fylla i " + string.Join(", ", filled));
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

        if (source == DateSource.Estimated || filled.Count > 0)
        {
            _logger.LogDebug(
                "{Item}: Date added {Old:yyyy-MM-dd} → {New} ({Source}: {Reason}) {Filled}",
                Describe(item, ctx),
                oldDate,
                newDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "oförändrat",
                source,
                reason,
                string.Join(", ", filled));
        }

        return ApplyResult.Changed;
    }

    /// <summary>
    /// Lägger tillbaka Jellyfins ursprungliga datum och pausar pluginet (Enabled = false).
    /// Ifylld metadata (titlar/datum) ligger kvar.
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
                _logger.LogInformation("Air Date Fixer: pluginet pausas (Enabled = false) innan återställning.");
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
                "Air Date Fixer: återställde {Restored} objekt ({Untouched} var borttagna eller ändrade av annat och lämnades). Pluginet är pausat.",
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

        // Bättre matchning framåt: spara seriens IMDb/TVDb/TVmaze-ID om de saknas.
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
            filled.Add("sändningsdatum " + info.AirDate.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
            if (!dryRun)
            {
                episode.PremiereDate = info.AirDate;
                episode.ProductionYear ??= info.AirDate.Value.Year;
            }
        }

        if (!string.IsNullOrWhiteSpace(info.Name) && MetadataHeuristics.LooksLikeFileName(episode.Name, episode.Path) && !locked.Contains(MetadataField.Name))
        {
            filled.Add("titel \"" + info.Name + "\"");
            if (!dryRun)
            {
                episode.Name = info.Name!;
            }
        }

        if (string.IsNullOrWhiteSpace(episode.Overview) && !string.IsNullOrWhiteSpace(info.Overview) && !locked.Contains(MetadataField.Overview))
        {
            filled.Add("beskrivning");
            if (!dryRun)
            {
                episode.Overview = info.Overview!;
            }
        }

        if (episode.ProviderIds is not null)
        {
            if (info.ImdbId is not null && MetadataHeuristics.GetProviderId(episode.ProviderIds, "Imdb") is null)
            {
                filled.Add("IMDb-ID " + info.ImdbId);
                if (!dryRun)
                {
                    episode.ProviderIds["Imdb"] = info.ImdbId;
                }
            }

            // TVmaze-ID sparas bara tillsammans med annan ifylld metadata (ingen egen skrivning bara för ID:t).
            if (filled.Count > 0 && info.TvMazeId is not null && MetadataHeuristics.GetProviderId(episode.ProviderIds, "TvMaze") is null)
            {
                filled.Add("TVmaze-ID");
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
            return fromYear.HasValue ? (fromYear, $"utgivningsår {item.ProductionYear}") : (null, null);
        }

        var series = GetSeries(episode, ctx);
        if (series is null)
        {
            return (null, null);
        }

        if (!ctx.SeriesEpisodes.TryGetValue(series.Id, out var siblings))
        {
            // Även virtuella (saknade) avsnitt har ofta datum från TMDb – bra underlag.
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
                "Air Date Fixer: klart. {Changed} {Verb} (från metadata {Meta}, IMDb/TVmaze {Online}, uppskattade {Estimated}), " +
                "{Filled} avsnitt fick saknad metadata, {Unchanged} redan rätt, {NoDate} utan datum (behandlas som nya), {Skipped} överhoppade, {Errors} fel. " +
                "{Lookups} uppslag ({Misses} utan träff, {Requests} anrop). Rapport: {Report}",
                ctx.ChangedTotal,
                ctx.Config.DryRun ? "skulle ändras (testläge)" : "ändrade",
                ctx.FromMetadata,
                ctx.FromOnline,
                ctx.FromEstimate,
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
                _logger.LogInformation("Air Date Fixer: {Line}", line);
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
                    "Air Date Fixer: serien \"{Name}\" fick {Ids}{How}.",
                    series.Name,
                    string.Join(", ", changed),
                    result.MatchedByName ? " (matchad på namn – kör \"Uppdatera metadata\" på serien så hämtar Jellyfin resten)" : string.Empty);
            }
        }
    }

    /// <summary>
    /// Räknar om seriens DateLastMediaAdded (= senaste avsnittets DateCreated), på samma sätt som Jellyfin gör.
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
            sb.AppendLine("Air Date and Metadata Fixer – rapport " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture));
            sb.AppendLine("Objekt som saknar sändnings-/releasedatum. De behåller Jellyfins datum och räknas som nya.");
            sb.AppendLine("Vanliga orsaker: filen ligger i fel serie, annan säsongsnumrering än TMDb/TVmaze, eller serien saknar IMDb-ID.");
            sb.AppendLine();
            foreach (var line in ctx.Report.OrderBy(l => l, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine(line);
            }

            if (ctx.ReportTruncated > 0)
            {
                sb.AppendLine($"… och {ctx.ReportTruncated} till.");
            }

            Directory.CreateDirectory(folder);
            File.WriteAllText(path, sb.ToString());
            return path;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Air Date Fixer: kunde inte skriva rapporten.");
            return null;
        }
    }

    /// <summary>Tillstånd för en körning.</summary>
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

        public int ChangedTotal => FromMetadata + FromOnline + FromEstimate;

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
