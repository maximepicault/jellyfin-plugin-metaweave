using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CustomMetadata.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.CustomMetadata.Providers
{
    /// <summary>
    /// Offers an episode's photos in "Modifier les images → Rechercher", so one can be chosen as the episode's image.
    /// Only answers that interactive search (Jellyfin's <c>/Items/{id}/RemoteImages</c> request): during scans and
    /// refreshes it returns nothing, so a photo is never applied by itself. Discovered by Jellyfin like the others.
    /// </summary>
    public class EpisodePhotoImageProvider : IRemoteImageProvider
    {
        private readonly IServiceProvider _services;

        public EpisodePhotoImageProvider(IServiceProvider services)
        {
            _services = services;
        }

        public string Name => "Photos de l'épisode";

        public bool Supports(BaseItem item) => item is Episode;

        public IEnumerable<ImageType> GetSupportedImages(BaseItem item) => new[] { ImageType.Primary };

        public Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
        {
            // resolved here and never constructor-injected: a missing registration would break the provider
            var request = _services.GetService<IHttpContextAccessor>()?.HttpContext?.Request;
            var photos = _services.GetService<EpisodePhotoService>();
            var links = _services.GetService<EpisodePhotoLinks>();
            if (item is not Episode episode || photos == null || links == null || !EpisodePhotoService.Enabled || request == null
                || !(request.Path.Value ?? string.Empty).EndsWith("/RemoteImages", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(Enumerable.Empty<RemoteImageInfo>());
            }

            // absolute addresses on the host the browser used: the dialog displays them as they are
            var baseUrl = $"{request.Scheme}://{request.Host}{request.PathBase}/";
            // the albums of the episode are in other libraries: only what the person searching may see
            var images = photos.List(episode, photos.ViewerOf(request.HttpContext.User)).Select(p => new RemoteImageInfo
            {
                ProviderName = Name,
                Type = ImageType.Primary,
                Url = p.File != null ? baseUrl + links.CreatePath(episode.Id, p.Key) : p.Url
            }).ToList();

            return Task.FromResult<IEnumerable<RemoteImageInfo>>(images);
        }

        public async Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            var links = _services.GetService<EpisodePhotoLinks>();
            var photo = links?.Resolve(url);
            if (photo != null)
            {
                var (contentType, data) = await links.ReadAsync(photo).ConfigureAwait(false);
                var content = new ByteArrayContent(data);
                content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }

            if (url.Contains("/" + EpisodePhotoLinks.RoutePrefix + "/", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            // a local source's photo given as a web address
            return await _services.GetRequiredService<IHttpClientFactory>().CreateClient().GetAsync(url, cancellationToken).ConfigureAwait(false);
        }
    }
}
