using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ReleaseDateAdded.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.ReleaseDateAdded;

/// <summary>
/// Sets "Date added" (DateCreated) to the air/release date (PremiereDate) so that
/// "Recently Added" is ordered by release instead of file creation time, and fills in
/// missing episode air dates and titles using the series' IMDb/TVDb ID.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Plugin ID. Same as in meta.json and configPage.html. Kept from 1.0 so updates work.</summary>
    public const string PluginGuid = "28bec86d-d278-4b70-8a9e-20934095496c";

    /// <summary>Short name used as a prefix in log messages.</summary>
    public const string LogPrefix = "IMDb Air Date Fixer:";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "IMDb Air Date Fixer";

    public override Guid Id => Guid.Parse(PluginGuid);

    public override string Description =>
        "Orders Recently Added by air/release date and fills in missing episode air dates and titles via IMDb IDs.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "ImdbAirDateFixer",
                DisplayName = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
            }
        };
    }
}
