using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.CustomMetadata.Providers
{
    /// <summary>
    /// Declares the plugin's "custom" provider id, so that the stored {prefix}-{id} (e.g. "dm-123") shows under
    /// "External IDs" in the metadata editor. It does not read file names: the providers extract the tag (TagParser).
    /// </summary>
    public class CustomExternalId : IExternalId
    {
        /// <inheritdoc />
        public string ProviderName => "Custom API";

        /// <inheritdoc />
        /// Always "custom": it is the key under which the whole plugin stores the ID (ProviderIds["custom"]).
        /// (It used to be PatternPrefix: with a prefix other than "custom", "External IDs" showed an empty field.)
        public string Key => "custom";

        /// <inheritdoc />
        public ExternalIdMediaType? Type => null; // Supports every type

        /// <inheritdoc />
        /// No link: the URL depends on the source (multi-source), not on the global config
        public string UrlFormatString => null;

        /// <inheritdoc />
        public bool Supports(IHasProviderIds item)
        {
            // Supports movies, series, seasons and episodes
            return item is Movie || item is Series || item is Season || item is Episode;
        }
    }
}
