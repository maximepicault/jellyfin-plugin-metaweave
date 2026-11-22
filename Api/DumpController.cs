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
using MediaBrowser.Controller.Entities.TV;
using System.Text.RegularExpressions;
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
        private readonly DumpCoverageCache _coverage;

        public DumpController(DumpStore dump, ILibraryManager libraryManager, CustomApiClient api, DumpCoverageCache coverage)
        {
            _dump = dump;
            _libraryManager = libraryManager;
            _api = api;
            _coverage = coverage;
        }

        // ---- what the coverage screen computes is kept (DumpCoverageCache), shared and read-only ----

        private static string SlotOf(SourceConfiguration source, string what) => (source.Prefix ?? string.Empty).Trim().ToLowerInvariant() + ":" + what;

        /// <summary>What a kept result must still equal: the mapping, and every setting of the dump (the matching uses them)</summary>
        private static string SettingsKey(SourceConfiguration source) => MappingKeyOf(source) + "|" + JsonSerializer.Serialize(source.Dump ?? new DumpSettings());

        /// <summary>The entries of the dump linked to the library (owned, series, seasons). Shared: never modify them.</summary>
        private List<DumpRow> LinkedRows(SourceConfiguration source, DumpCatalog catalog)
        {
            return _coverage.GetOrBuild(SlotOf(source, "rows"), catalog, SettingsKey(source), () =>
            {
                var rows = RowsOf(catalog, source);
                LinkOwned(rows, OwnedIds(source), catalog, source);
                LinkContainers(rows, source);
                return rows;
            });
        }

        private sealed class CoverageSummary
        {
            public int Total;
            public int Owned;
            public bool HasSeries;
            public List<DumpGroupCoverage> GroupsByMissing;
            public List<DumpGroupCoverage> GroupsByName;
            public List<DumpTagCount> Tags;
            public List<int> Years;
            public List<KeyValuePair<string, List<DumpRow>>> SeriesGroups;
        }

        /// <summary>The totals, groups, tags, years and series groups of the whole dump (independent of the screen's filters)</summary>
        private CoverageSummary SummaryOf(SourceConfiguration source, DumpCatalog catalog, List<DumpRow> rows)
        {
            return _coverage.GetOrBuild(SlotOf(source, "summary"), catalog, SettingsKey(source), () =>
            {
                var groups = rows.GroupBy(r => r.Group, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new DumpGroupCoverage { Group = g.Key, Total = g.Count(), Owned = g.Count(r => r.Owned), Missing = g.Count(r => !r.Owned) }).ToList();
                return new CoverageSummary
                {
                    Total = rows.Count,
                    Owned = rows.Count(r => r.Owned),
                    HasSeries = rows.Any(r => r.SeriesKey != null),
                    GroupsByMissing = groups.OrderByDescending(g => g.Missing).ThenBy(g => g.Group, StringComparer.OrdinalIgnoreCase).ToList(),
                    GroupsByName = groups.OrderBy(g => DumpStore.Normalize(g.Group), StringComparer.Ordinal).ToList(),
                    Tags = rows.SelectMany(r => r.Tags).GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                        .Select(g => new DumpTagCount { Name = g.Key, Count = g.Count() })
                        .OrderByDescending(t => t.Count).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Take(120).ToList(),
                    Years = rows.Where(r => r.Year.HasValue).Select(r => r.Year.Value).Distinct().OrderByDescending(y => y).ToList(),
                    SeriesGroups = rows.GroupBy(r => r.SeriesKey ?? AloneKey).Select(g => new KeyValuePair<string, List<DumpRow>>(g.Key, g.ToList())).ToList()
                };
            });
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

            // One page asks for dozens of pictures: the entries are indexed once per copy of the dump (no library involved)
            var index = _coverage.GetOrBuild(SlotOf(source, "index"), catalog, MappingKeyOf(source),
                () => RowsOf(catalog, source).ToDictionary(r => r.Id, r => r.ImageUrl, StringComparer.OrdinalIgnoreCase), libraryDependent: false);
            index.TryGetValue(id ?? string.Empty, out var url);
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
            // A failed reload that falls back to an older copy is still a failure here: the form's settings did not work
            var result = new DumpTestResult { DurationMs = stopwatch.ElapsedMilliseconds, Error = error };
            if (catalog == null || error != null)
            {
                result.ResponsePreview = _dump.LastResponsePreview(source);
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
                var matchWatch = Stopwatch.StartNew();
                var candidates = _dump.FindCandidates(catalog, source.Dump, path, name, 5, Math.Min(40, source.Dump.MinScore), out var how);
                result.MatchMs = matchWatch.Elapsed.TotalMilliseconds;
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

            var rows = LinkedRows(source, catalog);

            var summary = SummaryOf(source, catalog, rows);
            var coverage = new DumpCoverage
            {
                Prefix = source.Prefix,
                SourceName = source.Name,
                LoadedAtUtc = catalog.LoadedAtUtc,
                Error = error,
                Total = summary.Total,
                Owned = summary.Owned,
                Missing = summary.Total - summary.Owned,
                Groups = summary.GroupsByMissing,
                Tags = summary.Tags,
                Years = summary.Years
            };

            coverage.PreviewRatio = PreviewRatioOf(source);
            if (!string.IsNullOrWhiteSpace(query.Series) && query.Series != AloneKey)
            {
                coverage.SeriesInfo = SeriesOf(rows.Where(r => r.SeriesKey == query.Series.Trim()).ToList()).FirstOrDefault();
            }

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

        /// <summary>Key of the pseudo-series that holds the entries that belong to no series</summary>
        public const string AloneKey = "_alone";

        /// <summary>
        /// The first level of the catalogue screen: one card per inferred series (plus one for the entries that belong to none),
        /// like the series cards of the missing-episodes screen. Filtered, sorted and paged here.
        /// </summary>
        [HttpGet("Series")]
        public async Task<ActionResult<DumpSeriesPage>> SeriesCards([FromQuery] DumpSeriesQuery query, CancellationToken cancellationToken)
        {
            var source = FindSource(query.Prefix);
            if (source == null)
            {
                return NotFound("Source de dump introuvable");
            }

            var (catalog, error) = await _dump.GetCatalogAsync(source, query.Refresh, cancellationToken);
            if (catalog == null)
            {
                return Ok(new DumpSeriesPage { Prefix = source.Prefix, SourceName = source.Name, Error = error });
            }

            var rows = LinkedRows(source, catalog);
            var summary = SummaryOf(source, catalog, rows);
            var page = new DumpSeriesPage
            {
                Prefix = source.Prefix, SourceName = source.Name, LoadedAtUtc = catalog.LoadedAtUtc, Error = error,
                Total = summary.Total, Owned = summary.Owned
            };
            page.Missing = page.Total - page.Owned;
            page.Groups = summary.GroupsByName;
            page.HasSeries = summary.HasSeries;
            page.Years = summary.Years;
            page.PreviewRatio = PreviewRatioOf(source);

            var words = DumpStore.Normalize(query.Q ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var cards = new List<DumpSeriesCard>();
            foreach (var g in summary.SeriesGroups)
            {
                var members = string.IsNullOrWhiteSpace(query.Group)
                    ? g.Value
                    : g.Value.Where(r => string.Equals(r.Group, query.Group.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
                if (members.Count == 0)
                {
                    continue;
                }

                if (words.Length > 0 && !members.Any(r => words.All(w => r.Search.Contains(w, StringComparison.Ordinal))))
                {
                    continue;
                }

                // Score interval: keep the series that hold at least one owned file recognised within it
                if (ScoreFilterActive(query.MinScore, query.MaxScore)
                    && !members.Any(r => r.MatchScore.HasValue && r.MatchScore.Value >= (query.MinScore ?? 0) && r.MatchScore.Value <= (query.MaxScore ?? 100)))
                {
                    continue;
                }

                // Release years: keep the series that hold at least one entry released within them
                if (YearFilterActive(null, query.MinYear, query.MaxYear)
                    && !members.Any(r => r.Year.HasValue && r.Year.Value >= (query.MinYear ?? int.MinValue) && r.Year.Value <= (query.MaxYear ?? int.MaxValue)))
                {
                    continue;
                }

                var alone = false;
                var linked = members.Where(r => r.Owned && !string.IsNullOrWhiteSpace(r.LibrarySeries)).ToList();
                var withImage = members.Where(r => r.HasImage).OrderBy(r => r.Owned).FirstOrDefault();
                cards.Add(new DumpSeriesCard
                {
                    Key = g.Key,
                    Alone = alone,
                    Name = members[0].Series,
                    LibrarySeries = linked.GroupBy(r => r.LibrarySeries).OrderByDescending(x => x.Count()).Select(x => x.Key).FirstOrDefault() ?? members.FirstOrDefault(r => r.LinkItemId != null)?.LinkSeries,
                    LibrarySeasons = linked.Where(r => r.LibrarySeason.HasValue).Select(r => r.LibrarySeason.Value)
                        .Concat(members.Where(r => r.LinkSeason.HasValue).Select(r => r.LinkSeason.Value)).Distinct().OrderBy(x => x).ToList(),
                    LibraryItemId = members.FirstOrDefault(r => r.LinkItemId != null)?.LinkItemId,
                    // Only when every entry has the same group: otherwise the card would claim a brand it does not belong to
                    Group = members.Select(r => r.Group).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 && !string.IsNullOrWhiteSpace(members[0].Group) && members[0].Group != "(sans groupe)" ? members[0].Group : null,
                    Total = members.Count,
                    Owned = members.Count(r => r.Owned),
                    Missing = members.Count(r => !r.Owned),
                    ImageId = withImage?.Id,
                    // The poorest recognition among its owned files: where a file name is worth fixing by hand
                    WorstScore = members.Where(r => r.MatchScore.HasValue).Select(r => (int?)r.MatchScore).DefaultIfEmpty(null).Min(),
                    FirstDate = members.Where(r => r.Date.HasValue).Select(r => r.Date).DefaultIfEmpty(null).Min(),
                    LastDate = members.Where(r => r.Date.HasValue).Select(r => r.Date).DefaultIfEmpty(null).Max()
                });
            }

            // Complete / incomplete / all (the older boolean OnlyIncomplete still works)
            var completeness = (query.Completeness ?? (query.OnlyIncomplete ? "incomplete" : "all")).Trim().ToLowerInvariant();
            if (completeness == "incomplete")
            {
                cards = cards.Where(c => c.Missing > 0).ToList();
            }
            else if (completeness == "complete")
            {
                cards = cards.Where(c => c.Missing == 0).ToList();
            }

            var ordered = SortSeries(cards, query.Sort);
            var list = ordered.ToList();
            var size = Math.Clamp(query.PageSize <= 0 ? 24 : query.PageSize, 6, 120);
            page.Matching = list.Count;
            page.PageSize = size;
            page.Pages = Math.Max(1, (int)Math.Ceiling(list.Count / (double)size));
            page.Page = Math.Clamp(query.Page <= 0 ? 1 : query.Page, 1, page.Pages);
            page.Items = list.Skip((page.Page - 1) * size).Take(size).ToList();
            return Ok(page);
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

            var rows = LinkedRows(source, catalog);

            var sb = new StringBuilder("﻿tag;id;nom;groupe;date;tags;statut;fichier;score\r\n");
            foreach (var row in Filter(rows, query))
            {
                sb.Append(Csv(row.Tag)).Append(';').Append(Csv(row.Id)).Append(';').Append(Csv(row.Name)).Append(';').Append(Csv(row.Group)).Append(';')
                    .Append(row.Date?.ToString("yyyy-MM-dd") ?? string.Empty).Append(';').Append(Csv(string.Join(", ", row.Tags))).Append(';')
                    .Append(row.Owned ? "possédé" : "manquant").Append(';').Append(Csv(row.FileName)).Append(';').Append(row.MatchScore?.ToString() ?? string.Empty).Append("\r\n");
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

        private static string PreviewRatioOf(SourceConfiguration source) => source.Dump?.PreviewRatio == "2:3" ? "2:3" : "16:9";

        private static SourceConfiguration FindSource(string prefix)
        {
            return (Plugin.Instance?.Configuration?.Sources ?? new List<SourceConfiguration>())
                .FirstOrDefault(s => s.IsDump && string.Equals(s.Prefix, prefix, StringComparison.OrdinalIgnoreCase));
        }

        private static IEnumerable<DumpRow> Filter(List<DumpRow> rows, DumpQuery q)
        {
            IEnumerable<DumpRow> result = rows;
            var status = (q.Status ?? "missing").ToLowerInvariant();
            if (ScoreFilterActive(q.MinScore, q.MaxScore))
            {
                // Recognition scores only exist for the files the library owns: this filter is about those, whatever "Afficher" says
                var low = q.MinScore ?? 0;
                var high = q.MaxScore ?? 100;
                result = result.Where(r => r.Owned && r.MatchScore.HasValue && r.MatchScore.Value >= low && r.MatchScore.Value <= high);
            }
            else if (status == "missing")
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

            if (!string.IsNullOrWhiteSpace(q.Series))
            {
                result = q.Series.Trim() == AloneKey
                    ? result.Where(r => r.SeriesKey == null)
                    : result.Where(r => string.Equals(r.SeriesKey, q.Series.Trim(), StringComparison.Ordinal));
            }

            if (!string.IsNullOrWhiteSpace(q.Tag))
            {
                result = result.Where(r => r.Tags.Any(t => string.Equals(t, q.Tag.Trim(), StringComparison.OrdinalIgnoreCase)));
            }

            if (YearFilterActive(q.Year, q.MinYear, q.MaxYear))
            {
                var from = q.MinYear ?? q.Year ?? int.MinValue;
                var to = q.MaxYear ?? q.Year ?? int.MaxValue;
                result = result.Where(r => r.Year.HasValue && r.Year.Value >= from && r.Year.Value <= to);
            }

            var words = DumpStore.Normalize(q.Q ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 0)
            {
                result = result.Where(r => words.All(w => r.Search.Contains(w, StringComparison.Ordinal)));
            }

            return SortRows(result, q.Sort, q.Desc);
        }

        /// <summary>A release-date filter: one year (Year) or an interval of years (MinYear / MaxYear, either end optional)</summary>
        private static bool YearFilterActive(int? year, int? min, int? max) => year.HasValue || min.HasValue || max.HasValue;

        /// <summary>The interval is a filter only when it narrows the whole 0-100 range</summary>
        private static bool ScoreFilterActive(int? min, int? max) => (min ?? 0) > 0 || (max ?? 100) < 100;

        /// <summary>
        /// Several sort keys can be chained ("score,datenew,name"): each one breaks the ties of the one before. The direction toggle
        /// only reverses the first key. Release dates and scores that are missing always come last.
        /// </summary>
        private static IEnumerable<DumpRow> SortRows(IEnumerable<DumpRow> rows, string sort, bool desc)
        {
            var keys = (string.IsNullOrWhiteSpace(sort) ? "group" : sort).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(k => k.ToLowerInvariant()).Distinct().Take(3).ToList();
            IOrderedEnumerable<DumpRow> ordered = null;
            for (var i = 0; i < keys.Count; i++)
            {
                var d = i == 0 && desc;
                switch (keys[i])
                {
                    // Names sort as people read them: no case, no accents ("Épisode" next to "Episode", not after "Zorro")
                    case "name": ordered = Chain(rows, ordered, r => r.SortName, StringComparer.Ordinal, d); break;
                    case "id": ordered = Chain(rows, ordered, r => r.Id, new NaturalStringComparer(), d); break;
                    case "date": ordered = Chain(rows, ordered, r => r.Date ?? (d ? DateTime.MinValue : DateTime.MaxValue), Comparer<DateTime>.Default, d); break;
                    case "score": ordered = Chain(rows, ordered, r => r.MatchScore ?? (d ? -1 : int.MaxValue), Comparer<int>.Default, d); break;
                    default: ordered = Chain(rows, ordered, r => r.SortGroup, StringComparer.Ordinal, d); break;
                }
            }

            return ordered.ThenBy(r => r.SortName, StringComparer.Ordinal);
        }

        private static IOrderedEnumerable<DumpRow> Chain<TKey>(IEnumerable<DumpRow> rows, IOrderedEnumerable<DumpRow> ordered, Func<DumpRow, TKey> key, IComparer<TKey> comparer, bool desc)
        {
            if (ordered == null)
            {
                return desc ? rows.OrderByDescending(key, comparer) : rows.OrderBy(key, comparer);
            }

            return desc ? ordered.ThenByDescending(key, comparer) : ordered.ThenBy(key, comparer);
        }

        /// <summary>The series, sorted by one to three chained keys (see SortRows)</summary>
        private static IOrderedEnumerable<DumpSeriesCard> SortSeries(List<DumpSeriesCard> cards, string sort)
        {
            var keys = (string.IsNullOrWhiteSpace(sort) ? "missing" : sort).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(k => k.ToLowerInvariant()).Distinct().Take(3).ToList();
            var ordered = cards.OrderBy(_ => 0);
            foreach (var key in keys)
            {
                ordered = key switch
                {
                    "name" => ordered.ThenBy(c => DumpStore.Normalize(c.LibrarySeries ?? c.Name), StringComparer.Ordinal),
                    "progress" => ordered.ThenBy(c => c.Total == 0 ? 1d : (double)c.Owned / c.Total),
                    // The poorest recognised first / the best first; series with no owned file last
                    "score" => ordered.ThenBy(c => c.WorstScore ?? int.MaxValue),
                    "scoredesc" => ordered.ThenByDescending(c => c.WorstScore ?? -1),
                    // Release date of a series = its latest / earliest entry
                    "datenew" => ordered.ThenByDescending(c => c.LastDate ?? DateTime.MinValue),
                    "dateold" => ordered.ThenBy(c => c.FirstDate ?? DateTime.MaxValue),
                    _ => ordered.ThenByDescending(c => c.Missing)
                };
            }

            return ordered.ThenBy(c => DumpStore.Normalize(c.LibrarySeries ?? c.Name), StringComparer.Ordinal);
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

        private static readonly JsonFieldMapper ListReader = new JsonFieldMapper(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        /// <summary>What the light rows (and everything computed from them) depend on besides the downloaded copy: the mapping paths used and the series settings</summary>
        private static string MappingKeyOf(SourceConfiguration source)
        {
            var mapping = source.VideoFieldMappings ?? new FieldMapping();
            // The catalogue has its own date path, so the date can stay unmapped (kept out of Jellyfin) and still be used here
            var datePath = string.IsNullOrWhiteSpace(source.Dump?.ReleaseDatePath) ? mapping.PremiereDate : source.Dump.ReleaseDatePath;
            return string.Join("|", datePath, mapping.Tags, mapping.PosterUrl, mapping.ThumbUrl, mapping.BackdropUrl, source.Dump?.PreviewImagePath, mapping.TagsItemField, source.Dump?.SeriesMinEntries, source.Dump?.SeriesIdPath, source.Dump?.SeriesNamePath, source.Prefix);
        }

        private static List<DumpRow> RowsOf(DumpCatalog catalog, SourceConfiguration source)
        {
            var mapping = source.VideoFieldMappings ?? new FieldMapping();
            // The mapping can change without the dump changing: the cache is only valid for the same mapping
            var previewPath = source.Dump?.PreviewImagePath;
            var datePath = string.IsNullOrWhiteSpace(source.Dump?.ReleaseDatePath) ? mapping.PremiereDate : source.Dump.ReleaseDatePath;
            var mappingKey = MappingKeyOf(source);
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
                string seriesId = null;
                string seriesName = null;
                try
                {
                    using var doc = JsonDocument.Parse(entry.Raw);
                    var root = doc.RootElement;
                    if (!string.IsNullOrWhiteSpace(source.Dump?.SeriesIdPath))
                    {
                        seriesId = (JsonPathExtractor.GetString(root, source.Dump.SeriesIdPath) ?? string.Empty).Trim();
                    }

                    if (!string.IsNullOrWhiteSpace(source.Dump?.SeriesNamePath))
                    {
                        seriesName = HtmlText.Decode((JsonPathExtractor.GetString(root, source.Dump.SeriesNamePath) ?? string.Empty).Trim());
                    }

                    date = ReadDate(root, datePath);
                    // Read like the video mapping reads its tags: "tags", "tags[].name", the "Champ de chaque tag" of a list of objects, "a || b"
                    tags = ListReader.ReadStringList(root, mapping.Tags, mapping.TagsItemField)
                        .Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => HtmlText.Decode(t.Trim())).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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
                    Search = DumpStore.Normalize($"{entry.Name} {entry.Slug} {group} {string.Join(' ', tags)} {entry.Id}"),
                    SortName = DumpStore.Normalize(entry.Name ?? string.Empty),
                    SortGroup = DumpStore.Normalize(group),
                    Number = SplitNumber(entry.Name, out var seriesBase),
                    Series = seriesBase,
                    SeriesId = string.IsNullOrEmpty(seriesId) ? null : seriesId,
                    SeriesNameValue = string.IsNullOrEmpty(seriesName) ? null : seriesName
                });
            }

            GroupSeries(rows, Math.Max(0, source.Dump?.SeriesMinEntries ?? 2));
            foreach (var row in rows)
            {
                row.Search = (row.Search + " " + DumpStore.Normalize(row.Series ?? string.Empty)).Trim();
            }

            RowCache.Remove(catalog);
            RowCache.Add(catalog, Tuple.Create(mappingKey, rows));
            return rows.Select(r => r.Copy()).ToList();
        }

        private sealed class OwnedInfo
        {
            public string Series;
            public int? Season;

            /// <summary>The library file's path, and whether its name carries the entry's {prefix-id} tag (then it is certain)</summary>
            public string Path;
            public bool ByTag;
        }

        /// <summary>
        /// A date read from a path (alternatives with "||"): a text is parsed as UTC when it carries no zone, a number is Unix seconds
        /// (or milliseconds when it is that big). Null when nothing readable.
        /// </summary>
        private static DateTime? ReadDate(JsonElement root, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            foreach (var alternative in JsonPathExtractor.Alternatives(path))
            {
                if (!JsonPathExtractor.TryNavigate(root, alternative, out var value))
                {
                    continue;
                }

                if (value.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
                {
                    return parsed;
                }

                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number > 0)
                {
                    try
                    {
                        return number > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(number).UtcDateTime : DateTimeOffset.FromUnixTimeSeconds(number).UtcDateTime;
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        // not a date: try the next alternative
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Entries of this dump that the library already holds: an item that carries the "custom" id "prefix-id" (stored by a
        /// match) or the {prefix-id} tag in its own file name. Episodes and movies, every library. For an episode, the series and
        /// season Jellyfin files it under (so the screen can speak of "your series").
        /// </summary>
        private Dictionary<string, OwnedInfo> OwnedIds(SourceConfiguration source)
        {
            var prefix = (source.Prefix ?? string.Empty).Trim();
            var owned = new Dictionary<string, OwnedInfo>(StringComparer.OrdinalIgnoreCase);

            void Add(string tag, BaseItem item, bool byTag)
            {
                var dash = (tag ?? string.Empty).IndexOf('-');
                if (dash > 0 && string.Equals(tag.Substring(0, dash), prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var info = new OwnedInfo { Path = item.Path, ByTag = byTag || (owned.TryGetValue(tag.Substring(dash + 1), out var before) && before.ByTag) };
                    if (item is Episode episode && !string.IsNullOrWhiteSpace(episode.SeriesName))
                    {
                        info.Series = episode.SeriesName;
                        // ParentIndexNumber is stored on the episode; the season item is only loaded when it is missing
                        info.Season = episode.ParentIndexNumber ?? episode.Season?.IndexNumber;
                    }

                    owned[tag.Substring(dash + 1)] = info;
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
                Add(CustomId.Get(item.ProviderIds), item, false);
                if (!string.IsNullOrEmpty(item.Path))
                {
                    foreach (var tag in TagParser.ExtractAll(TagParser.GetOwnName(item.Path)))
                    {
                        Add(tag, item, true);
                    }
                }
            }

            return owned;
        }

        /// <summary>A series or season folder of the library that carries a {prefix-id} tag of this dump</summary>
        private sealed class ContainerInfo
        {
            public Guid Id;
            public bool IsSeason;
            public string SeriesName;
            public int? SeasonNumber;
        }

        /// <summary>
        /// Links the dump's series (those grouped by a series id) to the library: a season or series folder whose own name carries
        /// {prefix-seriesId} (or whose stored id is that), so the screen can open it even before any entry is owned. A season wins over
        /// a series with the same tag.
        /// </summary>
        private void LinkContainers(List<DumpRow> rows, SourceConfiguration source)
        {
            if (!rows.Any(r => r.SeriesId != null))
            {
                return;
            }

            var prefix = (source.Prefix ?? string.Empty).Trim();
            var found = new Dictionary<string, ContainerInfo>(StringComparer.OrdinalIgnoreCase);
            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Series, BaseItemKind.Season },
                Recursive = true,
                IsVirtualItem = false
            });

            foreach (var item in items)
            {
                var tags = new List<string>();
                if (!string.IsNullOrEmpty(item.Path))
                {
                    tags.AddRange(TagParser.ExtractAll(TagParser.GetOwnName(item.Path)));
                }

                var stored = CustomId.Get(item.ProviderIds);
                if (stored != null)
                {
                    tags.Add(stored);
                }

                foreach (var tag in tags)
                {
                    var dash = tag.IndexOf('-');
                    if (dash <= 0 || !string.Equals(tag.Substring(0, dash), prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var id = tag.Substring(dash + 1);
                    var season = item as Season;
                    if (found.TryGetValue(id, out var before) && before.IsSeason && season == null)
                    {
                        continue;
                    }

                    found[id] = new ContainerInfo
                    {
                        Id = item.Id,
                        IsSeason = season != null,
                        SeriesName = season != null ? season.SeriesName : item.Name,
                        SeasonNumber = season?.IndexNumber
                    };
                }
            }

            foreach (var row in rows.Where(r => r.SeriesId != null))
            {
                if (found.TryGetValue(row.SeriesId, out var container))
                {
                    row.LinkItemId = container.Id.ToString("N");
                    row.LinkKind = container.IsSeason ? "Season" : "Series";
                    row.LinkSeries = container.SeriesName;
                    row.LinkSeason = container.SeasonNumber;
                }
            }
        }

        /// <summary>
        /// Marks the owned entries and tells HOW each owned file was recognised: certain when its name carries the tag, otherwise the
        /// score of its name against the entry (so the poor matches can be found and their file names fixed by hand)
        /// </summary>
        private void LinkOwned(List<DumpRow> rows, Dictionary<string, OwnedInfo> owned, DumpCatalog catalog, SourceConfiguration source)
        {
            foreach (var row in rows)
            {
                if (!owned.TryGetValue(row.Id, out var info))
                {
                    continue;
                }

                row.Owned = true;
                row.LibrarySeries = info.Series;
                row.LibrarySeason = info.Season;
                row.FileName = string.IsNullOrEmpty(info.Path) ? null : System.IO.Path.GetFileName(info.Path);
                if (info.ByTag)
                {
                    row.MatchScore = 100;
                    row.MatchHow = "tag";
                }
                else if (catalog.ById.TryGetValue(row.Id, out var entry) && _dump.ScoreOf(catalog, source.Dump, info.Path, entry) is var (score, how))
                {
                    row.MatchScore = score;
                    row.MatchHow = how;
                }
            }
        }

        // "Title 2", "Title - Ep 3", "Title #4", "Title Partie 2": the trailing number (1-3 digits, a 4-digit one is a year)
        private static readonly Regex TrailingNumber = new Regex(
            @"^(?<base>.*?)[\s\-–:._]*(?:(?:ep|épisode|episode|partie|part|saison|season|vol|volume|n°|#)\.?\s*)?(?<n>\d{1,3})\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

        /// <summary>The name without its trailing number, and that number (null when there is none)</summary>
        private static int? SplitNumber(string name, out string baseName)
        {
            baseName = (name ?? string.Empty).Trim();
            var m = TrailingNumber.Match(baseName);
            if (m.Success && m.Groups["base"].Value.Trim().Length >= 3)
            {
                baseName = m.Groups["base"].Value.Trim().TrimEnd('-', '–', ':', '.', '_', ' ');
                return int.Parse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture);
            }

            return null;
        }

        /// <summary>
        /// Series of the entries: by the dump's series id when an entry has one (SeriesIdPath), else by name (see GroupSeriesByName).
        /// </summary>
        private static void GroupSeries(List<DumpRow> rows, int min)
        {
            // Entries that carry a series id are grouped by it first: reliable, whatever their titles look like
            var byId = rows.Where(r => r.SeriesId != null).GroupBy(r => r.SeriesId, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var group in byId)
            {
                var members = group.ToList();
                var name = members.Select(r => r.SeriesNameValue).FirstOrDefault(n => n != null);
                if (name == null)
                {
                    name = members.Count == 1 ? members[0].Name : CommonName(members, group.Key);
                }

                foreach (var r in members)
                {
                    r.SeriesKey = "s:" + group.Key;
                    r.Series = name;
                    r.Grouped = members.Count > 1;
                }
            }

            GroupSeriesByName(rows.Where(r => r.SeriesId == null).ToList(), min);
        }

        /// <summary>Name of a series that gives none: the most common title without its number, else the words the titles share, else "Série {id}"</summary>
        private static string CommonName(List<DumpRow> members, string id)
        {
            var common = members.Where(r => !string.IsNullOrWhiteSpace(r.Series)).GroupBy(r => r.Series, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).FirstOrDefault();
            if (common != null && common.Count() >= 2)
            {
                return common.Key;
            }

            var words = (members[0].Name ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            var shared = 0;
            while (shared < words.Count && members.All(r => (r.Name ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > shared
                && string.Equals((r.Name ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries)[shared], words[shared], StringComparison.OrdinalIgnoreCase)))
            {
                shared++;
            }

            var prefix = string.Join(' ', words.Take(shared)).Trim(' ', '-', '–', ':');
            return prefix.Length >= 3 ? prefix : "Série " + id;
        }

        /// <summary>
        /// Entries that share a name once the trailing number is removed form a "series" when there are at least
        /// <paramref name="min"/> of them. The display name is the one of the unnumbered entry when there is one.
        /// </summary>
        private static void GroupSeriesByName(List<DumpRow> rows, int min)
        {
            // An entry that belongs to no group of several is a series of ONE episode: its own card, named after it
            static void Alone(DumpRow r)
            {
                r.SeriesKey = "e:" + r.Id;
                r.Series = r.Name;
                r.Grouped = false;
            }

            if (min <= 0)
            {
                foreach (var r in rows) { Alone(r); }
                return;
            }

            foreach (var group in rows.GroupBy(r => DumpStore.Normalize(r.Series ?? string.Empty)))
            {
                var members = group.ToList();
                if (group.Key.Length < 3 || members.Count < min)
                {
                    foreach (var r in members) { Alone(r); }
                    continue;
                }

                var name = (members.FirstOrDefault(r => r.Number == null) ?? members[0]).Series;
                foreach (var r in members)
                {
                    r.SeriesKey = group.Key;
                    r.Series = name;
                    r.Grouped = true;
                }
            }
        }

        /// <summary>"prefix-seriesId" of a series grouped by the dump's series id (its entries' tags give the prefix); null otherwise</summary>
        private static string SeriesTagOf(List<DumpRow> members)
        {
            var withId = members.FirstOrDefault(r => r.SeriesId != null);
            var dash = withId?.Tag?.IndexOf('-') ?? -1;
            return dash > 0 ? withId.Tag.Substring(0, dash) + "-" + withId.SeriesId : null;
        }

        private static List<DumpSeriesCoverage> SeriesOf(List<DumpRow> rows)
        {
            return rows.Where(r => r.SeriesKey != null).GroupBy(r => r.SeriesKey).Select(g =>
            {
                var linked = g.Where(r => r.Owned && !string.IsNullOrWhiteSpace(r.LibrarySeries)).ToList();
                var container = g.FirstOrDefault(r => r.LinkItemId != null);
                return new DumpSeriesCoverage
                {
                    Key = g.Key,
                    Name = g.First().Series,
                    LibrarySeries = linked.GroupBy(r => r.LibrarySeries).OrderByDescending(x => x.Count()).Select(x => x.Key).FirstOrDefault() ?? container?.LinkSeries,
                    LibrarySeasons = linked.Where(r => r.LibrarySeason.HasValue).Select(r => r.LibrarySeason.Value)
                        .Concat(g.Where(r => r.LinkSeason.HasValue).Select(r => r.LinkSeason.Value)).Distinct().OrderBy(x => x).ToList(),
                    LibraryItemId = container?.LinkItemId,
                    LibraryItemKind = container?.LinkKind,
                    Total = g.Count(),
                    Owned = g.Count(r => r.Owned),
                    Missing = g.Count(r => !r.Owned),
                    Tag = SeriesTagOf(g.ToList()),
                    ImageId = g.FirstOrDefault(r => r.HasImage)?.Id
                };
            }).OrderByDescending(x => x.Missing).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Take(300).ToList();
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

        /// <summary>On a failure: the start of what the API answered (or of its error body), to see why the list was not found</summary>
        public string ResponsePreview { get; set; }

        /// <summary>Time spent matching the file name against the catalogue (without the download)</summary>
        public double MatchMs { get; set; }
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

        /// <summary>Release years, both ends included, either may be omitted (Year = one year)</summary>
        public int? MinYear { get; set; }
        public int? MaxYear { get; set; }
        public string Q { get; set; }

        /// <summary>Key of an inferred series</summary>
        public string Series { get; set; }

        /// <summary>Only the owned files recognised within this score interval (0-100, both ends included): the ones whose name may be worth fixing</summary>
        public int? MinScore { get; set; }
        public int? MaxScore { get; set; }

        /// <summary>One to three keys separated by commas, each breaking the ties of the one before: group (default) | name | date | id | score</summary>
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

        /// <summary>Inferred series (entries whose names only differ by a trailing number). An entry alone is a series of one episode.</summary>
        public string SeriesKey { get; set; }
        public string Series { get; set; }

        /// <summary>The series id and name read from the dump (SeriesIdPath / SeriesNamePath): they decide the series when present</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string SeriesId { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public string SeriesNameValue { get; set; }

        /// <summary>The library season or series whose folder carries this entry's series tag (LinkContainers); null when none</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string LinkItemId { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public string LinkKind { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public string LinkSeries { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public int? LinkSeason { get; set; }

        /// <summary>True when the series holds several entries (the trailing number of the title is then a hint)</summary>
        public bool Grouped { get; set; }

        /// <summary>The trailing number of the name: a season OR an episode, the title cannot tell. A hint only</summary>
        public int? Number { get; set; }

        /// <summary>Owned entries: the series and season Jellyfin files this episode under (null for a movie or an untagged file)</summary>
        public string LibrarySeries { get; set; }
        public int? LibrarySeason { get; set; }

        /// <summary>Owned entries: the library file's name, and how well it was recognised (100 = certain: the tag, an id, the exact name)</summary>
        public string FileName { get; set; }
        public int? MatchScore { get; set; }

        /// <summary>tag | id | slug | title | fuzzy | unconfirmed (the title matches but the episode number is not confirmed)</summary>
        public string MatchHow { get; set; }

        /// <summary>Name and group without case or accents, what the sort compares</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string SortName { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public string SortGroup { get; set; }

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

    public class DumpSeriesQuery
    {
        public string Prefix { get; set; }
        public string Q { get; set; }
        public string Group { get; set; }

        /// <summary>Only the series that miss something (default on the screen)</summary>
        public bool OnlyIncomplete { get; set; }

        /// <summary>One to three keys separated by commas, each breaking the ties of the one before: missing (default) | name | progress | score | scoredesc | datenew | dateold</summary>
        public string Sort { get; set; }

        /// <summary>Only the series that hold an owned file recognised within this score interval (both ends included)</summary>
        public int? MinScore { get; set; }
        public int? MaxScore { get; set; }

        /// <summary>Only the series that hold an entry released within these years (both ends included, either may be omitted)</summary>
        public int? MinYear { get; set; }
        public int? MaxYear { get; set; }

        /// <summary>all | incomplete | complete (default: all, or incomplete when OnlyIncomplete is set)</summary>
        public string Completeness { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
        public bool Refresh { get; set; }
    }

    public class DumpSeriesCard
    {
        public string Key { get; set; }

        /// <summary>Always false now (an entry alone is a series of one episode): kept so older pages do not break</summary>
        public bool Alone { get; set; }
        public string Name { get; set; }
        public string LibrarySeries { get; set; }
        public List<int> LibrarySeasons { get; set; } = new List<int>();

        /// <summary>The library season or series whose folder carries the series tag (Jellyfin item id, "N" format); null when none</summary>
        public string LibraryItemId { get; set; }

        /// <summary>The group (brand…) shared by all its entries; null when they differ</summary>
        public string Group { get; set; }
        public int Total { get; set; }
        public int Owned { get; set; }
        public int Missing { get; set; }

        /// <summary>An entry whose preview picture stands for the series</summary>
        public string ImageId { get; set; }

        /// <summary>The poorest recognition score among its owned files (null when it owns none)</summary>
        public int? WorstScore { get; set; }

        /// <summary>Release dates of its earliest and latest entries</summary>
        public DateTime? FirstDate { get; set; }
        public DateTime? LastDate { get; set; }
    }

    public class DumpSeriesPage
    {
        public string Prefix { get; set; }
        public string SourceName { get; set; }
        public DateTime? LoadedAtUtc { get; set; }
        public string Error { get; set; }
        public int Total { get; set; }
        public int Owned { get; set; }
        public int Missing { get; set; }

        /// <summary>The release years found in the dump, most recent first (for the date filter)</summary>
        public List<int> Years { get; set; } = new List<int>();

        /// <summary>false: no entry belongs to a series (grouping off or names too different): the screen lists the entries directly</summary>
        public bool HasSeries { get; set; }
        public string PreviewRatio { get; set; } = "16:9";
        public List<DumpGroupCoverage> Groups { get; set; } = new List<DumpGroupCoverage>();
        public int Matching { get; set; }
        public int Page { get; set; }
        public int Pages { get; set; }
        public int PageSize { get; set; }
        public List<DumpSeriesCard> Items { get; set; } = new List<DumpSeriesCard>();
    }

    public class DumpSeriesCoverage
    {
        public string Key { get; set; }

        /// <summary>The name inferred from the dump</summary>
        public string Name { get; set; }

        /// <summary>The series Jellyfin files the owned entries under (set when one of them is an episode of a series)</summary>
        public string LibrarySeries { get; set; }
        public List<int> LibrarySeasons { get; set; } = new List<int>();
        public int Total { get; set; }
        public int Owned { get; set; }
        public int Missing { get; set; }

        /// <summary>The library season or series whose folder carries the series tag, and which of the two it is ("Season" | "Series")</summary>
        public string LibraryItemId { get; set; }
        public string LibraryItemKind { get; set; }

        /// <summary>"prefix-seriesId" when the dump gives the series its own id (SeriesIdPath), else null: what the screen offers to copy</summary>
        public string Tag { get; set; }

        /// <summary>An entry whose picture stands for the series (null when none has one)</summary>
        public string ImageId { get; set; }
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

        /// <summary>The series being looked at (when the entries are filtered on one)</summary>
        public DumpSeriesCoverage SeriesInfo { get; set; }

        public string PreviewRatio { get; set; } = "16:9";

        /// <summary>Entries matching the filters</summary>
        public int Matching { get; set; }
        public int Page { get; set; }
        public int Pages { get; set; }
        public int PageSize { get; set; }
        public List<DumpRow> Items { get; set; } = new List<DumpRow>();
    }
}
