using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>
    /// Puts <see cref="ThemeScriptMiddleware"/> in front of Jellyfin's own pipeline (an IStartupFilter registered by
    /// PluginServiceRegistrator runs before the server's Configure).
    /// </summary>
    public class ThemeScriptStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.UseMiddleware<ThemeScriptMiddleware>();
                next(app);
            };
        }
    }

    /// <summary>
    /// Adds the plugin's scripts to jellyfin-web's index.html as it is served: the Projection theme's
    /// (<c>EnableThemeScript</c>, CustomMetadata/Theme/projection.js and projection-index.js) and the episode photo gallery's
    /// (<c>EnableEpisodePhotos</c>, CustomMetadata/Photos/gallery.js). Nothing is written to the server's web folder
    /// (often read-only, and replaced by every Jellyfin update); turning a setting off restores the stock page at the
    /// next load. The relative paths work under a base URL.
    /// </summary>
    public class ThemeScriptMiddleware
    {
        private const string ThemeMarker = "data-projection-theme";
        private const string GalleryMarker = "data-episode-photos";
        private readonly RequestDelegate _next;
        private readonly ILogger<ThemeScriptMiddleware> _logger;

        public ThemeScriptMiddleware(RequestDelegate next, ILogger<ThemeScriptMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var config = Plugin.Instance?.Configuration;
            var theme = config?.EnableThemeScript ?? false;
            var gallery = config?.EnableEpisodePhotos ?? false;
            if (!IsIndexRequest(context.Request) || !(theme || gallery))
            {
                await _next(context);
                return;
            }

            // a plain, complete page is needed to edit it: no compression, no 304 from a cached copy
            context.Request.Headers.Remove("Accept-Encoding");
            context.Request.Headers.Remove("If-None-Match");
            context.Request.Headers.Remove("If-Modified-Since");

            var original = context.Response.Body;
            using var buffer = new MemoryStream();
            context.Response.Body = buffer;
            try
            {
                await _next(context);
            }
            finally
            {
                context.Response.Body = original;
            }

            buffer.Position = 0;
            var response = context.Response;
            var isHtml = (response.ContentType ?? string.Empty).StartsWith("text/html", StringComparison.OrdinalIgnoreCase);
            if (response.StatusCode == StatusCodes.Status200OK && isHtml && !response.Headers.ContainsKey("Content-Encoding"))
            {
                var html = Encoding.UTF8.GetString(buffer.ToArray());
                var at = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                var tags = new StringBuilder();
                if (theme && !html.Contains(ThemeMarker, StringComparison.Ordinal))
                {
                    tags.Append("<script defer " + ThemeMarker + " src=\"../CustomMetadata/Theme/projection.js?v=" + ThemeAssets.ScriptVersion + "\"></script>");
                    tags.Append("<script defer " + ThemeMarker + " src=\"../CustomMetadata/Theme/projection-index.js?v=" + ThemeAssets.IndexScriptVersion + "\"></script>");
                }

                if (gallery && !html.Contains(GalleryMarker, StringComparison.Ordinal))
                {
                    tags.Append("<script defer " + GalleryMarker + " src=\"../CustomMetadata/Photos/gallery.js?v=" + ThemeAssets.GalleryVersion + "\"></script>");
                }

                if (at >= 0 && tags.Length > 0)
                {
                    html = html.Insert(at, tags.ToString());
                    var bytes = Encoding.UTF8.GetBytes(html);
                    response.ContentLength = bytes.Length;
                    response.Headers.Remove("ETag");
                    response.Headers.Remove("Last-Modified");
                    response.Headers["Cache-Control"] = "no-cache";
                    await original.WriteAsync(bytes);
                    _logger.LogDebug("🎨 [THEME] script(s) added to {Path} (theme: {Theme}, photos: {Photos})", context.Request.Path, theme, gallery);
                    return;
                }
            }

            await buffer.CopyToAsync(original);
        }

        // jellyfin-web's page: /web/ or /web/index.html, under any base URL
        private static bool IsIndexRequest(HttpRequest request)
        {
            if (!HttpMethods.IsGet(request.Method))
            {
                return false;
            }

            var path = (request.PathBase.Value ?? string.Empty) + (request.Path.Value ?? string.Empty);
            return path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase);
        }
    }
}
