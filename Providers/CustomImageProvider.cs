using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CustomMetadata.Api;
using Jellyfin.Plugin.CustomMetadata.Configuration;
using Jellyfin.Plugin.CustomMetadata.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Providers
{
    /// <summary>
    /// Custom image provider
    /// MULTI-SOURCE: automatically detects the source from the ID prefix
    /// </summary>
    public class CustomImageProvider : IRemoteImageProvider
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<CustomImageProvider> _logger;
        private readonly CustomApiClient _apiClient;
        private readonly SourceResolver _sourceResolver;
        private readonly LocalSourceStore _localStore;

        public CustomImageProvider(
            IHttpClientFactory httpClientFactory,
            ILogger<CustomImageProvider> logger,
            CustomApiClient apiClient,
            SourceResolver sourceResolver,
            LocalSourceStore localStore)
        {
            _localStore = localStore;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _apiClient = apiClient;
            _sourceResolver = sourceResolver;
        }

        public string Name => "Custom Image Provider";

        public bool Supports(BaseItem item)
        {
            // Supports movies, series, seasons and episodes
            return item is Movie || item is Series || item is Season || item is Episode;
        }

        /// <summary>
        /// Fetches the list of available images
        /// MULTI-SOURCE: automatically detects the source from the ID prefix
        /// </summary>
        public async Task<IEnumerable<RemoteImageInfo>> GetImages(
            BaseItem item,
            CancellationToken cancellationToken)
        {
            var results = new List<RemoteImageInfo>();

            try
            {
                if (!Plugin.Instance.Configuration.EnableImages)
                {
                    return results;
                }

                // Get the item identifier (with prefix)
                var idWithPrefix = GetItemIdentifier(item);
                if (string.IsNullOrEmpty(idWithPrefix))
                {
                    _logger.LogDebug("🖼️ [IMAGE PROVIDER] No identifier found for item {ItemName} - SKIPPING (this is normal for non-custom files)", item.Name);
                    return results;  // ← EARLY RETURN: no ID = no custom images
                }

                _logger.LogInformation("🖼️ [IMAGE PROVIDER] Found identifier '{IdWithPrefix}' for item {ItemName}", 
                    idWithPrefix, item.Name);

                // Determine the item type to use the right endpoint
                string itemType = item switch
                {
                    Series => "series",
                    Season => "season",
                    Episode => "episode",
                    Movie => "movie",
                    _ => "video"
                };

                // MULTI-SOURCE: detect the source from the prefix
                var prefix = _sourceResolver.ExtractPrefixFromId(idWithPrefix);
                SourceConfiguration source = null;
                string itemId = idWithPrefix;
                
                if (!string.IsNullOrEmpty(prefix))
                {
                    _logger.LogDebug("🖼️ [IMAGE PROVIDER] 🔍 Extracted prefix '{Prefix}' from ID", prefix);
                    source = _sourceResolver.GetSourceByPrefix(prefix);
                    
                    if (source != null)
                    {
                        // Multi-source mode: extract the ID without prefix
                        itemId = _sourceResolver.ExtractIdWithoutPrefix(idWithPrefix);
                        _logger.LogInformation("🖼️ [IMAGE PROVIDER] 🌐 Multi-source mode: Using source '{SourceName}' (prefix: {Prefix})", 
                            source.Name, prefix);
                        _logger.LogInformation("🖼️ [IMAGE PROVIDER] 🆔 Item ID (without prefix): '{ItemId}'", itemId);
                    }
                    else
                    {
                        _logger.LogWarning("🖼️ [IMAGE PROVIDER] ⚠️ No source found for prefix '{Prefix}' - SKIPPING", prefix);
                        return results;  // ← EARLY RETURN: prefix found but no source configured
                    }
                }
                else
                {
                    _logger.LogDebug("🖼️ [IMAGE PROVIDER] 📝 No prefix detected - SKIPPING (file doesn't have {{PREFIX-ID}} pattern)");
                    return results;  // ← EARLY RETURN: no prefix = not a custom file
                }

                // API call to fetch the images
                List<ImageInfo> images;
                if (source != null)
                {
                    // Multi-source mode: call with a specific source
                    images = await _apiClient.GetImagesAsync(itemId, itemType, source, cancellationToken);
                }
                else
                {
                    // Should never get here thanks to the early returns above
                    _logger.LogWarning("🖼️ [IMAGE PROVIDER] ⚠️ Source is null after all checks - this shouldn't happen!");
                    return results;
                }
                
                if (images == null || !images.Any())
                {
                    // The extra IDs of a multi-tag episode file can still provide images
                    _logger.LogDebug("🖼️ [IMAGE PROVIDER] No images found for item {ItemName}", item.Name);
                    images = new List<ImageInfo>();
                }

                // Conversion to the Jellyfin format
                foreach (var image in images)
                {
                    var imageType = MapImageType(image.Type);
                    if (imageType.HasValue)
                    {
                        // Portrait → 16:9 conversion: only episode thumbnails (Primary/Thumb, shown
                        // in a landscape frame), never a logo, a banner or a backdrop
                        RememberImageSource(image.Url, source, item is Episode && imageType.Value is ImageType.Primary or ImageType.Thumb);
                        results.Add(new RemoteImageInfo
                        {
                            Url = image.Url,
                            Type = imageType.Value,
                            Width = image.Width,
                            Height = image.Height,
                            ProviderName = Name
                        });
                    }
                }

                // Episode: jellyfin-web shows the PRIMARY image in episode lists (like TMDb "stills").
                // The API usually provides a thumbnail (ThumbUrl → Thumb): without a Primary coming from the API,
                // Jellyfin takes a frame of the video (Screen Grabber / Embedded Image Extractor).
                // So the thumbnail is also offered as Primary when the API has no poster for the episode.
                if (item is Episode
                    && !results.Any(r => r.Type == ImageType.Primary)
                    && results.FirstOrDefault(r => r.Type == ImageType.Thumb) is RemoteImageInfo thumb)
                {
                    results.Add(new RemoteImageInfo
                    {
                        Url = thumb.Url,
                        Type = ImageType.Primary,
                        Width = thumb.Width,
                        Height = thumb.Height,
                        ProviderName = Name
                    });
                    _logger.LogInformation("🖼️ [IMAGE PROVIDER] Episode without poster: using thumb {Url} as Primary for {ItemName}", thumb.Url, item.Name);
                }

                // File bundling several episodes ("Ep {dm-1} {dm-2}.mkv"): the images of the following IDs are
                // added AFTER those of the first one (which therefore stays selected by default) so that they can be picked
                // by hand in the image editor
                if (item is Episode multiEpisode && !string.IsNullOrEmpty(multiEpisode.Path))
                {
                    foreach (var extraId in TagParser.ExtractAll(TagParser.GetOwnName(multiEpisode.Path))
                        .Where(id => !string.Equals(id, idWithPrefix, StringComparison.OrdinalIgnoreCase)))
                    {
                        var extraSource = _sourceResolver.GetSourceByPrefix(_sourceResolver.ExtractPrefixFromId(extraId));
                        if (extraSource == null)
                        {
                            continue;
                        }

                        var extraImages = await _apiClient.GetImagesAsync(
                            _sourceResolver.ExtractIdWithoutPrefix(extraId), itemType, extraSource, cancellationToken);
                        foreach (var image in extraImages ?? new List<ImageInfo>())
                        {
                            var extraType = MapImageType(image.Type);
                            if (!extraType.HasValue)
                            {
                                continue;
                            }

                            RememberImageSource(image.Url, extraSource, extraType.Value is ImageType.Primary or ImageType.Thumb);
                            results.Add(new RemoteImageInfo
                            {
                                Url = image.Url,
                                Type = extraType.Value,
                                Width = image.Width,
                                Height = image.Height,
                                ProviderName = $"{Name} ({extraId})"
                            });
                        }

                        _logger.LogInformation("🖼️ [IMAGE PROVIDER] 🔗 Additional tag '{ExtraId}': images added as alternatives", extraId);
                    }
                }

                _logger.LogInformation("🖼️ [IMAGE PROVIDER] ✅ Found {Count} images for {ItemName}", results.Count, item.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "🖼️ [IMAGE PROVIDER] ❌ Error fetching images for {ItemName}", item.Name);
            }

            return results;
        }

        /// <summary>
        /// Supported image types
        /// </summary>
        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        {
            return new[]
            {
                ImageType.Primary,      // Poster principal
                ImageType.Backdrop,     // Image de fond
                ImageType.Banner,       // Banner
                ImageType.Logo,         // Logo
                ImageType.Thumb,        // Miniature
                ImageType.Art           // Art alternatif
            };
        }

        // Image URL → prefix of the source that supplied it (GetImages), to apply that source's config
        // to the download even if the image is on another domain (CDN) than BaseUrl
        // (+ whether it is an episode image: only those are converted portrait → 16:9)
        private static readonly ConcurrentDictionary<string, ImageOrigin> ImageSourcePrefixes = new ConcurrentDictionary<string, ImageOrigin>(StringComparer.Ordinal);
        private const int MaxRememberedImageUrls = 20000;

        private sealed record ImageOrigin(string Prefix, bool IsEpisodeThumbnail);

        private static void RememberImageSource(string url, SourceConfiguration source, bool isEpisodeThumbnail)
        {
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(source?.Prefix))
            {
                return;
            }

            if (ImageSourcePrefixes.Count >= MaxRememberedImageUrls)
            {
                ImageSourcePrefixes.Clear();
            }

            ImageSourcePrefixes.AddOrUpdate(
                url,
                new ImageOrigin(source.Prefix, isEpisodeThumbnail),
                // Same URL seen as an episode thumbnail AND something else: keep "thumbnail"
                (_, previous) => new ImageOrigin(source.Prefix, isEpisodeThumbnail || previous.IsEpisodeThumbnail));
        }

        public async Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            var client = _httpClientFactory.CreateClient();

            // Source that supplied this URL, otherwise the source whose BaseUrl has the same host
            SourceConfiguration source = null;
            var isEpisodeImage = false;
            if (ImageSourcePrefixes.TryGetValue(url ?? string.Empty, out var origin))
            {
                source = _sourceResolver.GetSourceByPrefix(origin.Prefix);
                isEpisodeImage = origin.IsEpisodeThumbnail;
            }

            source ??= FindSourceByUrl(url);

            if (source != null)
            {
                _logger.LogDebug("🖼️ [IMAGE DOWNLOAD] Using source '{SourceName}' settings for URL {Url}", source.Name, url);
                ApplyHeadersFromSource(client, source);
            }
            else
            {
                _logger.LogDebug("🖼️ [IMAGE DOWNLOAD] No source matches URL {Url} - downloading without source settings", url);
            }

            // Image uploaded into a local source: read from the data folder, no network
            HttpResponseMessage response;
            if (LocalSourceStore.IsLocalImageUrl(url))
            {
                if (_localStore.TryReadImage(url, out var localData, out var localMime))
                {
                    response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(localData) };
                    response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(localMime);
                }
                else
                {
                    _logger.LogWarning("🖼️ [IMAGE DOWNLOAD] ⚠️ Local image not found: {Url}", url);
                    response = new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
                }
            }
            else
            {
                response = await client.GetAsync(url, cancellationToken);
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden && source != null)
                {
                    // CORS rejection: same request with the opposite Origin/Referer choice (once)
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (SourceHeaders.LooksLikeCorsRejection(403, body) && SourceHeaders.FlipOrigin(client, source, _logger, "🖼️ [IMAGE DOWNLOAD]") is bool sending)
                    {
                        response.Dispose();
                        response = await client.GetAsync(url, cancellationToken);
                        if (response.IsSuccessStatusCode)
                        {
                            SourceHeaders.RememberOrigin(source, sending);
                        }
                    }
                }
            }

            // Jellyfin SILENTLY ignores (Debug log) a 403/404 image and keeps the old one: we report it here
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("🖼️ [IMAGE DOWNLOAD] ⚠️ HTTP {StatusCode} (HTTP/{Version}) for {Url} (source '{SourceName}') - Jellyfin will ignore this image",
                    (int)response.StatusCode, response.Version, url, source?.Name ?? "none");
            }
            else
            {
                _logger.LogDebug("🖼️ [IMAGE DOWNLOAD] ✅ HTTP {StatusCode} (HTTP/{Version}) for {Url}", (int)response.StatusCode, response.Version, url);

                // Portrait episode thumbnail → 16:9 on a blurred background (source option)
                if (isEpisodeImage && source?.LandscapePortraitThumbs == true)
                {
                    var data = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                    var landscape = PortraitThumbConverter.ToLandscapeIfPortrait(data, _logger, url);
                    var content = new ByteArrayContent(landscape ?? data);
                    if (landscape != null)
                    {
                        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
                    }
                    else if (response.Content.Headers.ContentType != null)
                    {
                        content.Headers.ContentType = response.Content.Headers.ContentType;
                    }

                    response.Content.Dispose();
                    response.Content = content;
                }
            }

            return response;
        }

        /// <summary>
        /// Finds the configured source whose BaseUrl matches the image URL
        /// </summary>
        private SourceConfiguration FindSourceByUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;

            var sources = Plugin.Instance?.Configuration?.Sources;
            if (sources == null || sources.Count == 0) return null;

            foreach (var source in sources)
            {
                if (source.Enabled && !string.IsNullOrEmpty(source.BaseUrl))
                {
                    try
                    {
                        var sourceHost = new Uri(source.BaseUrl).Host;
                        var urlHost = new Uri(url).Host;
                        if (string.Equals(sourceHost, urlHost, StringComparison.OrdinalIgnoreCase))
                        {
                            return source;
                        }
                    }
                    catch
                    {
                        // Invalid URI, skip this source
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Applies a source's HTTP headers to the client (same options as the API calls, without X-API-Key)
        /// </summary>
        private void ApplyHeadersFromSource(HttpClient client, SourceConfiguration source)
        {
            SourceHeaders.Apply(client, source, null, _logger, LogLevel.Debug, "🖼️ [IMAGE DOWNLOAD]");
        }

        /// <summary>
        /// Extracts the item identifier for the API
        /// MULTI-SOURCE: checks that the ID has a prefix, otherwise re-extracts it from the file
        /// </summary>
        private string GetItemIdentifier(BaseItem item)
        {
            _logger.LogInformation("🖼️ [IMAGE PROVIDER] ========== GetItemIdentifier START ==========");
            _logger.LogInformation("🖼️ [IMAGE PROVIDER] Item.Name: '{ItemName}'", item.Name);
            _logger.LogInformation("🖼️ [IMAGE PROVIDER] Item.Type: {ItemType}", item.GetType().Name);
            _logger.LogInformation("🖼️ [IMAGE PROVIDER] Item.Path: '{ItemPath}'", item.Path ?? "(null)");
            
            // PRIORITY #1: for EPISODES, ALWAYS extract from the file first
            // Because ProviderIds is not yet available during the initial scan
            if (item is Episode episode && !string.IsNullOrEmpty(episode.Path))
            {
                _logger.LogInformation("🖼️ [IMAGE PROVIDER] ✅ Item is Episode, extracting ID from filename...");
                _logger.LogInformation("🖼️ [IMAGE PROVIDER] 📁 Full episode path: '{Path}'", episode.Path);
                
                var extractedId = ExtractIdFromFilename(episode.Path);
                if (!string.IsNullOrEmpty(extractedId))
                {
                    _logger.LogInformation("🖼️ [IMAGE PROVIDER] ✅ Extracted ID from episode filename: '{ExtractedId}'", extractedId);
                    return extractedId;
                }
                
                _logger.LogWarning("🖼️ [IMAGE PROVIDER] ⚠️ No ID found in episode filename!");
                _logger.LogWarning("🖼️ [IMAGE PROVIDER] ⚠️ Expected pattern {{PREFIX-ID}} in path, but none found");
            }
            else if (item is Episode)
            {
                _logger.LogWarning("🖼️ [IMAGE PROVIDER] ⚠️ Item is Episode but Path is NULL or EMPTY!");
            }

            // PRIORITY #2: for SEASONS, extract from the folder
            if (item is Season season && !string.IsNullOrEmpty(season.Path))
            {
                _logger.LogDebug("🖼️ [IMAGE PROVIDER] Item is Season, extracting ID from folder name...");
                _logger.LogDebug("🖼️ [IMAGE PROVIDER]   Season.Path: {Path}", season.Path);
                
                var extractedId = ExtractIdFromFolderName(season.Path);
                if (!string.IsNullOrEmpty(extractedId))
                {
                    _logger.LogInformation("🖼️ [IMAGE PROVIDER] ✅ Extracted ID from season folder: '{ExtractedId}'", extractedId);
                    return extractedId;
                }
                
                _logger.LogDebug("🖼️ [IMAGE PROVIDER] No ID found in season folder, checking ProviderIds...");
            }
            
            // PRIORITY #3: for SERIES, extract from the folder
            if (item is Series series && !string.IsNullOrEmpty(series.Path))
            {
                _logger.LogDebug("🖼️ [IMAGE PROVIDER] Item is Series, extracting ID from folder name...");
                _logger.LogDebug("🖼️ [IMAGE PROVIDER]   Series.Path: {Path}", series.Path);
                
                var extractedId = ExtractIdFromFolderName(series.Path);
                if (!string.IsNullOrEmpty(extractedId))
                {
                    _logger.LogInformation("🖼️ [IMAGE PROVIDER] ✅ Extracted ID from series folder: '{ExtractedId}'", extractedId);
                    return extractedId;
                }
                
                _logger.LogDebug("🖼️ [IMAGE PROVIDER] No ID found in series folder, checking ProviderIds...");
            }
            
            // PRIORITY #4: fall back to ProviderIds (for items already scanned)
            if (CustomId.Get(item.ProviderIds) is string customId)
            {
                _logger.LogInformation("🖼️ [IMAGE PROVIDER] Found custom ID in ProviderIds: '{CustomId}'", customId);
                
                // MULTI-SOURCE: check whether the ID contains a prefix (PREFIX-ID format)
                if (!string.IsNullOrEmpty(customId) && customId.Contains("-"))
                {
                    var parts = customId.Split('-', 2);
                    if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]) && !string.IsNullOrWhiteSpace(parts[1]))
                    {
                        _logger.LogInformation("🖼️ [IMAGE PROVIDER] ✅ ID has prefix format (PREFIX-ID), using it as-is");
                        return customId;
                    }
                }
                
                // If the ID has NO prefix (old plugin version)
                _logger.LogWarning("🖼️ [IMAGE PROVIDER] ⚠️ ID '{CustomId}' has NO PREFIX (old format from previous plugin version)", customId);
                _logger.LogWarning("🖼️ [IMAGE PROVIDER] ⚠️ Cannot use legacy ID without prefix, file must have {{PREFIX-ID}} pattern");
                return null;
            }
            
            _logger.LogWarning("🖼️ [IMAGE PROVIDER] ⚠️ Could not extract any ID with prefix for item '{ItemName}'", item.Name);
            _logger.LogWarning("🖼️ [IMAGE PROVIDER] ⚠️ Make sure your files are named with {{PREFIX-ID}} pattern (e.g., {{mv-EP001}})");
            
            return null;
        }
        
        /// <summary>
        /// Extracts the ID from the {PREFIX-ID} pattern in a file name
        /// MULTI-SOURCE: captures ANY prefix, not only the one of the global config
        /// Examples:
        /// - "Episode 01 {mv-123}.mkv" → "mv-123"
        /// - "S01E01 {yt-abc}.mkv" → "yt-abc"
        /// - "Video {custom-789}.mp4" → "custom-789"
        /// </summary>
        private string ExtractIdFromFilename(string filepath)
        {
            _logger.LogDebug("🖼️ [IMAGE PROVIDER] 🔍 ExtractIdFromFilename - INPUT filepath: '{Filepath}'", filepath ?? "(null)");
            
            if (string.IsNullOrEmpty(filepath))
            {
                _logger.LogDebug("🖼️ [IMAGE PROVIDER] ❌ Filepath is null or empty");
                return null;
            }

            // Extract the file name from the full path
            var filename = TagParser.GetOwnName(filepath);
            
            _logger.LogDebug("🖼️ [IMAGE PROVIDER] 📄 After Path.GetFileName(): '{Filename}'", filename ?? "(null)");

            // Shared generic {PREFIX-ID} pattern (TagParser): {mv-EP001}, {yt-video_123}, {custom-ABC-XYZ}
            _logger.LogDebug("🖼️ [IMAGE PROVIDER] 🔎 Regex pattern: '{Pattern}' (generic multi-source)", TagParser.Pattern);
            _logger.LogDebug("🖼️ [IMAGE PROVIDER] 🔎 Searching in text: '{Filename}'", filename);
            
            var fullId = TagParser.Extract(filename);
            
            _logger.LogDebug("🖼️ [IMAGE PROVIDER] 🔎 Regex match.Success = {Success}", fullId != null);
            
            if (fullId != null)
            {
                _logger.LogInformation("🖼️ [IMAGE PROVIDER] ✅ 📝 REGEX MATCHED! Extracted ID '{FullId}' (with prefix) from filename '{Filename}'", 
                    fullId, filename);
                
                return fullId;  // Retourne "PREFIX-ID" (ex: "mv-123", "yt-456")
            }

            _logger.LogDebug("🖼️ [IMAGE PROVIDER] ❌ No match found for pattern '{Pattern}' in filename '{Filename}'", 
                TagParser.Pattern, filename);
            
            return null;
        }
        
        /// <summary>
        /// Extracts the ID from the {PREFIX-ID} pattern in a folder name
        /// MULTI-SOURCE: captures ANY prefix
        /// Examples:
        /// - "Source YouTube {mv-SOURCE-YT}" → "mv-SOURCE-YT"
        /// - "Season 1 {yt-SEASON1}" → "yt-SEASON1"
        /// - "Series {custom-123}" → "custom-123"
        /// </summary>
        private string ExtractIdFromFolderName(string folderPath)
        {
            _logger.LogDebug("🖼️ [IMAGE PROVIDER] 🔍 ExtractIdFromFolderName - INPUT folderPath: '{FolderPath}'", folderPath ?? "(null)");
            
            if (string.IsNullOrEmpty(folderPath))
            {
                _logger.LogDebug("🖼️ [IMAGE PROVIDER] ❌ FolderPath is null or empty");
                return null;
            }

            // Extract only the folder name (last element of the path)
            // E.g.: "/var/media/Series/Season {mv-123}" → "Season {mv-123}"
            var folderName = TagParser.GetOwnName(folderPath);
            
            _logger.LogDebug("🖼️ [IMAGE PROVIDER] 📁 After Path.GetFileName(): '{FolderName}'", folderName ?? "(null)");

            // Shared generic {PREFIX-ID} pattern (TagParser)
            _logger.LogDebug("🖼️ [IMAGE PROVIDER] 🔎 Regex pattern: '{Pattern}' (generic multi-source)", TagParser.Pattern);
            _logger.LogDebug("🖼️ [IMAGE PROVIDER] 🔎 Searching in text: '{FolderName}'", folderName);
            
            var fullId = TagParser.Extract(folderName);
            
            _logger.LogDebug("🖼️ [IMAGE PROVIDER] 🔎 Regex match.Success = {Success}", fullId != null);
            
            if (fullId != null)
            {
                _logger.LogInformation("🖼️ [IMAGE PROVIDER] ✅ 📝 REGEX MATCHED! Extracted ID '{FullId}' (with prefix) from folder '{FolderName}'", 
                    fullId, folderName);
                
                return fullId;  // Retourne "PREFIX-ID" (ex: "mv-123", "yt-456")
            }

            _logger.LogDebug("🖼️ [IMAGE PROVIDER] ❌ No match found for pattern '{Pattern}' in folder '{FolderName}'", 
                TagParser.Pattern, folderName);
            
            return null;
        }

        /// <summary>
        /// Maps your API's image types to Jellyfin
        /// </summary>
        private ImageType? MapImageType(string apiType)
        {
            return apiType?.ToLower() switch
            {
                "primary" or "poster" => ImageType.Primary,
                "backdrop" or "background" => ImageType.Backdrop,
                "banner" => ImageType.Banner,
                "logo" => ImageType.Logo,
                "thumb" or "thumbnail" => ImageType.Thumb,
                "art" => ImageType.Art,
                _ => null
            };
        }
    }
}
