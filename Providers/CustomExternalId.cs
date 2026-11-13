using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.CustomMetadata.Providers
{
    /// <summary>
    /// Lets Jellyfin recognise the {PREFIX-ID} pattern in file names
    /// The prefix is configurable (default "custom")
    /// Example: "Matrix {custom-123}.mkv" or "Matrix {myapp-123}.mkv"
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
