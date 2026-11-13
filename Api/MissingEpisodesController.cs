using System.Net.Http;
using Jellyfin.Plugin.CustomMetadata.ScheduledTasks;
using Jellyfin.Plugin.CustomMetadata.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Api
{
    /// <summary>
    /// API of the "Missing episodes" screen (Configuration/missing.html)
    /// Administrators only
    /// </summary>
    [ApiController]
    [Route("CustomMetadata/MissingEpisodes")]
    [Authorize(Policy = "RequiresElevation")]
    public class MissingEpisodesController : ControllerBase
    {
        private readonly MissingEpisodeService _service;
        private readonly ITaskManager _taskManager;

        public MissingEpisodesController(
            MissingEpisodeService service,
            ITaskManager taskManager)
        {
            _service = service;
            _taskManager = taskManager;
        }

        /// <summary>
        /// Dernier rapport + indicateur d'analyse en cours
        /// </summary>
        [HttpGet]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult GetReport()
        {
            return Ok(new
            {
                Running = MissingEpisodeService.IsRunning,
                Report = _service.GetLastReport()
            });
        }

        /// <summary>
        /// Starts the scheduled task (progress also visible in Dashboard → Scheduled tasks)
        /// </summary>
        /// <param name="full">true = ignore the cache and reprocess every missing episode</param>
        [HttpPost("Analyze")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult Analyze([FromQuery] bool full = false)
        {
            if (full)
            {
                MissingEpisodeService.RequestFullReprocess();
            }

            _taskManager.QueueScheduledTask<MissingEpisodesTask>();
            return NoContent();
        }
    }
}
