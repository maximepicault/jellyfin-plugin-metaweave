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

        /// <summary>
        /// "Dump" source: ONE call returns the whole catalogue ({ "data": [ … ] }). The plugin keeps it in memory
        /// (DumpStore) and serves each entry in place of an API call, so a file is enriched by the tag
        /// {prefix-id} OR, without a tag, by matching its name against the catalogue.
        /// </summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsDump => string.Equals(SourceType, "Dump", System.StringComparison.OrdinalIgnoreCase);

        /// <summary>Dump source: where the entries are in the response, how a file is matched to them</summary>
        public DumpSettings Dump { get; set; } = new DumpSettings();

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
        /// Whether this source is enabled
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
        /// LANDSCAPE series and season posters cropped to portrait 2:3 (centre) at the source's full resolution, on download. Without it
        /// Jellyfin shrinks the landscape picture, then zooms on its middle in the browser: a 1080p picture ends up blurry.
        /// </summary>
        public bool LandscapePosterCrop { get; set; } = false;

        /// <summary>
        /// Missing episodes: download the video of each one (EpisodeListMapping.VideoUrl, else PreviewUrl) into the season folder,
        /// named "{title} {prefix-id}.mp4", so that the next library scan turns the missing episode into a real one.
        /// HLS (.m3u8) streams are remuxed to MP4 with the server's ffmpeg. Off by default.
        /// </summary>
        public bool AutoDownloadVideos { get; set; } = false;

        /// <summary>
        /// Quality picked in an HLS master playlist by the automatic download: "best" (default, empty too), "1080" or "720" (maximum height),
        /// "lowest". Ignored for direct files and for playlists with a single stream.
        /// </summary>
        public string VideoQuality { get; set; } = "best";

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
    /// Settings of a "Dump" source (SourceType = "Dump"). The URL of the dump is the source's BaseUrl; what is
    /// shown in Jellyfin comes from the source's VideoFieldMappings, applied to each entry.
    /// </summary>
    public class DumpSettings
    {
        /// <summary>Path of the list of entries in the response. Empty = the response is the list itself</summary>
        public string ItemsPath { get; set; } = "data";

        /// <summary>Identifier of an entry (what {prefix-id} designates)</summary>
        public string IdPath { get; set; } = "id";

        /// <summary>Name of an entry, used to match a file that has no tag</summary>
        public string NamePath { get; set; } = "name";

        /// <summary>Slug of an entry (exact match of the file name). Empty = none</summary>
        public string SlugPath { get; set; } = "slug";

        /// <summary>
        /// Other titles of an entry, usually several languages glued together WITHOUT a separator:
        /// only used as a substring search. Empty = none
        /// </summary>
        public string SearchTitlesPath { get; set; } = "search_titles";

        /// <summary>Group of an entry (brand, studio, channel…): the coverage screen groups the missing entries by it</summary>
        public string GroupPath { get; set; } = "brand";

        /// <summary>
        /// Series an entry belongs to ("seriesId", "series.id"): the coverage screen groups the entries that share it into one series, which is
        /// reliable where the names only differ by a trailing number. Empty, or an entry without one = the grouping by name is used.
        /// </summary>
        public string SeriesIdPath { get; set; } = string.Empty;

        /// <summary>Name of that series ("series.name"). Empty = the most common name without its trailing number among its entries</summary>
        public string SeriesNamePath { get; set; } = string.Empty;

        /// <summary>Counters turned into a community rating: likes / (likes + dislikes) × 10. Both empty = no rating</summary>
        public string LikesPath { get; set; } = "likes";
        public string DislikesPath { get; set; } = "dislikes";

        /// <summary>
        /// false = the images of the dump are NEVER given to Jellyfin (no poster, backdrop or thumbnail), but the mapping still
        /// reads them: the coverage screen shows them as previews
        /// </summary>
        public bool UseImages { get; set; } = true;

        /// <summary>
        /// Path of the picture shown on the coverage screen. Empty = the first of the video mapping's thumbnail, poster
        /// and backdrop. The server downloads it with the source's headers (a browser is often refused by hotlink protection)
        /// </summary>
        public string PreviewImagePath { get; set; } = string.Empty;

        /// <summary>
        /// Entries whose names only differ by a trailing number ("Title", "Title 2") are grouped into a "series" on the coverage
        /// screen when at least this many share the name. 0 = no grouping. The number is only a hint (season or episode: the
        /// title cannot tell), never used to build an SxxExx.
        /// </summary>
        public int SeriesMinEntries { get; set; } = 2;

        /// <summary>
        /// Path of the release date shown, filtered and sorted on the coverage screen ("released_at || released_at_unix"; a number
        /// is read as Unix seconds). Independent from the video mapping's date: leave that one empty to keep the date out of Jellyfin
        /// (another source gives it) and still use it here. Empty = the mapping's date.
        /// </summary>
        public string ReleaseDatePath { get; set; } = "released_at || created_at";

        /// <summary>Shape of the preview pictures on the coverage screen: "16:9" (landscape, the default) or "2:3" (poster)</summary>
        public string PreviewRatio { get; set; } = "16:9";

        /// <summary>
        /// Pagination parameter added to the dump URL (e.g. "page" → ?page=1, ?page=2...). A template with {page} is a whole
        /// query parameter, e.g. options={"pagination":{"page":{page},"pageSize":100}}. Empty = one call only.
        /// Pages are read until one is empty, repeats the previous one, or answers 404 after the first.
        /// </summary>
        public string PageParameter { get; set; } = string.Empty;

        /// <summary>Number of the first page (often 1, sometimes 0)</summary>
        public int FirstPage { get; set; } = 1;

        /// <summary>Safety net: maximum number of pages read. Reaching it fails the download (an incomplete dump is never kept)</summary>
        public int MaxPages { get; set; } = 200;

        /// <summary>How long the downloaded dump is reused, in minutes</summary>
        public int CacheMinutes { get; set; } = 60;

        /// <summary>A file with no {prefix-id} tag is matched against the dump by its name (otherwise tags only)</summary>
        public bool MatchWithoutTag { get; set; } = true;

        /// <summary>…first by the entry's slug, then by its normalised name</summary>
        public bool MatchBySlug { get; set; } = true;

        /// <summary>…then by a fuzzy score on the name and the other titles</summary>
        public bool MatchByName { get; set; } = true;

        /// <summary>Minimum score (0-100) to accept a fuzzy match</summary>
        public int MinScore { get; set; } = 70;

        /// <summary>Words ignored when matching by name (languages, quality, release…), separated by spaces or commas</summary>
        public string NoiseWords { get; set; } =
            "raw vostfr vosta vostf vosten vf vo va multi mult subbed dubbed uncensored censored "
            + "1080p 720p 480p 2160p 4k x264 x265 h264 h265 hevc aac flac web webrip webdl bluray bdrip hdtv";
    }

    /// <summary>
    /// Mapping of the PersonEndpoint response (dot notation)
    /// </summary>
    public class PersonMapping
    {
        public string Name { get; set; } = "name";

        /// <summary>Path (or URL) of the photo. A path is completed with PhotoBaseUrl, otherwise with the API's BaseUrl</summary>
        public string Photo { get; set; } = "photo";

        /// <summary>
        /// Where the photo PATH lives when it is not on the API: a prefix ("https://img.example.com/p/w185") or a
        /// template with {value} ("https://img.example.com/{value}?w=200"). Empty = the API's BaseUrl.
        /// </summary>
        public string PhotoBaseUrl { get; set; } = string.Empty;

        public string Bio { get; set; } = "bio";

        /// <summary>Optional paths (empty = not read)</summary>
        public string BirthDate { get; set; } = string.Empty;

        public string DeathDate { get; set; } = string.Empty;

        public string BirthPlace { get; set; } = string.Empty;

        public string Website { get; set; } = string.Empty;

        /// <summary>
        /// Optional path of the person's background images (Backdrop): an array of addresses or paths ("images.backdrops",
        /// "images[].file_path") or a single value. Completed like the photo (PhotoBaseUrl). Empty = not read.
        /// </summary>
        public string Backdrops { get; set; } = string.Empty;

        /// <summary>Optional: where the API gives the person's link on a network (one path per network of the people screen)</summary>
        public List<PersonNetworkMapping> Networks { get; set; } = new List<PersonNetworkMapping>();

        /// <summary>
        /// Optional: networks given as a LIST of objects, each carrying an identifier of the network and a handle, like
        /// [{"providerId": "1", "handle": "name1"}, …]. The table of identifiers says which network each one is.
        /// </summary>
        public PersonNetworkListMapping NetworkList { get; set; } = new PersonNetworkListMapping();
    }

    /// <summary>Networks read from a list of objects: where the list is, how to read each item, and what each network identifier means</summary>
    public class PersonNetworkListMapping
    {
        /// <summary>Path of the list ("profileSocials")</summary>
        public string ItemsPath { get; set; } = string.Empty;

        /// <summary>Path, inside each item, of the network identifier ("providerId")</summary>
        public string KeyPath { get; set; } = string.Empty;

        /// <summary>Path, inside each item, of the handle or address ("handle")</summary>
        public string ValuePath { get; set; } = string.Empty;

        /// <summary>Which network of the people screen each identifier is ("1" → instagram)</summary>
        public List<PersonNetworkKey> Keys { get; set; } = new List<PersonNetworkKey>();
    }

    public class PersonNetworkKey
    {
        /// <summary>Value found at KeyPath ("1")</summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>Id of a network of the people screen ("instagram")</summary>
        public string NetworkId { get; set; } = string.Empty;
    }

    /// <summary>Path of the value (handle or address) of one network in the PersonEndpoint response</summary>
    public class PersonNetworkMapping
    {
        /// <summary>Id of a network of the people screen ("instagram")</summary>
        public string NetworkId { get; set; } = string.Empty;

        /// <summary>Path of the handle or address (a single value or an array of them)</summary>
        public string Path { get; set; } = string.Empty;
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
        /// URL of the full video (direct file or HLS playlist) used by the source's AutoDownloadVideos option.
        /// Empty = the PreviewUrl is used
        /// </summary>
        public string VideoUrl { get; set; } = string.Empty;

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

        /// <summary>false = kept in the configuration but not sent (a configuration saved before this option has none: enabled)</summary>
        public bool Enabled { get; set; } = true;
    }
}
