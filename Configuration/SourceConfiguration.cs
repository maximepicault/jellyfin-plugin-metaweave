using System.Collections.Generic;

namespace Jellyfin.Plugin.CustomMetadata.Configuration
{
    /// <summary>
    /// Configuration of a metadata source
    /// Each source can have its own BaseUrl, endpoints and mappings
    /// </summary>
    public class SourceConfiguration
    {
        /// <summary>
        /// Name of the source (for display)
        /// </summary>
        public string Name { get; set; } = "Default Source";

        /// <summary>
        /// Prefix used in file names
        /// E.g. "mv1" for {mv1-123}, "yt" for {yt-456}
        /// </summary>
        public string Prefix { get; set; } = "custom";

        /// <summary>
        /// "Api" (default): metadata from a REST API. "Local": content typed in the plugin
        /// (series, seasons, episodes), stored in a JSON file of the data folder (LocalSourceStore)
        /// and served in place of an API: the providers, the cache, the images and the missing-episode analysis
        /// therefore work as for an API source.
        /// </summary>
        public string SourceType { get; set; } = "Api";

        /// <summary>
        /// Local source: stable identifier of the content file (independent of the prefix, which can be renamed)
        /// </summary>
        public string LocalDataId { get; set; } = string.Empty;

        /// <summary>
        /// "People" source: supplies PEOPLE (cast) to the plugin's directory, by identifier
        /// (PersonEndpoint). It enriches no media and is never chosen by a file's tag.
        /// </summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsPeople => string.Equals(SourceType, "People", System.StringComparison.OrdinalIgnoreCase);

        /// <summary>People source: endpoint of ONE person. {id} = their identifier in this API</summary>
        public string PersonEndpoint { get; set; } = "/people/{id}";

        /// <summary>People source: JSON fields of the response</summary>
        public PersonMapping PersonMappings { get; set; } = new PersonMapping();

        [System.Xml.Serialization.XmlIgnore]
        public bool IsLocal => string.Equals(SourceType, "Local", System.StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Base URL of the API for this source
        /// </summary>
        public string BaseUrl { get; set; } = string.Empty;

        /// <summary>
        /// Optional API key for this source (can differ per source)
        /// </summary>
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>
        /// Endpoint to fetch the metadata of a video/episode
        /// Uses {id} as a placeholder
        /// </summary>
        public string VideoEndpoint { get; set; } = "/videos/{id}";

        /// <summary>
        /// Endpoint to fetch the metadata of a source/season
        /// Uses {id} as a placeholder
        /// </summary>
        public string SourceEndpoint { get; set; } = "/sources/{id}";

        /// <summary>
        /// Endpoint to fetch the metadata of a series
        /// Uses {id} as a placeholder
        /// </summary>
        public string SeriesEndpoint { get; set; } = "/series/{id}";

        /// <summary>
        /// Endpoint returning the COMPLETE list of the episodes of a source/season
        /// {id} = ID of the {PREFIX-ID} season folder. E.g. "/sources/{id}/videos"
        /// Empty = no missing-episode detection for this source
        /// </summary>
        public string EpisodeListEndpoint { get; set; } = string.Empty;

        /// <summary>
        /// JSON field mapping of the episode list
        /// </summary>
        public EpisodeListMapping EpisodeListMappings { get; set; } = new EpisodeListMapping();

        /// <summary>
        /// JSON field mapping for videos/episodes
        /// </summary>
        public FieldMapping VideoFieldMappings { get; set; } = new FieldMapping();

        /// <summary>
        /// JSON field mapping for sources/seasons
        /// </summary>
        public FieldMapping SourceFieldMappings { get; set; } = new FieldMapping();

        /// <summary>
        /// JSON field mapping for series
        /// </summary>
        public FieldMapping SeriesFieldMappings { get; set; } = new FieldMapping();

        /// <summary>
        /// Custom HTTP headers for this source
        /// Format: Key=Header-Name, Value=Header-Value
        /// </summary>
        public List<CustomHeader> CustomHeaders { get; set; } = new List<CustomHeader>();

        /// <summary>
        /// Activer cette source
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Maximum time of ONE HTTP attempt (connection + reading the response), in seconds
        /// </summary>
        public int TimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Number of retries after a temporary failure (timeout, network error, HTTP 408/429/5xx).
        /// 0 = a single attempt. Delay: 1s, 2s, 4s… (or the API's Retry-After header)
        /// </summary>
        public int MaxRetries { get; set; } = 2;

        /// <summary>
        /// Maximum simultaneous requests to this source (avoids bursts during a scan,
        /// which anti-bot protections such as Cloudflare block). 0 = unlimited
        /// </summary>
        public int MaxConcurrentRequests { get; set; } = 4;

        /// <summary>
        /// Send a browser User-Agent (Chrome) instead of "Jellyfin.Plugin.CustomMetadata/1.0".
        /// Helps behind some anti-bot protections, but others (Cloudflare…) precisely block
        /// a client that claims to be Chrome without having its network fingerprint.
        /// </summary>
        public bool SendBrowserUserAgent { get; set; } = true;

        /// <summary>
        /// Send Origin and Referer (derived from BaseUrl). Useless from server to server, and some
        /// backends answer 403 to an Origin they do not allow (CORS, e.g. Spring).
        /// </summary>
        public bool SendOriginReferer { get; set; } = true;

        /// <summary>
        /// Forced value for Origin when SendOriginReferer is on. Empty = derived from BaseUrl ("https://api.x:8080")
        /// </summary>
        public string OriginOverride { get; set; } = string.Empty;

        /// <summary>
        /// Forced value for Referer when SendOriginReferer is on. Empty = BaseUrl
        /// </summary>
        public string RefererOverride { get; set; } = string.Empty;

        /// <summary>
        /// Request HTTP/2 (:method, :scheme, :authority, :path headers generated by .NET),
        /// falling back to HTTP/1.1 if the server does not support it. No effect over http:// (h2c is not negotiated).
        /// </summary>
        public bool UseHttp2 { get; set; } = false;

        /// <summary>
        /// PORTRAIT episode thumbnails converted to 16:9 (whole photo on a blurred background) on download.
        /// Clients display these thumbnails in a cropped landscape frame ("cover").
        /// </summary>
        public bool LandscapePortraitThumbs { get; set; } = false;

        /// <summary>
        /// Builds a source from the old single-source global config (PatternPrefix, ApiBaseUrl…).
        /// Used by SourceResolver when Sources is empty, and by the movie provider (legacy path).
        /// Static method: ignored by XmlSerializer.
        /// </summary>
        public static SourceConfiguration FromLegacyConfig(PluginConfiguration config, string name)
        {
            if (config == null)
            {
                return null;
            }

            return new SourceConfiguration
            {
                Name = name,
                Prefix = config.PatternPrefix,
                BaseUrl = config.ApiBaseUrl,
                ApiKey = config.ApiKey,
                VideoEndpoint = config.VideoEndpoint,
                SourceEndpoint = config.SourceEndpoint,
                SeriesEndpoint = config.SeriesEndpoint,
                VideoFieldMappings = config.VideoFieldMappings ?? new FieldMapping(),
                SourceFieldMappings = config.SourceFieldMappings ?? new FieldMapping(),
                SeriesFieldMappings = config.SeriesFieldMappings ?? new FieldMapping(),
                Enabled = true
            };
        }
    }

    /// <summary>
    /// Mapping of the PersonEndpoint response (dot notation)
    /// </summary>
    public class PersonMapping
    {
        public string Name { get; set; } = "name";

        /// <summary>URL of the photo (relative = prefixed with BaseUrl)</summary>
        public string Photo { get; set; } = "photo";

        public string Bio { get; set; } = "bio";
    }

    /// <summary>
    /// Mapping of the EpisodeListEndpoint response (dot notation, like FieldMapping)
    /// </summary>
    public class EpisodeListMapping
    {
        /// <summary>
        /// Path to the episodes array. Empty = the response is directly an array
        /// Ex: "data", "data.videos"
        /// </summary>
        public string ItemsPath { get; set; } = string.Empty;

        /// <summary>
        /// ID field of each episode: must match the {PREFIX-ID} of the file names
        /// </summary>
        public string Id { get; set; } = "id";

        public string Title { get; set; } = "title";

        public string Overview { get; set; } = "overview";

        public string PremiereDate { get; set; } = "premiereDate";

        public string IndexNumber { get; set; } = "indexNumber";

        /// <summary>
        /// Duration of the episode → RunTimeTicks of the virtual episode.
        /// Formats: "mm:ss", "hh:mm:ss", seconds (number or text), ISO 8601 ("PT1H2M3S"). Empty = ignored
        /// </summary>
        public string Duration { get; set; } = "duration";

        /// <summary>
        /// URL of the episode thumbnail. Saved as the virtual episode's PRIMARY image:
        /// it is the image Jellyfin shows in a season's episode list
        /// </summary>
        public string ThumbUrl { get; set; } = "thumb";

        /// <summary>
        /// URL of the preview video → LOCAL trailer of the virtual episode ("Bande-annonce" button),
        /// played through the Jellyfin server (see MissingEpisodeService.SyncPreviewAsync)
        /// </summary>
        public string PreviewUrl { get; set; } = "preview.url";

        /// <summary>
        /// Pagination parameter added to the URL (e.g. "page" → ?page=1, ?page=2...)
        /// Empty = no pagination (a single call)
        /// </summary>
        public string PageParameter { get; set; } = string.Empty;

        /// <summary>
        /// Number of the first page (often 1, sometimes 0)
        /// </summary>
        public int FirstPage { get; set; } = 1;

        /// <summary>
        /// Garde-fou : nombre maximum de pages lues
        /// </summary>
        public int MaxPages { get; set; } = 100;
    }

    /// <summary>
    /// Represents a custom HTTP header
    /// Used to serialise to XML (Dictionary is not supported)
    /// </summary>
    public class CustomHeader
    {
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }
}
