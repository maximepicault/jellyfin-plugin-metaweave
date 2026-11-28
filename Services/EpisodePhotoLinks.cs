using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>
    /// Signed, short-lived links to episode photos, for "Modifier les images → Rechercher" (EpisodePhotoImageProvider).
    /// That dialog shows each image straight from its URL (no authentication header) and the server downloads the chosen
    /// one with a plain request, so the URL carries an HMAC over (episode, photo key, expiry) instead of a session. The key
    /// is random per server process. Links are only handed out by Jellyfin's authenticated RemoteImages request, and a
    /// valid one still only opens a photo currently listed for that episode.
    /// </summary>
    public class EpisodePhotoLinks
    {
        public const string RoutePrefix = "CustomMetadata/Photos/Remote";

        private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        private readonly EpisodePhotoService _photos;
        private readonly ILibraryManager _library;
        private readonly ILogger<EpisodePhotoLinks> _logger;

        public EpisodePhotoLinks(EpisodePhotoService photos, ILibraryManager library, ILogger<EpisodePhotoLinks> logger)
        {
            _photos = photos;
            _library = library;
            _logger = logger;
        }

        /// <summary>"CustomMetadata/Photos/Remote/{id}/{key}?expires=…&amp;signature=…", relative to the server</summary>
        public string CreatePath(Guid itemId, string photoKey)
        {
            var expires = DateTimeOffset.UtcNow.Add(Lifetime).ToUnixTimeSeconds();
            return string.Create(CultureInfo.InvariantCulture,
                $"{RoutePrefix}/{itemId:N}/{Uri.EscapeDataString(photoKey)}?expires={expires}&signature={Sign(itemId, photoKey, expires)}");
        }

        /// <summary>The photo a link designates; null when it is invalid, expired or the photo is gone</summary>
        public PhotoFile Resolve(Guid itemId, string photoKey, long expires, string signature)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (photoKey == null || string.IsNullOrEmpty(signature) || expires < now || expires > now + (long)Lifetime.TotalSeconds
                || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(itemId, photoKey, expires)), Encoding.ASCII.GetBytes(signature)))
            {
                return null;
            }

            // the signature proves an authorised user got this link, so no user-scoped lookup here
            return _library.GetItemById(itemId) is Episode episode ? _photos.Find(episode, photoKey, _ => true)?.File : null;
        }

        /// <summary>The same, from an absolute URL built on <see cref="CreatePath"/></summary>
        public PhotoFile Resolve(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.AbsolutePath.Contains("/" + RoutePrefix + "/", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var segments = uri.AbsolutePath.Split('/');
            var query = QueryHelpers.ParseQuery(uri.Query);
            return segments.Length >= 2
                && Guid.TryParse(segments[^2], out var itemId)
                && long.TryParse(query.TryGetValue("expires", out var e) ? e.ToString() : null, NumberStyles.None, CultureInfo.InvariantCulture, out var expires)
                ? Resolve(itemId, Uri.UnescapeDataString(segments[^1]), expires, query.TryGetValue("signature", out var s) ? s.ToString() : null)
                : null;
        }

        /// <summary>The bytes to hand Jellyfin: the EXIF rotation baked in, since its resizer ignores the tag</summary>
        public async Task<(string ContentType, byte[] Data)> ReadAsync(PhotoFile photo)
        {
            var data = await File.ReadAllBytesAsync(photo.FullPath).ConfigureAwait(false);
            return (EpisodePhotoFolders.ContentTypeOf(photo.Name), ExifOrientation.Normalize(data, _logger, photo.Name));
        }

        private string Sign(Guid itemId, string photoKey, long expires)
        {
            var message = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{itemId:N}\n{photoKey}\n{expires}"));
            return WebEncoders.Base64UrlEncode(HMACSHA256.HashData(_key, message));
        }
    }

    /// <summary>
    /// Lets Jellyfin save the photo chosen in the image search without downloading it over the network: its download
    /// request fetches the URL with an HTTP client, but the URL carries the host the BROWSER used, which the server often
    /// cannot reach (Docker port mapping, reverse proxy, NAT). Jellyfin first looks the URL up in the shared memory cache
    /// (key: the URL, value: (content type, bytes)), so the photo is put there just before the request reaches it. This
    /// never answers the request: authorisation stays with Jellyfin.
    /// </summary>
    public class EpisodePhotoDownloadStartupFilter : IStartupFilter
    {
        private readonly EpisodePhotoLinks _links;
        private readonly IMemoryCache _cache;
        private readonly ILogger<EpisodePhotoDownloadStartupFilter> _logger;

        public EpisodePhotoDownloadStartupFilter(EpisodePhotoLinks links, IMemoryCache cache, ILogger<EpisodePhotoDownloadStartupFilter> logger)
        {
            _links = links;
            _cache = cache;
            _logger = logger;
        }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use(async (HttpContext context, Func<Task> following) =>
                {
                    if (HttpMethods.IsPost(context.Request.Method)
                        && (context.Request.Path.Value ?? string.Empty).EndsWith("/RemoteImages/Download", StringComparison.OrdinalIgnoreCase))
                    {
                        await PrimeAsync(context.Request.Query["imageUrl"].ToString()).ConfigureAwait(false);
                    }

                    await following().ConfigureAwait(false);
                });
                next(app);
            };
        }

        private async Task PrimeAsync(string imageUrl)
        {
            try
            {
                var photo = _links.Resolve(imageUrl);
                if (photo == null)
                {
                    return;
                }

                (string ContentType, byte[] ImageContents) entry = await _links.ReadAsync(photo).ConfigureAwait(false);
                _cache.Set(imageUrl, entry, TimeSpan.FromMinutes(1));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "📷 [PHOTOS] ⚠️ The photo chosen in the image search cannot be read");
            }
        }
    }
}
