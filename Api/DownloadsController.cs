using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.CustomMetadata.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.CustomMetadata.Api
{
    /// <summary>
    /// Follow-up of the automatic video downloads of missing episodes (the "Téléchargements" page).
    /// Administrators only. The history lives in memory: it starts empty after a server restart.
    /// </summary>
    [ApiController]
    [Route("CustomMetadata/Downloads")]
    [Authorize(Policy = "RequiresElevation")]
    public class DownloadsController : ControllerBase
    {
        private readonly VideoDownloadService _downloads;

        public DownloadsController(VideoDownloadService downloads)
        {
            _downloads = downloads;
        }

        /// <summary>Every download with its state, plus how many sources have the option on (the page explains how to enable it when none)</summary>
        [HttpGet]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<DownloadsState> Get()
        {
            var sources = Plugin.Instance?.Configuration?.Sources ?? new List<Configuration.SourceConfiguration>();
            return Ok(new DownloadsState
            {
                EnabledSources = sources.Count(s => s.Enabled && s.AutoDownloadVideos),
                Items = _downloads.Snapshot()
            });
        }

        /// <summary>Stops a waiting or running download</summary>
        [HttpPost("Cancel")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult Cancel([FromBody] DownloadKeyRequest request)
            => !string.IsNullOrWhiteSpace(request?.Key) && _downloads.Cancel(request.Key) ? Ok() : NotFound();

        /// <summary>Queues a failed or cancelled download again</summary>
        [HttpPost("Retry")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult Retry([FromBody] DownloadKeyRequest request)
            => !string.IsNullOrWhiteSpace(request?.Key) && _downloads.Retry(request.Key) ? Ok() : NotFound();

        /// <summary>Forgets the finished rows</summary>
        [HttpPost("Clear")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<int> Clear() => Ok(_downloads.ClearFinished());
    }

    public class DownloadKeyRequest
    {
        public string Key { get; set; }
    }

    public class DownloadsState
    {
        public int EnabledSources { get; set; }
        public List<VideoDownloadService.DownloadInfo> Items { get; set; }
    }
}
