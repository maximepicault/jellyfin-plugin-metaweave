using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Api
{
    /// <summary>
    /// Serves the Projection web theme (theme/projection.css, embedded in the DLL) from the server itself, so the
    /// Custom CSS can import it with <c>@import url("../CustomMetadata/Theme/projection.css");</c>: the theme then
    /// follows the plugin version installed on the server, without a CDN cache or a Git branch to follow.
    /// ANONYMOUS, like the people photos: the browser fetches an @import without Jellyfin's token. The file is public
    /// in the repository anyway.
    /// </summary>
    [ApiController]
    [Route("CustomMetadata/Theme")]
    public class ThemeController : ControllerBase
    {
        private const string ResourceName = "Jellyfin.Plugin.CustomMetadata.theme.projection.css";

        // read once: the file only changes with the DLL
        private static readonly Lazy<(byte[] Data, string ETag)> Css = new(() =>
        {
            using var stream = typeof(ThemeController).Assembly.GetManifestResourceStream(ResourceName);
            if (stream == null)
            {
                return (null, null);
            }

            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            var data = copy.ToArray();
            var hash = Convert.ToHexString(SHA256.HashData(data)).Substring(0, 16).ToLowerInvariant();
            return (data, "\"" + hash + "\"");
        });

        private readonly ILogger<ThemeController> _logger;

        public ThemeController(ILogger<ThemeController> logger)
        {
            _logger = logger;
        }

        /// <summary>The theme's stylesheet (text/css). 304 when the browser already has this version.</summary>
        [HttpGet("projection.css")]
        [AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status304NotModified)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult Projection()
        {
            var (data, etag) = Css.Value;
            if (data == null)
            {
                _logger.LogWarning("🎨 [THEME] {Resource} is not embedded in the DLL", ResourceName);
                return NotFound();
            }

            // a short cache and a validator: a plugin update reaches the browsers within minutes, and in between
            // a reload costs a 304
            Response.Headers["Cache-Control"] = "public, max-age=300";
            Response.Headers["ETag"] = etag;
            if (Request.Headers.TryGetValue("If-None-Match", out var sent) && sent.Any(v => v != null && v.Split(',').Any(t => t.Trim() == etag)))
            {
                return StatusCode(StatusCodes.Status304NotModified);
            }

            return File(data, "text/css; charset=utf-8");
        }
    }
}
