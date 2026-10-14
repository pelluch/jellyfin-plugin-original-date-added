using Jellyfin.Plugin.OriginalDateAdded.Arr;
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
        serviceCollection.AddSingleton<ServerSetup>();
        serviceCollection.AddSingleton<AddedDateResolver>();
        serviceCollection.AddSingleton<AddedDateApplier>();
        serviceCollection.AddSingleton<SourceAvailability>();
        // Order matters here. Sources defined first run first.
        serviceCollection.AddSingleton<IAddedDateSource, ArrSource>();
        serviceCollection.AddSingleton<IAddedDateSource, RemovedItemsSource>();
        serviceCollection.AddHostedService<RemovedItemsRecorder>();
        serviceCollection.AddSingleton<ArrClient>();
        serviceCollection.AddSingleton<ArrTester>();
    }
}
