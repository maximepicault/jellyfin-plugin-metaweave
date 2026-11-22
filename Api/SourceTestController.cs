using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CustomMetadata.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.CustomMetadata.Api
{
    /// <summary>
    /// "Test the source" button of the configuration page.
    /// Receives the source as it is in the form (no need to save it first).
    /// Administrators only: the server calls the configured URL.
    /// </summary>
    [ApiController]
    [Route("CustomMetadata/TestSource")]
    [Authorize(Policy = "RequiresElevation")]
    public class SourceTestController : ControllerBase
    {
        private readonly CustomApiClient _apiClient;
        private readonly Services.DumpStore _dump;

        public SourceTestController(CustomApiClient apiClient, Services.DumpStore dump)
        {
            _apiClient = apiClient;
            _dump = dump;
        }

        [HttpPost]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<ActionResult<SourceTestResult>> Test([FromBody] SourceTestRequest request, CancellationToken cancellationToken)
        {
            if (request?.Source == null)
            {
                return BadRequest("Source manquante");
            }

            var id = NormalizeId(request.Id, request.Source.Prefix);
            if (string.IsNullOrEmpty(id) && request.Source.IsDump)
            {
                // A dump test needs no id: the first entry of the catalogue is read (the one the editor test already downloaded, or a fresh copy)
                var (catalog, error) = await _dump.GetCatalogAsync(request.Source, false, cancellationToken);
                if (catalog == null || catalog.Entries.Count == 0)
                {
                    return Ok(new SourceTestResult { ItemType = request.ItemType ?? "video", Error = catalog == null ? error : "Le dump ne contient aucune entrée" });
                }

                id = catalog.Entries[0].Id;
            }

            if (string.IsNullOrEmpty(id))
            {
                return BadRequest("ID manquant");
            }

            return Ok(await _apiClient.TestSourceAsync(request.Source, request.ItemType ?? "video", id, cancellationToken));
        }

        /// <summary>
        /// Accepts "123", "mv1-123" or "{mv1-123}": strips braces and the source prefix
        /// </summary>
        private static string NormalizeId(string id, string prefix)
        {
            id = (id ?? string.Empty).Trim().Trim('{', '}').Trim();
            if (!string.IsNullOrEmpty(prefix) && id.StartsWith(prefix + "-", System.StringComparison.OrdinalIgnoreCase))
            {
                id = id.Substring(prefix.Length + 1);
            }

            return id;
        }
    }

    public class SourceTestRequest
    {
        public SourceConfiguration Source { get; set; }

        /// <summary>"video" | "season" | "series" | "episodelist"</summary>
        public string ItemType { get; set; }

        public string Id { get; set; }
    }
}
