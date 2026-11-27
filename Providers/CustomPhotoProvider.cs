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
    /// Metadata of one photo. A custom provider with a late order: it runs AFTER Jellyfin's "Embedded Information" provider (the
    /// file's EXIF, which rewrites the fields at each of its runs), so what the source gives wins and what it leaves empty keeps the
    /// file's value. Forced, to run each time that provider does. A locked item or field is left alone.
    /// </summary>
    public class CustomPhotoProvider : ICustomMetadataProvider<Photo>, IHasOrder, IForcedProvider
    {
        private readonly PhotoLibraryService _photos;
        private readonly ILogger<CustomPhotoProvider> _logger;

        public CustomPhotoProvider(PhotoLibraryService photos, ILogger<CustomPhotoProvider> logger)
        {
            _photos = photos;
            _logger = logger;
        }

        public string Name => "Custom Metadata Provider";

        public int Order => 100;

        public async Task<ItemUpdateType> FetchAsync(Photo item, MetadataRefreshOptions options, CancellationToken cancellationToken)
        {
            if (item.IsLocked || !(Plugin.Instance?.Configuration.EnableMetadata ?? true))
            {
                return ItemUpdateType.None;
            }

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
                    // The album's tags are added to the photo's own: the source's, else the file's
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
}
