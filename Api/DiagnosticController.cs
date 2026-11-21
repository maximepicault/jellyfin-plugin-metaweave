using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.CustomMetadata.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.CustomMetadata.Api
{
    /// <summary>
    /// Diagnostic page ("why is this element not enriched?") and the counters of the tab bar shared by the plugin's pages.
    /// Administrators only. Nothing here changes a library, an item or the configuration.
    /// </summary>
    [ApiController]
    [Route("CustomMetadata")]
    [Authorize(Policy = "RequiresElevation")]
    public class DiagnosticController : ControllerBase
    {
        private readonly DiagnosticService _diagnostic;
        private readonly MissingEpisodeService _missing;
        private readonly PeopleStore _people;
        private readonly ILibraryManager _library;
        private readonly TaxonomyStore _taxonomy;
        private readonly VideoDownloadService _downloads;

        public DiagnosticController(DiagnosticService diagnostic, MissingEpisodeService missing, PeopleStore people, ILibraryManager library, TaxonomyStore taxonomy, VideoDownloadService downloads)
        {
            _downloads = downloads;
            _library = library;
            _taxonomy = taxonomy;
            _diagnostic = diagnostic;
            _missing = missing;
            _people = people;
        }

        /// <summary>One file or folder name, step by step. {Kind} ("episode", "season", "series") is only used when the library has no such element.</summary>
        [HttpPost("Diagnostic/Element")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<ActionResult<DiagnosticResult>> Element([FromBody] DiagnosticRequest request, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(request?.Name))
            {
                return BadRequest("Nom manquant");
            }

            return Ok(await _diagnostic.DiagnoseAsync(request.Name, request.Kind, cancellationToken));
        }

        /// <summary>The last list of library elements that are not enriched (204 when the library was never checked)</summary>
        [HttpGet("Diagnostic")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult<DiagnosticList> GetList()
        {
            var list = _diagnostic.GetLast();
            return list == null ? NoContent() : Ok(list);
        }

        /// <summary>Checks the library again (no API call: names, sources, library providers, ids already stored)</summary>
        [HttpPost("Diagnostic")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<DiagnosticList> Scan() => Ok(_diagnostic.Scan());

        /// <summary>Counters of the tab bar: cheap, read from what is already in memory or on disk</summary>
        [HttpGet("Status")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<PluginStatus> Status()
        {
            var report = _missing.GetLastReport();
            var seasons = report?.Seasons;
            return Ok(new PluginStatus
            {
                Sources = Plugin.Instance?.Configuration?.Sources?.Count ?? 0,
                MissingEpisodes = seasons?.Sum(s => s.Missing?.Count ?? 0),
                SourceErrors = seasons?.Count(s => !string.IsNullOrEmpty(s.Error)) ?? 0,
                People = _people.Load()?.People?.Count ?? 0,
                NotEnriched = _diagnostic.GetLast()?.Total,
                TaggedSeries = CountTaggedSeries(),
                Downloads = _downloads.ActiveCount,
                DownloadsFailed = _downloads.FailedCount,
                TaxonomyRules = _taxonomy.Load().Rules.Count(r => r.Ignored || r.ReplaceWith != null)
            });
        }

        /// <summary>Series folders that carry a tag of a configured source (the setup guide's "a folder is tagged" step)</summary>
        private int CountTaggedSeries()
        {
            var prefixes = (Plugin.Instance?.Configuration?.Sources ?? new System.Collections.Generic.List<Configuration.SourceConfiguration>())
                .Where(s => !s.IsPeople && !string.IsNullOrWhiteSpace(s.Prefix))
                .Select(s => s.Prefix.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (prefixes.Count == 0)
            {
                return 0;
            }

            return _library.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.Series }, Recursive = true })
                .Count(i =>
                {
                    var tag = string.IsNullOrEmpty(i.Path) ? null : TagParser.Extract(Path.GetFileName(i.Path.TrimEnd('/', '\\')));
                    var dash = tag?.IndexOf('-') ?? -1;
                    return dash > 0 && prefixes.Contains(tag.Substring(0, dash));
                });
        }
    }

    public class DiagnosticRequest
    {
        public string Name { get; set; }
        public string Kind { get; set; }
    }

    public class PluginStatus
    {
        public int Sources { get; set; }

        /// <summary>null: no analysis yet</summary>
        public int? MissingEpisodes { get; set; }

        public int SourceErrors { get; set; }
        public int People { get; set; }

        /// <summary>null: the library was never checked</summary>
        public int? NotEnriched { get; set; }

        /// <summary>Series folders carrying a tag of a configured source</summary>
        public int TaggedSeries { get; set; }

        /// <summary>Rules of the "Genres et tags" screen that ignore or rename a term</summary>
        public int TaxonomyRules { get; set; }

        /// <summary>Downloads of missing episodes waiting or running</summary>
        public int Downloads { get; set; }

        /// <summary>Downloads that failed since the server started (until cleared)</summary>
        public int DownloadsFailed { get; set; }
    }
}
