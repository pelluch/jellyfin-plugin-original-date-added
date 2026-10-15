using Jellyfin.Plugin.OriginalDateAdded.Arr;
using Jellyfin.Plugin.OriginalDateAdded.Sources;
using Jellyfin.Plugin.OriginalDateAdded.Store;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OriginalDateAdded;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<AddedDateStore>();
        serviceCollection.AddSingleton(services => new AddedDateResolver(
            services.GetRequiredService<RemovedItemsSource>(),
            services.GetRequiredService<ArrSource>(),
            services.GetRequiredService<AddedDateStore>(),
            services.GetRequiredService<ILogger<AddedDateResolver>>()));
        serviceCollection.AddSingleton<AddedDateApplier>();
        serviceCollection.AddSingleton<SourceAvailability>();
        serviceCollection.AddSingleton<ArrSource>();
        serviceCollection.AddSingleton<RemovedItemsSource>();
        serviceCollection.AddHostedService<RemovedItemsRecorder>();
        serviceCollection.AddSingleton<ArrClient>();
        serviceCollection.AddSingleton<ArrTester>();
    }
}
