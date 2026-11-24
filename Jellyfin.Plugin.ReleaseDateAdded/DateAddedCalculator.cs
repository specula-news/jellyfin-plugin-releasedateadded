using System;

namespace Jellyfin.Plugin.ReleaseDateAdded;

/// <summary>
/// Pure logic (no Jellyfin dependencies) that computes the new "Date added".
/// </summary>
public static class DateAddedCalculator
{
    /// <summary>Differences smaller than this count as "already correct".</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(1);

    private static readonly DateTime MinimumSaneDate = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Computes the new DateCreated from the release date.
    /// </summary>
    /// <param name="currentDateAdded">Current DateCreated.</param>
    /// <param name="releaseDate">Air/release date (PremiereDate, looked up, estimated or fallback).</param>
    /// <param name="utcNow">Current time (UTC).</param>
    /// <param name="keepTimeOfDay">Keep the time of day from the current DateCreated if the release date has no time.</param>
    /// <returns>The new date, or <c>null</c> if nothing should change.</returns>
    public static DateTime? CalculateNewDateAdded(DateTime currentDateAdded, DateTime? releaseDate, DateTime utcNow, bool keepTimeOfDay)
    {
        if (!releaseDate.HasValue)
        {
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
            // Not released yet according to the metadata (time zones / early releases).
            // Keep Jellyfin's date (= just added) until the release date has passed.
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
