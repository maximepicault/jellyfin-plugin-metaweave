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
        private readonly CustomApiClient _api;
        private readonly DumpCoverageService _svc;

        public DumpController(DumpStore dump, CustomApiClient api, DumpCoverageService svc)
        {
            _dump = dump;
            _api = api;
            _svc = svc;
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
            var index = _svc.ImageIndex(source, catalog);
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

            var rows = _svc.LinkedRows(source, catalog);

            var summary = _svc.SummaryOf(source, catalog, rows);
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
                coverage.SeriesInfo = DumpCoverageService.SeriesOf(rows.Where(r => r.SeriesKey == query.Series.Trim()).ToList()).FirstOrDefault();
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

            var rows = _svc.LinkedRows(source, catalog);
            var summary = _svc.SummaryOf(source, catalog, rows);
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

            var rows = _svc.LinkedRows(source, catalog);

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
