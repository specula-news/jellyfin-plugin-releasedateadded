using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ReleaseDateAdded;

/// <summary>Episode details from an online source.</summary>
public sealed class EpisodeInfo
{
    public DateTime? AirDate { get; set; }

    public string? Name { get; set; }

    public string? Overview { get; set; }

    public string? ImdbId { get; set; }

    public string? TvMazeId { get; set; }

    public string? TmdbId { get; set; }

    public string Source { get; set; } = string.Empty;

    public EpisodeInfo Clone() => (EpisodeInfo)MemberwiseClone();
}

/// <summary>Result of a lookup.</summary>
public sealed class LookupResult
{
    public EpisodeInfo? Episode { get; set; }

    /// <summary>Explanation when nothing was found (goes into the report).</summary>
    public string? Problem { get; set; }

    public int? TvMazeShowId { get; set; }

    public string? ShowName { get; set; }

    public string? ShowImdbId { get; set; }

    public string? ShowTvdbId { get; set; }

    public string? ShowTmdbId { get; set; }

    /// <summary>The series was found by name search (it had no IDs).</summary>
    public bool MatchedByName { get; set; }
}

/// <summary>Series data needed for lookups (decoupled from Jellyfin's Series class for testability).</summary>
public sealed record SeriesKey(string Name, int? Year, string? ImdbId, string? TvdbId, string? TvMazeId, string? TmdbId = null);

/// <summary>Which sources to use. Sources that need a (free) key are skipped when the key is empty.</summary>
public sealed record LookupOptions(bool UseTvMaze, string? TmdbApiKey, string? TraktClientId, string? OmdbApiKey, bool UseWikidata = true)
{
    public static LookupOptions TvMazeOnly { get; } = new(true, null, null, null, false);
}

/// <summary>
/// Looks up missing air dates/titles via the series' IMDb/TVDb/TMDb ID, using free sources:
/// TVmaze (no key), TMDb (free key), Trakt (free client ID), Wikidata (no key, by IMDb ID)
/// and OMDb (IMDb data, free key).
/// </summary>
public sealed class OnlineEpisodeLookup
{
    /// <summary>Name of the HttpClient.</summary>
    public const string HttpClientName = "ImdbAirDateFixer";

    private const string TvMaze = "https://api.tvmaze.com";
    private const string Tmdb = "https://api.themoviedb.org/3";
    private const string Trakt = "https://api.trakt.tv";
    private const string Omdb = "https://www.omdbapi.com/";
    private const string Wikidata = "https://query.wikidata.org/sparql";
    private static readonly TimeSpan CacheTime = TimeSpan.FromHours(6);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OnlineEpisodeLookup> _logger;
    private readonly SemaphoreSlim _throttle = new(1, 1);
    private readonly ConcurrentDictionary<string, (DateTime At, object? Value)> _cache = new();
    private DateTime _lastTvMazeRequest = DateTime.MinValue;

    public OnlineEpisodeLookup(IHttpClientFactory httpClientFactory, ILogger<OnlineEpisodeLookup> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>Minimum time between TVmaze requests (they allow about 20 requests per 10 s).</summary>
    public TimeSpan TvMazeInterval { get; set; } = TimeSpan.FromMilliseconds(600);

    /// <summary>Number of HTTP requests (for logging/tests).</summary>
    public int RequestCount { get; private set; }

    /// <summary>Looks up an episode in all enabled sources and merges what they know.</summary>
    public async Task<LookupResult> FindEpisodeAsync(SeriesKey series, int season, int number, LookupOptions options, CancellationToken cancellationToken)
    {
        var result = new LookupResult();
        var tried = new List<string>();
        string? firstProblem = null;

        void Merge(EpisodeInfo? found)
        {
            if (found is null)
            {
                return;
            }

            if (result.Episode is null)
            {
                result.Episode = found.Clone();
                return;
            }

            var e = result.Episode;
            var added = false;
            if (!e.AirDate.HasValue && found.AirDate.HasValue)
            {
                e.AirDate = found.AirDate;
                added = true;
            }

            if (MetadataHeuristics.IsPlaceholderTitle(e.Name) && !MetadataHeuristics.IsPlaceholderTitle(found.Name))
            {
                e.Name = found.Name;
                added = true;
            }

            if (e.Overview is null && found.Overview is not null)
            {
                e.Overview = found.Overview;
                added = true;
            }

            if (e.ImdbId is null && found.ImdbId is not null)
            {
                e.ImdbId = found.ImdbId;
                added = true;
            }

            e.TvMazeId ??= found.TvMazeId;
            e.TmdbId ??= found.TmdbId;
            if (added)
            {
                e.Source += "+" + found.Source;
            }
        }

        bool Complete() => result.Episode is { AirDate: not null, Overview: not null, ImdbId: not null }
                           && !MetadataHeuristics.IsPlaceholderTitle(result.Episode.Name);

        // 1. TVmaze (free, no key).
        if (options.UseTvMaze)
        {
            tried.Add("TVmaze");
            var (show, episodes) = await TvMazeAsync(series, cancellationToken).ConfigureAwait(false);
            if (show is not null)
            {
                result.TvMazeShowId = int.TryParse(show.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tvMazeShowId) ? tvMazeShowId : null;
                result.ShowName = show.Name;
                result.ShowImdbId ??= show.ImdbId;
                result.ShowTvdbId ??= show.TvdbId;
                result.MatchedByName |= show.MatchedByName;

                if (episodes is not null && episodes.TryGetValue((season, number), out var ep))
                {
                    Merge(ep);
                }
                else if (episodes is not null && episodes.Count > 0)
                {
                    var seasons = episodes.Keys.Select(k => k.Item1).Where(s => s > 0).Distinct().OrderBy(s => s).ToList();
                    firstProblem ??= seasons.Contains(season)
                        ? $"S{season:00}E{number:00} does not exist on TVmaze for \"{show.Name}\" (the season has {episodes.Keys.Count(k => k.Item1 == season)} episodes) – check the numbering"
                        : $"season {season} does not exist on TVmaze for \"{show.Name}\" (it has season {Describe(seasons)}) – is the file in the wrong series, or numbered differently?";
                }
            }
        }

        // 2. TMDb (free API key).
        if (!Complete() && !string.IsNullOrWhiteSpace(options.TmdbApiKey))
        {
            tried.Add("TMDb");
            var key = series with { ImdbId = series.ImdbId ?? result.ShowImdbId, TvdbId = series.TvdbId ?? result.ShowTvdbId };
            var tmdbId = await TmdbShowIdAsync(key, options.TmdbApiKey!, cancellationToken).ConfigureAwait(false);
            if (tmdbId is not null)
            {
                result.ShowTmdbId ??= tmdbId;
                var seasonEpisodes = await TmdbSeasonAsync(tmdbId, season, options.TmdbApiKey!, cancellationToken).ConfigureAwait(false);
                if (seasonEpisodes is not null && seasonEpisodes.TryGetValue(number, out var ep))
                {
                    Merge(ep);
                }
            }
        }

        // 3. Trakt (free client ID).
        if (!Complete() && !string.IsNullOrWhiteSpace(options.TraktClientId))
        {
            tried.Add("Trakt");
            var key = series with { ImdbId = series.ImdbId ?? result.ShowImdbId, TvdbId = series.TvdbId ?? result.ShowTvdbId };
            var show = await TraktShowAsync(key, options.TraktClientId!, cancellationToken).ConfigureAwait(false);
            if (show is not null)
            {
                result.ShowImdbId ??= show.ImdbId;
                result.ShowTvdbId ??= show.TvdbId;
                result.ShowTmdbId ??= show.TmdbId;
                result.ShowName ??= show.Name;
                result.MatchedByName |= show.MatchedByName && result.TvMazeShowId is null;
                var seasonEpisodes = await TraktSeasonAsync(show.Id, season, options.TraktClientId!, cancellationToken).ConfigureAwait(false);
                if (seasonEpisodes is not null && seasonEpisodes.TryGetValue(number, out var ep))
                {
                    Merge(ep);
                }
            }
        }

        var imdbSeriesId = series.ImdbId ?? result.ShowImdbId;

        // 4. Wikidata (free, no key – matched by the series' IMDb ID).
        if (!Complete() && options.UseWikidata && !string.IsNullOrWhiteSpace(imdbSeriesId))
        {
            tried.Add("Wikidata");
            var wdSeason = await WikidataSeasonAsync(imdbSeriesId!, season, cancellationToken).ConfigureAwait(false);
            if (wdSeason is not null && wdSeason.TryGetValue(number, out var ep))
            {
                Merge(ep);
            }
        }

        // 5. OMDb (IMDb data, free API key).
        if (!Complete() && !string.IsNullOrWhiteSpace(options.OmdbApiKey) && !string.IsNullOrWhiteSpace(imdbSeriesId))
        {
            tried.Add("OMDb/IMDb");
            var omdbSeason = await OmdbSeasonAsync(options.OmdbApiKey!, imdbSeriesId!, season, cancellationToken).ConfigureAwait(false);
            if (omdbSeason is not null && omdbSeason.TryGetValue(number, out var ep))
            {
                Merge(ep);
            }
        }

        if (result.Episode is null)
        {
            var sources = tried.Count == 0 ? "no sources enabled" : "not found on " + string.Join(", ", tried);
            result.Problem = firstProblem is null
                ? (result.TvMazeShowId is null && result.ShowTmdbId is null && result.ShowImdbId is null
                    ? $"series \"{series.Name}\" not found ({sources}; no IMDb/TVDb ID, or unknown name)"
                    : $"S{season:00}E{number:00} {sources}")
                : firstProblem + $" ({sources})";
        }

        return result;
    }

    private static string Describe(List<int> seasons)
        => seasons.Count == 0 ? "none" : seasons.Count == 1 ? seasons[0].ToString(CultureInfo.InvariantCulture) : $"{seasons[0]}–{seasons[^1]}";

    // ---------------------------------------------------------------- TVmaze

    private async Task<(ShowData? Show, Dictionary<(int, int), EpisodeInfo>? Episodes)> TvMazeAsync(SeriesKey series, CancellationToken ct)
    {
        var show = await TvMazeShowAsync(series, ct).ConfigureAwait(false);
        if (show is null)
        {
            return (null, null);
        }

        var episodes = await Cached($"tvmaze-eps:{show.Id}", async () =>
        {
            var json = await GetJsonAsync($"{TvMaze}/shows/{show.Id}/episodes", Source.TvMaze, null, ct).ConfigureAwait(false);
            if (json is not JsonElement arr || arr.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var dict = new Dictionary<(int, int), EpisodeInfo>();
            foreach (var e in arr.EnumerateArray())
            {
                if (GetInt(e, "season") is int s && GetInt(e, "number") is int n)
                {
                    dict[(s, n)] = new EpisodeInfo
                    {
                        AirDate = ParseDate(GetString(e, "airdate")),
                        Name = GetString(e, "name"),
                        Overview = MetadataHeuristics.StripHtml(GetString(e, "summary")),
                        TvMazeId = GetInt(e, "id")?.ToString(CultureInfo.InvariantCulture),
                        Source = "TVmaze"
                    };
                }
            }

            return dict;
        }).ConfigureAwait(false);

        return (show, episodes);
    }

    private async Task<ShowData?> TvMazeShowAsync(SeriesKey series, CancellationToken ct)
    {
        var cacheKey = "tvmaze-show:" + KeyOf(series);
        if (TryCache(cacheKey, out ShowData? cached))
        {
            return cached;
        }

        ShowData? show = null;
        if (series.TvMazeId is not null && int.TryParse(series.TvMazeId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tvMazeId))
        {
            show = ParseTvMazeShow(await GetJsonAsync($"{TvMaze}/shows/{tvMazeId}", Source.TvMaze, null, ct).ConfigureAwait(false));
        }

        if (show is null && series.ImdbId is not null)
        {
            show = ParseTvMazeShow(await GetJsonAsync($"{TvMaze}/lookup/shows?imdb={Uri.EscapeDataString(series.ImdbId)}", Source.TvMaze, null, ct).ConfigureAwait(false));
        }

        if (show is null && series.TvdbId is not null)
        {
            show = ParseTvMazeShow(await GetJsonAsync($"{TvMaze}/lookup/shows?thetvdb={Uri.EscapeDataString(series.TvdbId)}", Source.TvMaze, null, ct).ConfigureAwait(false));
        }

        if (show is null && !string.IsNullOrWhiteSpace(series.Name))
        {
            var candidate = ParseTvMazeShow(await GetJsonAsync($"{TvMaze}/singlesearch/shows?q={Uri.EscapeDataString(series.Name)}", Source.TvMaze, null, ct).ConfigureAwait(false));
            if (candidate is not null && IsSameShow(series, candidate.Name, candidate.Year))
            {
                candidate.MatchedByName = true;
                show = candidate;
            }
            else if (candidate is not null)
            {
                _logger.LogDebug("{Prefix} TVmaze name search for \"{Name}\" returned \"{Found}\" ({Year}) – not accepted.", Plugin.LogPrefix, series.Name, candidate.Name, candidate.Year);
            }
        }

        Store(cacheKey, show);
        if (show is not null)
        {
            // The same show is also reachable via its other IDs (which the plugin may have saved on the series).
            StoreIfMissing("tvmaze-show:tvmaze:" + show.Id.ToString(CultureInfo.InvariantCulture), show);
            if (show.ImdbId is not null)
            {
                StoreIfMissing("tvmaze-show:imdb:" + show.ImdbId, show);
            }

            if (show.TvdbId is not null)
            {
                StoreIfMissing("tvmaze-show:tvdb:" + show.TvdbId, show);
            }
        }

        return show;
    }

    private static ShowData? ParseTvMazeShow(JsonElement? json)
    {
        if (json is not JsonElement e || e.ValueKind != JsonValueKind.Object || GetInt(e, "id") is not int id)
        {
            return null;
        }

        string? imdb = null, tvdb = null;
        if (e.TryGetProperty("externals", out var ext) && ext.ValueKind == JsonValueKind.Object)
        {
            imdb = GetString(ext, "imdb");
            tvdb = GetInt(ext, "thetvdb")?.ToString(CultureInfo.InvariantCulture);
        }

        return new ShowData
        {
            Id = id.ToString(CultureInfo.InvariantCulture),
            Name = GetString(e, "name") ?? string.Empty,
            Year = ParseDate(GetString(e, "premiered"))?.Year,
            ImdbId = imdb,
            TvdbId = tvdb
        };
    }

    // ------------------------------------------------------------------ TMDb

    private async Task<string?> TmdbShowIdAsync(SeriesKey series, string apiKey, CancellationToken ct)
    {
        if (series.TmdbId is not null)
        {
            return series.TmdbId;
        }

        return await Cached("tmdb-show:" + KeyOf(series), async () =>
        {
            foreach (var (id, source) in new[] { (series.ImdbId, "imdb_id"), (series.TvdbId, "tvdb_id") })
            {
                if (id is null)
                {
                    continue;
                }

                var json = await GetJsonAsync($"{Tmdb}/find/{Uri.EscapeDataString(id)}?external_source={source}", Source.Tmdb, apiKey, ct).ConfigureAwait(false);
                if (json is JsonElement e && e.TryGetProperty("tv_results", out var tv) && tv.ValueKind == JsonValueKind.Array
                    && tv.GetArrayLength() > 0 && GetInt(tv[0], "id") is int tmdbId)
                {
                    return tmdbId.ToString(CultureInfo.InvariantCulture);
                }
            }

            if (string.IsNullOrWhiteSpace(series.Name))
            {
                return null;
            }

            var url = $"{Tmdb}/search/tv?query={Uri.EscapeDataString(series.Name)}" + (series.Year is int y ? $"&first_air_date_year={y}" : string.Empty);
            var search = await GetJsonAsync(url, Source.Tmdb, apiKey, ct).ConfigureAwait(false);
            if (search is JsonElement s && s.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in results.EnumerateArray())
                {
                    var year = ParseDate(GetString(r, "first_air_date"))?.Year;
                    if ((IsSameShow(series, GetString(r, "name"), year) || IsSameShow(series, GetString(r, "original_name"), year))
                        && GetInt(r, "id") is int found)
                    {
                        return found.ToString(CultureInfo.InvariantCulture);
                    }
                }
            }

            return null;
        }).ConfigureAwait(false);
    }

    private Task<Dictionary<int, EpisodeInfo>?> TmdbSeasonAsync(string tmdbId, int season, string apiKey, CancellationToken ct)
        => Cached($"tmdb-season:{tmdbId}:{season}", async () =>
        {
            var json = await GetJsonAsync($"{Tmdb}/tv/{tmdbId}/season/{season}", Source.Tmdb, apiKey, ct).ConfigureAwait(false);
            if (json is not JsonElement e || !e.TryGetProperty("episodes", out var eps) || eps.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var dict = new Dictionary<int, EpisodeInfo>();
            foreach (var ep in eps.EnumerateArray())
            {
                if (GetInt(ep, "episode_number") is int n)
                {
                    dict[n] = new EpisodeInfo
                    {
                        AirDate = ParseDate(GetString(ep, "air_date")),
                        Name = GetString(ep, "name"),
                        Overview = string.IsNullOrWhiteSpace(GetString(ep, "overview")) ? null : GetString(ep, "overview"),
                        TmdbId = GetInt(ep, "id")?.ToString(CultureInfo.InvariantCulture),
                        Source = "TMDb"
                    };
                }
            }

            return dict;
        });

    // ----------------------------------------------------------------- Trakt

    private Task<ShowData?> TraktShowAsync(SeriesKey series, string clientId, CancellationToken ct)
        => Cached("trakt-show:" + KeyOf(series), async () =>
        {
            foreach (var (id, kind) in new[] { (series.ImdbId, "imdb"), (series.TvdbId, "tvdb"), (series.TmdbId, "tmdb") })
            {
                if (id is null)
                {
                    continue;
                }

                var show = FirstTraktShow(await GetJsonAsync($"{Trakt}/search/{kind}/{Uri.EscapeDataString(id)}?type=show", Source.Trakt, clientId, ct).ConfigureAwait(false));
                if (show is not null)
                {
                    return show;
                }
            }

            if (string.IsNullOrWhiteSpace(series.Name))
            {
                return null;
            }

            var json = await GetJsonAsync($"{Trakt}/search/show?query={Uri.EscapeDataString(series.Name)}", Source.Trakt, clientId, ct).ConfigureAwait(false);
            if (json is JsonElement arr && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var hit in arr.EnumerateArray())
                {
                    var show = ParseTraktShow(hit);
                    if (show is not null && IsSameShow(series, show.Name, show.Year))
                    {
                        show.MatchedByName = true;
                        return show;
                    }
                }
            }

            return null;
        });

    private static ShowData? FirstTraktShow(JsonElement? json)
        => json is JsonElement arr && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0 ? ParseTraktShow(arr[0]) : null;

    private static ShowData? ParseTraktShow(JsonElement hit)
    {
        if (!hit.TryGetProperty("show", out var s) || s.ValueKind != JsonValueKind.Object
            || !s.TryGetProperty("ids", out var ids) || GetInt(ids, "trakt") is not int traktId)
        {
            return null;
        }

        return new ShowData
        {
            Id = traktId.ToString(CultureInfo.InvariantCulture),
            Name = GetString(s, "title") ?? string.Empty,
            Year = GetInt(s, "year"),
            ImdbId = GetString(ids, "imdb"),
            TvdbId = GetInt(ids, "tvdb")?.ToString(CultureInfo.InvariantCulture),
            TmdbId = GetInt(ids, "tmdb")?.ToString(CultureInfo.InvariantCulture)
        };
    }

    private Task<Dictionary<int, EpisodeInfo>?> TraktSeasonAsync(string traktId, int season, string clientId, CancellationToken ct)
        => Cached($"trakt-season:{traktId}:{season}", async () =>
        {
            var json = await GetJsonAsync($"{Trakt}/shows/{traktId}/seasons/{season}?extended=full", Source.Trakt, clientId, ct).ConfigureAwait(false);
            if (json is not JsonElement arr || arr.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var dict = new Dictionary<int, EpisodeInfo>();
            foreach (var ep in arr.EnumerateArray())
            {
                if (GetInt(ep, "number") is not int n)
                {
                    continue;
                }

                string? imdb = null, tmdb = null;
                if (ep.TryGetProperty("ids", out var ids) && ids.ValueKind == JsonValueKind.Object)
                {
                    imdb = GetString(ids, "imdb");
                    tmdb = GetInt(ids, "tmdb")?.ToString(CultureInfo.InvariantCulture);
                }

                DateTime? aired = null;
                if (GetString(ep, "first_aired") is string fa
                    && DateTime.TryParse(fa, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t))
                {
                    aired = DateTime.SpecifyKind(t.Date, DateTimeKind.Utc);
                }

                dict[n] = new EpisodeInfo
                {
                    AirDate = aired,
                    Name = GetString(ep, "title"),
                    Overview = string.IsNullOrWhiteSpace(GetString(ep, "overview")) ? null : GetString(ep, "overview"),
                    ImdbId = imdb is not null && imdb.StartsWith("tt", StringComparison.Ordinal) ? imdb : null,
                    TmdbId = tmdb,
                    Source = "Trakt"
                };
            }

            return dict;
        });

    // ------------------------------------------------------------ OMDb/IMDb

    private Task<Dictionary<int, EpisodeInfo>?> OmdbSeasonAsync(string apiKey, string imdbSeriesId, int season, CancellationToken ct)
        => Cached($"omdb-season:{imdbSeriesId}:{season}", async () =>
        {
            var url = $"{Omdb}?apikey={Uri.EscapeDataString(apiKey)}&i={Uri.EscapeDataString(imdbSeriesId)}&Season={season}";
            var json = await GetJsonAsync(url, Source.Omdb, null, ct).ConfigureAwait(false);
            if (json is JsonElement root && root.ValueKind == JsonValueKind.Object
                && string.Equals(GetString(root, "Response"), "True", StringComparison.OrdinalIgnoreCase)
                && root.TryGetProperty("Episodes", out var eps) && eps.ValueKind == JsonValueKind.Array)
            {
                var dict = new Dictionary<int, EpisodeInfo>();
                foreach (var e in eps.EnumerateArray())
                {
                    if (!int.TryParse(GetString(e, "Episode"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                    {
                        continue;
                    }

                    var imdbId = GetString(e, "imdbID");
                    dict[number] = new EpisodeInfo
                    {
                        AirDate = ParseDate(GetString(e, "Released")),
                        Name = GetString(e, "Title"),
                        ImdbId = imdbId is not null && imdbId.StartsWith("tt", StringComparison.Ordinal) ? imdbId : null,
                        Source = "OMDb/IMDb"
                    };
                }

                return dict;
            }

            if (json is JsonElement err && err.ValueKind == JsonValueKind.Object && GetString(err, "Error") is string error)
            {
                _logger.LogDebug("{Prefix} OMDb answered \"{Error}\" for {Imdb} season {Season}.", Plugin.LogPrefix, error, imdbSeriesId, season);
            }

            return (Dictionary<int, EpisodeInfo>?)null;
        });

    // -------------------------------------------------------------- Wikidata

    private Task<Dictionary<int, EpisodeInfo>?> WikidataSeasonAsync(string imdbSeriesId, int season, CancellationToken ct)
        => Cached($"wikidata-season:{imdbSeriesId}:{season}", async () =>
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(imdbSeriesId, "^tt[0-9]+$"))
            {
                return null;
            }

            var query = $$"""
                SELECT ?epnum ?date ?imdb ?title WHERE {
                  ?series wdt:P345 "{{imdbSeriesId}}" .
                  ?season p:P179 ?ss . ?ss ps:P179 ?series ; pq:P1545 "{{season.ToString(CultureInfo.InvariantCulture)}}" .
                  ?ep p:P4908 ?es . ?es ps:P4908 ?season ; pq:P1545 ?epnum .
                  OPTIONAL { ?ep wdt:P577 ?date }
                  OPTIONAL { ?ep wdt:P345 ?imdb }
                  OPTIONAL { ?ep rdfs:label ?title FILTER(LANG(?title) = "en") }
                }
                """;
            var json = await GetJsonAsync($"{Wikidata}?format=json&query={Uri.EscapeDataString(query)}", Source.Wikidata, null, ct).ConfigureAwait(false);
            if (json is not JsonElement root || !root.TryGetProperty("results", out var results)
                || !results.TryGetProperty("bindings", out var bindings) || bindings.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            static string? Val(JsonElement b, string name)
                => b.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? GetString(v, "value") : null;

            var dict = new Dictionary<int, EpisodeInfo>();
            foreach (var b in bindings.EnumerateArray())
            {
                if (!int.TryParse(Val(b, "epnum"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                {
                    continue;
                }

                if (!dict.TryGetValue(n, out var info))
                {
                    info = new EpisodeInfo { Source = "Wikidata" };
                    dict[n] = info;
                }

                // An episode can have several publication dates (one per country) – keep the earliest.
                if (Val(b, "date") is string d
                    && DateTime.TryParse(d, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date))
                {
                    var day = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
                    if (!info.AirDate.HasValue || day < info.AirDate.Value)
                    {
                        info.AirDate = day;
                    }
                }

                info.ImdbId ??= Val(b, "imdb") is string i && i.StartsWith("tt", StringComparison.Ordinal) ? i : null;
                info.Name ??= Val(b, "title");
            }

            return dict;
        });

    // --------------------------------------------------------------- helpers

    private enum Source
    {
        TvMaze,
        Tmdb,
        Trakt,
        Omdb,
        Wikidata
    }

    private static string KeyOf(SeriesKey s)
        => s.TvMazeId is not null ? "tvmaze:" + s.TvMazeId
            : s.ImdbId is not null ? "imdb:" + s.ImdbId
            : s.TvdbId is not null ? "tvdb:" + s.TvdbId
            : s.TmdbId is not null ? "tmdb:" + s.TmdbId
            : "name:" + MetadataHeuristics.NormalizeTitle(s.Name) + "|" + s.Year;

    /// <summary>Requires the same normalized name, and the same premiere year (±1) if both years are known.</summary>
    private static bool IsSameShow(SeriesKey series, string? candidateName, int? candidateYear)
    {
        var a = MetadataHeuristics.NormalizeTitle(series.Name);
        var b = MetadataHeuristics.NormalizeTitle(candidateName);
        if (a.Length == 0 || a != b)
        {
            return false;
        }

        return series.Year is null || candidateYear is null || Math.Abs(series.Year.Value - candidateYear.Value) <= 1;
    }

    private bool TryCache<T>(string key, out T? value)
    {
        if (_cache.TryGetValue(key, out var entry) && DateTime.UtcNow - entry.At < CacheTime)
        {
            value = (T?)entry.Value;
            return true;
        }

        value = default;
        return false;
    }

    private void Store(string key, object? value) => _cache[key] = (DateTime.UtcNow, value);

    private void StoreIfMissing(string key, object? value) => _cache.TryAdd(key, (DateTime.UtcNow, value));

    private async Task<T?> Cached<T>(string key, Func<Task<T?>> factory)
        where T : class
    {
        if (TryCache(key, out T? cached))
        {
            return cached;
        }

        var value = await factory().ConfigureAwait(false);
        Store(key, value);
        return value;
    }

    private async Task<JsonElement?> GetJsonAsync(string url, Source source, string? key, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (source == Source.TvMaze)
            {
                await ThrottleAsync(ct).ConfigureAwait(false);
            }

            try
            {
                using var client = _httpClientFactory.CreateClient(HttpClientName);
                client.Timeout = TimeSpan.FromSeconds(20);
                var requestUrl = url;
                using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
                request.Headers.TryAddWithoutValidation("User-Agent", "Jellyfin-ImdbAirDateFixer/1.3");
                request.Headers.TryAddWithoutValidation("Accept", "application/json");

                if (source == Source.Tmdb && key is not null)
                {
                    if (key.StartsWith("eyJ", StringComparison.Ordinal))
                    {
                        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
                    }
                    else
                    {
                        request.RequestUri = new Uri(url + (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "api_key=" + Uri.EscapeDataString(key));
                    }
                }
                else if (source == Source.Trakt && key is not null)
                {
                    request.Headers.TryAddWithoutValidation("trakt-api-version", "2");
                    request.Headers.TryAddWithoutValidation("trakt-api-key", key);
                }

                RequestCount++;
                using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }

                if (response.StatusCode == (HttpStatusCode)429 && attempt == 0)
                {
                    _logger.LogDebug("{Prefix} rate limit hit ({Source}), waiting 10 s.", Plugin.LogPrefix, source);
                    await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    {
                        _logger.LogWarning("{Prefix} {Source} rejected the API key ({Status}). Check the key in the plugin settings.", Plugin.LogPrefix, source, (int)response.StatusCode);
                    }
                    else
                    {
                        _logger.LogDebug("{Prefix} {Status} from {Url}", Plugin.LogPrefix, (int)response.StatusCode, Redact(url));
                    }

                    return null;
                }

                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.Clone();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "{Prefix} lookup failed: {Url}", Plugin.LogPrefix, Redact(url));
                return null;
            }
        }

        return null;
    }

    private async Task ThrottleAsync(CancellationToken ct)
    {
        await _throttle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = _lastTvMazeRequest + TvMazeInterval - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }

            _lastTvMazeRequest = DateTime.UtcNow;
        }
        finally
        {
            _throttle.Release();
        }
    }

    private static string Redact(string url)
    {
        var i = url.IndexOf("apikey=", StringComparison.OrdinalIgnoreCase);
        if (i < 0)
        {
            return url;
        }

        var end = url.IndexOf('&', i);
        return url[..(i + 7)] + "***" + (end < 0 ? string.Empty : url[end..]);
    }

    private static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "N/A")
        {
            return null;
        }

        string[] formats = { "yyyy-MM-dd", "dd MMM yyyy", "d MMM yyyy" };
        if (DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d))
        {
            return DateTime.SpecifyKind(d.Date, DateTimeKind.Utc);
        }

        return null;
    }

    private static string? GetString(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int? GetInt(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) ? v : null;

    private sealed class ShowData
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public int? Year { get; set; }

        public string? ImdbId { get; set; }

        public string? TvdbId { get; set; }

        public string? TmdbId { get; set; }

        public bool MatchedByName { get; set; }
    }
}
