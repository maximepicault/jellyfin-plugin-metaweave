using System.Collections.Generic;
using Jellyfin.Plugin.CustomMetadata.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.CustomMetadata.Api
{
    /// <summary>
    /// "Genres et tags" screen: the rules (ignore, rename, merge, fix the spelling, create) and their application. Administrators only.
    /// </summary>
    [ApiController]
    [Route("CustomMetadata/Taxonomy")]
    [Authorize(Policy = "RequiresElevation")]
    public class TaxonomyController : ControllerBase
    {
        private readonly TaxonomyStore _store;
        private readonly TaxonomyService _service;

        public TaxonomyController(TaxonomyStore store, TaxonomyService service)
        {
            _store = store;
            _service = service;
        }

        /// <summary>Every term of the library and of the rules, with the rule that applies to it</summary>
        [HttpGet]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<TaxonomyCatalog> Get() => Ok(new TaxonomyCatalog { Terms = _service.Catalog() });

        /// <summary>Saves the rules (cleaned: see TaxonomyStore.Save) and returns them with the catalog</summary>
        [HttpPut]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public ActionResult<TaxonomyCatalog> Put([FromBody] TaxonomyFile file)
        {
            if (file == null)
            {
                return BadRequest("Règles manquantes");
            }

            _store.Save(_service.NormalizeTargets(file));
            TaxonomyService.InvalidateNames();
            return Ok(new TaxonomyCatalog { Terms = _service.Catalog() });
        }

        /// <summary>Names to suggest in the fields of the local editor, by kind ("genre", "tag"), the most used first.
        /// Optional: <c>kind</c> (genre|tag), <c>q</c> (search text, matched among all terms), <c>limit</c> (per list, 300 at most).</summary>
        [HttpGet("Names")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<Dictionary<string, List<string>>> Names([FromQuery] string kind = null, [FromQuery] string q = null, [FromQuery] int limit = 300) => Ok(_service.Names(kind, q, limit));

        /// <summary>How many items "Appliquer" would change</summary>
        [HttpGet("Impact")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<TaxonomyImpact> Impact() => Ok(_service.Impact());

        /// <summary>Rewrites, in the background, the items that hold a term a rule changes</summary>
        [HttpPost("Apply")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<TaxonomyApplyStatus> Apply() => Ok(_service.Start());

        [HttpGet("Apply/Status")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<TaxonomyApplyStatus> ApplyStatus() => Ok(TaxonomyService.Status);
    }

    public class TaxonomyCatalog
    {
        public List<TaxonomyTerm> Terms { get; set; } = new List<TaxonomyTerm>();
    }
}
