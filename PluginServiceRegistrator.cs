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
                () => sp.GetRequiredService<PeopleStore>(),
                () => sp.GetRequiredService<IServerApplicationHost>().GetLocalApiUrl("127.0.0.1")));

            serviceCollection.AddSingleton(sp => new PeopleStore(
                sp.GetRequiredService<ILogger<PeopleStore>>(),
                sp.GetRequiredService<LocalSourceStore>(),
                () => sp.GetRequiredService<IServerApplicationHost>().GetLocalApiUrl("127.0.0.1")));

            // Projection theme: adds its script to jellyfin-web's page when EnableThemeScript is on (ThemeScriptMiddleware)
            serviceCollection.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter, ThemeScriptStartupFilter>();

            serviceCollection.AddSingleton<PersonNetworkStore>();
            // Lets the person-network link providers answer their real names once Jellyfin has sorted them (see PersonNetworkUrlProvider.Ready)
            serviceCollection.AddHostedService<Providers.PersonNetworkReadyService>();

            // "Dump" sources: one catalogue downloaded once, served entry by entry and matched against file names
            serviceCollection.AddSingleton(sp => new DumpStore(
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<ILogger<DumpStore>>()));

            serviceCollection.AddSingleton(sp => new CustomApiClient(
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<ILogger<CustomApiClient>>(),
                sp.GetRequiredService<MetadataCacheService>(),
                sp.GetRequiredService<LocalSourceStore>(),
                sp.GetRequiredService<DumpStore>()));

            serviceCollection.AddSingleton(sp => new SourceResolver(
                sp.GetRequiredService<ILogger<SourceResolver>>()));

            // Rules on genres and tags: also reachable as TaxonomyStore.Current by the providers and static helpers
            serviceCollection.AddSingleton(sp => new TaxonomyStore(sp.GetRequiredService<ILogger<TaxonomyStore>>()));
            serviceCollection.AddHostedService(sp => sp.GetRequiredService<TaxonomyStore>());

            serviceCollection.AddSingleton(sp => new CastOverlayStore(
                sp.GetRequiredService<ILogger<CastOverlayStore>>()));

            serviceCollection.AddSingleton(sp => new SourceRefreshService(
                sp.GetRequiredService<MediaBrowser.Controller.Library.ILibraryManager>(),
                sp.GetRequiredService<MediaBrowser.Model.IO.IFileSystem>(),
                sp.GetRequiredService<ILogger<SourceRefreshService>>(),
                sp.GetRequiredService<CustomApiClient>(),
                sp.GetRequiredService<SourceResolver>(),
                sp.GetRequiredService<MediaBrowser.Controller.Providers.IProviderManager>(),
                sp.GetRequiredService<DumpStore>()));

            serviceCollection.AddSingleton<CastCatalog>();
            serviceCollection.AddSingleton<CastInheritance>();
            serviceCollection.AddSingleton<CastApplyService>();

            // Source option "download the videos": a background queue, one download at a time
            serviceCollection.AddSingleton(sp => new EpisodePositionService(
                sp.GetRequiredService<CustomApiClient>(),
                sp.GetRequiredService<SourceResolver>(),
                sp.GetRequiredService<ILogger<EpisodePositionService>>()));
            // Preview video of a real episode (its source's preview.url), for the theme's hover preview (PreviewController)
            serviceCollection.AddSingleton(sp => new EpisodePreviewService(
                sp.GetRequiredService<CustomApiClient>(),
                sp.GetRequiredService<SourceResolver>(),
                sp.GetRequiredService<ILogger<EpisodePreviewService>>()));
            serviceCollection.AddSingleton<VideoDownloadService>();
            serviceCollection.AddHostedService(sp => sp.GetRequiredService<VideoDownloadService>());

            serviceCollection.AddSingleton<MissingEpisodeService>();
            serviceCollection.AddSingleton<TaxonomyService>();
            // Applies the rules to a managed item each time it is saved
            serviceCollection.AddHostedService(sp => sp.GetRequiredService<TaxonomyService>());
            // Saves the chapters of episodes that have a file once their refresh is over
            serviceCollection.AddHostedService<ChapterService>();
            // Keeps the dump coverage screen's results until the dump, its settings or the library change
            serviceCollection.AddSingleton<DumpCoverageCache>();
            serviceCollection.AddHostedService(sp => sp.GetRequiredService<DumpCoverageCache>());
            serviceCollection.AddSingleton<DumpCoverageService>();
            serviceCollection.AddSingleton<DiagnosticService>();
        }
    }
}
