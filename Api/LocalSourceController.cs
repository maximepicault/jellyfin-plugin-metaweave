using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CustomMetadata.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.CustomMetadata.Api
{
    /// <summary>
    /// Content of local sources (series → seasons → episodes typed in the plugin), edited from the
    /// configuration page. Administrators only. {dataId} = SourceConfiguration.LocalDataId.
    /// The content is sent as a whole: the editor works on the entire tree and saves it in one go.
    /// </summary>
    [ApiController]
    [Route("CustomMetadata/Local")]
    [Authorize(Policy = "RequiresElevation")]
    public class LocalSourceController : ControllerBase
    {
        private readonly LocalSourceStore _store;

        public LocalSourceController(LocalSourceStore store)
        {
            _store = store;
        }

        [HttpGet("{dataId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public ActionResult<LocalContent> Get(string dataId)
        {
            if (!LocalSourceStore.IsValidDataId(dataId))
            {
                return BadRequest("Identifiant de données invalide");
            }

            return Ok(_store.Load(dataId));
        }

        /// <summary>Saves the whole tree. Returns the normalised content (ids generated for new elements)</summary>
        [HttpPut("{dataId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public ActionResult<LocalContent> Put(string dataId, [FromBody] LocalContent content)
        {
            if (!LocalSourceStore.IsValidDataId(dataId))
            {
                return BadRequest("Identifiant de données invalide");
            }

            if (content == null)
            {
                return BadRequest("Contenu manquant");
            }

            return Ok(_store.Save(dataId, content));
        }

        /// <summary>
        /// Uploads an image (body = file bytes). Returns its "local://img/…" address to put in an image
        /// field of the {node} element; Jellyfin reads it directly from the data folder, without network.
        /// </summary>
        [HttpPost("{dataId}/Image")]
        [RequestSizeLimit(LocalSourceStore.MaxImageBytes + 1024)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<ActionResult<LocalImageResult>> UploadImage(string dataId, [FromQuery] string node, CancellationToken cancellationToken)
        {
            if (!LocalSourceStore.IsValidDataId(dataId))
            {
                return BadRequest("Identifiant de données invalide");
            }

            using var buffer = new MemoryStream();
            await Request.Body.CopyToAsync(buffer, cancellationToken);
            var url = _store.SaveImage(dataId, node, buffer.ToArray(), out var error);
            return url == null ? BadRequest(error) : Ok(new LocalImageResult { Url = url });
        }

        /// <summary>Reads an uploaded image (preview in the editor)</summary>
        [HttpGet("{dataId}/Image/{file}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult GetImage(string dataId, string file)
        {
            if (!_store.TryReadImage(dataId, file, out var data, out var mime))
            {
                return NotFound();
            }

            Response.Headers["Cache-Control"] = "private, max-age=3600";
            return File(data, mime);
        }

        [HttpDelete("{dataId}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public IActionResult Delete(string dataId)
        {
            _store.Delete(dataId);
            return NoContent();
        }
    }

    public class LocalImageResult
    {
        public string Url { get; set; }
    }
}
