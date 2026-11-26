using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CustomMetadata.Services;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.CustomMetadata.Api
{
    /// <summary>
    /// The episode photo gallery. Every route needs a signed-in user who can see the episode (library access, parental
    /// rating, tags), except the gallery's script (a script tag sends no token) and the signed links of the image search.
    /// </summary>
    [ApiController]
    [Route("CustomMetadata/Photos")]
    [Authorize]
    public class PhotosController : ControllerBase
    {
        // claims set by Jellyfin's authentication handler
        private const string UserIdClaim = "Jellyfin-UserId";
        private const string IsApiKeyClaim = "Jellyfin-IsApiKey";

        private readonly EpisodePhotoService _photos;
        private readonly EpisodePhotoThumbnails _thumbnails;
        private readonly EpisodePhotoLinks _links;

        public PhotosController(EpisodePhotoService photos, EpisodePhotoThumbnails thumbnails, EpisodePhotoLinks links)
        {
            _photos = photos;
            _thumbnails = thumbnails;
            _links = links;
        }

        /// <summary>The episode's photos; an empty list for any other item. 404 when the user cannot see the item.</summary>
        [HttpGet("{itemId:guid}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<EpisodePhotoList> List(Guid itemId)
        {
            var item = _photos.GetVisibleItem(itemId, UserId, IsApiKey);
            if (item == null)
            {
                return NotFound();
            }

            Response.Headers["Cache-Control"] = "private, no-cache";
            var id = item.Id.ToString("N", CultureInfo.InvariantCulture);
            return new EpisodePhotoList
            {
                ItemId = item.Id,
                Photos = _photos.List(item as Episode).Select(p =>
                {
                    var (width, height) = p.File != null ? _thumbnails.SizeOf(p.File) : (0, 0);
                    var key = Uri.EscapeDataString(p.Key);
                    return new EpisodePhotoDto
                    {
                        Key = p.Key,
                        Name = p.Name,
                        Width = width,
                        Height = height,
                        // paths relative to the server (fetched with the user's token); a web address is used as it is
                        ImageUrl = p.File != null ? $"CustomMetadata/Photos/{id}/Image/{key}" : null,
                        ThumbnailUrl = p.File != null ? $"CustomMetadata/Photos/{id}/Thumb/{key}" : null,
                        ExternalUrl = p.Url
                    };
                }).ToList()
            };
        }

        [HttpGet("{itemId:guid}/Image/{key}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult Image(Guid itemId, string key)
        {
            var photo = Find(itemId, key);
            return photo == null ? NotFound() : File(photo, photo.FullPath, EpisodePhotoFolders.ContentTypeOf(photo.Name), "o");
        }

        /// <summary>A WebP thumbnail (the original when the format cannot be decoded)</summary>
        [HttpGet("{itemId:guid}/Thumb/{key}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Thumb(Guid itemId, string key, [FromQuery] int? width, CancellationToken cancellationToken)
        {
            var photo = Find(itemId, key);
            if (photo == null)
            {
                return NotFound();
            }

            var size = EpisodePhotoThumbnails.NormalizeWidth(width);
            var thumbnail = await _thumbnails.GetAsync(photo, size, cancellationToken);
            return thumbnail == null
                ? File(photo, photo.FullPath, EpisodePhotoFolders.ContentTypeOf(photo.Name), "o")
                : File(photo, thumbnail, "image/webp", "t" + size.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>A photo through a signed link of the image search: anonymous, the signature is the authorisation</summary>
        [HttpGet("Remote/{itemId:guid}/{key}")]
        [AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Remote(Guid itemId, string key, [FromQuery] long expires, [FromQuery] string signature)
        {
            var photo = _links.Resolve(itemId, key, expires, signature);
            if (photo == null)
            {
                return NotFound();
            }

            var (contentType, data) = await _links.ReadAsync(photo);
            Response.Headers["Cache-Control"] = "private, max-age=3600";
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            return File(data, contentType);
        }

        /// <summary>The gallery's script, added to jellyfin-web's page by ThemeScriptMiddleware. Public in the repository.</summary>
        [HttpGet("gallery.js")]
        [AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status304NotModified)]
        public IActionResult Script()
        {
            var script = ThemeAssets.Gallery;
            if (script.Data == null)
            {
                return NotFound();
            }

            Response.Headers["Cache-Control"] = "public, max-age=300";
            Response.Headers["ETag"] = script.ETag;
            return Request.Headers["If-None-Match"].ToString() == script.ETag
                ? StatusCode(StatusCodes.Status304NotModified)
                : File(script.Data, "text/javascript; charset=utf-8");
        }

        private Guid UserId => Guid.TryParse(User.FindFirst(UserIdClaim)?.Value, out var id) ? id : Guid.Empty;

        private bool IsApiKey => bool.TryParse(User.FindFirst(IsApiKeyClaim)?.Value, out var apiKey) && apiKey;

        private PhotoFile Find(Guid itemId, string key)
            => _photos.GetVisibleItem(itemId, UserId, IsApiKey) is Episode episode ? _photos.Find(episode, key)?.File : null;

        private IActionResult File(PhotoFile source, string path, string contentType, string variant)
        {
            var etag = new EntityTagHeaderValue(string.Create(CultureInfo.InvariantCulture,
                $"\"{source.Length:x}-{source.LastWriteTimeUtc.Ticks:x}-{variant}\""));
            Response.Headers["Cache-Control"] = "private, max-age=3600";
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            return PhysicalFile(path, contentType, new DateTimeOffset(DateTime.SpecifyKind(source.LastWriteTimeUtc, DateTimeKind.Utc)), etag, enableRangeProcessing: true);
        }
    }

    public class EpisodePhotoList
    {
        public Guid ItemId { get; set; }

        public System.Collections.Generic.List<EpisodePhotoDto> Photos { get; set; }
    }

    public class EpisodePhotoDto
    {
        public string Key { get; set; }

        public string Name { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }

        public string ImageUrl { get; set; }

        public string ThumbnailUrl { get; set; }

        public string ExternalUrl { get; set; }
    }
}
