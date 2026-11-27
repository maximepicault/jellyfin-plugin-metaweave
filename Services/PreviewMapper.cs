using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>
    /// Video preview of missing episodes.
    /// No RemoteTrailers: jellyfin-web only plays an external URL if it is youtube.com.
    /// The preview is created as a LOCAL trailer (a Trailer whose Path is the URL), played through the server
    /// (proxy / transcoding) on every client. See MissingEpisodeService.SyncPreviewAsync.
    /// </summary>
    public static class PreviewMapper
    {
        public const string NamePrefix = "Aperçu";

        /// <summary>
        /// ProviderId set on the Trailers created by the plugin (value = the episode's "{prefix}-{id}" key)
        /// </summary>
        public const string ProviderKey = "customPreview";

        /// <summary>
        /// Previews (Trailers) created by the plugin for this episode: the items it owns (OwnerId, Jellyfin 12 has no ExtraIds).
        /// Identified WITHOUT relying on ProviderIds: a trailer extra of THIS episode whose path is a URL
        /// (a virtual episode has no file, hence no extra other than ours).
        /// </summary>
        public static List<Trailer> GetPreviewTrailers(Episode episode, ILibraryManager libraryManager)
        {
            // A database query: callers must filter out the items that are not virtual episodes first
            return libraryManager.GetItemList(new InternalItemsQuery
                {
                    OwnerIds = new[] { episode.Id },
                    ExtraTypes = new[] { ExtraType.Trailer }
                })
                .OfType<Trailer>()
                .Where(t => t.Path != null
                    && (t.Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                        || t.Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        /// <summary>
        /// Relative URL ("/previews/1.mp4") → prefixed with the source's BaseUrl. Null if empty
        /// </summary>
        public static string ResolveUrl(string url, string sourceBaseUrl)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            url = url.Trim();
            if (url.StartsWith("/", StringComparison.Ordinal)
                && !url.StartsWith("//", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(sourceBaseUrl))
            {
                url = sourceBaseUrl.TrimEnd('/') + url;
            }

            return url;
        }

        public static string GetTrailerName(string episodeTitle)
        {
            return string.IsNullOrWhiteSpace(episodeTitle) ? NamePrefix : $"{NamePrefix} – {episodeTitle}";
        }

        /// <summary>
        /// Remote trailer set by a previous version of the plugin (to be cleaned up)
        /// </summary>
        public static bool IsLegacyRemoteTrailer(MediaUrl trailer)
        {
            return trailer?.Name != null && trailer.Name.StartsWith(NamePrefix, StringComparison.Ordinal);
        }
    }
}
