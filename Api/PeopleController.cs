using System;
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
    /// Directory of people (cast): reading/saving the list, fetching a person from a
    /// people source, and photos. Administrators only, except the photo (see <see cref="Photo"/>).
    /// </summary>
    [ApiController]
    [Route("CustomMetadata/People")]
    [Authorize(Policy = "RequiresElevation")]
    public class PeopleController : ControllerBase
    {
        private readonly PeopleStore _people;
        private readonly CustomApiClient _apiClient;
        private readonly LocalSourceStore _images;
        private readonly CastOverlayStore _overlay;
        private readonly CastCatalog _catalog;
        private readonly CastApplyService _apply;

        public PeopleController(PeopleStore people, CustomApiClient apiClient, LocalSourceStore images, CastOverlayStore overlay, CastCatalog catalog, CastApplyService apply)
        {
            _apply = apply;
            _people = people;
            _apiClient = apiClient;
            _images = images;
            _overlay = overlay;
            _catalog = catalog;
        }

        [HttpGet]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<PeopleDirectory> Get() => Ok(_people.Load());

        /// <summary>Saves the whole directory (ids generated for new entries). Returns the normalised version</summary>
        [HttpPut]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public ActionResult<PeopleDirectory> Put([FromBody] PeopleDirectory directory)
        {
            if (directory == null)
            {
                return BadRequest("Annuaire manquant");
            }

            var saved = _people.Save(directory);
            return Ok(saved);
        }

        /// <summary>Cast distribution of API-source episodes, by episode tag</summary>
        [HttpGet("EpisodeCast")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<EpisodeCastFile> GetEpisodeCast() => Ok(_overlay.Load());

        [HttpPut("EpisodeCast")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public ActionResult<EpisodeCastFile> PutEpisodeCast([FromBody] EpisodeCastFile file)
        {
            if (file == null)
            {
                return BadRequest("Distribution manquante");
            }

            // People deleted from the directory do not remain in the distributions
            foreach (var episode in file.Episodes ?? new System.Collections.Generic.List<EpisodeCastEntry>())
            {
                episode.Cast = (episode.Cast ?? new System.Collections.Generic.List<LocalCastEntry>()).Where(c => _people.Find(c?.PersonId) != null).ToList();
            }

            var saved = _overlay.Save(file);
            return Ok(saved);
        }

        /// <summary>
        /// "Apply to episodes": restarts the enrichment of ALREADY enriched episodes that depend on what changed
        /// (modified people, modified distributions, local sources). Empty = everything that has a cast.
        /// </summary>
        [HttpPost("Apply")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<CastApplyResult> Apply([FromBody] CastApplyRequest request) => Ok(_apply.Apply(request));

        /// <summary>Tags of the seasons and episodes below one series or season: the preview of its "cascade"</summary>
        [HttpGet("Children")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<CastChildren> GetChildren([FromQuery] string type, [FromQuery] string key) => Ok(_catalog.Children(type, key));

        /// <summary>Series, seasons and episodes of the library that carry an API source tag</summary>
        [HttpGet("Library")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<System.Collections.Generic.List<CastCatalogSeries>> GetLibrary() => Ok(_catalog.Build());

        /// <summary>
        /// Queries a people source for an identifier, saving nothing. Says whether the person is already
        /// known (same identifier, or same name) so that the screen adds the identifier to their entry instead of a duplicate.
        /// </summary>
        [HttpPost("Fetch")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<ActionResult<PersonFetchResult>> Fetch([FromBody] PersonFetchRequest request, CancellationToken cancellationToken)
        {
            var prefix = (request?.Prefix ?? string.Empty).Trim();
            var id = (request?.ExternalId ?? string.Empty).Trim().Trim('{', '}').Trim();
            if (id.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase) && prefix.Length > 0)
            {
                id = id.Substring(prefix.Length + 1);
            }

            if (prefix.Length == 0 || id.Length == 0)
            {
                return BadRequest("Source et identifiant obligatoires");
            }

            var source = Plugin.Instance?.Configuration?.Sources?
                .FirstOrDefault(s => s.IsPeople && s.Enabled && string.Equals(s.Prefix, prefix, StringComparison.OrdinalIgnoreCase));
            if (source == null)
            {
                return Ok(new PersonFetchResult { Prefix = prefix, ExternalId = id, Error = $"Aucune source de personnes activée avec le préfixe « {prefix} »" });
            }

            var (person, error) = await _apiClient.GetPersonAsync(id, source, cancellationToken);
            if (person == null)
            {
                return Ok(new PersonFetchResult { Prefix = source.Prefix, ExternalId = id, Error = error });
            }

            var match = _people.FindMatch(source.Prefix, id, person.Name);
            return Ok(new PersonFetchResult
            {
                Prefix = source.Prefix,
                ExternalId = id,
                Name = person.Name,
                Photo = person.Photo,
                Bio = person.Bio,
                MatchPersonId = match?.Person.Id,
                MatchReason = match?.Reason
            });
        }

        /// <summary>
        /// Photo of a person uploaded into the plugin. ANONYMOUS: Jellyfin itself downloads it (the cast's
        /// ImageUrl address) and it has no token. Only the directory's photos are served, under a random name.
        /// </summary>
        [HttpGet("Photo/{file}")]
        [AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult Photo(string file)
        {
            if (!_images.TryReadImage(PeopleStore.ImagesDataId, file, out var data, out var mime))
            {
                return NotFound();
            }

            Response.Headers["Cache-Control"] = "private, max-age=3600";
            return File(data, mime);
        }
    }

    public class PersonFetchRequest
    {
        public string Prefix { get; set; }

        public string ExternalId { get; set; }
    }

    public class PersonFetchResult
    {
        public string Prefix { get; set; }

        public string ExternalId { get; set; }

        public string Name { get; set; }

        public string Photo { get; set; }

        public string Bio { get; set; }

        /// <summary>Entry already in the directory: same identifier ("link") or same name ("name")</summary>
        public string MatchPersonId { get; set; }

        public string MatchReason { get; set; }

        public string Error { get; set; }
    }
}
