using System.Collections.Generic;
using MediaBrowser.Model.Plugins;
using Jellyfin.Plugin.CustomMetadata.Configuration;

namespace Jellyfin.Plugin.CustomMetadata
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        public PluginConfiguration()
        {
            // Automatic migration: if no source is configured but the old config exists
            MigrateOldConfigurationIfNeeded();
        }
        
        /// <summary>
        /// Automatically migrates the old single-source configuration to the new multi-source config
        /// </summary>
        private void MigrateOldConfigurationIfNeeded()
        {
            // If Sources is null or empty AND a global config exists
            if ((Sources == null || Sources.Count == 0) && 
                !string.IsNullOrWhiteSpace(ApiBaseUrl) && 
                ApiBaseUrl != "https://votre-api.com" &&
                !ApiBaseUrl.Contains("example.com"))
            {
                // Automatically create a source from the global config
                Sources = new List<SourceConfiguration>
                {
                    new SourceConfiguration
                    {
                        Name = "Default Source (migrated)",
                        Prefix = PatternPrefix ?? "mv",
                        BaseUrl = ApiBaseUrl,
                        ApiKey = ApiKey,
                        Enabled = true,
                        VideoEndpoint = VideoEndpoint ?? "/videos/{id}",
                        SourceEndpoint = SourceEndpoint ?? "/sources/{id}",
                        SeriesEndpoint = SeriesEndpoint ?? "/series/{id}",
                        VideoFieldMappings = VideoFieldMappings ?? new FieldMapping(),
                        SourceFieldMappings = SourceFieldMappings ?? new FieldMapping(),
                        SeriesFieldMappings = SeriesFieldMappings ?? new FieldMapping(),
                        CustomHeaders = new List<CustomHeader>()
                    }
                };
                
                // Migration log (visible when the plugin starts)
                System.Diagnostics.Debug.WriteLine("🔄 [CONFIG MIGRATION] Automatically migrated old configuration to multi-source format");
                System.Diagnostics.Debug.WriteLine($"🔄 [CONFIG MIGRATION] Created source with prefix '{PatternPrefix ?? "mv"}' and URL '{ApiBaseUrl}'");
            }
        }
        // === Multi-Source Configuration ===
        /// <summary>
        /// List of the configured metadata sources
        /// Each source can have its own BaseUrl, endpoints and mappings
        /// If empty or null, the global configuration below is used (backward compatibility)
        /// </summary>
        public List<SourceConfiguration> Sources { get; set; } = new List<SourceConfiguration>();
        
        // === Pattern configuration (backward compatibility - used if Sources is empty) ===
        /// <summary>
        /// Prefix used in file names
        /// Default "custom" → {custom-123}
        /// Can be changed to "myapp" → {myapp-123}
        /// NOTE: only used if Sources is empty (single-source mode)
        /// </summary>
        public string PatternPrefix { get; set; } = "custom";
        
        // === API Configuration ===
        public string ApiBaseUrl { get; set; } = "";
        
        public string ApiKey { get; set; } = "";
        
        // === Endpoints per type ===
        /// <summary>
        /// Endpoint to fetch videos/episodes
        /// Uses {id} as a placeholder. E.g. "/videos/{id}"
        /// </summary>
        public string VideoEndpoint { get; set; } = "/videos/{id}";
        
        /// <summary>
        /// Endpoint to fetch sources (seasons in Jellyfin)
        /// Uses {id} as a placeholder. E.g. "/sources/{id}"
        /// </summary>
        public string SourceEndpoint { get; set; } = "/sources/{id}";
        
        /// <summary>
        /// Endpoint to fetch series
        /// Uses {id} as a placeholder. E.g. "/series/{id}"
        /// </summary>
        public string SeriesEndpoint { get; set; } = "/series/{id}";
        
        // === Source configuration ===
        /// <summary>
        /// Default name for sources (e.g. "YouTube channel")
        /// Only used if the API returns no title AND the folder name,
        /// once the {PREFIX-ID} tag is removed, is empty (see CustomSeasonProvider.GetSeasonName)
        /// </summary>
        public string DefaultSourceName { get; set; } = "Source";
        
        // === Features ===
        public bool EnableMetadata { get; set; } = true;
        
        public bool EnableImages { get; set; } = true;
        
        public bool EnableCasting { get; set; } = true;

        /// <summary>
        /// Adds the Projection theme's script (theme/projection.js, served by ThemeController) to jellyfin-web's
        /// index.html through ThemeScriptMiddleware: hover previews, episode numbers, previous/next, progress counts.
        /// Off by default: it changes every page of jellyfin-web; the theme's CSS works without it.
        /// </summary>
        public bool EnableThemeScript { get; set; } = false;

        /// <summary>
        /// Creates virtual (greyed-out) episodes for the missing episodes detected through EpisodeListEndpoint
        /// Visible with the user setting "Show missing episodes in seasons"
        /// </summary>
        public bool CreateVirtualEpisodes { get; set; } = true;

        /// <summary>
        /// How many videos the automatic download of missing episodes fetches at the same time (1 to 8, default 1).
        /// Read again before each download, so a change applies without restarting the server.
        /// </summary>
        public int MaxParallelDownloads { get; set; } = 1;

        // === Cache ===
        /// <summary>
        /// Cache lifetime in hours (default 24h)
        /// </summary>
        public int CacheTTLHours { get; set; } = 24;

        /// <summary>
        /// Enable/disable the cache entirely
        /// </summary>
        public bool EnableCache { get; set; } = true;

        // === Mappings per type ===
        /// <summary>
        /// JSON field mapping for VIDEOS/EPISODES
        /// </summary>
        public FieldMapping VideoFieldMappings { get; set; } = new FieldMapping();
        
        /// <summary>
        /// JSON field mapping for SOURCES (seasons)
        /// </summary>
        public FieldMapping SourceFieldMappings { get; set; } = new FieldMapping();
        
        /// <summary>
        /// JSON field mapping for SERIES
        /// </summary>
        public FieldMapping SeriesFieldMappings { get; set; } = new FieldMapping();
        
        // Old mapping kept for backward compatibility
        public FieldMapping FieldMappings { get; set; } = new FieldMapping();
    }

    /// <summary>
    /// JSON field mapping configuration
    /// </summary>
    public class FieldMapping
    {
        /// <summary>A copy to adjust without touching the saved configuration</summary>
        public FieldMapping Clone() => (FieldMapping)MemberwiseClone();

        // Main metadata
        public string Title { get; set; } = "title";
        public string Overview { get; set; } = "overview";
        public string PremiereDate { get; set; } = "premiereDate";
        public string CommunityRating { get; set; } = "communityRating";
        public string Tags { get; set; } = "tags";
        public string TagsItemField { get; set; } = "label";  // Field to extract if tags is an array of objects

        public string SeriesName { get; set; } = "";

        /// <summary>Studio(s): a text, a list of texts or a list of objects (with the same item field as the tags). Empty = ignored</summary>
        public string Studio { get; set; } = "studio";

        /// <summary>The overview is HTML (br, p, li, entities): converted to plain text. Off by default</summary>
        public bool OverviewIsHtml { get; set; } = false;

        public string Genres { get; set; } = "genres";
        public string GenresItemField { get; set; } = "name";  // Field to extract if genres is an array of objects
        
        // Series-specific
        public string Status { get; set; } = "status";
        public string ProductionYear { get; set; } = "productionYear";
        
        // Episode-specific
        public string IndexNumber { get; set; } = "indexNumber";
        public string ParentIndexNumber { get; set; } = "parentIndexNumber";

        /// <summary>
        /// If the overview is only a list ("#a #b", "A, B, C"): "tags" or "genres" to add it there
        /// (the overview is then cleared). Empty = disabled. See OverviewListDetector.
        /// </summary>
        public string OverviewListTarget { get; set; } = "";
        
        // Chapters (episodes): time codes and names
        /// <summary>
        /// Path of the chapters: a list of objects (time and name fields below), a list of "12:34 Title" texts, or one multi-line text.
        /// Empty = disabled. Time codes: "mm:ss", "hh:mm:ss", seconds (359), "6m12s", ISO 8601: see ChapterParser.
        /// </summary>
        public string ChaptersArray { get; set; } = "chapters";
        public string ChapterTime { get; set; } = "time";
        public string ChapterTitle { get; set; } = "title";

        /// <summary>Optional end of each chapter (same forms as the start). Jellyfin has no end: a chapter that ends before the next one starts gets a closing chapter</summary>
        public string ChapterEnd { get; set; } = "";

        /// <summary>"seconds" or "milliseconds": what a bare number means in a chapter's time code (359 or 359000)</summary>
        public string ChapterTimeUnit { get; set; } = "seconds";

        /// <summary>No chapters field in the API: read the lines of the synopsis that start with a time code ("12:34 Title"), two at least</summary>
        public bool ChaptersFromOverview { get; set; } = false;

        // Casting
        public string Cast { get; set; } = "cast";
        public string CastName { get; set; } = "name";
        public string CastRole { get; set; } = "role";
        public string CastType { get; set; } = "type";
        public string CastOrder { get; set; } = "order";
        public string CastImage { get; set; } = "image";   // Person's photo (URL)
        
        // Images - direct URLs
        public string PosterUrl { get; set; } = "poster";           // Main image (poster)
        public string BackdropUrl { get; set; } = "backdrop";       // Background image
        public string ThumbUrl { get; set; } = "thumb";             // Thumbnail (episodes)
        public string LogoUrl { get; set; } = "logo";               // Logo
        public string BannerUrl { get; set; } = "banner";           // Banner
        // Images - array structure (if the API returns an array of images)
        public string ImagesArray { get; set; } = "images";         // Path to the images array
        public string ImageUrl { get; set; } = "url";               // URL inside each image object
        public string ImageType { get; set; } = "type";             // Image type (Primary, Backdrop, etc.)
        public string ImageWidth { get; set; } = "width";           // Largeur
        public string ImageHeight { get; set; } = "height";         // Hauteur
    }
}
