using Jellyfin.Plugin.OriginalDateAdded.Arr;
using Jellyfin.Plugin.OriginalDateAdded.Jellystat;
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
        serviceCollection.AddSingleton<ServerSetup>();
        serviceCollection.AddSingleton<AddedDateResolver>();
        serviceCollection.AddSingleton<AddedDateApplier>();
        // Order matters here. Sources defined first run first.
        serviceCollection.AddSingleton<IAddedDateSource, ArrSource>();
        serviceCollection.AddSingleton<IAddedDateSource, RemovedItemsSource>();
        serviceCollection.AddSingleton<IAddedDateSource, TracearrSource>();
        serviceCollection.AddSingleton<IAddedDateSource, JellystatSource>();
        serviceCollection.AddHostedService<RemovedItemsRecorder>();
        serviceCollection.AddSingleton<TracearrClient>();
        serviceCollection.AddSingleton<JellystatClient>();
        serviceCollection.AddSingleton<ArrClient>();
        serviceCollection.AddSingleton<ArrTester>();
    }
}
