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
    /// Metadata provider for seasons
    /// ADAPTED for seasons used as content SOURCES
    /// MULTI-SOURCE: automatically detects the source from the prefix
    /// </summary>
    public class CustomSeasonProvider : IRemoteMetadataProvider<Season, SeasonInfo>
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<CustomSeasonProvider> _logger;
        private readonly CustomApiClient _apiClient;
        private readonly SourceResolver _sourceResolver;
        private readonly CastOverlayStore _castOverlay;
        private readonly PeopleStore _people;

        public CustomSeasonProvider(
            IHttpClientFactory httpClientFactory,
            ILogger<CustomSeasonProvider> logger,
            CustomApiClient apiClient,
            SourceResolver sourceResolver,
            CastOverlayStore castOverlay,
            PeopleStore people)
        {
            _castOverlay = castOverlay;
            _people = people;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _apiClient = apiClient;
            _sourceResolver = sourceResolver;
        }

        public string Name => "Custom Metadata Provider";

        /// <summary>
        /// Fetches the metadata of a season (= a content SOURCE)
        /// MULTI-SOURCE: automatically detects the source from the ID prefix
        /// Extracts the ID from the folder name: "Source A {mv1-sourceA}"
        /// </summary>
        public async Task<MetadataResult<Season>> GetMetadata(
            SeasonInfo info,
            CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Season>();

            try
            {
                if (!(Plugin.Instance?.Configuration.EnableMetadata ?? true))
                {
                    _logger.LogDebug("📁 [SEASON PROVIDER] Metadata disabled in plugin settings - skipping {SeasonName}", info.Name);
                    return result;
                }

                _logger.LogInformation("📁 [SEASON PROVIDER] ========== Processing season: {SeasonName} ==========", info.Name);
                
                // Extract the source ID (with prefix) from the season's FOLDER NAME. info.Name is the title parsed by
                // Jellyfin ("Season 2024"): the {prefix-id} tag has been stripped from it. Same rule as the other providers (info.Path).
                var idWithPrefix = ExtractSourceId(info.Path) ?? ExtractSourceId(info.Name) ?? CustomId.Get(info.ProviderIds);
                
                if (string.IsNullOrEmpty(idWithPrefix))
                {
                    _logger.LogDebug("📁 [SEASON PROVIDER] ❌ No source ID found in season name '{SeasonName}'", info.Name);
                    return result;
                }

                _logger.LogInformation(
                    "📁 [SEASON PROVIDER] ✅ Found identifier '{IdWithPrefix}' for season", idWithPrefix);

                // MULTI-SOURCE: detect the source from the prefix
                var prefix = _sourceResolver.ExtractPrefixFromId(idWithPrefix);
                SourceConfiguration source = null;
                string itemId = idWithPrefix;
                
                if (!string.IsNullOrEmpty(prefix))
                {
                    _logger.LogDebug("📁 [SEASON PROVIDER] 🔍 Extracted prefix '{Prefix}' from ID", prefix);
                    source = _sourceResolver.GetSourceByPrefix(prefix);
                    
                    if (source != null)
                    {
                        // Multi-source mode: extract the ID without prefix
                        itemId = _sourceResolver.ExtractIdWithoutPrefix(idWithPrefix);
                        _logger.LogInformation("📁 [SEASON PROVIDER] 🌐 Multi-source mode: Using source '{SourceName}' (prefix: {Prefix})", 
                            source.Name, prefix);
                        _logger.LogInformation("📁 [SEASON PROVIDER] 🆔 Item ID (without prefix): '{ItemId}'", itemId);
                    }
                    else
                    {
                        _logger.LogWarning("📁 [SEASON PROVIDER] ⚠️ No source found for prefix '{Prefix}' - SKIPPING", prefix);
                        _logger.LogWarning("📁 [SEASON PROVIDER] ⚠️ Please configure a source with this prefix in plugin settings");
                        return result;  // ← EARLY RETURN
                    }
                }
                else
                {
                    _logger.LogDebug("📁 [SEASON PROVIDER] 📝 No prefix detected - SKIPPING (folder doesn't have {{PREFIX-ID}} pattern)");
                    return result;  // ← EARLY RETURN
                }

                // At this point, source is necessarily non-null
                if (source == null)
                {
                    _logger.LogError("📁 [SEASON PROVIDER] ❌ BUG: source is null after all checks!");
                    return result;
                }

                // API call to fetch the source's metadata
                var metadata = await _apiClient.GetMetadataAsync(itemId, "season", source, cancellationToken, bypassCache: !info.IsAutomated);

                if (metadata == null)
                {
                    _logger.LogWarning("No metadata returned for source '{ItemId}'", itemId);
                    return result;
                }

                // Build the Season object (which represents the source)
                var season = new Season
                {
                    Name = GetSeasonName(metadata.Title, info.Name),
                    Overview = metadata.Overview,
                    PremiereDate = metadata.PremiereDate,

                    // Always return the season number: otherwise "Replace all metadata" sets it to null
                    IndexNumber = info.IndexNumber
                };

                // Add the tags
                if (metadata.Tags != null && metadata.Tags.Any())
                {
                    season.Tags = metadata.Tags.ToArray();
                }

                // Store the source ID in the ProviderIds (with prefix)
                season.ProviderIds["custom"] = idWithPrefix;

                result.Item = season;
                result.HasMetadata = true;

                // Season cast: the API one (season mapping) + the cast added in the People screen (by its tag)
                var overlayCast = _people.ResolveCast(_castOverlay.Get("season", new[] { idWithPrefix }));
                CastMapper.AddCast(result, overlayCast.Count > 0 ? MetadataMerger.UnionCast(new[] { metadata.Cast, overlayCast }) : metadata.Cast, _logger);

                _logger.LogInformation(
                    "Successfully enriched metadata for source '{IdWithPrefix}'", idWithPrefix);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error enriching metadata for season {SeasonName}", info.Name);
            }

            return result;
        }

        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(
            SeasonInfo searchInfo,
            CancellationToken cancellationToken)
        {
            return await Task.FromResult(Enumerable.Empty<RemoteSearchResult>());
        }

        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            var client = _httpClientFactory.CreateClient();
            return client.GetAsync(url, cancellationToken);
        }

        /// <summary>
        /// Displayed name of the season/source, in priority order:
        /// 1. Title returned by the API
        /// 2. Folder name without the tag: "Source A {mv1-sourceA}" → "Source A"
        /// 3. DefaultSourceName from the configuration (if the folder holds only the tag)
        /// </summary>
        private string GetSeasonName(string apiTitle, string seasonName)
        {
            if (!string.IsNullOrWhiteSpace(apiTitle))
            {
                return apiTitle;
            }

            var nameWithoutTag = TagParser.Strip(TagParser.GetOwnName(seasonName ?? string.Empty));
            if (!string.IsNullOrWhiteSpace(nameWithoutTag))
            {
                _logger.LogDebug("📁 [SEASON PROVIDER] No title from API, using folder name '{Name}'", nameWithoutTag);
                return nameWithoutTag;
            }

            var defaultName = Plugin.Instance?.Configuration.DefaultSourceName;
            if (!string.IsNullOrWhiteSpace(defaultName))
            {
                _logger.LogDebug("📁 [SEASON PROVIDER] No title from API nor folder, using DefaultSourceName '{Name}'", defaultName);
                return defaultName;
            }

            return seasonName;
        }

        /// <summary>
        /// Extracts the source ID from the folder name
        /// MULTI-SOURCE: captures ANY prefix, not only the one of the global config
        /// Exemples :
        /// - "Source A {mv1-sourceA}" → "mv1-sourceA"
        /// - "YouTube Videos {yt-youtube}" → "yt-youtube"
        /// - "Conferences 2024 {custom-conf2024}" → "custom-conf2024"
        /// </summary>
        private string ExtractSourceId(string seasonName)
        {
            if (string.IsNullOrEmpty(seasonName))
                return null;

            // Extract only the folder name (last element of the path)
            // Ex: "/var/media/Serie/Saison {mv-123}" → "Saison {mv-123}"
            var folderName = TagParser.GetOwnName(seasonName);
            _logger.LogDebug("📁 [SEASON PROVIDER] Extracted folder name: {FolderName} from path: {SeasonName}", folderName, seasonName);

            var fullId = TagParser.Extract(folderName);
            if (fullId != null)
            {
                _logger.LogDebug("📁 [SEASON PROVIDER] ✅ Extracted ID '{FullId}' (with prefix) from folder name '{FolderName}' using pattern '{Pattern}'", 
                    fullId, folderName, TagParser.Pattern);
                return fullId;  // Retourne "PREFIX-ID" (ex: "mv1-sourceA", "yt-youtube")
            }

            _logger.LogDebug("📁 [SEASON PROVIDER] ❌ No {{PREFIX-ID}} pattern found in folder name '{FolderName}'", folderName);
            return null;
        }
    }
}
