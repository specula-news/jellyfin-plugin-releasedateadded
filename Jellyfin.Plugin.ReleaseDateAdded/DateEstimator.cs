using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.ReleaseDateAdded;

/// <summary>Säsong, avsnittsnummer och sändningsdatum för ett avsnitt i samma serie.</summary>
public readonly record struct EpisodeDate(int? Season, int? Number, DateTime? Date);

/// <summary>
/// Uppskattar ett rimligt releasedatum när metadata saknas helt (ren logik, testbar).
/// Målet: gamla avsnitt/filmer utan metadata ska inte hamna först i Recently Added.
/// </summary>
public static class DateEstimator
{
    /// <summary>
    /// Uppskattar ett avsnitts datum utifrån övriga avsnitt i serien.
    /// </summary>
    /// <returns>Datum och en kort förklaring, eller (null, null) om inget går att säga – då behandlas avsnittet som nytt.</returns>
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
                    return (prev.Date, $"samma datum som avsnitt {s}x{prev.Number} (föregående)");
                }

                var next = dated.Where(x => x.Season == s && x.Number > n).OrderBy(x => x.Number).FirstOrDefault();
                if (next.Date.HasValue)
                {
                    return (next.Date, $"samma datum som avsnitt {s}x{next.Number} (nästa)");
                }
            }

            if (seasonPremiere.HasValue && seasonPremiere.Value <= utcNow)
            {
                return (seasonPremiere, $"säsong {s}:s premiärdatum");
            }

            var laterSeasonDated = dated.Any(x => x.Season > s);
            if (laterSeasonDated || seriesEnded)
            {
                var earlier = dated.Where(x => x.Season > 0 && x.Season < s).Select(x => x.Date).Max();
                if (earlier.HasValue)
                {
                    return (earlier, "senaste kända avsnitt i tidigare säsong");
                }

                if (laterSeasonDated)
                {
                    return (dated.Where(x => x.Season > s).Select(x => x.Date).Min(), "första kända avsnitt i senare säsong");
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
                return (end, "serien är avslutad");
            }
        }

        return (null, null);
    }

    /// <summary>1 januari utgivningsåret, men bara för år före innevarande (innevarande år räknas som nytt).</summary>
    public static DateTime? FromYear(int? year, DateTime utcNow)
    {
        if (year is int y && y >= 1900 && y < utcNow.Year)
        {
            return new DateTime(y, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        }

        return null;
    }
}
