using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CustomMetadata.Api;
using Jellyfin.Plugin.CustomMetadata.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Providers
{
    /// <summary>
    /// What the two album providers share. Jellyfin makes a PhotoAlbum of a folder that directly holds photos, and a plain Folder
    /// of one that only holds sub-folders: both are albums for the plugin. No cast: Jellyfin keeps none on these items.
    /// </summary>
    public abstract class AlbumProviderBase<T> : IRemoteMetadataProvider<T, ItemLookupInfo>
        where T : Folder, IHasLookupInfo<ItemLookupInfo>, new()
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger _logger;
        private readonly PhotoLibraryService _photos;

        protected AlbumProviderBase(IHttpClientFactory httpClientFactory, ILogger logger, PhotoLibraryService photos)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _photos = photos;
        }

        public string Name => "Custom Metadata Provider";

        public async Task<MetadataResult<T>> GetMetadata(ItemLookupInfo info, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<T>();
            if (!(Plugin.Instance?.Configuration.EnableMetadata ?? true))
            {
                return result;
            }

            try
            {
                var found = await _photos.GetAlbumAsync(info.Path, info.ProviderIds, !info.IsAutomated, cancellationToken);
                if (found is not { } album)
                {
                    return result;
                }

                var data = album.Data;
                var ownName = TagParser.Strip(TagParser.GetOwnName(info.Path ?? string.Empty));
                var item = new T
                {
                    Name = !string.IsNullOrWhiteSpace(data.Title) ? data.Title : (!string.IsNullOrWhiteSpace(ownName) ? ownName : info.Name),
                    Overview = data.Overview,
                    PremiereDate = data.PremiereDate,
                    ProductionYear = data.PremiereDate?.Year,
                    CommunityRating = data.CommunityRating
                };

                // The rules of the "Genres et tags" screen win over what the source gives
                var tags = TaxonomyStore.Tags(data.Tags);
                if (tags.Length > 0) { item.Tags = tags; }
                var genres = TaxonomyStore.Genres(data.Genres);
                if (genres.Length > 0) { item.Genres = genres; }
                if (data.Studios?.Count > 0) { item.Studios = data.Studios.ToArray(); }

                item.ProviderIds["custom"] = album.Tag;
                result.Item = item;
                result.HasMetadata = true;
                _logger.LogInformation("📸 [ALBUM PROVIDER] ✅ Metadata for {Tag}: '{Name}'", album.Tag, item.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "📸 [ALBUM PROVIDER] ❌ Error enriching {Name}", info.Name);
            }

            return result;
        }

        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(ItemLookupInfo searchInfo, CancellationToken cancellationToken)
            => Task.FromResult(Enumerable.Empty<RemoteSearchResult>());

        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
            => _httpClientFactory.CreateClient().GetAsync(url, cancellationToken);
    }

    /// <summary>An album folder that directly holds photos</summary>
    public class CustomPhotoAlbumProvider : AlbumProviderBase<PhotoAlbum>
    {
        public CustomPhotoAlbumProvider(IHttpClientFactory httpClientFactory, ILogger<CustomPhotoAlbumProvider> logger, PhotoLibraryService photos)
            : base(httpClientFactory, logger, photos)
        {
        }
    }

    /// <summary>An album folder that only holds sub-folders: a plain Folder for Jellyfin</summary>
    public class CustomFolderProvider : AlbumProviderBase<Folder>
    {
        public CustomFolderProvider(IHttpClientFactory httpClientFactory, ILogger<CustomFolderProvider> logger, PhotoLibraryService photos)
            : base(httpClientFactory, logger, photos)
        {
        }
    }
}
