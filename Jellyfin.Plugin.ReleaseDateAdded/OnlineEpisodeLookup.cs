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

/// <summary>Uppgifter om ett avsnitt från TVmaze/OMDb.</summary>
public sealed class EpisodeInfo
{
    public DateTime? AirDate { get; set; }

    public string? Name { get; set; }

    public string? Overview { get; set; }

    public string? ImdbId { get; set; }

    public string? TvMazeId { get; set; }

    public string Source { get; set; } = string.Empty;
}

/// <summary>Resultat av ett uppslag.</summary>
public sealed class LookupResult
{
    public EpisodeInfo? Episode { get; set; }

    /// <summary>Förklaring om inget hittades (hamnar i rapporten).</summary>
    public string? Problem { get; set; }

    public int? TvMazeShowId { get; set; }

    public string? ShowName { get; set; }

    public string? ShowImdbId { get; set; }

    public string? ShowTvdbId { get; set; }

    /// <summary>Serien hittades via namnsökning (hade inga ID:n).</summary>
    public bool MatchedByName { get; set; }
}

/// <summary>Seriedata som behövs för uppslag (frikopplat från Jellyfins Series-klass för testbarhet).</summary>
public sealed record SeriesKey(string Name, int? Year, string? ImdbId, string? TvdbId, string? TvMazeId);

/// <summary>
/// Slår upp saknade sändningsdatum/titlar via seriens IMDb-/TVDb-ID hos TVmaze (gratis, ingen nyckel)
/// och – om en API-nyckel är angiven – OMDb (IMDb-data).
/// </summary>
public sealed class OnlineEpisodeLookup
{
    /// <summary>Namn på HttpClient (kan bytas ut i tester).</summary>
    public const string HttpClientName = "AirDateFixer";

    private const string TvMaze = "https://api.tvmaze.com";
    private const string Omdb = "https://www.omdbapi.com/";
    private static readonly TimeSpan CacheTime = TimeSpan.FromHours(6);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OnlineEpisodeLookup> _logger;
    private readonly SemaphoreSlim _throttle = new(1, 1);
    private readonly ConcurrentDictionary<string, (DateTime At, ShowData? Show)> _shows = new();
    private readonly ConcurrentDictionary<int, (DateTime At, Dictionary<(int, int), EpisodeInfo>? Episodes)> _episodes = new();
    private readonly ConcurrentDictionary<string, (DateTime At, Dictionary<int, EpisodeInfo>? Episodes)> _omdbSeasons = new();
    private DateTime _lastTvMazeRequest = DateTime.MinValue;

    public OnlineEpisodeLookup(IHttpClientFactory httpClientFactory, ILogger<OnlineEpisodeLookup> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>Minsta tid mellan anrop till TVmaze (de tillåter ca 20 anrop per 10 s).</summary>
    public TimeSpan TvMazeInterval { get; set; } = TimeSpan.FromMilliseconds(600);

    /// <summary>Antal HTTP-anrop (för loggning/tester).</summary>
    public int RequestCount { get; private set; }

    /// <summary>Slår upp ett avsnitt.</summary>
    public async Task<LookupResult> FindEpisodeAsync(SeriesKey series, int season, int number, string? omdbApiKey, CancellationToken cancellationToken)
    {
        var result = new LookupResult();
        var show = await FindShowAsync(series, cancellationToken).ConfigureAwait(false);

        if (show is not null)
        {
            result.TvMazeShowId = show.Id;
            result.ShowName = show.Name;
            result.ShowImdbId = show.ImdbId;
            result.ShowTvdbId = show.TvdbId;
            result.MatchedByName = show.MatchedByName;

            var episodes = await GetTvMazeEpisodesAsync(show.Id, cancellationToken).ConfigureAwait(false);
            if (episodes is not null && episodes.TryGetValue((season, number), out var ep))
            {
                result.Episode = ep;
            }
            else if (episodes is not null && episodes.Count > 0)
            {
                var seasons = episodes.Keys.Select(k => k.Item1).Where(s => s > 0).Distinct().OrderBy(s => s).ToList();
                result.Problem = seasons.Contains(season)
                    ? $"S{season:00}E{number:00} finns inte hos TVmaze för \"{show.Name}\" (säsongen har {episodes.Keys.Count(k => k.Item1 == season)} avsnitt) – kontrollera numreringen"
                    : $"säsong {season} finns inte hos TVmaze för \"{show.Name}\" (har säsong {Describe(seasons)}) – ligger filen i fel serie eller är numreringen annorlunda?";
            }
        }
        else
        {
            result.Problem = $"hittade inte serien \"{series.Name}\" hos TVmaze (saknar IMDb-/TVDb-ID eller okänt namn)";
        }

        // OMDb (IMDb-data) som komplement om nyckel finns.
        var imdbSeriesId = series.ImdbId ?? show?.ImdbId;
        if (!string.IsNullOrWhiteSpace(omdbApiKey) && !string.IsNullOrWhiteSpace(imdbSeriesId)
            && (result.Episode?.AirDate is null || result.Episode.ImdbId is null))
        {
            var omdbSeason = await GetOmdbSeasonAsync(omdbApiKey!, imdbSeriesId!, season, cancellationToken).ConfigureAwait(false);
            if (omdbSeason is not null && omdbSeason.TryGetValue(number, out var omdbEp))
            {
                if (result.Episode is null)
                {
                    result.Episode = omdbEp;
                    result.Problem = null;
                }
                else
                {
                    result.Episode.AirDate ??= omdbEp.AirDate;
                    result.Episode.ImdbId ??= omdbEp.ImdbId;
                    if (MetadataHeuristics.IsPlaceholderTitle(result.Episode.Name) && !string.IsNullOrWhiteSpace(omdbEp.Name))
                    {
                        result.Episode.Name = omdbEp.Name;
                    }

                    result.Episode.Source += "+OMDb";
                }
            }
        }

        return result;
    }

    private static string Describe(List<int> seasons)
        => seasons.Count == 0 ? "inga" : seasons.Count == 1 ? seasons[0].ToString(CultureInfo.InvariantCulture) : $"{seasons[0]}–{seasons[^1]}";

    private async Task<ShowData?> FindShowAsync(SeriesKey series, CancellationToken ct)
    {
        var cacheKey = series.TvMazeId is not null ? "tvmaze:" + series.TvMazeId
            : series.ImdbId is not null ? "imdb:" + series.ImdbId
            : series.TvdbId is not null ? "tvdb:" + series.TvdbId
            : "name:" + MetadataHeuristics.NormalizeTitle(series.Name) + "|" + series.Year;

        if (_shows.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.At < CacheTime)
        {
            return cached.Show;
        }

        ShowData? show = null;
        if (series.TvMazeId is not null && int.TryParse(series.TvMazeId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tvMazeId))
        {
            show = ParseShow(await GetJsonAsync($"{TvMaze}/shows/{tvMazeId}", true, ct).ConfigureAwait(false));
        }

        if (show is null && series.ImdbId is not null)
        {
            show = ParseShow(await GetJsonAsync($"{TvMaze}/lookup/shows?imdb={Uri.EscapeDataString(series.ImdbId)}", true, ct).ConfigureAwait(false));
        }

        if (show is null && series.TvdbId is not null)
        {
            show = ParseShow(await GetJsonAsync($"{TvMaze}/lookup/shows?thetvdb={Uri.EscapeDataString(series.TvdbId)}", true, ct).ConfigureAwait(false));
        }

        if (show is null && !string.IsNullOrWhiteSpace(series.Name))
        {
            var candidate = ParseShow(await GetJsonAsync($"{TvMaze}/singlesearch/shows?q={Uri.EscapeDataString(series.Name)}", true, ct).ConfigureAwait(false));
            if (candidate is not null && IsSameShow(series, candidate))
            {
                candidate.MatchedByName = true;
                show = candidate;
            }
            else if (candidate is not null)
            {
                _logger.LogDebug("Air Date Fixer: namnsökning på \"{Name}\" gav \"{Found}\" ({Year}) – godtas inte.", series.Name, candidate.Name, candidate.PremieredYear);
            }
        }

        var at = DateTime.UtcNow;
        _shows[cacheKey] = (at, show);
        if (show is not null)
        {
            // Samma serie nås även via sina andra ID:n (som pluginet kan ha sparat på serien).
            _shows.TryAdd("tvmaze:" + show.Id.ToString(CultureInfo.InvariantCulture), (at, show));
            if (show.ImdbId is not null)
            {
                _shows.TryAdd("imdb:" + show.ImdbId, (at, show));
            }

            if (show.TvdbId is not null)
            {
                _shows.TryAdd("tvdb:" + show.TvdbId, (at, show));
            }
        }

        return show;
    }

    /// <summary>Kräver samma normaliserade namn, och samma premiärår (±1) om året är känt.</summary>
    private static bool IsSameShow(SeriesKey series, ShowData candidate)
    {
        var a = MetadataHeuristics.NormalizeTitle(series.Name);
        var b = MetadataHeuristics.NormalizeTitle(candidate.Name);
        if (a.Length == 0 || a != b)
        {
            return false;
        }

        return series.Year is null || candidate.PremieredYear is null || Math.Abs(series.Year.Value - candidate.PremieredYear.Value) <= 1;
    }

    private async Task<Dictionary<(int, int), EpisodeInfo>?> GetTvMazeEpisodesAsync(int showId, CancellationToken ct)
    {
        if (_episodes.TryGetValue(showId, out var cached) && DateTime.UtcNow - cached.At < CacheTime)
        {
            return cached.Episodes;
        }

        Dictionary<(int, int), EpisodeInfo>? result = null;
        var json = await GetJsonAsync($"{TvMaze}/shows/{showId}/episodes", true, ct).ConfigureAwait(false);
        if (json is JsonElement arr && arr.ValueKind == JsonValueKind.Array)
        {
            result = new Dictionary<(int, int), EpisodeInfo>();
            foreach (var e in arr.EnumerateArray())
            {
                var season = GetInt(e, "season");
                var number = GetInt(e, "number");
                if (season is null || number is null)
                {
                    continue;
                }

                result[(season.Value, number.Value)] = new EpisodeInfo
                {
                    AirDate = ParseDate(GetString(e, "airdate")),
                    Name = GetString(e, "name"),
                    Overview = MetadataHeuristics.StripHtml(GetString(e, "summary")),
                    TvMazeId = GetInt(e, "id")?.ToString(CultureInfo.InvariantCulture),
                    Source = "TVmaze"
                };
            }
        }

        _episodes[showId] = (DateTime.UtcNow, result);
        return result;
    }

    private async Task<Dictionary<int, EpisodeInfo>?> GetOmdbSeasonAsync(string apiKey, string imdbSeriesId, int season, CancellationToken ct)
    {
        var key = imdbSeriesId + "|" + season;
        if (_omdbSeasons.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < CacheTime)
        {
            return cached.Episodes;
        }

        Dictionary<int, EpisodeInfo>? result = null;
        var url = $"{Omdb}?apikey={Uri.EscapeDataString(apiKey)}&i={Uri.EscapeDataString(imdbSeriesId)}&Season={season}";
        var json = await GetJsonAsync(url, false, ct).ConfigureAwait(false);
        if (json is JsonElement root && root.ValueKind == JsonValueKind.Object
            && string.Equals(GetString(root, "Response"), "True", StringComparison.OrdinalIgnoreCase)
            && root.TryGetProperty("Episodes", out var eps) && eps.ValueKind == JsonValueKind.Array)
        {
            result = new Dictionary<int, EpisodeInfo>();
            foreach (var e in eps.EnumerateArray())
            {
                if (!int.TryParse(GetString(e, "Episode"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                {
                    continue;
                }

                var imdbId = GetString(e, "imdbID");
                result[number] = new EpisodeInfo
                {
                    AirDate = ParseDate(GetString(e, "Released")),
                    Name = GetString(e, "Title"),
                    ImdbId = imdbId is not null && imdbId.StartsWith("tt", StringComparison.Ordinal) ? imdbId : null,
                    Source = "OMDb/IMDb"
                };
            }
        }
        else if (json is JsonElement err && err.ValueKind == JsonValueKind.Object && GetString(err, "Error") is string error)
        {
            _logger.LogDebug("Air Date Fixer: OMDb svarade \"{Error}\" för {Imdb} säsong {Season}.", error, imdbSeriesId, season);
        }

        _omdbSeasons[key] = (DateTime.UtcNow, result);
        return result;
    }

    private async Task<JsonElement?> GetJsonAsync(string url, bool tvMaze, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (tvMaze)
            {
                await ThrottleAsync(ct).ConfigureAwait(false);
            }

            try
            {
                using var client = _httpClientFactory.CreateClient(HttpClientName);
                client.Timeout = TimeSpan.FromSeconds(20);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", "Jellyfin-AirDateMetadataFixer/1.1");
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                RequestCount++;
                using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }

                if (response.StatusCode == (HttpStatusCode)429 && attempt == 0)
                {
                    _logger.LogDebug("Air Date Fixer: TVmaze begränsar anrop, väntar 10 s.");
                    await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("Air Date Fixer: {Status} från {Url}", (int)response.StatusCode, Redact(url));
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
                _logger.LogDebug(ex, "Air Date Fixer: uppslag misslyckades: {Url}", Redact(url));
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

    private static ShowData? ParseShow(JsonElement? json)
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
            Id = id,
            Name = GetString(e, "name") ?? string.Empty,
            PremieredYear = ParseDate(GetString(e, "premiered"))?.Year,
            ImdbId = imdb,
            TvdbId = tvdb
        };
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
        => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int? GetInt(JsonElement e, string name)
        => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) ? v : null;

    private sealed class ShowData
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public int? PremieredYear { get; set; }

        public string? ImdbId { get; set; }

        public string? TvdbId { get; set; }

        public bool MatchedByName { get; set; }
    }
}
