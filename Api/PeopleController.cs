using System;
using Microsoft.Extensions.Logging;
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
        private readonly PersonNetworkStore _networks;
        private readonly Microsoft.Extensions.Logging.ILogger<PeopleController> _logger;

        public PeopleController(PersonNetworkStore networks, Microsoft.Extensions.Logging.ILogger<PeopleController> logger, PeopleStore people, CustomApiClient apiClient, LocalSourceStore images, CastOverlayStore overlay, CastCatalog catalog, CastApplyService apply)
        {
            _apply = apply;
            _networks = networks;
            _logger = logger;
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
        public async Task<ActionResult<PeopleDirectory>> Put([FromBody] PeopleDirectory directory, CancellationToken cancellationToken)
        {
            if (directory == null)
            {
                return BadRequest("Annuaire manquant");
            }

            // A remote photo is copied into the plugin: its address may be signed and expire, and Jellyfin reads it much later
            await ImportRemoteImagesAsync(directory, cancellationToken);
            var saved = _people.Save(directory);
            return Ok(saved);
        }

        /// <summary>Networks a person can carry (name, icon, address template)</summary>
        [HttpGet("Networks")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<System.Collections.Generic.List<PersonNetwork>> GetNetworks() => Ok(_networks.Load());

        /// <summary>The original list of networks (to offer "reset")</summary>
        [HttpGet("Networks/Defaults")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<System.Collections.Generic.List<PersonNetwork>> GetDefaultNetworks() => Ok(PersonNetworkStore.Defaults());

        [HttpPut("Networks")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public ActionResult<System.Collections.Generic.List<PersonNetwork>> PutNetworks([FromBody] System.Collections.Generic.List<PersonNetwork> networks)
        {
            if (networks == null)
            {
                return BadRequest("Liste de réseaux manquante");
            }

            return Ok(_networks.Save(networks));
        }

        /// <summary>
        /// Downloads every http(s) photo and backdrop of the directory into the plugin's image folder and replaces the address
        /// by the local one. An image that cannot be fetched keeps its address (logged), so nothing is lost.
        /// </summary>
        private async Task ImportRemoteImagesAsync(PeopleDirectory directory, CancellationToken cancellationToken)
        {
            var sources = Plugin.Instance?.Configuration?.Sources ?? new System.Collections.Generic.List<Configuration.SourceConfiguration>();
            async Task<string> Import(string url, Person person)
            {
                if (string.IsNullOrWhiteSpace(url) || LocalSourceStore.IsLocalImageUrl(url) || PeopleStore.NormalizeWebsite(url) == null)
                {
                    return url;
                }

                var source = sources.FirstOrDefault(s => s.IsPeople && (person.Links ?? new System.Collections.Generic.List<PersonLink>()).Any(l => string.Equals(l.Prefix, s.Prefix, StringComparison.OrdinalIgnoreCase)));
                var (data, _, error) = await _apiClient.DownloadImageAsync(url.Trim(), source, false, cancellationToken);
                if (data == null)
                {
                    _logger.LogWarning("👥 [PEOPLE] ⚠️ Cannot import the image of '{Name}': {Error}", person.Name, error);
                    return url;
                }

                var local = _images.SaveImage(PeopleStore.ImagesDataId, "p", data, out var saveError);
                if (local == null)
                {
                    _logger.LogWarning("👥 [PEOPLE] ⚠️ Image of '{Name}' refused: {Error}", person.Name, saveError);
                    return url;
                }

                return local;
            }

            foreach (var person in directory.People ?? new System.Collections.Generic.List<Person>())
            {
                if (person == null) { continue; }
                person.Photo = await Import(person.Photo, person);
                for (var i = 0; i < (person.Backdrops?.Count ?? 0); i++)
                {
                    person.Backdrops[i] = await Import(person.Backdrops[i], person);
                }
            }
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
            foreach (var episode in (file.Episodes ?? new System.Collections.Generic.List<EpisodeCastEntry>()).Where(e => e != null))
            {
                episode.Cast = (episode.Cast ?? new System.Collections.Generic.List<LocalCastEntry>()).Where(c => _people.Find(c?.PersonId) != null).ToList();
            }

            var saved = _overlay.Save(file);
            return Ok(saved);
        }

        /// <summary>
        /// Merges two entries of the directory: {RemoveId} disappears into {KeepId}. The kept entry keeps its own values, takes the
        /// other's where it has none, and gets its identifiers. Every cast that named the removed person (API overlay and local
        /// sources) now names the kept one. Saved at once; Jellyfin's items follow with "Appliquer aux épisodes".
        /// </summary>
        [HttpPost("Merge")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public ActionResult<PersonMergeResult> Merge([FromBody] PersonMergeRequest request)
        {
            var directory = _people.Load();
            var keep = directory.People.FirstOrDefault(p => string.Equals(p.Id, request?.KeepId, StringComparison.OrdinalIgnoreCase));
            var remove = directory.People.FirstOrDefault(p => string.Equals(p.Id, request?.RemoveId, StringComparison.OrdinalIgnoreCase));
            if (keep == null || remove == null || ReferenceEquals(keep, remove))
            {
                return BadRequest("Les deux fiches à fusionner sont introuvables. Enregistrez l'annuaire, puis réessayez.");
            }

            if (string.IsNullOrWhiteSpace(keep.Photo)) { keep.Photo = remove.Photo; }
            if (string.IsNullOrWhiteSpace(keep.Bio)) { keep.Bio = remove.Bio; }
            if (string.IsNullOrWhiteSpace(keep.BirthDate)) { keep.BirthDate = remove.BirthDate; }
            if (string.IsNullOrWhiteSpace(keep.DeathDate)) { keep.DeathDate = remove.DeathDate; }
            if (string.IsNullOrWhiteSpace(keep.BirthPlace)) { keep.BirthPlace = remove.BirthPlace; }
            if (string.IsNullOrWhiteSpace(keep.Website)) { keep.Website = remove.Website; }
            keep.Backdrops = (keep.Backdrops ?? new System.Collections.Generic.List<string>()).Concat(remove.Backdrops ?? new System.Collections.Generic.List<string>()).ToList();
            keep.Networks = (keep.Networks ?? new System.Collections.Generic.List<PersonNetworkValue>()).Concat(remove.Networks ?? new System.Collections.Generic.List<PersonNetworkValue>()).ToList();
            if (!string.IsNullOrWhiteSpace(remove.Notes))
            {
                keep.Notes = string.IsNullOrWhiteSpace(keep.Notes) ? remove.Notes : keep.Notes + "\n" + remove.Notes;
            }
            keep.Links ??= new System.Collections.Generic.List<PersonLink>();
            foreach (var link in remove.Links ?? new System.Collections.Generic.List<PersonLink>())
            {
                if (!keep.Links.Any(l => string.Equals(l.Prefix, link.Prefix, StringComparison.OrdinalIgnoreCase) && string.Equals(l.ExternalId, link.ExternalId, StringComparison.OrdinalIgnoreCase)))
                {
                    keep.Links.Add(link);
                }
            }

            directory.People.Remove(remove);

            // References first, the directory last: a failure in between leaves the removed entry in place, nothing dangling
            bool Rename(System.Collections.Generic.List<LocalCastEntry> cast)
            {
                var changed = false;
                foreach (var entry in cast ?? new System.Collections.Generic.List<LocalCastEntry>())
                {
                    if (string.Equals(entry?.PersonId, remove.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        entry.PersonId = keep.Id;
                        changed = true;
                    }
                }

                return changed;
            }

            var result = new PersonMergeResult { KeptId = keep.Id };
            var overlay = _overlay.Load();
            var overlayChanged = false;
            foreach (var entry in overlay.Episodes ?? new System.Collections.Generic.List<EpisodeCastEntry>())
            {
                if (entry != null && Rename(entry.Cast))
                {
                    overlayChanged = true;
                    result.Casts++;
                }
            }

            if (overlayChanged)
            {
                _overlay.Save(overlay);   // normalises: the same person twice with the same job becomes one entry
            }

            foreach (var source in (Plugin.Instance?.Configuration?.Sources ?? new System.Collections.Generic.List<Configuration.SourceConfiguration>()).Where(s => s.IsLocal && LocalSourceStore.IsValidDataId(s.LocalDataId)))
            {
                var content = _images.Load(source.LocalDataId);
                var changed = false;
                foreach (var episode in (content.Series ?? new System.Collections.Generic.List<LocalSeries>()).SelectMany(se => se.Seasons ?? new System.Collections.Generic.List<LocalSeason>()).SelectMany(se => se.Episodes ?? new System.Collections.Generic.List<LocalEpisode>()))
                {
                    if (Rename(episode.Cast))
                    {
                        changed = true;
                        result.Casts++;
                    }
                }

                if (changed)
                {
                    _images.Save(source.LocalDataId, content);
                }
            }

            result.Directory = _people.Save(directory);
            return Ok(result);
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
                BirthDate = person.BirthDate,
                DeathDate = person.DeathDate,
                BirthPlace = person.BirthPlace,
                Website = person.Website,
                Backdrops = person.Backdrops,
                Networks = person.Networks,
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

        public string BirthDate { get; set; }

        public string DeathDate { get; set; }

        public string BirthPlace { get; set; }

        public string Website { get; set; }

        public System.Collections.Generic.List<string> Backdrops { get; set; }

        public System.Collections.Generic.List<PersonNetworkValue> Networks { get; set; }

        /// <summary>Entry already in the directory: same identifier ("link") or same name ("name")</summary>
        public string MatchPersonId { get; set; }

        public string MatchReason { get; set; }

        public string Error { get; set; }
    }

    public class PersonMergeRequest
    {
        /// <summary>The entry that stays</summary>
        public string KeepId { get; set; }

        /// <summary>The entry that disappears into the other</summary>
        public string RemoveId { get; set; }
    }

    public class PersonMergeResult
    {
        public string KeptId { get; set; }

        /// <summary>Casts (elements) that named the removed person</summary>
        public int Casts { get; set; }

        public PeopleDirectory Directory { get; set; }
    }
}
