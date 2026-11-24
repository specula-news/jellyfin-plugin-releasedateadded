using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ReleaseDateAdded.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.ReleaseDateAdded;

/// <summary>
/// Sätter "Date added" (DateCreated) till releasedatum (PremiereDate) så att
/// "Recently added" sorteras efter när något släpptes, inte när filen skapades.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Plugin-ID. Samma som i meta.json och configPage.html.</summary>
    public const string PluginGuid = "28bec86d-d278-4b70-8a9e-20934095496c";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Air Date and Metadata Fixer";

    public override Guid Id => Guid.Parse(PluginGuid);

    public override string Description =>
        "Sorterar Recently Added efter sändnings-/releasedatum och fyller i saknade sändningsdatum och titlar via IMDb/TVmaze.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "AirDateMetadataFixer",
                DisplayName = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
            }
        };
    }
}
