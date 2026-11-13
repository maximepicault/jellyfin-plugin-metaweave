using System.Net.Http;
using Jellyfin.Plugin.CustomMetadata.Api;
using Jellyfin.Plugin.CustomMetadata.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata
{
    /// <summary>
    /// Registers the plugin's services in Jellyfin's DI container (discovered automatically).
    /// One single cache / API client / resolver instance for the whole plugin: the providers,
    /// the scheduled task and the controller receive them by injection instead of each creating their own.
    /// The helpers take a non-generic ILogger: each gets the typed logger of its own class
    /// (log category = CustomApiClient, MetadataCacheService…); the emoji tags stay the same.
    /// </summary>
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.AddSingleton(sp => new MetadataCacheService(
                sp.GetRequiredService<IMemoryCache>(),
                sp.GetRequiredService<ILogger<MetadataCacheService>>()));

            serviceCollection.AddSingleton(sp => new LocalSourceStore(
                sp.GetRequiredService<ILogger<LocalSourceStore>>(),
                () => sp.GetRequiredService<PeopleStore>()));

            serviceCollection.AddSingleton(sp => new PeopleStore(
                sp.GetRequiredService<ILogger<PeopleStore>>(),
                sp.GetRequiredService<LocalSourceStore>(),
                () => sp.GetRequiredService<IServerApplicationHost>().GetLocalApiUrl("127.0.0.1")));

            serviceCollection.AddSingleton(sp => new CustomApiClient(
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<ILogger<CustomApiClient>>(),
                sp.GetRequiredService<MetadataCacheService>(),
                sp.GetRequiredService<LocalSourceStore>()));

            serviceCollection.AddSingleton(sp => new SourceResolver(
                sp.GetRequiredService<ILogger<SourceResolver>>()));

            serviceCollection.AddSingleton(sp => new CastOverlayStore(
                sp.GetRequiredService<ILogger<CastOverlayStore>>()));

            serviceCollection.AddSingleton<CastCatalog>();
            serviceCollection.AddSingleton<CastApplyService>();

            serviceCollection.AddSingleton<MissingEpisodeService>();
        }
    }
}
