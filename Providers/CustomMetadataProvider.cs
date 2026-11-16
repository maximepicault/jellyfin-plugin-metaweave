using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CustomMetadata.Api;
using Jellyfin.Plugin.CustomMetadata.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Providers
{
    public class CustomMetadataProvider : IRemoteMetadataProvider<Movie, MovieInfo>
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<CustomMetadataProvider> _logger;
        private readonly CustomApiClient _apiClient;
        private readonly DumpStore _dump;

        public CustomMetadataProvider(
            IHttpClientFactory httpClientFactory, 
            ILogger<CustomMetadataProvider> logger,
            CustomApiClient apiClient,
            DumpStore dump)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _apiClient = apiClient;
            _dump = dump;
        }

        public string Name => "Custom Metadata Provider";

        /// <summary>
        /// Fetches the metadata from your custom API
        /// </summary>
        public async Task<MetadataResult<Movie>> GetMetadata(
            MovieInfo info, 
            CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Movie>();

            try
            {
                if (!(Plugin.Instance?.Configuration.EnableMetadata ?? true))
                {
                    _logger.LogDebug("Metadata disabled in plugin settings - skipping {ItemName}", info.Name);
                    return result;
                }

                // "Dump" source first (library of standalone files): a {prefix-id} tag, the id kept on the item, or a match by name.
                // The movie path below stays the legacy single-source one for everything else.
                var dumpMatch = await _dump.ResolveAsync(info.Path, info.Name, info.ProviderIds, cancellationToken);

                CustomMetadataResponse metadata;
                if (dumpMatch != null)
                {
                    _logger.LogInformation("📦 [DUMP] Movie '{Name}' → {Tag} ({How}, score {Score:F0})", info.Name, dumpMatch.Tag, dumpMatch.How, dumpMatch.Score);
                    metadata = await _apiClient.GetMetadataAsync(dumpMatch.Id, "video", dumpMatch.Source, cancellationToken, bypassCache: !info.IsAutomated);
                }
                else
                {
                    // Identify the item - you can use the name, the IMDb ID, or a custom ID
                    var itemId = GetItemIdentifier(info);
                    if (string.IsNullOrEmpty(itemId))
                    {
                        return result;
                    }

                    // Call your API
                    metadata = await _apiClient.GetMetadataAsync(itemId, "video", cancellationToken);
                }

                if (metadata == null)
                {
                    return result;
                }

                // Build the Movie object with the enriched metadata
                var movie = new Movie
                {
                    Name = metadata.Title,
                    Overview = metadata.Overview,
                    PremiereDate = metadata.PremiereDate,
                    CommunityRating = metadata.CommunityRating
                };

                // Add the tags (labels)
                if (metadata.Tags != null && metadata.Tags.Any())
                {
                    movie.Tags = metadata.Tags.ToArray();
                }

                // Add the genres
                if (metadata.Genres != null && metadata.Genres.Any())
                {
                    foreach (var genre in metadata.Genres)
                    {
                        movie.AddGenre(genre);
                    }
                }

                if (metadata.Studios != null && metadata.Studios.Any())
                {
                    movie.Studios = metadata.Studios.ToArray();
                }

                // The entry of a dump is remembered on the item: the next refreshes and the coverage screen use it
                if (dumpMatch != null)
                {
                    movie.ProviderIds[CustomId.Key] = dumpMatch.Tag;
                }

                result.Item = movie;
                result.HasMetadata = true;

                CastMapper.AddCast(result, metadata.Cast, _logger);

                _logger.LogInformation("Successfully enriched metadata for {ItemName}", movie.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error enriching metadata for {ItemName}", info.Name);
            }

            return result;
        }

        /// <summary>
        /// Item search (optional, but useful)
        /// </summary>
#pragma warning disable CS1998 // Async method lacks 'await' operators
        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(
            MovieInfo searchInfo, 
            CancellationToken cancellationToken)
        {
            var results = new List<RemoteSearchResult>();

            try
            {
                // Only the dump sources can be searched (the best candidates for the "Identify" screen)
                results.AddRange(await DumpSearch.ToSearchResultsAsync(_dump, searchInfo.Path, searchInfo.Name, Name, cancellationToken));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching for {SearchName}", searchInfo.Name);
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
        /// Extracts the item's identifier for the API
        /// </summary>
        private string GetItemIdentifier(MovieInfo info)
        {
            // Option 1: use the custom ID from the {custom-ID} pattern in the file name
            if (info.ProviderIds.TryGetValue("custom", out var customId))
            {
                _logger.LogDebug("Using CustomAPI ID: {CustomId}", customId);
                return customId;
            }

            // Option 2: use the IMDb ID if available
            if (info.ProviderIds.TryGetValue("Imdb", out var imdbId))
            {
                _logger.LogDebug("Using IMDb ID: {ImdbId}", imdbId);
                return imdbId;
            }

            // Option 3: use the file name/title
            _logger.LogDebug("Using movie name: {MovieName}", info.Name);
            return info.Name;
        }
    }
}
