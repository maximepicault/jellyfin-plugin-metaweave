using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.CustomMetadata.Configuration;
using Jellyfin.Plugin.CustomMetadata.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.CustomMetadata.Api
{
    /// <summary>
    /// "Dump" sources: the test of the editor (does the dump load, which entry does a file name give) and the coverage screen
    /// (what the dump holds, what the library already owns, what is missing). Administrators only.
    /// </summary>
    [ApiController]
    [Route("CustomMetadata/Dump")]
    [Authorize(Policy = "RequiresElevation")]
    public class DumpController : ControllerBase
    {
        private readonly DumpStore _dump;
        private readonly ILibraryManager _libraryManager;
        private readonly CustomApiClient _api;

        public DumpController(DumpStore dump, ILibraryManager libraryManager, CustomApiClient api)
        {
            _dump = dump;
            _libraryManager = libraryManager;
            _api = api;
        }

        /// <summary>
        /// The preview picture of an entry (coverage screen), downloaded by the server with the source's headers and settings:
        /// the browser cannot always fetch it itself (hotlink protection, Referer checks). Never given to Jellyfin.
        /// </summary>
        [HttpGet("Image")]
        public async Task<ActionResult> Image([FromQuery] string prefix, [FromQuery] string id, CancellationToken cancellationToken)
        {
            var source = FindSource(prefix);
            var catalog = source == null ? null : _dump.Peek(source);
            if (source == null || catalog == null)
            {
                return NotFound();
            }

            var url = RowsOf(catalog, source).FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase))?.ImageUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                return NotFound();
            }

            var (data, mime, _) = await _api.DownloadImageAsync(url, source, false, cancellationToken);
            if (data == null)
            {
                return NotFound();
            }

            Response.Headers["Cache-Control"] = "private, max-age=86400";
            return File(data, string.IsNullOrWhiteSpace(mime) ? "image/jpeg" : mime);
        }

        private static readonly string[] VideoExtensions = { ".mkv", ".mp4", ".avi", ".mov", ".m4v", ".wmv", ".webm", ".ts", ".flv", ".mpg", ".mpeg" };

        // ------------------------------------------------------------------------------------------------------
        // Editor test
        // ------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Downloads the dump with the source AS IT IS IN THE FORM (nothing saved), counts what it holds, and, when a file name
        /// is given, tells which entries it would match and with which score.
        /// </summary>
        [HttpPost("Test")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<ActionResult<DumpTestResult>> Test([FromBody] DumpTestRequest request, CancellationToken cancellationToken)
        {
            var source = request?.Source;
            if (source == null || !source.IsDump)
            {
                return BadRequest("Source de type Dump attendue");
            }

            source.Dump ??= new DumpSettings();
            var stopwatch = Stopwatch.StartNew();
            var (catalog, error) = await _dump.GetCatalogAsync(source, true, cancellationToken);
            var result = new DumpTestResult { DurationMs = stopwatch.ElapsedMilliseconds, Error = catalog == null ? error : null };
            if (catalog == null)
            {
                return Ok(result);
            }

            result.Ok = true;
            result.Count = catalog.Entries.Count;
            result.WithSlug = catalog.Entries.Count(e => !string.IsNullOrWhiteSpace(e.Slug));
            result.WithSearchTitles = catalog.Entries.Count(e => !string.IsNullOrWhiteSpace(e.SearchTitles));
            result.WithGroup = catalog.Entries.Count(e => !string.IsNullOrWhiteSpace(e.Group));
            result.Samples = catalog.Entries.Take(5).Select(e => new DumpTestEntry { Id = e.Id, Name = e.Name, Group = e.Group }).ToList();
            result.Groups = catalog.Entries
                .Where(e => !string.IsNullOrWhiteSpace(e.Group))
                .GroupBy(e => e.Group.Trim(), StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Take(8).Select(g => new DumpTestGroup { Name = g.Key, Count = g.Count() }).ToList();
            result.GroupCount = catalog.Entries.Where(e => !string.IsNullOrWhiteSpace(e.Group)).Select(e => e.Group.Trim().ToLowerInvariant()).Distinct().Count();

            if (!string.IsNullOrWhiteSpace(request.FileName))
            {
                var name = request.FileName.Trim();
                // A name typed with a video extension is matched like a real file (extension dropped); without one, as is
                var path = VideoExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase)) ? name : null;
                var candidates = _dump.FindCandidates(catalog, source.Dump, path, name, 5, Math.Min(40, source.Dump.MinScore), out var how);
                result.MinScore = source.Dump.MinScore;
                result.Candidates = candidates.Select((c, index) => new DumpTestCandidate
                {
                    Id = c.Entry.Id,
                    Name = c.Entry.Name,
                    Group = c.Entry.Group,
                    Score = Math.Round(c.Score),
                    Accepted = c.Score >= source.Dump.MinScore,
                    How = index == 0 ? (how ?? "fuzzy") : "fuzzy",
                    Tag = $"{(source.Prefix ?? string.Empty).Trim().ToLowerInvariant()}-{c.Entry.Id}"
                }).ToList();
            }

            return Ok(result);
        }

        /// <summary>Forgets the copy in memory and downloads the dump again (the "Recharger" button of the coverage screen)</summary>
        [HttpPost("Reload")]
        public async Task<ActionResult> Reload([FromQuery] string prefix, CancellationToken cancellationToken)
        {
            var source = FindSource(prefix);
            if (source == null)
            {
                return NotFound("Source de dump introuvable");
            }

            var (catalog, error) = await _dump.GetCatalogAsync(source, true, cancellationToken);
            return catalog == null ? StatusCode(StatusCodes.Status502BadGateway, error) : NoContent();
        }

        // ------------------------------------------------------------------------------------------------------
        // Coverage
        // ------------------------------------------------------------------------------------------------------

        /// <summary>The dump sources, with what is already known about them (for the source choice of the screen)</summary>
        [HttpGet("Sources")]
        public ActionResult<List<DumpSourceInfo>> Sources()
        {
            var sources = (Plugin.Instance?.Configuration?.Sources ?? new List<SourceConfiguration>()).Where(s => s.IsDump).ToList();
            return Ok(sources.Select(s => new DumpSourceInfo
            {
                Prefix = s.Prefix,
                Name = s.Name,
                Enabled = s.Enabled,
                Loaded = _dump.Peek(s) != null,
                Count = _dump.Peek(s)?.Entries.Count
            }).ToList());
        }

        /// <summary>
        /// What the dump holds against what the library owns: totals, the coverage of each group, and a page of entries
        /// (filtered and sorted here: a dump can hold tens of thousands of entries).
        /// </summary>
        [HttpGet("Coverage")]
        public async Task<ActionResult<DumpCoverage>> Coverage([FromQuery] DumpQuery query, CancellationToken cancellationToken)
        {
            var source = FindSource(query.Prefix);
            if (source == null)
            {
                return NotFound("Source de dump introuvable");
            }

            var (catalog, error) = await _dump.GetCatalogAsync(source, query.Refresh, cancellationToken);
            if (catalog == null)
            {
                return Ok(new DumpCoverage { Prefix = source.Prefix, SourceName = source.Name, Error = error });
            }

            var rows = RowsOf(catalog, source);
            var owned = OwnedIds(source);
            foreach (var row in rows)
            {
                row.Owned = owned.Contains(row.Id);
            }

            var coverage = new DumpCoverage
            {
                Prefix = source.Prefix,
                SourceName = source.Name,
                LoadedAtUtc = catalog.LoadedAtUtc,
                Error = error,
                Total = rows.Count,
                Owned = rows.Count(r => r.Owned),
            };
            coverage.Missing = coverage.Total - coverage.Owned;
            coverage.Groups = rows.GroupBy(r => r.Group, StringComparer.OrdinalIgnoreCase)
                .Select(g => new DumpGroupCoverage { Group = g.Key, Total = g.Count(), Owned = g.Count(r => r.Owned), Missing = g.Count(r => !r.Owned) })
                .OrderByDescending(g => g.Missing).ThenBy(g => g.Group, StringComparer.OrdinalIgnoreCase).ToList();
            coverage.Tags = rows.SelectMany(r => r.Tags).GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                .Select(g => new DumpTagCount { Name = g.Key, Count = g.Count() })
                .OrderByDescending(t => t.Count).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Take(120).ToList();
            coverage.Years = rows.Where(r => r.Year.HasValue).Select(r => r.Year.Value).Distinct().OrderByDescending(y => y).ToList();

            var filtered = Filter(rows, query).ToList();
            coverage.Matching = filtered.Count;
            var size = Math.Clamp(query.PageSize <= 0 ? 24 : query.PageSize, 10, 200);
            var pages = Math.Max(1, (int)Math.Ceiling(filtered.Count / (double)size));
            coverage.PageSize = size;
            coverage.Pages = pages;
            coverage.Page = Math.Clamp(query.Page <= 0 ? 1 : query.Page, 1, pages);
            coverage.Items = filtered.Skip((coverage.Page - 1) * size).Take(size).ToList();
            return Ok(coverage);
        }

        /// <summary>The same filters, every matching entry, as CSV</summary>
        [HttpGet("Export")]
        public async Task<ActionResult> Export([FromQuery] DumpQuery query, CancellationToken cancellationToken)
        {
            var source = FindSource(query.Prefix);
            if (source == null)
            {
                return NotFound("Source de dump introuvable");
            }

            var (catalog, error) = await _dump.GetCatalogAsync(source, false, cancellationToken);
            if (catalog == null)
            {
                return StatusCode(StatusCodes.Status502BadGateway, error);
            }

            var rows = RowsOf(catalog, source);
            var owned = OwnedIds(source);
            foreach (var row in rows)
            {
                row.Owned = owned.Contains(row.Id);
            }

            var sb = new StringBuilder("﻿tag;id;nom;groupe;date;tags;statut\r\n");
            foreach (var row in Filter(rows, query))
            {
                sb.Append(Csv(row.Tag)).Append(';').Append(Csv(row.Id)).Append(';').Append(Csv(row.Name)).Append(';').Append(Csv(row.Group)).Append(';')
                    .Append(row.Date?.ToString("yyyy-MM-dd") ?? string.Empty).Append(';').Append(Csv(string.Join(", ", row.Tags))).Append(';')
                    .Append(row.Owned ? "possédé" : "manquant").Append("\r\n");
            }

            return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv; charset=utf-8", $"{source.Prefix}-dump.csv");
        }

        private static string Csv(string value)
        {
            value ??= string.Empty;
            // A cell that starts with = + - @ would be read as a formula by a spreadsheet
            if (value.Length > 0 && "=+-@".IndexOf(value[0]) >= 0)
            {
                value = "'" + value;
            }

            return value.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }

        private static SourceConfiguration FindSource(string prefix)
        {
            return (Plugin.Instance?.Configuration?.Sources ?? new List<SourceConfiguration>())
                .FirstOrDefault(s => s.IsDump && string.Equals(s.Prefix, prefix, StringComparison.OrdinalIgnoreCase));
        }

        private static IEnumerable<DumpRow> Filter(List<DumpRow> rows, DumpQuery q)
        {
            IEnumerable<DumpRow> result = rows;
            var status = (q.Status ?? "missing").ToLowerInvariant();
            if (status == "missing")
            {
                result = result.Where(r => !r.Owned);
            }
            else if (status == "owned")
            {
                result = result.Where(r => r.Owned);
            }

            if (!string.IsNullOrWhiteSpace(q.Group))
            {
                result = result.Where(r => string.Equals(r.Group, q.Group.Trim(), StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(q.Tag))
            {
                result = result.Where(r => r.Tags.Any(t => string.Equals(t, q.Tag.Trim(), StringComparison.OrdinalIgnoreCase)));
            }

            if (q.Year.HasValue)
            {
                result = result.Where(r => r.Year == q.Year);
            }

            var words = DumpStore.Normalize(q.Q ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 0)
            {
                result = result.Where(r => words.All(w => r.Search.Contains(w, StringComparison.Ordinal)));
            }

            var desc = q.Desc;
            return (q.Sort ?? "group").ToLowerInvariant() switch
            {
                "name" => Order(result, r => r.Name ?? string.Empty, desc),
                "date" => desc ? result.OrderByDescending(r => r.Date ?? DateTime.MinValue).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                               : result.OrderBy(r => r.Date ?? DateTime.MaxValue).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
                "id" => desc ? result.OrderByDescending(r => r.Id, new NaturalStringComparer()) : result.OrderBy(r => r.Id, new NaturalStringComparer()),
                _ => desc ? result.OrderByDescending(r => r.Group, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Date ?? DateTime.MaxValue)
                          : result.OrderBy(r => r.Group, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Date ?? DateTime.MaxValue)
            };
        }

        private static IEnumerable<DumpRow> Order(IEnumerable<DumpRow> rows, Func<DumpRow, string> key, bool desc)
            => desc ? rows.OrderByDescending(key, StringComparer.OrdinalIgnoreCase) : rows.OrderBy(key, StringComparer.OrdinalIgnoreCase);

        /// <summary>"2" before "10"</summary>
        private sealed class NaturalStringComparer : IComparer<string>
        {
            public int Compare(string a, string b)
            {
                if (long.TryParse(a, out var x) && long.TryParse(b, out var y))
                {
                    return x.CompareTo(y);
                }

                return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            }
        }

        // The light view of each entry (computed once per downloaded copy: parsing thousands of entries at every click would be slow)
        private static readonly ConditionalWeakTable<DumpCatalog, Tuple<string, List<DumpRow>>> RowCache = new ConditionalWeakTable<DumpCatalog, Tuple<string, List<DumpRow>>>();

        private static List<DumpRow> RowsOf(DumpCatalog catalog, SourceConfiguration source)
        {
            var mapping = source.VideoFieldMappings ?? new FieldMapping();
            // The mapping can change without the dump changing: the cache is only valid for the same mapping
            var previewPath = source.Dump?.PreviewImagePath;
            var mappingKey = string.Join("|", mapping.PremiereDate, mapping.Tags, mapping.PosterUrl, mapping.ThumbUrl, mapping.BackdropUrl, previewPath, source.Prefix);
            if (RowCache.TryGetValue(catalog, out var cached) && cached.Item1 == mappingKey)
            {
                return cached.Item2.Select(r => r.Copy()).ToList();
            }

            var prefix = (source.Prefix ?? string.Empty).Trim().ToLowerInvariant();
            var rows = new List<DumpRow>(catalog.Entries.Count);
            foreach (var entry in catalog.Entries)
            {
                DateTime? date = null;
                var tags = new List<string>();
                string image = null;
                try
                {
                    using var doc = JsonDocument.Parse(entry.Raw);
                    var root = doc.RootElement;
                    date = string.IsNullOrWhiteSpace(mapping.PremiereDate) ? null : JsonPathExtractor.GetDateTime(root, mapping.PremiereDate);
                    tags = string.IsNullOrWhiteSpace(mapping.Tags) || mapping.Tags.Contains("[]") ? tags
                        : JsonPathExtractor.GetStringArray(root, mapping.Tags).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => HtmlText.Decode(t.Trim())).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    foreach (var path in new[] { previewPath, mapping.ThumbUrl, mapping.PosterUrl, mapping.BackdropUrl })
                    {
                        if (string.IsNullOrWhiteSpace(path))
                        {
                            continue;
                        }

                        var value = JsonPathExtractor.GetString(root, path);
                        if (!string.IsNullOrWhiteSpace(value) && value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        {
                            image = value;
                            break;
                        }
                    }
                }
                catch (JsonException)
                {
                    // An entry that cannot be read again keeps its identity fields
                }

                var group = string.IsNullOrWhiteSpace(entry.Group) ? "(sans groupe)" : entry.Group.Trim();
                rows.Add(new DumpRow
                {
                    Id = entry.Id,
                    Tag = $"{prefix}-{entry.Id}",
                    Name = entry.Name,
                    Group = group,
                    Date = date,
                    Year = date?.Year,
                    Tags = tags,
                    ImageUrl = image,
                    Search = DumpStore.Normalize($"{entry.Name} {entry.Slug} {group} {string.Join(' ', tags)} {entry.Id}")
                });
            }

            RowCache.Remove(catalog);
            RowCache.Add(catalog, Tuple.Create(mappingKey, rows));
            return rows.Select(r => r.Copy()).ToList();
        }

        /// <summary>
        /// Ids of this dump that the library already holds: an item that carries the "custom" id "prefix-id" (stored by a
        /// match) or the {prefix-id} tag in its own file name. Episodes and movies, every library.
        /// </summary>
        private HashSet<string> OwnedIds(SourceConfiguration source)
        {
            var prefix = (source.Prefix ?? string.Empty).Trim();
            var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string tag)
            {
                var dash = (tag ?? string.Empty).IndexOf('-');
                if (dash > 0 && string.Equals(tag.Substring(0, dash), prefix, StringComparison.OrdinalIgnoreCase))
                {
                    owned.Add(tag.Substring(dash + 1));
                }
            }

            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Episode, BaseItemKind.Movie },
                Recursive = true,
                IsVirtualItem = false
            });

            foreach (var item in items)
            {
                Add(CustomId.Get(item.ProviderIds));
                if (!string.IsNullOrEmpty(item.Path))
                {
                    foreach (var tag in TagParser.ExtractAll(TagParser.GetOwnName(item.Path)))
                    {
                        Add(tag);
                    }
                }
            }

            return owned;
        }
    }

    // ----------------------------------------------------------------------------------------------------------
    // Models
    // ----------------------------------------------------------------------------------------------------------

    public class DumpTestRequest
    {
        /// <summary>The source as it is in the form</summary>
        public SourceConfiguration Source { get; set; }

        /// <summary>A file name to try (optional)</summary>
        public string FileName { get; set; }
    }

    public class DumpTestResult
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        public long DurationMs { get; set; }
        public int Count { get; set; }
        public int WithSlug { get; set; }
        public int WithSearchTitles { get; set; }
        public int WithGroup { get; set; }
        public int GroupCount { get; set; }
        public List<DumpTestEntry> Samples { get; set; } = new List<DumpTestEntry>();
        public List<DumpTestGroup> Groups { get; set; } = new List<DumpTestGroup>();
        public int MinScore { get; set; }
        public List<DumpTestCandidate> Candidates { get; set; }
    }

    public class DumpTestEntry
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Group { get; set; }
    }

    public class DumpTestGroup
    {
        public string Name { get; set; }
        public int Count { get; set; }
    }

    public class DumpTestCandidate : DumpTestEntry
    {
        public double Score { get; set; }

        /// <summary>The score reaches the source's minimum: the file would be matched automatically</summary>
        public bool Accepted { get; set; }
        public string How { get; set; }
        public string Tag { get; set; }
    }

    public class DumpSourceInfo
    {
        public string Prefix { get; set; }
        public string Name { get; set; }
        public bool Enabled { get; set; }
        public bool Loaded { get; set; }
        public int? Count { get; set; }
    }

    public class DumpQuery
    {
        public string Prefix { get; set; }

        /// <summary>missing (default) | owned | all</summary>
        public string Status { get; set; }
        public string Group { get; set; }
        public string Tag { get; set; }
        public int? Year { get; set; }
        public string Q { get; set; }

        /// <summary>group (default) | name | date | id</summary>
        public string Sort { get; set; }
        public bool Desc { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }

        /// <summary>Download the dump again before answering</summary>
        public bool Refresh { get; set; }
    }

    public class DumpRow
    {
        public string Id { get; set; }

        /// <summary>"prefix-id": what to put between braces in a file name</summary>
        public string Tag { get; set; }
        public string Name { get; set; }
        public string Group { get; set; }
        public DateTime? Date { get; set; }
        public int? Year { get; set; }
        public List<string> Tags { get; set; } = new List<string>();

        /// <summary>The picture's address in the dump (kept on the server: the screen asks the "Image" route for it)</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string ImageUrl { get; set; }

        public bool HasImage => !string.IsNullOrWhiteSpace(ImageUrl);
        public bool Owned { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public string Search { get; set; }

        public DumpRow Copy() => (DumpRow)MemberwiseClone();
    }

    public class DumpGroupCoverage
    {
        public string Group { get; set; }
        public int Total { get; set; }
        public int Owned { get; set; }
        public int Missing { get; set; }
    }

    public class DumpTagCount
    {
        public string Name { get; set; }
        public int Count { get; set; }
    }

    public class DumpCoverage
    {
        public string Prefix { get; set; }
        public string SourceName { get; set; }
        public DateTime? LoadedAtUtc { get; set; }

        /// <summary>Why the dump could not be loaded (or why the copy shown is not up to date)</summary>
        public string Error { get; set; }
        public int Total { get; set; }
        public int Owned { get; set; }
        public int Missing { get; set; }
        public List<DumpGroupCoverage> Groups { get; set; } = new List<DumpGroupCoverage>();
        public List<DumpTagCount> Tags { get; set; } = new List<DumpTagCount>();
        public List<int> Years { get; set; } = new List<int>();

        /// <summary>Entries matching the filters</summary>
        public int Matching { get; set; }
        public int Page { get; set; }
        public int Pages { get; set; }
        public int PageSize { get; set; }
        public List<DumpRow> Items { get; set; } = new List<DumpRow>();
    }
}
