using Jellyfin.Plugin.CustomMetadata.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.CustomMetadata.Api
{
    /// <summary>
    /// Targeted refresh of the library items that carry a source's prefix (see SourceRefreshService). Administrators only.
    /// POST starts it (in the background), GET Status follows it.
    /// </summary>
    [ApiController]
    [Route("CustomMetadata/Refresh")]
    [Authorize(Policy = "RequiresElevation")]
    public class SourceRefreshController : ControllerBase
    {
        private readonly SourceRefreshService _service;

        public SourceRefreshController(SourceRefreshService service)
        {
            _service = service;
        }

        [HttpPost]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<SourceRefreshStatus> Start([FromBody] SourceRefreshRequest request) => Ok(_service.Start(request));

        [HttpGet("Status")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<SourceRefreshStatus> Status() => Ok(_service.Status());
    }
}
