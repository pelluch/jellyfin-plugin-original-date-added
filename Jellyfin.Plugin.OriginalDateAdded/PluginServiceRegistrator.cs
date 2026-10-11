using Jellyfin.Plugin.OriginalDateAdded.Sources;
using Jellyfin.Plugin.OriginalDateAdded.Store;
using Jellyfin.Plugin.OriginalDateAdded.Tracearr;
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
        serviceCollection.AddSingleton<IAddedDateSource, TracearrSource>();
        serviceCollection.AddHostedService<RemovedItemsRecorder>();
        serviceCollection.AddSingleton<TracearrClient>();
    }
}
