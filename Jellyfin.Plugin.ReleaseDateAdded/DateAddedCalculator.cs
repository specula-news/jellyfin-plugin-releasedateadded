using System;

namespace Jellyfin.Plugin.ReleaseDateAdded;

/// <summary>
/// Ren logik (utan Jellyfin-beroenden) för att räkna fram nytt "Date added".
/// </summary>
public static class DateAddedCalculator
{
    /// <summary>Skillnader mindre än detta räknas som "redan rätt".</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(1);

    private static readonly DateTime MinimumSaneDate = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Räknar fram nytt DateCreated utifrån releasedatum.
    /// </summary>
    /// <param name="currentDateAdded">Nuvarande DateCreated.</param>
    /// <param name="releaseDate">PremiereDate från metadata (TMDb/IMDb/TVDb/NFO).</param>
    /// <param name="utcNow">Aktuell tid (UTC).</param>
    /// <param name="keepTimeOfDay">Behåll klockslaget från nuvarande DateCreated om releasedatumet saknar klockslag.</param>
    /// <returns>Nytt datum, eller <c>null</c> om inget ska ändras.</returns>
    public static DateTime? CalculateNewDateAdded(DateTime currentDateAdded, DateTime? releaseDate, DateTime utcNow, bool keepTimeOfDay)
    {
        if (!releaseDate.HasValue)
        {
            // Inget releasedatum i metadatan → låt Jellyfins datum vara.
            return null;
        }

        var release = AsUtc(releaseDate.Value);
        var now = AsUtc(utcNow);

        if (release < MinimumSaneDate)
        {
            return null;
        }

        if (release > now)
        {
            // Enligt metadatan är det inte släppt än (tidszoner/tidiga släpp).
            // Behåll Jellyfins datum (= nyss tillagt) tills releasedatumet passerat.
            return null;
        }

        var current = AsUtc(currentDateAdded);
        var target = release;

        if (keepTimeOfDay && release.TimeOfDay == TimeSpan.Zero && current >= MinimumSaneDate)
        {
            target = release.Date + current.TimeOfDay;
            if (target > now)
            {
                target = now;
            }
        }

        target = DateTime.SpecifyKind(target, DateTimeKind.Utc);

        if ((target - current).Duration() < Tolerance)
        {
            return null;
        }

        return target;
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
