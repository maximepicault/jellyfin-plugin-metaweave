using System.Collections.Generic;
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
        private readonly SourceResolver _resolver;
        private readonly CustomApiClient _api;

        public LocalSourceController(LocalSourceStore store, SourceResolver resolver, CustomApiClient api)
        {
            _store = store;
            _resolver = resolver;
            _api = api;
        }

        /// <summary>
        /// Episode list of an API season ("dm-123"), shaped like local episodes whose id is the API episode's tag
        /// ("dm-a1v1"): the editor merges it into a local season that overrides that season. Saves nothing.
        /// </summary>
        [HttpPost("ImportEpisodes")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<ActionResult<List<LocalEpisode>>> ImportEpisodes([FromBody] ImportEpisodesRequest request, CancellationToken cancellationToken)
        {
            var tag = (request?.Tag ?? string.Empty).Trim().Trim('{', '}');
            var prefix = _resolver.ExtractPrefixFromId(tag);
            var source = string.IsNullOrEmpty(prefix) ? null : _resolver.GetSourceByPrefix(prefix);
            if (source == null || source.IsLocal)
            {
                return BadRequest($"Aucune source API active n'a le préfixe « {prefix} ».");
            }

            if (string.IsNullOrWhiteSpace(source.EpisodeListEndpoint))
            {
                return BadRequest($"La source « {source.Name} » n'a pas d'endpoint de liste des épisodes (onglet Endpoints).");
            }

            var items = await _api.GetEpisodeListAsync(_resolver.ExtractIdWithoutPrefix(tag), source, cancellationToken);
            if (items == null)
            {
                return BadRequest("La liste d'épisodes n'a pas pu être récupérée (voir les logs 📃 [EPISODE LIST]).");
            }

            var key = source.Prefix.Trim();
            return Ok(items.Select(i => new LocalEpisode
            {
                Id = $"{key}-{i.Id}",
                Title = i.Title,
                Overview = i.Overview,
                PremiereDate = i.PremiereDate?.ToString("yyyy-MM-dd"),
                IndexNumber = i.IndexNumber,
                Duration = i.RunTimeTicks.HasValue ? FormatDuration(System.TimeSpan.FromTicks(i.RunTimeTicks.Value)) : null,
                Thumb = i.ThumbUrl,
                PreviewUrl = i.PreviewUrl
            }).ToList());
        }

        private static string FormatDuration(System.TimeSpan d)
            => d.TotalHours >= 1 ? $"{(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}" : $"{d.Minutes}:{d.Seconds:00}";

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

    public class ImportEpisodesRequest
    {
        /// <summary>Tag of the API season: "dm-123" or "{dm-123}"</summary>
        public string Tag { get; set; }
    }
}
