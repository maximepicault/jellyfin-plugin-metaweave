using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CustomMetadata.Configuration;
using Jellyfin.Plugin.CustomMetadata.Services;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Api
{
    /// <summary>
    /// The preview video of a real episode (its source's preview.url), streamed through the server for the Projection theme's
    /// hover preview: the source's headers (API key, custom headers) stay on the server, an http source plays on an https
    /// Jellyfin, a local source's uploaded video (a loopback address) is reachable, and Range requests are forwarded so the
    /// browser can seek. Any signed-in user (the browser passes api_key in the URL, a video tag sends no header).
    /// </summary>
    [ApiController]
    [Route("CustomMetadata/Preview")]
    [Authorize]
    public class PreviewController : ControllerBase
    {
        private readonly EpisodePreviewService _previews;
        private readonly ILibraryManager _library;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<PreviewController> _logger;

        public PreviewController(EpisodePreviewService previews, ILibraryManager library, IHttpClientFactory httpClientFactory, ILogger<PreviewController> logger)
        {
            _previews = previews;
            _library = library;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <summary>The episode's preview video, or 404 when its source has none</summary>
        [HttpGet("{itemId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status206PartialContent)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task Stream(Guid itemId, CancellationToken cancellationToken)
        {
            var episode = _library.GetItemById(itemId) as Episode;
            var (url, source) = await _previews.GetAsync(episode, cancellationToken);
            if (string.IsNullOrEmpty(url) || !(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var client = _httpClientFactory.CreateClient();
            SourceHeaders.Apply(client, source, !string.IsNullOrEmpty(source?.ApiKey) ? source.ApiKey : Plugin.Instance?.Configuration.ApiKey ?? string.Empty,
                _logger, LogLevel.Debug, "🎞️ [PREVIEW]");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (Request.Headers.TryGetValue("Range", out var range) && RangeHeaderValue.TryParse(range.ToString(), out var parsed))
            {
                request.Headers.Range = parsed;
            }

            using var upstream = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!upstream.IsSuccessStatusCode)
            {
                _logger.LogWarning("🎞️ [PREVIEW] {Url} answered {Status}", url, (int)upstream.StatusCode);
                Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            Response.StatusCode = (int)upstream.StatusCode;
            var content = upstream.Content.Headers;
            Response.ContentType = content.ContentType?.ToString() is { Length: > 0 } type && !type.StartsWith("application/octet", StringComparison.OrdinalIgnoreCase) ? type : "video/mp4";
            if (content.ContentLength is long length) { Response.ContentLength = length; }
            if (content.ContentRange != null) { Response.Headers["Content-Range"] = content.ContentRange.ToString(); }
            Response.Headers["Accept-Ranges"] = upstream.Headers.AcceptRanges.Any() ? string.Join(", ", upstream.Headers.AcceptRanges) : "bytes";
            Response.Headers["Cache-Control"] = "private, max-age=3600";
            await using var body = await upstream.Content.ReadAsStreamAsync(cancellationToken);
            try
            {
                await body.CopyToAsync(Response.Body, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // the pointer left the card: the browser dropped the request
            }
        }
    }
}
