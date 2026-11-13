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

        public CustomMetadataProvider(
            IHttpClientFactory httpClientFactory, 
            ILogger<CustomMetadataProvider> logger,
            CustomApiClient apiClient)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _apiClient = apiClient;
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

                // Identify the item - you can use the name, the IMDb ID, or a custom ID
                var itemId = GetItemIdentifier(info);
                if (string.IsNullOrEmpty(itemId))
                {
                    return result;
                }

                // Call your API
                var metadata = await _apiClient.GetMetadataAsync(itemId, "video", cancellationToken);
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
                // You can implement a search in your API
                // Pour l'instant on retourne vide
                _logger.LogDebug("Search requested for: {SearchName}", searchInfo.Name);
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
