using Jellyfin.Plugin.OriginalDateAdded.Sources;
using Jellyfin.Plugin.OriginalDateAdded.Store;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.OriginalDateAdded;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<AddedDateStore>();
        serviceCollection.AddSingleton<AddedDateResolver>();
        serviceCollection.AddSingleton<IAddedDateSource, RemovedItemsSource>();
        serviceCollection.AddHostedService<RemovedItemsRecorder>();
    }
}
