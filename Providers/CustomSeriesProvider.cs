using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CustomMetadata.Api;
using Jellyfin.Plugin.CustomMetadata.Configuration;
using Jellyfin.Plugin.CustomMetadata.Services;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Providers
{
    /// <summary>
    /// Metadata provider for TV series
    /// MULTI-SOURCE: automatically detects the source from the prefix
    /// </summary>
    public class CustomSeriesProvider : IRemoteMetadataProvider<Series, SeriesInfo>
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<CustomSeriesProvider> _logger;
        private readonly CustomApiClient _apiClient;
        private readonly SourceResolver _sourceResolver;
        private readonly CastOverlayStore _castOverlay;
        private readonly PeopleStore _people;

        public CustomSeriesProvider(
            IHttpClientFactory httpClientFactory,
            ILogger<CustomSeriesProvider> logger,
            CustomApiClient apiClient,
            SourceResolver sourceResolver,
            CastOverlayStore castOverlay,
            PeopleStore people)
        {
            _castOverlay = castOverlay;
            _people = people;
            logger.LogInformation("📺 [SERIES PROVIDER] ========== PROVIDER INITIALIZING ==========");
            
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _apiClient = apiClient;
            _sourceResolver = sourceResolver;
            
            logger.LogInformation("📺 [SERIES PROVIDER] ========== PROVIDER READY ==========");
        }

        public string Name => "Custom Metadata Provider";

        /// <summary>
        /// Fetches the metadata of a series from the custom API
        /// MULTI-SOURCE: automatically detects the source from the ID prefix
        /// </summary>
        public async Task<MetadataResult<Series>> GetMetadata(
            SeriesInfo info,
            CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Series>();

            try
            {
                if (!(Plugin.Instance?.Configuration.EnableMetadata ?? true))
                {
                    _logger.LogDebug("📺 [SERIES PROVIDER] Metadata disabled in plugin settings - skipping {SeriesName}", info.Name);
                    return result;
                }

                _logger.LogInformation("📺 [SERIES PROVIDER] ========== Processing series: {SeriesName} ==========", info.Name);
                
                // Get the custom ID from the {PREFIX-ID} pattern or from the folder name
                var idWithPrefix = GetItemIdentifier(info);
                if (string.IsNullOrEmpty(idWithPrefix))
                {
                    _logger.LogDebug("📺 [SERIES PROVIDER] ❌ No identifier found for series {SeriesName}", info.Name);
                    return result;
                }

                _logger.LogInformation("📺 [SERIES PROVIDER] ✅ Found identifier '{IdWithPrefix}' for series {SeriesName}", 
                    idWithPrefix, info.Name);

                // MULTI-SOURCE: detect the source from the prefix
                var prefix = _sourceResolver.ExtractPrefixFromId(idWithPrefix);
                SourceConfiguration source = null;
                string itemId = idWithPrefix;
                
                if (!string.IsNullOrEmpty(prefix))
                {
                    _logger.LogDebug("📺 [SERIES PROVIDER] 🔍 Extracted prefix '{Prefix}' from ID", prefix);
                    source = _sourceResolver.GetSourceByPrefix(prefix);
                    
                    if (source != null)
                    {
                        // Multi-source mode: extract the ID without prefix
                        itemId = _sourceResolver.ExtractIdWithoutPrefix(idWithPrefix);
                        _logger.LogInformation("📺 [SERIES PROVIDER] 🌐 Multi-source mode: Using source '{SourceName}' (prefix: {Prefix})", 
                            source.Name, prefix);
                        _logger.LogInformation("📺 [SERIES PROVIDER] 🆔 Item ID (without prefix): '{ItemId}'", itemId);
                    }
                    else
                    {
                        _logger.LogWarning("📺 [SERIES PROVIDER] ⚠️ No source found for prefix '{Prefix}' - SKIPPING", prefix);
                        _logger.LogWarning("📺 [SERIES PROVIDER] ⚠️ Please configure a source with this prefix in plugin settings");
                        return result;  // ← EARLY RETURN
                    }
                }
                else
                {
                    _logger.LogDebug("📺 [SERIES PROVIDER] 📝 No prefix detected - SKIPPING (folder doesn't have {{PREFIX-ID}} pattern)");
                    return result;  // ← EARLY RETURN
                }

                // At this point, source is necessarily non-null
                if (source == null)
                {
                    _logger.LogError("📺 [SERIES PROVIDER] ❌ BUG: source is null after all checks!");
                    return result;
                }

                // API call
                var metadata = await _apiClient.GetMetadataAsync(itemId, "series", source, cancellationToken, bypassCache: !info.IsAutomated);
                
                if (metadata == null)
                {
                    _logger.LogWarning("No metadata returned for series {SeriesName}", info.Name);
                    return result;
                }

                // Build the Series object with the enriched metadata
                var series = new Series
                {
                    Name = metadata.Title,
                    Overview = metadata.Overview,
                    PremiereDate = metadata.PremiereDate,
                    CommunityRating = metadata.CommunityRating
                };

                // Add the tags (labels)
                if (metadata.Tags != null && metadata.Tags.Any())
                {
                    series.Tags = metadata.Tags.ToArray();
                }

                // Add the genres
                if (metadata.Genres != null && metadata.Genres.Any())
                {
                    foreach (var genre in metadata.Genres)
                    {
                        series.AddGenre(genre);
                    }
                }

                // Store the custom ID for future reference (with prefix)
                series.ProviderIds["custom"] = idWithPrefix;

                result.Item = series;
                result.HasMetadata = true;

                // Cast added to this series in the People screen (by its tag), without duplicates with the API cast
                var overlayCast = _people.ResolveCast(_castOverlay.Get("series", new[] { idWithPrefix }));
                CastMapper.AddCast(result, overlayCast.Count > 0 ? MetadataMerger.UnionCast(new[] { metadata.Cast, overlayCast }) : metadata.Cast, _logger);

                _logger.LogInformation("Successfully enriched metadata for series {SeriesName}", series.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error enriching metadata for series {SeriesName}", info.Name);
            }

            return result;
        }

        /// <summary>
        /// Series search (optional)
        /// </summary>
#pragma warning disable CS1998 // Async method lacks 'await' operators
        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(
            SeriesInfo searchInfo,
            CancellationToken cancellationToken)
        {
            var results = new List<RemoteSearchResult>();

            try
            {
                _logger.LogDebug("Search requested for series: {SearchName}", searchInfo.Name);
                // You can implement a search in your API here
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching for series {SearchName}", searchInfo.Name);
            }

            return results;
        }
#pragma warning restore CS1998

        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            var client = _httpClientFactory.CreateClient();
            return client.GetAsync(url, cancellationToken);
        }

        /// <summary>
        /// Extracts the series identifier for the API
        /// Priority: 1. Custom API ID, 2. Manual extraction, 3. IMDb ID, 4. Series name
        /// </summary>
        private string GetItemIdentifier(SeriesInfo info)
        {
            // Option 1: use the custom ID if present in the {PREFIX-ID} pattern (via CustomExternalId)
            if (CustomId.Get(info.ProviderIds) is string customId)
            {
                _logger.LogDebug("📺 [SERIES PROVIDER] 🎯 Using CustomAPI ID from ProviderIds: {CustomId}", customId);
                return customId;
            }

            // Option 2: manual extraction from the series folder name
            var extractedId = ExtractSeriesIdFromName(info.Name);
            if (!string.IsNullOrEmpty(extractedId))
            {
                _logger.LogDebug("📺 [SERIES PROVIDER] 🎯 Manually extracted ID from series name: {ExtractedId}", extractedId);
                return extractedId;
            }

            // Option 3: use the IMDb ID if available
            if (info.ProviderIds.TryGetValue("Imdb", out var imdbId))
            {
                _logger.LogDebug("📺 [SERIES PROVIDER] 🎯 Using IMDb ID: {ImdbId}", imdbId);
                return imdbId;
            }

            // Option 4: use the series name
            _logger.LogDebug("📺 [SERIES PROVIDER] 🎯 Using series name: {SeriesName}", info.Name);
            return info.Name;
        }

        /// <summary>
        /// Extracts the ID from the {PREFIX-ID} pattern in the series folder name
        /// MULTI-SOURCE: captures ANY prefix, not only the one of the global config
        /// Exemples:
        /// - "Breaking Bad {mv1-bb}" → "mv1-bb"
        /// - "YouTube Series {yt-series123}" → "yt-series123"
        /// - "My Show {custom-show789}" → "custom-show789"
        /// </summary>
        private string ExtractSeriesIdFromName(string seriesName)
        {
            if (string.IsNullOrEmpty(seriesName))
                return null;

            // Extract only the folder name (last element of the path)
            var folderName = TagParser.GetOwnName(seriesName);
            _logger.LogDebug("📺 [SERIES PROVIDER] Extracted folder name: {FolderName} from path: {SeriesName}", folderName, seriesName);

            // Shared generic {PREFIX-ID} pattern (TagParser): {mv1-123}, {yt-ABC-123}, {tw-SOURCE-YT}
            var fullId = TagParser.Extract(folderName);
            if (fullId != null)
            {
                _logger.LogDebug("📺 [SERIES PROVIDER] ✅ Extracted ID '{FullId}' (with prefix) from folder name '{FolderName}'", 
                    fullId, folderName);
                return fullId;  // Retourne "PREFIX-ID" (ex: "mv1-bb", "yt-series123")
            }

            _logger.LogDebug("📺 [SERIES PROVIDER] ❌ No {{PREFIX-ID}} pattern found in folder name '{FolderName}'", folderName);
            return null;
        }
    }
}
