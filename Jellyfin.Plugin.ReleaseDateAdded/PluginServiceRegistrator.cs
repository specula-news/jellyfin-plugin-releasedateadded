using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ReleaseDateAdded;

/// <summary>
/// Registrerar pluginets tjänster i Jellyfin.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<OnlineEpisodeLookup>();
        serviceCollection.AddSingleton<ReleaseDateService>();
        serviceCollection.AddHostedService<LibraryWatcher>();
    }
}
