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
    /// Metadata provider for episodes
    /// Uses the parent series' name and ID for the request
    /// MULTI-SOURCE: automatically detects the source from the prefix
    /// </summary>
    public class CustomEpisodeProvider : IRemoteMetadataProvider<Episode, EpisodeInfo>
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<CustomEpisodeProvider> _logger;
        private readonly CustomApiClient _apiClient;
        private readonly SourceResolver _sourceResolver;
        private readonly CastOverlayStore _castOverlay;
        private readonly PeopleStore _people;
        private readonly CastInheritance _inheritance;

        public CustomEpisodeProvider(
            IHttpClientFactory httpClientFactory,
            ILogger<CustomEpisodeProvider> logger,
            CustomApiClient apiClient,
            SourceResolver sourceResolver,
            CastOverlayStore castOverlay,
            PeopleStore people,
            CastInheritance inheritance)
        {
            _inheritance = inheritance;
            _castOverlay = castOverlay;
            _people = people;
            logger.LogInformation("🎬 [EPISODE PROVIDER] ========== PROVIDER INITIALIZING ==========");
            
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _apiClient = apiClient;
            _sourceResolver = sourceResolver;
            
            logger.LogInformation("🎬 [EPISODE PROVIDER] ========== PROVIDER READY ==========");
        }

        public string Name => "Custom Metadata Provider";

        /// <summary>
        /// Fetches the metadata of an episode from the custom API
        /// MULTI-SOURCE: automatically detects the source from the ID prefix
        /// </summary>
        public async Task<MetadataResult<Episode>> GetMetadata(
            EpisodeInfo info,
            CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Episode>();

            try
            {
                if (!(Plugin.Instance?.Configuration.EnableMetadata ?? true))
                {
                    _logger.LogDebug("🎬 [EPISODE PROVIDER] Metadata disabled in plugin settings - skipping {EpisodeName}", info.Name);
                    return result;
                }

                // Virtual episode (no file) without a readable "custom" ID: its metadata is handled by
                // the missing-episode analysis, no point guessing an ID and calling the API for nothing.
                if (string.IsNullOrEmpty(info.Path) && CustomId.Get(info.ProviderIds) == null)
                {
                    _logger.LogDebug("🎬 [EPISODE PROVIDER] Virtual episode without id, managed by missing-episodes analysis - skipping {EpisodeName}", info.Name);
                    return result;
                }

                _logger.LogInformation("🎬 [EPISODE PROVIDER] ========== Processing episode: {EpisodeName} ==========", info.Name);
                _logger.LogDebug("🎬 [EPISODE PROVIDER] Episode details - Name: {Name}, Season: {Season}, Episode: {Episode}", 
                    info.Name, info.ParentIndexNumber, info.IndexNumber);
                
                // Get the custom ID from the {PREFIX-ID} pattern in the file name
                // Ex: {mv1-123}, {yt-456}, {custom-789}
                var idWithPrefix = GetEpisodeIdentifier(info);
                
                // If there is no custom ID, use the parent series + S/E
                if (string.IsNullOrEmpty(idWithPrefix))
                {
                    _logger.LogDebug("🎬 [EPISODE PROVIDER] 🔍 No direct ID found, trying series-based identifier");
                    idWithPrefix = GetSeriesBasedIdentifier(info);
                }

                if (string.IsNullOrEmpty(idWithPrefix))
                {
                    _logger.LogDebug("🎬 [EPISODE PROVIDER] ❌ No identifier found for episode {EpisodeName}", info.Name);
                    return result;
                }

                _logger.LogInformation(
                    "🎬 [EPISODE PROVIDER] ✅ Found identifier '{IdWithPrefix}' for episode '{EpisodeName}'",
                    idWithPrefix, info.Name);

                // MULTI-SOURCE: detect the source from the prefix
                var prefix = _sourceResolver.ExtractPrefixFromId(idWithPrefix);
                SourceConfiguration source = null;
                string itemId = idWithPrefix;
                
                if (!string.IsNullOrEmpty(prefix))
                {
                    _logger.LogDebug("🎬 [EPISODE PROVIDER] 🔍 Extracted prefix '{Prefix}' from ID", prefix);
                    source = _sourceResolver.GetSourceByPrefix(prefix);
                    
                    if (source != null)
                    {
                        // Multi-source mode: extract the ID without prefix
                        itemId = _sourceResolver.ExtractIdWithoutPrefix(idWithPrefix);
                        _logger.LogInformation("🎬 [EPISODE PROVIDER] 🌐 Multi-source mode: Using source '{SourceName}' (prefix: {Prefix})", 
                            source.Name, prefix);
                        _logger.LogInformation("🎬 [EPISODE PROVIDER] 🆔 Item ID (without prefix): '{ItemId}'", itemId);
                    }
                    else
                    {
                        _logger.LogWarning("🎬 [EPISODE PROVIDER] ⚠️ No source found for prefix '{Prefix}' - SKIPPING", prefix);
                        _logger.LogWarning("🎬 [EPISODE PROVIDER] ⚠️ Please configure a source with this prefix in plugin settings");
                        return result;  // ← EARLY RETURN: prefix found but no source configured
                    }
                }
                else
                {
                    _logger.LogDebug("🎬 [EPISODE PROVIDER] 📝 No prefix detected - SKIPPING (file doesn't have {{PREFIX-ID}} pattern)");
                    return result;  // ← EARLY RETURN: no prefix = not a custom file
                }

                // At this point, source is necessarily non-null
                if (source == null)
                {
                    _logger.LogError("🎬 [EPISODE PROVIDER] ❌ BUG: source is null after all checks!");
                    return result;
                }

                // API call (always in multi-source mode now)
                var metadata = await _apiClient.GetMetadataAsync(itemId, "episode", source, cancellationToken, bypassCache: !info.IsAutomated);

                // File bundling several API episodes ("Ep {dm-1} {dm-2}.mkv"): the following IDs complete
                // the first one (lists merged without duplicates, single values as a fallback when empty in the first)
                var extraMetadata = new List<CustomMetadataResponse>();
                foreach (var extraId in GetExtraIds(info, idWithPrefix))
                {
                    var extraSource = _sourceResolver.GetSourceByPrefix(_sourceResolver.ExtractPrefixFromId(extraId));
                    if (extraSource == null)
                    {
                        _logger.LogWarning("🎬 [EPISODE PROVIDER] ⚠️ Additional tag '{ExtraId}': no enabled source for its prefix - ignored", extraId);
                        continue;
                    }

                    var extra = await _apiClient.GetMetadataAsync(
                        _sourceResolver.ExtractIdWithoutPrefix(extraId), "episode", extraSource, cancellationToken, bypassCache: !info.IsAutomated);
                    if (extra == null)
                    {
                        _logger.LogWarning("🎬 [EPISODE PROVIDER] ⚠️ Additional tag '{ExtraId}': no metadata returned - ignored", extraId);
                        continue;
                    }

                    extraMetadata.Add(extra);
                    _logger.LogInformation("🎬 [EPISODE PROVIDER] 🔗 Additional tag '{ExtraId}' merged (first tag '{IdWithPrefix}' is the reference)", extraId, idWithPrefix);
                }

                var primaryMetadata = metadata;
                metadata = MetadataMerger.Merge(metadata, extraMetadata);

                // Cast entered in the People screen for this API episode (attached to its tag): added to the API cast
                // without duplicates. On a copy: the response may come from the cache.
                var overlayKeys = new List<string> { idWithPrefix };
                overlayKeys.AddRange(GetExtraIds(info, idWithPrefix));
                // + cast inherited from the season / series when their distribution is marked "cascade"
                var overlayCast = _people.ResolveCast(CastInheritance.Combine(
                    _castOverlay.Get("episode", overlayKeys),
                    _inheritance.ForEpisode(info.Path, idWithPrefix)));
                if (overlayCast.Count > 0)
                {
                    metadata = MetadataMerger.WithExtraCast(metadata, overlayCast);
                    _logger.LogInformation("🎬 [EPISODE PROVIDER] 👥 {Count} people added from the cast overlay of '{Key}'", overlayCast.Count, idWithPrefix);
                }

                if (metadata == null)
                {
                    _logger.LogWarning(
                        "No metadata returned for episode '{EpisodeName}'",
                        info.Name);
                    return result;
                }

                // Build the Episode object
                var episode = new Episode
                {
                    Name = metadata.Title,
                    Overview = metadata.Overview,
                    PremiereDate = metadata.PremiereDate,
                    CommunityRating = metadata.CommunityRating
                };

                // Numbers: always return the item's own. Otherwise "Replace all metadata" (replaceData)
                // replaces them with null: episodes without a number, and virtual episodes deleted by
                // SeriesMetadataService.RemoveObsoleteEpisodes.
                episode.IndexNumber = info.IndexNumber;
                episode.ParentIndexNumber = info.ParentIndexNumber;
                episode.IndexNumberEnd = info.IndexNumberEnd;

                // Episode number from the API. Never for a virtual episode (no file): its number is chosen by
                // the missing-episode analysis so that it does not collide with a real episode.
                if (metadata.IndexNumber.HasValue && !string.IsNullOrEmpty(info.Path))
                {
                    episode.IndexNumber = metadata.IndexNumber;
                    _logger.LogInformation("🎬 [EPISODE PROVIDER] Episode number from API: {IndexNumber}", metadata.IndexNumber);
                }

                // Double episode: if the API numbers are consecutive, Jellyfin shows it as a range ("3-4")
                if (extraMetadata.Count > 0 && !string.IsNullOrEmpty(info.Path) && primaryMetadata?.IndexNumber is int first)
                {
                    var end = first;
                    foreach (var n in extraMetadata.Select(e => e.IndexNumber).Where(n => n.HasValue).Select(n => n.Value).OrderBy(n => n))
                    {
                        if (n == end + 1)
                        {
                            end = n;
                        }
                    }

                    if (end > first)
                    {
                        episode.IndexNumberEnd = end;
                        _logger.LogInformation("🎬 [EPISODE PROVIDER] Multi-episode file: {First}-{End}", first, end);
                    }
                }

                // Add the tags (with the series name as the default tag)
                var tagsList = metadata.Tags != null ? new List<string>(metadata.Tags) : new List<string>();
                if (!string.IsNullOrEmpty(metadata.SeriesName) && !tagsList.Contains(metadata.SeriesName, StringComparer.OrdinalIgnoreCase))
                {
                    tagsList.Insert(0, metadata.SeriesName);
                    _logger.LogInformation("🎬 [EPISODE PROVIDER] Added series name '{SeriesName}' as default tag", metadata.SeriesName);
                }
                if (tagsList.Any())
                {
                    episode.Tags = tagsList.ToArray();
                }

                // Add the genres
                if (metadata.Genres != null && metadata.Genres.Any())
                {
                    foreach (var genre in metadata.Genres)
                    {
                        episode.AddGenre(genre);
                    }
                }

                // Store the custom ID if present
                if (CustomId.Get(info.ProviderIds) is string customId)
                {
                    episode.ProviderIds[CustomId.Key] = customId;
                }

                result.Item = episode;
                result.HasMetadata = true;

                CastMapper.AddCast(result, metadata.Cast, _logger);

                _logger.LogInformation(
                    "Successfully enriched metadata for episode {EpisodeName}",
                    episode.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error enriching metadata for episode {EpisodeName}", info.Name);
            }

            return result;
        }

        /// <summary>
        /// Additional tags of the file name (besides the reference ID), for multi-episode files
        /// </summary>
        private static List<string> GetExtraIds(EpisodeInfo info, string primaryId)
        {
            if (string.IsNullOrEmpty(info.Path))
            {
                return new List<string>();
            }

            return TagParser.ExtractAll(TagParser.GetOwnName(info.Path))
                .Where(id => !string.Equals(id, primaryId, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(
            EpisodeInfo searchInfo,
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
        /// Extracts the custom ID from the {PREFIX-ID} pattern in the file name
        /// </summary>
        private string GetEpisodeIdentifier(EpisodeInfo info)
        {
            _logger.LogInformation("🎬 [EPISODE PROVIDER] 🔍 GetEpisodeIdentifier - RAW info.Name: '{InfoName}'", info.Name ?? "(null)");
            _logger.LogInformation("🎬 [EPISODE PROVIDER] 🔍 GetEpisodeIdentifier - RAW info.Path: '{InfoPath}'", info.Path ?? "(null)");
            
            // Option 1: if the episode already has its custom ID (extracted by Jellyfin via CustomExternalId)
            if (CustomId.Get(info.ProviderIds) is string customId)
            {
                _logger.LogDebug("🎬 [EPISODE PROVIDER] 🎯 Using episode CustomAPI ID from ProviderIds: {CustomId}", customId);
                return customId;
            }

            // Option 2: manual extraction from the PATH (not Name, which is parsed by Jellyfin)
            // In case Jellyfin did not extract it automatically
            
            // IMPORTANT: info.Name is the parsed title (e.g. "Episode 1"), not the file name!
            // You must use info.Path, which holds the full path
            string filenameToSearch = null;
            
            if (!string.IsNullOrEmpty(info.Path))
            {
                // Extract the file name from the full path
                filenameToSearch = System.IO.Path.GetFileName(info.Path);
                _logger.LogInformation("🎬 [EPISODE PROVIDER] 📄 Extracted filename from Path: '{Filename}'", filenameToSearch);
            }
            else
            {
                // Fall back to info.Name if there is no Path (should never happen)
                _logger.LogWarning("🎬 [EPISODE PROVIDER] ⚠️ No Path available, falling back to Name: '{Name}'", info.Name);
                filenameToSearch = info.Name;
            }
            
            if (string.IsNullOrEmpty(filenameToSearch))
            {
                _logger.LogWarning("🎬 [EPISODE PROVIDER] ❌ No filename to search for ID");
                return null;
            }
            
            var extractedId = ExtractIdFromFilename(filenameToSearch);
            if (!string.IsNullOrEmpty(extractedId))
            {
                _logger.LogInformation("🎬 [EPISODE PROVIDER] 🎯 Manually extracted ID from filename: {ExtractedId}", extractedId);
                return extractedId;
            }

            _logger.LogDebug("🎬 [EPISODE PROVIDER] ⚠️ No direct ID found in filename");
            return null;
        }
        
        /// <summary>
        /// Extracts the ID from the {PREFIX-ID} pattern in a file name
        /// MULTI-SOURCE: captures ANY prefix, not only the one of the global config
        /// Exemples :
        /// - "Episode 01 {mv1-123}.mkv" → "mv1-123"
        /// - "S01E01 {yt-abc}.mkv" → "yt-abc"
        /// - "Video {custom-789}.mp4" → "custom-789"
        /// </summary>
        private string ExtractIdFromFilename(string filename)
        {
            _logger.LogDebug("🎬 [EPISODE PROVIDER] 🔍 ExtractIdFromFilename - INPUT filename: '{Filename}'", filename ?? "(null)");
            
            if (string.IsNullOrEmpty(filename))
            {
                _logger.LogDebug("🎬 [EPISODE PROVIDER] ❌ Filename is null or empty");
                return null;
            }

            // Extract only the file name (last element of the path)
            // Ex: "/var/media/Serie/Saison/Episode {mv-123}.mkv" → "Episode {mv-123}.mkv"
            var actualFilename = TagParser.GetOwnName(filename);
            
            _logger.LogDebug("🎬 [EPISODE PROVIDER] 📄 After Path.GetFileName(): '{ActualFilename}'", actualFilename ?? "(null)");
            
            if (actualFilename != filename)
            {
                _logger.LogDebug("🎬 [EPISODE PROVIDER] ✂️ Extracted filename: '{ActualFilename}' from full path: '{Filename}'", actualFilename, filename);
            }

            // Shared generic {PREFIX-ID} pattern (TagParser): {mv1-EP001}, {yt-video_123}, {custom-ABC-XYZ}
            _logger.LogDebug("🎬 [EPISODE PROVIDER] 🔎 Regex pattern: '{Pattern}' (generic multi-source)", TagParser.Pattern);
            _logger.LogDebug("🎬 [EPISODE PROVIDER] 🔎 Searching in text: '{ActualFilename}'", actualFilename);
            
            var fullId = TagParser.Extract(actualFilename);
            
            _logger.LogDebug("🎬 [EPISODE PROVIDER] 🔎 Regex match.Success = {Success}", fullId != null);
            
            if (fullId != null)
            {
                _logger.LogInformation("🎬 [EPISODE PROVIDER] ✅ 📝 REGEX MATCHED! Extracted ID '{FullId}' (with prefix) from filename '{ActualFilename}'", 
                    fullId, actualFilename);
                
                return fullId;  // Retourne "PREFIX-ID" (ex: "mv1-123", "yt-456")
            }
            
            _logger.LogDebug("🎬 [EPISODE PROVIDER] ❌ No match found for pattern '{Pattern}' in filename '{ActualFilename}'", 
                TagParser.Pattern, actualFilename);
            
            return null;
        }

        /// <summary>
        /// Builds an identifier based on the parent series + S/E
        /// For example: "breaking-bad-s01e01" or uses the series ID
        /// </summary>
        private string GetSeriesBasedIdentifier(EpisodeInfo info)
        {
            var seasonNumber = info.ParentIndexNumber ?? 0;
            var episodeNumber = info.IndexNumber ?? 0;

            // Option 1: the series' custom ID + S/E
            if (CustomId.Get(info.SeriesProviderIds) is string seriesCustomId)
            {
                var identifier = $"{seriesCustomId}-s{seasonNumber:D2}e{episodeNumber:D2}";
                _logger.LogDebug("Using series-based identifier: {Identifier}", identifier);
                return identifier;
            }

            // Option 2: the series' IMDb ID + S/E
            if (info.SeriesProviderIds.TryGetValue("Imdb", out var seriesImdbId))
            {
                var identifier = $"{seriesImdbId}-s{seasonNumber:D2}e{episodeNumber:D2}";
                _logger.LogDebug("Using IMDb-based identifier: {Identifier}", identifier);
                return identifier;
            }

            // Option 3: fallback - return null and let Jellyfin handle it
            _logger.LogDebug("No custom identifier found, using default Jellyfin behavior");
            return null;
        }
    }
}
