using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.ReleaseDateAdded;

/// <summary>Season, episode number and air date of an episode in the same series.</summary>
public readonly record struct EpisodeDate(int? Season, int? Number, DateTime? Date);

/// <summary>
/// Estimates a sensible release date when metadata is missing (pure, testable logic).
/// Goal: old episodes/movies without metadata must not end up first in Recently Added.
/// </summary>
public static class DateEstimator
{
    /// <summary>
    /// Estimates an episode's date from the other episodes of the series.
    /// </summary>
    /// <returns>Date and a short explanation, or (null, null) if nothing can be said.</returns>
    public static (DateTime? Date, string? Reason) EstimateEpisode(
        int? season,
        int? number,
        IReadOnlyCollection<EpisodeDate> siblings,
        DateTime? seasonPremiere,
        bool seriesEnded,
        DateTime? seriesEnd,
        DateTime? seriesPremiere,
        int? seriesYear,
        DateTime utcNow)
    {
        var dated = siblings.Where(x => x.Date.HasValue && x.Date.Value <= utcNow).ToList();

        if (season is int s && s > 0)
        {
            if (number is int n)
            {
                var prev = dated.Where(x => x.Season == s && x.Number < n).OrderByDescending(x => x.Number).FirstOrDefault();
                if (prev.Date.HasValue)
                {
                    return (prev.Date, $"same date as episode {s}x{prev.Number} (previous)");
                }

                var next = dated.Where(x => x.Season == s && x.Number > n).OrderBy(x => x.Number).FirstOrDefault();
                if (next.Date.HasValue)
                {
                    return (next.Date, $"same date as episode {s}x{next.Number} (next)");
                }
            }

            if (seasonPremiere.HasValue && seasonPremiere.Value <= utcNow)
            {
                return (seasonPremiere, $"season {s} premiere date");
            }

            var laterSeasonDated = dated.Any(x => x.Season > s);
            if (laterSeasonDated || seriesEnded)
            {
                var earlier = dated.Where(x => x.Season > 0 && x.Season < s).Select(x => x.Date).Max();
                if (earlier.HasValue)
                {
                    return (earlier, "latest known episode of an earlier season");
                }

                if (laterSeasonDated)
                {
                    return (dated.Where(x => x.Season > s).Select(x => x.Date).Min(), "first known episode of a later season");
                }
            }
        }

        if (seriesEnded)
        {
            var end = seriesEnd
                      ?? dated.Select(x => x.Date).Max()
                      ?? seriesPremiere
                      ?? FromYear(seriesYear, utcNow);
            if (end.HasValue && end.Value <= utcNow)
            {
                return (end, "series has ended");
            }
        }

        return (null, null);
    }

    /// <summary>January 1st of the release year, but only for years before the current one.</summary>
    public static DateTime? FromYear(int? year, DateTime utcNow)
    {
        if (year is int y && y >= 1900 && y < utcNow.Year)
        {
            return new DateTime(y, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        }

        return null;
    }

    /// <summary>
    /// Last resort for items with no date at all: January 1st of the item's own year, otherwise the series'
    /// start year, otherwise <paramref name="fallbackYear"/>. Never in the future.
    /// </summary>
    public static (DateTime Date, string Reason) Fallback(int? itemYear, int? seriesYear, int fallbackYear, DateTime utcNow)
    {
        static bool Valid(int? y, DateTime now) => y is int v && v >= 1900 && v <= now.Year;

        if (Valid(itemYear, utcNow))
        {
            return (new DateTime(itemYear!.Value, 1, 1, 0, 0, 0, DateTimeKind.Utc), $"no date found, using its year {itemYear}");
        }

        if (Valid(seriesYear, utcNow))
        {
            return (new DateTime(seriesYear!.Value, 1, 1, 0, 0, 0, DateTimeKind.Utc), $"no date found, using the series' start year {seriesYear}");
        }

        var y = Valid(fallbackYear, utcNow) ? fallbackYear : 2000;
        return (new DateTime(y, 1, 1, 0, 0, 0, DateTimeKind.Utc), $"no date or year found, using {y}");
    }
}
