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
    /// No RemoteTrailers: jellyfin-web 10.11 only plays an external URL if it is youtube.com.
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
        /// Previews (Trailers) created by the plugin for this episode, through its ExtraIds.
        /// Identified WITHOUT relying on ProviderIds: a trailer extra of THIS episode whose path is a URL
        /// (a virtual episode has no file, hence no extra other than ours).
        /// </summary>
        public static List<Trailer> GetPreviewTrailers(Episode episode, ILibraryManager libraryManager)
        {
            return episode.ExtraIds
                .Select(id => libraryManager.GetItemById(id))
                .OfType<Trailer>()
                .Where(t => t.ExtraType == ExtraType.Trailer
                    && t.OwnerId.Equals(episode.Id)
                    && t.Path != null
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
