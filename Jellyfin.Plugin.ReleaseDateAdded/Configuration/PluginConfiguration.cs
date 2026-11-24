using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ReleaseDateAdded.Configuration;

/// <summary>
/// Plugin settings. Edited under Dashboard → Plugins → IMDb Air Date Fixer.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Master switch. Turned off automatically by the restore task.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Apply to movies.</summary>
    public bool EnableMovies { get; set; } = true;

    /// <summary>Apply to TV episodes.</summary>
    public bool EnableEpisodes { get; set; } = true;

    /// <summary>Apply to music videos.</summary>
    public bool EnableMusicVideos { get; set; } = true;

    /// <summary>Apply to other videos (mixed content / home video libraries). Extras are never touched.</summary>
    public bool EnableOtherVideos { get; set; } = true;

    /// <summary>Look up missing air dates/titles via the series' IMDb/TVDb ID on TVmaze (free, no key).</summary>
    public bool EnableOnlineLookup { get; set; } = true;

    /// <summary>Optional OMDb API key (IMDb data). Free at omdbapi.com. Empty = not used.</summary>
    public string OmdbApiKey { get; set; } = string.Empty;

    /// <summary>Optional TMDb API key or read access token. Free at themoviedb.org. Empty = not used.</summary>
    public string TmdbApiKey { get; set; } = string.Empty;

    /// <summary>Optional Trakt client ID. Free at trakt.tv/oauth/applications. Empty = not used.</summary>
    public string TraktClientId { get; set; } = string.Empty;

    /// <summary>Use Wikidata (free, no key) to find air dates via the series' IMDb ID.</summary>
    public bool EnableWikidata { get; set; } = true;

    /// <summary>
    /// Ask Jellyfin to search again for missing metadata (like "Refresh metadata → Search for missing metadata")
    /// for series/movies that still lack air dates, using all metadata providers Jellyfin has.
    /// </summary>
    public bool AskJellyfinToSearchAgain { get; set; } = true;

    /// <summary>Install new versions of this plugin automatically (Jellyfin then asks for a restart).</summary>
    public bool AutoInstallUpdates { get; set; } = true;

    /// <summary>Save found metadata (air date, title, overview, IMDb ID) on the episode, not just use the date for sorting.</summary>
    public bool FillMissingMetadata { get; set; } = true;

    /// <summary>
    /// Estimate a date when none is found: nearest episode in the season, season premiere,
    /// end date of ended series, or the release year for movies.
    /// </summary>
    public bool EstimateMissingDates { get; set; } = true;

    /// <summary>
    /// Items without any date at all get January 1st of their own year or the series' start year
    /// (or <see cref="FallbackYear"/>), so they never end up first in Recently Added.
    /// </summary>
    public bool PushUndatedItemsDown { get; set; } = true;

    /// <summary>Year used when an undated item has no year at all.</summary>
    public int FallbackYear { get; set; } = 2000;

    /// <summary>React immediately when Jellyfin adds/updates an item (otherwise only via the scheduled task).</summary>
    public bool EnableRealtime { get; set; } = true;

    /// <summary>
    /// Keep the time of day from when the file was added (release date + time of day),
    /// so episodes released on the same day are ordered by download time.
    /// </summary>
    public bool KeepTimeOfDay { get; set; } = true;

    /// <summary>Update the series' "Date episode added" (DateLastMediaAdded) to follow the episodes' new dates.</summary>
    public bool UpdateSeriesDateLastMediaAdded { get; set; } = true;

    /// <summary>Skip items that are locked in the metadata editor.</summary>
    public bool SkipLockedItems { get; set; } = true;

    /// <summary>Also write the new date to NFO files (if NFO saving is enabled for the library).</summary>
    public bool UpdateNfoFiles { get; set; }

    /// <summary>Dry run: only log what would change.</summary>
    public bool DryRun { get; set; }
}
