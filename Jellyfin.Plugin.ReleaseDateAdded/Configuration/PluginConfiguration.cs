using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ReleaseDateAdded.Configuration;

/// <summary>
/// Inställningar för pluginet. Ändras under Dashboard → Plugins → Release Date Added.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Huvudbrytare. Sätts till false av återställningsuppgiften.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gäller filmer.</summary>
    public bool EnableMovies { get; set; } = true;

    /// <summary>Gäller TV-avsnitt.</summary>
    public bool EnableEpisodes { get; set; } = true;

    /// <summary>Gäller musikvideor.</summary>
    public bool EnableMusicVideos { get; set; } = true;

    /// <summary>Uppdatera direkt när Jellyfin lägger till/uppdaterar ett objekt (annars bara via schemalagd uppgift).</summary>
    public bool EnableRealtime { get; set; } = true;

    /// <summary>
    /// Behåll klockslaget från när filen lades till (releasedatum + klockslag).
    /// Gör att avsnitt som släpps samma dag sorteras i den ordning de laddades ner.
    /// </summary>
    public bool KeepTimeOfDay { get; set; } = true;

    /// <summary>Uppdatera seriens "Date episode added" (DateLastMediaAdded) så den följer avsnittens nya datum.</summary>
    public bool UpdateSeriesDateLastMediaAdded { get; set; } = true;

    /// <summary>Hoppa över objekt som är låsta i metadata-redigeraren.</summary>
    public bool SkipLockedItems { get; set; } = true;

    /// <summary>Skriv även nytt datum till NFO-filer (om NFO-sparning är påslaget för biblioteket).</summary>
    public bool UpdateNfoFiles { get; set; }

    /// <summary>Testläge: logga bara vad som skulle ändras.</summary>
    public bool DryRun { get; set; }
}
