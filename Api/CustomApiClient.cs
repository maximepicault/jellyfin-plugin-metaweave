using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CustomMetadata.Configuration;
using Jellyfin.Plugin.CustomMetadata.Services;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Api
{
    public class CustomApiClient
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger _logger;
        private readonly MetadataCacheService _cacheService;
        private readonly JsonFieldMapper _fieldMapper;
        private readonly ResilientHttp _http;
        private readonly LocalSourceStore _localStore;

        public CustomApiClient(
            IHttpClientFactory httpClientFactory, 
            ILogger logger,
            MetadataCacheService cacheService,
            LocalSourceStore localStore = null)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _cacheService = cacheService;
            _fieldMapper = new JsonFieldMapper(logger);
            _http = new ResilientHttp(logger, localStore);
            _localStore = localStore;
        }

        /// <summary>
        /// URL missing or a placeholder from the config examples ("votre-api.com", "example.com"):
        /// the source is not configured, the API is not called.
        /// </summary>
        private static bool IsPlaceholderUrl(string url)
        {
            return string.IsNullOrWhiteSpace(url) ||
                   url.Contains("votre-api.com") ||
                   url.Contains("example.com");
        }

        /// <summary>
        /// Endpoint + mapping to use depending on the item type.
        /// "series" → Series*, "season"/"source" → Source*, everything else (video, episode, movie) → Video*.
        /// Label/MappingName are only used for logs.
        /// </summary>
        private static (string Endpoint, FieldMapping Mapping, string Label, string MappingName) ResolveEndpoint(
            string itemType,
            SourceConfiguration source)
        {
            switch (itemType?.ToLowerInvariant())
            {
                case "series":
                    return (source.SeriesEndpoint ?? "/series/{id}", source.SeriesFieldMappings ?? new FieldMapping(), "SERIES", "SeriesFieldMappings");
                case "season":
                case "source":
                    return (source.SourceEndpoint ?? "/sources/{id}", source.SourceFieldMappings ?? new FieldMapping(), "SOURCE/SEASON", "SourceFieldMappings");
                default:
                    return (source.VideoEndpoint ?? "/videos/{id}", source.VideoFieldMappings ?? new FieldMapping(), "VIDEO/EPISODE", "VideoFieldMappings");
            }
        }

        /// <summary>
        /// Gets the API key from a source or the global config
        /// </summary>
        private string GetApiKey(SourceConfiguration source = null)
        {
            if (source != null && !string.IsNullOrEmpty(source.ApiKey))
            {
                return source.ApiKey;
            }
            return Plugin.Instance?.Configuration.ApiKey ?? "";
        }

        private bool IsCacheEnabled()
        {
            return Plugin.Instance?.Configuration.EnableCache ?? true;
        }

        /// <summary>
        /// Builds the identifier used in the cache key: "{prefix}-{itemId}"
        /// The providers pass the ID WITHOUT prefix: without this step, two sources returning
        /// the same ID would share the same cache entry.
        /// MetadataCacheService.GetCacheKey() splits at the first hyphen ("mv1-SOURCE-YT" → MV1 / SOURCE-YT),
        /// which works because a source prefix cannot contain a hyphen.
        /// </summary>
        private static string GetCacheId(SourceConfiguration source, string itemId)
        {
            if (string.IsNullOrWhiteSpace(source?.Prefix))
            {
                return itemId;
            }

            return $"{source.Prefix.Trim().ToLowerInvariant()}-{itemId}";
        }

        /// <summary>
        /// Adds the HTTP headers (API key + the source's custom headers)
        /// Includes default headers to get around anti-bot protections
        /// </summary>
        private void AddHeadersToClient(HttpClient client, SourceConfiguration source = null)
        {
            _logger.LogInformation("📋 [HEADERS] ========== Adding HTTP headers ==========");
            SourceHeaders.Apply(client, source, GetApiKey(source), _logger, LogLevel.Information, "📋 [HEADERS]");
        }

        /// <summary>
        /// Fetches the metadata from the API (with cache)
        /// MULTI-SOURCE VERSION: uses a specific SourceConfiguration
        /// </summary>
        /// <param name="bypassCache">
        /// true for a refresh requested by the user (ItemLookupInfo.IsAutomated = false):
        /// the item's metadata AND image entries are invalidated before the call, otherwise
        /// "Replace metadata / images" would return the cached data (up to CacheTTLHours).
        /// Jellyfin calls the remote image providers after the metadata ones: they therefore find the cache empty.
        /// </param>
        public async Task<CustomMetadataResponse> GetMetadataAsync(
            string itemId, 
            string itemType,
            SourceConfiguration source,
            CancellationToken cancellationToken,
            bool bypassCache = false)
        {
            if (source == null)
            {
                _logger.LogError("📡 [API CLIENT] ❌ Source is NULL! Cannot fetch metadata.");
                return null;
            }
            
            _logger.LogDebug("📡 [API CLIENT] Using source '{SourceName}' (prefix: {Prefix})", source.Name, source.Prefix);
            
            var cacheKey = $"metadata:{itemType}:{GetCacheId(source, itemId)}";

            // Local source: reads a local file, no API to spare. No cache: a change
            // typed in the plugin is taken into account immediately, without waiting for expiry
            if (source.IsLocal)
            {
                return await FetchMetadataFromApiAsync(itemId, itemType, source, cancellationToken);
            }

            if (bypassCache && IsCacheEnabled())
            {
                var imagesKey = $"images:{GetCacheId(source, itemId)}:{itemType}";
                _logger.LogInformation("💾 [CACHE] Manual refresh → invalidating {MetadataKey} and {ImagesKey}", cacheKey, imagesKey);
                await _cacheService.InvalidateAsync(cacheKey);
                await _cacheService.InvalidateAsync(imagesKey);
            }

            // If the cache is disabled, call the API directly
            if (!IsCacheEnabled())
            {
                return await FetchMetadataFromApiAsync(itemId, itemType, source, cancellationToken);
            }

            // Use the cache
            return await _cacheService.GetOrCreateAsync(
                cacheKey,
                () => FetchMetadataFromApiAsync(itemId, itemType, source, cancellationToken),
                cancellationToken
            );
        }

        /// <summary>
        /// LEGACY VERSION (backward compatibility): fetches the metadata using the global config
        /// </summary>
        public async Task<CustomMetadataResponse> GetMetadataAsync(
            string itemId, 
            string itemType,
            CancellationToken cancellationToken)
        {
            _logger.LogDebug("📡 [API CLIENT] Using legacy mode (global config as source)");
            
            // Build a source from the global config
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                _logger.LogError("📡 [API CLIENT] ❌ Plugin configuration is NULL!");
                return null;
            }
            
            var globalSource = SourceConfiguration.FromLegacyConfig(config, "Global Configuration (Legacy)");
            
            // Call the multi-source version with the built source
            return await GetMetadataAsync(itemId, itemType, globalSource, cancellationToken);
        }

        /// <summary>
        /// MULTI-SOURCE VERSION: fetches the metadata from the API using a specific source
        /// </summary>
        private async Task<CustomMetadataResponse> FetchMetadataFromApiAsync(
            string itemId,
            string itemType,
            SourceConfiguration source,
            CancellationToken cancellationToken)
        {
            try
            {
                if (source == null)
                {
                    _logger.LogError("📡 [API CALL] ❌ Source is NULL!");
                    return null;
                }
                
                // Validation de l'URL
                if (IsPlaceholderUrl(source.BaseUrl))
                {
                    _logger.LogWarning("📡 [API CALL] ⚠️ No valid API URL configured for source '{SourceName}'!", source.Name ?? "(unnamed)");
                    _logger.LogWarning("📡 [API CALL] ⚠️ Current URL: '{Url}' - Please configure a real API URL in the plugin settings.", source.BaseUrl ?? "(null)");
                    return null;
                }
                
                _logger.LogInformation("📡 [API CALL] Source: '{SourceName}' (prefix: {Prefix})", source.Name, source.Prefix);
                
                // Determine the right endpoint and mapping depending on the type
                var (endpoint, mapping, label, mappingName) = ResolveEndpoint(itemType, source);
                _logger.LogDebug("📂 [TYPE] Processing as {Label}", label);
                _logger.LogInformation("🗺️ [MAPPING] Using source's {MappingName}", mappingName);
                
                var client = _httpClientFactory.CreateClient();
                AddHeadersToClient(client, source);

                var url = ResilientHttp.BuildUrl(source.BaseUrl, endpoint, itemId);
                
                _logger.LogInformation("📡 [API CALL] GET {Url}", url);
                
                var fetch = await _http.GetStringAsync(client, url, source, "📡 [API CALL]", cancellationToken);
                
                if (!fetch.IsSuccess)
                {
                    if (fetch.StatusCode.HasValue)
                    {
                        _logger.LogWarning("⚠️ [API RESPONSE] Status {StatusCode} for {Url}", (HttpStatusCode)fetch.StatusCode.Value, url);
                    }
                    else
                    {
                        _logger.LogError("❌ [API ERROR] Error fetching metadata for {ItemType} {ItemId}: {Error} ({Attempts} attempt(s))",
                            itemType, itemId, fetch.Error, fetch.Attempts);
                    }

                    return null;
                }

                var content = fetch.Content;
                _logger.LogDebug("📡 [API RESPONSE] Received {Length} bytes", content.Length);

                // Parse and map the JSON
                using var jsonDoc = JsonDocument.Parse(content);
                var metadata = _fieldMapper.MapToMetadata(jsonDoc, mapping);

                _logger.LogInformation("✅ [API SUCCESS] Metadata fetched for {ItemType} {ItemId}", itemType, itemId);
                return metadata;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ [API ERROR] Error fetching metadata for {ItemType} {ItemId}", itemType, itemId);
                return null;
            }
        }

        /// <summary>
        /// Fetches the images from the API (with cache)
        /// VERSION MULTI-SOURCE
        /// </summary>
        public async Task<List<ImageInfo>> GetImagesAsync(
            string itemId, 
            string itemType, 
            SourceConfiguration source,
            CancellationToken cancellationToken)
        {
            if (source == null)
            {
                _logger.LogError("🖼️ [IMAGE FETCH] ❌ Source is NULL! Cannot fetch images.");
                return new List<ImageInfo>();
            }
            
            _logger.LogDebug("🖼️ [IMAGE FETCH] Using source '{SourceName}' (prefix: {Prefix})", source.Name, source.Prefix);
            
            var cacheKey = $"images:{GetCacheId(source, itemId)}:{itemType}";

            if (!IsCacheEnabled() || source.IsLocal)
            {
                return await FetchImagesFromApiAsync(itemId, itemType, source, cancellationToken);
            }

            return await _cacheService.GetOrCreateAsync(
                cacheKey,
                () => FetchImagesFromApiAsync(itemId, itemType, source, cancellationToken),
                cancellationToken
            );
        }

        /// <summary>
        /// MULTI-SOURCE VERSION: fetches the images from the API using a specific source
        /// </summary>
        private async Task<List<ImageInfo>> FetchImagesFromApiAsync(
            string itemId,
            string itemType,
            SourceConfiguration source,
            CancellationToken cancellationToken)
        {
            try
            {
                if (source == null)
                {
                    _logger.LogError("🖼️ [IMAGE FETCH] ❌ Source is NULL!");
                    return new List<ImageInfo>();
                }
                
                // Validation de l'URL
                if (IsPlaceholderUrl(source.BaseUrl))
                {
                    _logger.LogWarning("🖼️ [IMAGE FETCH] ⚠️ No valid API URL configured for source '{SourceName}'!", source.Name ?? "(unnamed)");
                    _logger.LogWarning("🖼️ [IMAGE FETCH] ⚠️ Current URL: '{Url}' - Please configure a real API URL in the plugin settings.", source.BaseUrl ?? "(null)");
                    return new List<ImageInfo>();
                }
                
                _logger.LogInformation("🖼️ [IMAGE FETCH] Starting for itemType='{ItemType}', itemId='{ItemId}'", itemType, itemId);
                _logger.LogInformation("🖼️ [IMAGE FETCH] Source: '{SourceName}' (prefix: {Prefix})", source.Name, source.Prefix);
                
                // Determine the right endpoint and mapping depending on the type
                var (endpoint, mapping, label, mappingName) = ResolveEndpoint(itemType, source);
                _logger.LogInformation("🖼️ [IMAGE FETCH] Using {Label} mapping", label);
                _logger.LogInformation("🖼️ [IMAGE FETCH]   {MappingName}.PosterUrl: '{PosterUrl}'", mappingName, mapping.PosterUrl ?? "(null)");
                _logger.LogInformation("🖼️ [IMAGE FETCH]   {MappingName}.ImagesArray: '{ImagesArray}'", mappingName, mapping.ImagesArray ?? "(null)");

                // Call the metadata endpoint (which also contains the images)
                var client = _httpClientFactory.CreateClient();
                AddHeadersToClient(client, source);

                var url = ResilientHttp.BuildUrl(source.BaseUrl, endpoint, itemId);
                _logger.LogDebug("🖼️ [IMAGE FETCH] Calling URL: {Url}", url);
                
                var fetch = await _http.GetStringAsync(client, url, source, "🖼️ [IMAGE FETCH]", cancellationToken);

                if (!fetch.IsSuccess)
                {
                    if (fetch.StatusCode.HasValue)
                    {
                        _logger.LogWarning("🖼️ [IMAGE FETCH] ⚠️ API returned {StatusCode}", (HttpStatusCode)fetch.StatusCode.Value);
                    }
                    else
                    {
                        _logger.LogError("🖼️ [IMAGE FETCH] ❌ Error fetching images for item {ItemId} of type {ItemType}: {Error} ({Attempts} attempt(s))",
                            itemId, itemType, fetch.Error, fetch.Attempts);
                    }

                    return new List<ImageInfo>();
                }

                var content = fetch.Content;
                
                // Extract the images from the JSON with the right mapping
                using var jsonDoc = JsonDocument.Parse(content);
                return _fieldMapper.MapToImages(jsonDoc, mapping);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "🖼️ [IMAGE FETCH] ❌ Error fetching images for item {ItemId} of type {ItemType}", itemId, itemType);
                return new List<ImageInfo>();
            }
        }

        /// <summary>
        /// Fetches the COMPLETE episode list of a source/season through EpisodeListEndpoint (no cache)
        /// Returns null on error (even partial): an incomplete list would make false "missing" episodes
        /// appear or delete virtual episodes wrongly.
        /// </summary>
        public async Task<List<EpisodeListItem>> GetEpisodeListAsync(
            string sourceItemId,
            SourceConfiguration source,
            CancellationToken cancellationToken)
        {
            if (source == null || string.IsNullOrWhiteSpace(source.EpisodeListEndpoint))
            {
                return null;
            }

            if (IsPlaceholderUrl(source.BaseUrl))
            {
                _logger.LogWarning("📃 [EPISODE LIST] ⚠️ No valid API URL configured for source '{SourceName}'", source.Name ?? "(unnamed)");
                return null;
            }

            var mapping = source.EpisodeListMappings ?? new EpisodeListMapping();
            var baseUrl = ResilientHttp.BuildUrl(source.BaseUrl, source.EpisodeListEndpoint, sourceItemId);
            var paginated = !string.IsNullOrWhiteSpace(mapping.PageParameter);
            var maxPages = paginated ? Math.Max(1, mapping.MaxPages) : 1;

            var client = _httpClientFactory.CreateClient();
            AddHeadersToClient(client, source);

            var all = new List<EpisodeListItem>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < maxPages; i++)
            {
                var url = paginated ? AppendPageParameter(baseUrl, mapping, mapping.FirstPage + i) : baseUrl;

                try
                {
                    _logger.LogInformation("📃 [EPISODE LIST] GET {Url}", url);
                    var fetch = await _http.GetStringAsync(client, url, source, "📃 [EPISODE LIST]", cancellationToken);
                    if (!fetch.IsSuccess)
                    {
                        if (i > 0 && fetch.StatusCode == (int)HttpStatusCode.NotFound)
                        {
                            // Many paginated APIs return 404 after the last page
                            _logger.LogDebug("📃 [EPISODE LIST] 404 on page {Page} - end of list", mapping.FirstPage + i);
                            break;
                        }

                        if (fetch.StatusCode.HasValue)
                        {
                            _logger.LogWarning("📃 [EPISODE LIST] ⚠️ Status {StatusCode} for {Url}", (HttpStatusCode)fetch.StatusCode.Value, url);
                        }
                        else
                        {
                            _logger.LogError("📃 [EPISODE LIST] ❌ Error fetching {Url}: {Error} ({Attempts} attempt(s))", url, fetch.Error, fetch.Attempts);
                        }

                        // Incomplete list = null: the season is left as it is
                        return null;
                    }

                    var content = fetch.Content;
                    using var jsonDoc = JsonDocument.Parse(content);
                    var page = _fieldMapper.MapToEpisodeList(jsonDoc, mapping);
                    if (page == null)
                    {
                        // Array not found: error on the 1st page (bad config), end of list afterwards
                        if (i == 0)
                        {
                            return null;
                        }

                        break;
                    }

                    // Empty page, or an API that ignores the parameter and always returns the same page → end
                    var newItems = page.Where(e => seenIds.Add(e.Id)).ToList();
                    if (newItems.Count == 0)
                    {
                        break;
                    }

                    all.AddRange(newItems);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "📃 [EPISODE LIST] ❌ Error fetching {Url}", url);
                    return null;
                }
            }

            _logger.LogInformation("📃 [EPISODE LIST] ✅ {Count} episodes for source '{SourceName}' / '{SourceItemId}'",
                all.Count, source.Name, sourceItemId);
            return all;
        }

        /// <summary>
        /// People source: fetches ONE person by identifier (PersonEndpoint). No cache: the call is
        /// triggered by hand from the People screen. Error → Person null + a message in French (shown in the UI).
        /// </summary>
        public async Task<(PersonData Person, string Error)> GetPersonAsync(string externalId, SourceConfiguration source, CancellationToken cancellationToken)
        {
            if (source == null || !source.IsPeople)
            {
                return (null, "Source de personnes introuvable");
            }

            if (IsPlaceholderUrl(source.BaseUrl))
            {
                return (null, "URL de base absente ou d'exemple : configurez la source de personnes");
            }

            var url = ResilientHttp.BuildUrl(source.BaseUrl, source.PersonEndpoint, externalId);
            var client = _httpClientFactory.CreateClient();
            AddHeadersToClient(client, source);
            _logger.LogInformation("👥 [PEOPLE] GET {Url} (source '{SourceName}')", url, source.Name);
            var fetch = await _http.GetStringAsync(client, url, source, "👥 [PEOPLE]", cancellationToken);
            if (!fetch.IsSuccess)
            {
                var detail = fetch.StatusCode.HasValue ? $"HTTP {fetch.StatusCode}" : fetch.Error;
                return (null, $"La source n'a pas répondu ({detail}) pour l'identifiant « {externalId} »");
            }

            try
            {
                using var jsonDoc = JsonDocument.Parse(fetch.Content);
                var person = _fieldMapper.MapToPerson(jsonDoc, source.PersonMappings);
                if (person == null)
                {
                    return (null, "Réponse reçue mais aucun nom trouvé : vérifiez le champ « Nom » de la source de personnes");
                }

                person.Photo = PreviewMapper.ResolveUrl(person.Photo, source.BaseUrl);
                return (person, null);
            }
            catch (JsonException)
            {
                return (null, "La réponse n'est pas du JSON valide");
            }
        }

        /// <summary>
        /// Downloads an image with the source's HTTP config (headers, HTTP/2, timeout).
        /// Used where Jellyfin would otherwise download with its own client, without these settings
        /// (missing-episode thumbnails). Error → Data null + Error set, never an HTTP exception.
        /// </summary>
        /// <param name="landscapeIfPortrait">Converts a portrait image to 16:9 (PortraitThumbConverter)</param>
        public async Task<(byte[] Data, string MimeType, string Error)> DownloadImageAsync(
            string url,
            SourceConfiguration source,
            bool landscapeIfPortrait,
            CancellationToken cancellationToken)
        {
            // Image uploaded into a local source: read from the data folder
            if (LocalSourceStore.IsLocalImageUrl(url))
            {
                if (_localStore == null || !_localStore.TryReadImage(url, out var localData, out var localMime))
                {
                    return (null, null, "Image locale introuvable");
                }

                if (landscapeIfPortrait && PortraitThumbConverter.ToLandscapeIfPortrait(localData, _logger, url) is byte[] landscapeLocal)
                {
                    return (landscapeLocal, "image/jpeg", null);
                }

                return (localData, localMime, null);
            }

            var client = _httpClientFactory.CreateClient();
            SourceHeaders.Apply(client, source, null, _logger, LogLevel.Debug, "🖼️ [IMAGE DOWNLOAD]");
            client.Timeout = TimeSpan.FromSeconds(source != null && source.TimeoutSeconds > 0 ? source.TimeoutSeconds : 30);

            try
            {
                using var response = await client.GetAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return (null, null, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase} (HTTP/{response.Version})");
                }

                // Content-Type missing or generic (application/octet-stream) → deduced from the extension
                var mimeType = response.Content.Headers.ContentType?.MediaType;
                if (string.IsNullOrEmpty(mimeType) || !mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    mimeType = MediaBrowser.Model.Net.MimeTypes.GetMimeType(new Uri(url).AbsolutePath);
                }

                var data = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (landscapeIfPortrait && PortraitThumbConverter.ToLandscapeIfPortrait(data, _logger, url) is byte[] landscape)
                {
                    return (landscape, "image/jpeg", null);
                }

                return (data, mimeType, null);
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                return (null, null, ex is OperationCanceledException ? "Timeout" : ex.Message);
            }
        }

        /// <summary>
        /// "Test" button: downloads an episode's thumbnails (Primary/Thumb) and tells whether the
        /// portrait → 16:9 conversion would apply (real dimensions, EXIF, bars, readable format)
        /// </summary>
        private async Task<List<string>> CheckThumbnailsAsync(List<ImageInfo> images, SourceConfiguration source, CancellationToken cancellationToken)
        {
            var checks = new List<string>();
            var urls = (images ?? new List<ImageInfo>())
                .Where(i => i.Type == "Primary" || i.Type == "Thumb")
                .Select(i => i.Url)
                .Where(u => !string.IsNullOrEmpty(u))
                .Distinct()
                .Take(3);

            foreach (var url in urls)
            {
                var (data, _, error) = await DownloadImageAsync(url, source, false, cancellationToken);
                var verdict = data == null ? "téléchargement impossible : " + error : PortraitThumbConverter.Describe(data);
                _logger.LogInformation("🧪 [SOURCE TEST] 📐 {Url}: {Verdict}", url, verdict);
                checks.Add($"{url} : {verdict}");
            }

            return checks;
        }

        private static string AppendPageParameter(string url, EpisodeListMapping mapping, int page)
        {
            var separator = url.Contains('?') ? "&" : "?";
            return $"{url}{separator}{Uri.EscapeDataString(mapping.PageParameter.Trim())}={page}";
        }

        /// <summary>
        /// "Test" button of the configuration page: calls a source's endpoint (even unsaved)
        /// WITHOUT cache and returns the raw response + what the mapping extracts from it.
        /// itemType : "video" | "season" | "series" | "episodelist"
        /// </summary>
        public async Task<SourceTestResult> TestSourceAsync(
            SourceConfiguration source,
            string itemType,
            string itemId,
            CancellationToken cancellationToken)
        {
            var result = new SourceTestResult { ItemType = itemType, Id = itemId };

            if (source == null)
            {
                result.Error = "Aucune source fournie";
                return result;
            }

            if (IsPlaceholderUrl(source.BaseUrl))
            {
                result.Error = "URL de base absente ou d'exemple (votre-api.com / example.com)";
                return result;
            }

            var isEpisodeList = string.Equals(itemType, "episodelist", StringComparison.OrdinalIgnoreCase);
            var isPerson = string.Equals(itemType, "person", StringComparison.OrdinalIgnoreCase);
            FieldMapping mapping = null;
            string url;

            if (isPerson)
            {
                url = ResilientHttp.BuildUrl(source.BaseUrl, source.PersonEndpoint, itemId);
            }
            else if (isEpisodeList)
            {
                if (string.IsNullOrWhiteSpace(source.EpisodeListEndpoint))
                {
                    result.Error = "Endpoint « Liste des épisodes » vide";
                    return result;
                }

                var listMapping = source.EpisodeListMappings ?? new EpisodeListMapping();
                url = ResilientHttp.BuildUrl(source.BaseUrl, source.EpisodeListEndpoint, itemId);
                if (!string.IsNullOrWhiteSpace(listMapping.PageParameter))
                {
                    url = AppendPageParameter(url, listMapping, listMapping.FirstPage);
                }
            }
            else
            {
                string endpoint;
                (endpoint, mapping, _, _) = ResolveEndpoint(itemType, source);
                url = ResilientHttp.BuildUrl(source.BaseUrl, endpoint, itemId);
            }

            _logger.LogInformation("🧪 [SOURCE TEST] GET {Url} (source '{SourceName}', type {ItemType})", url, source.Name, itemType);

            var client = _httpClientFactory.CreateClient();
            AddHeadersToClient(client, source);
            var fetch = await _http.GetStringAsync(client, url, source, "🧪 [SOURCE TEST]", cancellationToken);

            result.Url = url;
            result.RequestHeaders = SourceHeaders.Describe(client);
            result.HttpVersion = fetch.HttpVersion;
            result.ErrorBody = fetch.ErrorBody;
            result.StatusCode = fetch.StatusCode;
            result.Attempts = fetch.Attempts;
            result.DurationMs = fetch.DurationMs;
            result.Error = fetch.Error;

            if (!fetch.IsSuccess)
            {
                _logger.LogWarning("🧪 [SOURCE TEST] ❌ {Error} for {Url}", fetch.Error, url);
                return result;
            }

            result.RawJson = fetch.Content.Length > SourceTestResult.MaxRawLength
                ? fetch.Content.Substring(0, SourceTestResult.MaxRawLength) + "\n… (tronqué)"
                : fetch.Content;

            try
            {
                using var jsonDoc = JsonDocument.Parse(fetch.Content);
                if (isPerson)
                {
                    result.Person = _fieldMapper.MapToPerson(jsonDoc, source.PersonMappings);
                    if (result.Person == null)
                    {
                        result.Error = "Aucun nom trouvé : vérifiez le champ « Nom » du mapping";
                        return result;
                    }

                    result.Person.Photo = PreviewMapper.ResolveUrl(result.Person.Photo, source.BaseUrl);
                }
                else if (isEpisodeList)
                {
                    var episodes = _fieldMapper.MapToEpisodeList(jsonDoc, source.EpisodeListMappings ?? new EpisodeListMapping());
                    if (episodes == null)
                    {
                        result.Error = "Tableau d'épisodes introuvable : vérifiez « Tableau d'épisodes » dans le mapping";
                        return result;
                    }

                    episodes.ForEach(e => e.RawJson = null);
                    result.EpisodeCount = episodes.Count;
                    result.Episodes = episodes.Take(SourceTestResult.MaxEpisodes).ToList();
                }
                else
                {
                    result.Metadata = _fieldMapper.MapToMetadata(jsonDoc, mapping);
                    result.Images = _fieldMapper.MapToImages(jsonDoc, mapping);
                    if (!string.Equals(itemType, "season", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(itemType, "series", StringComparison.OrdinalIgnoreCase))
                    {
                        result.ThumbnailChecks = await CheckThumbnailsAsync(result.Images, source, cancellationToken);
                    }
                }

                result.Success = true;
                _logger.LogInformation("🧪 [SOURCE TEST] ✅ HTTP {StatusCode} in {DurationMs} ms for {Url}", fetch.StatusCode, fetch.DurationMs, url);
            }
            catch (JsonException ex)
            {
                result.Error = "La réponse n'est pas du JSON valide : " + ex.Message;
                _logger.LogWarning("🧪 [SOURCE TEST] ❌ Invalid JSON from {Url}: {Message}", url, ex.Message);
            }

            return result;
        }
    }

    /// <summary>
    /// Result of the "Test" button of the configuration page
    /// </summary>
    public class SourceTestResult
    {
        public const int MaxRawLength = 20000;
        public const int MaxEpisodes = 20;

        public bool Success { get; set; }
        public string ItemType { get; set; }
        public string Id { get; set; }
        public string Url { get; set; }
        public int? StatusCode { get; set; }
        public int Attempts { get; set; }
        public long DurationMs { get; set; }
        public string Error { get; set; }
        public string HttpVersion { get; set; }
        public List<string> RequestHeaders { get; set; }

        /// <summary>Start of an error response body (Cloudflare page, "Invalid CORS request" message…)</summary>
        public string ErrorBody { get; set; }
        public string RawJson { get; set; }
        public CustomMetadataResponse Metadata { get; set; }
        public PersonData Person { get; set; }
        public List<ImageInfo> Images { get; set; }

        /// <summary>Episode thumbnails: real dimensions and the portrait → 16:9 conversion decision</summary>
        public List<string> ThumbnailChecks { get; set; }
        public int? EpisodeCount { get; set; }
        public List<EpisodeListItem> Episodes { get; set; }
    }

    // Classes to map your API's responses
    public class CustomMetadataResponse
    {
        public string Title { get; set; }
        public string Overview { get; set; }
        public string SeriesName { get; set; }
        public List<string> Tags { get; set; }
        public List<string> Genres { get; set; }
        public List<CastMember> Cast { get; set; }
        public DateTime? PremiereDate { get; set; }
        public float? CommunityRating { get; set; }

        /// <summary>Episode number (FieldMapping.IndexNumber)</summary>
        public int? IndexNumber { get; set; }
    }

    public class CastMember
    {
        public string Name { get; set; }
        public string Role { get; set; }
        public string Type { get; set; } // "Actor", "Director", "Writer", etc.
        public int? Order { get; set; }

        /// <summary>URL of the person's photo (http/https), shown on their page in Jellyfin</summary>
        public string Image { get; set; }
    }

    public class ImageInfo
    {
        public string Url { get; set; }
        public string Type { get; set; } // "Primary", "Backdrop", "Banner", etc.
        public int? Width { get; set; }
        public int? Height { get; set; }
    }

    /// <summary>
    /// An episode of the list returned by EpisodeListEndpoint
    /// </summary>
    public class EpisodeListItem
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Overview { get; set; }
        public DateTime? PremiereDate { get; set; }
        public int? IndexNumber { get; set; }

        /// <summary>Duration of the episode (EpisodeListMapping.Duration) → RunTimeTicks</summary>
        public long? RunTimeTicks { get; set; }

        public string ThumbUrl { get; set; }
        public string PreviewUrl { get; set; }

        /// <summary>Overview recognised as a list of tags/genres (VideoFieldMappings.OverviewListTarget): cleared</summary>
        public bool OverviewIsList { get; set; }

        /// <summary>Items of this list</summary>
        public List<string> OverviewItems { get; set; }

        /// <summary>Raw JSON of the element (missing-episodes cache)</summary>
        public string RawJson { get; set; }
    }
}
