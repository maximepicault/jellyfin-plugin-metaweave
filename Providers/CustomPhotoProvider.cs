using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CustomMetadata.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Providers
{
    /// <summary>
    /// Metadata of one photo or video of an album. A custom provider with a late order: it runs AFTER Jellyfin's own reading of
    /// the file (the EXIF of a photo, the probe of a video), which rewrites fields at each of its runs, so what the source gives
    /// wins and what it leaves empty keeps the file's value. Forced, to run each time those do. A locked item or field is left
    /// alone. Both are identified the same way (PhotoLibraryService): their own tag, else their file name in their album.
    /// </summary>
    public abstract class AlbumMediaProviderBase<T> : ICustomMetadataProvider<T>, IHasOrder, IForcedProvider
        where T : BaseItem
    {
        private readonly PhotoLibraryService _photos;
        private readonly AlbumCastService _cast;
        private readonly ILogger _logger;

        protected AlbumMediaProviderBase(PhotoLibraryService photos, AlbumCastService cast, ILogger logger)
        {
            _cast = cast;
            _photos = photos;
            _logger = logger;
        }

        public string Name => "Custom Metadata Provider";

        public abstract int Order { get; }

        public async Task<ItemUpdateType> FetchAsync(T item, MetadataRefreshOptions options, CancellationToken cancellationToken)
        {
            // A video of an album only: never a film or an episode (types of their own), nor an extra or a trailer
            if (!SourceRefreshService.IsAlbumMedia(item) || item.IsLocked || !(Plugin.Instance?.Configuration.EnableMetadata ?? true))
            {
                return ItemUpdateType.None;
            }

            // People: from the file's entry, the People screen or an album above, even when the source says nothing else of it
            await _cast.ApplyAsync(item, cancellationToken);

            try
            {
                var match = await _photos.GetPhotoAsync(item.Path, item.ProviderIds, cancellationToken);
                if (match == null)
                {
                    return ItemUpdateType.None;
                }

                var data = match.Data;
                bool Free(MetadataField field) => item.LockedFields?.Contains(field) != true;

                if (!string.IsNullOrWhiteSpace(data.Title) && Free(MetadataField.Name)) { item.Name = data.Title; }
                if (!string.IsNullOrWhiteSpace(data.Overview) && Free(MetadataField.Overview)) { item.Overview = data.Overview; }
                if (data.PremiereDate.HasValue)
                {
                    item.PremiereDate = data.PremiereDate;
                    item.ProductionYear = data.PremiereDate.Value.Year;
                }

                if (data.CommunityRating.HasValue) { item.CommunityRating = data.CommunityRating; }

                if (Free(MetadataField.Tags))
                {
                    // The album's tags are added to the file's own: the source's, else those read in the file
                    var tags = TaxonomyStore.Tags(data.Tags);
                    if (data.InheritedTags?.Count > 0)
                    {
                        tags = (tags.Length > 0 ? tags : item.Tags ?? Array.Empty<string>())
                            .Concat(TaxonomyStore.Tags(data.InheritedTags))
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    }

                    if (tags.Length > 0) { item.Tags = tags; }
                }

                var genres = TaxonomyStore.Genres(data.Genres);
                if (genres.Length > 0 && Free(MetadataField.Genres)) { item.Genres = genres; }
                if (data.Studios?.Count > 0 && Free(MetadataField.Studios)) { item.Studios = data.Studios.ToArray(); }
                if (match.OwnTag != null) { item.ProviderIds["custom"] = match.OwnTag; }

                _logger.LogDebug("📸 [PHOTO PROVIDER] ✅ {File}: applied ({How})", Path.GetFileName(item.Path), match.OwnTag ?? "album list");
                return ItemUpdateType.MetadataImport;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "📸 [PHOTO PROVIDER] ❌ Error enriching {File}", Path.GetFileName(item.Path));
                return ItemUpdateType.None;
            }
        }
    }

    /// <summary>One photo, after Jellyfin's "Embedded Information" (EXIF)</summary>
    public class CustomPhotoProvider : AlbumMediaProviderBase<Photo>
    {
        public CustomPhotoProvider(PhotoLibraryService photos, AlbumCastService cast, ILogger<CustomPhotoProvider> logger)
            : base(photos, cast, logger)
        {
        }

        public override int Order => 100;
    }

    /// <summary>One video of an album (a plain Video of a library of home videos and photos), after Jellyfin has probed the file</summary>
    public class CustomAlbumVideoProvider : AlbumMediaProviderBase<Video>
    {
        public CustomAlbumVideoProvider(PhotoLibraryService photos, AlbumCastService cast, ILogger<CustomAlbumVideoProvider> logger)
            : base(photos, cast, logger)
        {
        }

        public override int Order => 1000;
    }
}
