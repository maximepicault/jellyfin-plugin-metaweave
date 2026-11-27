using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CustomMetadata.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.CustomMetadata.Providers
{
    /// <summary>
    /// The people of an album folder. A custom provider (it has the item, so its id): the album's metadata comes from
    /// CustomPhotoAlbumProvider / CustomFolderProvider, the links to people from here.
    /// </summary>
    public class CustomAlbumCastProvider : ICustomMetadataProvider<PhotoAlbum>, ICustomMetadataProvider<Folder>, IHasOrder, IForcedProvider
    {
        private readonly AlbumCastService _cast;

        public CustomAlbumCastProvider(AlbumCastService cast)
        {
            _cast = cast;
        }

        public string Name => "Custom Metadata Provider";

        public int Order => 1000;

        public Task<ItemUpdateType> FetchAsync(PhotoAlbum item, MetadataRefreshOptions options, CancellationToken cancellationToken) => Apply(item, cancellationToken);

        public Task<ItemUpdateType> FetchAsync(Folder item, MetadataRefreshOptions options, CancellationToken cancellationToken) => Apply(item, cancellationToken);

        private async Task<ItemUpdateType> Apply(BaseItem item, CancellationToken cancellationToken)
        {
            if (SourceRefreshService.IsAlbum(item) && !item.IsLocked)
            {
                await _cast.ApplyAsync(item, cancellationToken);
            }

            // The links are kept by the plugin: the item itself is not changed
            return ItemUpdateType.None;
        }
    }
}
