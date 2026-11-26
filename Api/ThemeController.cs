using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Jellyfin.Plugin.CustomMetadata.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>The Projection theme's files, embedded in the DLL (theme/projection.css, theme/projection.js).</summary>
    public static class ThemeAssets
    {
        private static readonly Lazy<(byte[] Data, string ETag)> CssFile = new(() => Load("projection.css"));
        private static readonly Lazy<(byte[] Data, string ETag)> JsFile = new(() => Load("projection.js"));

        public static (byte[] Data, string ETag) Css => CssFile.Value;

        public static (byte[] Data, string ETag) Js => JsFile.Value;

        /// <summary>Changes with the script's content: the injected tag carries it, so browsers fetch a new script at once.</summary>
        public static string ScriptVersion => (JsFile.Value.ETag ?? "0").Trim('"');

        private static (byte[] Data, string ETag) Load(string file)
        {
            using var stream = typeof(ThemeAssets).Assembly.GetManifestResourceStream("Jellyfin.Plugin.CustomMetadata.theme." + file);
            if (stream == null)
            {
                return (null, null);
            }

            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            var data = copy.ToArray();
            var hash = Convert.ToHexString(SHA256.HashData(data)).Substring(0, 16).ToLowerInvariant();
            return (data, "\"" + hash + "\"");
        }
    }
}

namespace Jellyfin.Plugin.CustomMetadata.Api
{
    /// <summary>
    /// Serves the Projection web theme from the server itself:
    /// - projection.css, for the Custom CSS: <c>@import url("../CustomMetadata/Theme/projection.css");</c> (the theme then
    ///   follows the plugin version installed, without a CDN cache or a Git branch to follow);
    /// - projection.js, added to jellyfin-web's page by ThemeScriptMiddleware when "EnableThemeScript" is on.
    /// ANONYMOUS, like the people photos: the browser fetches an @import or a script tag without Jellyfin's token. Both
    /// files are public in the repository anyway.
    /// </summary>
    [ApiController]
    [Route("CustomMetadata/Theme")]
    public class ThemeController : ControllerBase
    {
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
        public IActionResult Projection() => Serve(ThemeAssets.Css, "text/css; charset=utf-8", "projection.css");

        /// <summary>The theme's script (text/javascript). 304 when the browser already has this version.</summary>
        [HttpGet("projection.js")]
        [AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status304NotModified)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult Script() => Serve(ThemeAssets.Js, "text/javascript; charset=utf-8", "projection.js");

        private IActionResult Serve((byte[] Data, string ETag) file, string contentType, string name)
        {
            if (file.Data == null)
            {
                _logger.LogWarning("🎨 [THEME] {File} is not embedded in the DLL", name);
                return NotFound();
            }

            // always revalidated (a reload costs a 304): with a cache of a few minutes, a browser kept the old CSS after a
            // plugin update while it fetched the new script at once, and the script's new elements showed unstyled
            Response.Headers["Cache-Control"] = "no-cache";
            Response.Headers["ETag"] = file.ETag;
            if (Request.Headers.TryGetValue("If-None-Match", out var sent) && sent.Any(v => v != null && v.Split(',').Any(t => t.Trim() == file.ETag)))
            {
                return StatusCode(StatusCodes.Status304NotModified);
            }

            return File(file.Data, contentType);
        }
    }
}
