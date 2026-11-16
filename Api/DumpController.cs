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
            LinkOwned(rows, OwnedIds(source));

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

            var rows = RowsOf(catalog, source);
            LinkOwned(rows, OwnedIds(source));
            var page = new DumpSeriesPage
            {
                Prefix = source.Prefix, SourceName = source.Name, LoadedAtUtc = catalog.LoadedAtUtc, Error = error,
                Total = rows.Count, Owned = rows.Count(r => r.Owned)
            };
            page.Missing = page.Total - page.Owned;
            page.Groups = rows.GroupBy(r => r.Group, StringComparer.OrdinalIgnoreCase)
                .Select(g => new DumpGroupCoverage { Group = g.Key, Total = g.Count(), Owned = g.Count(r => r.Owned), Missing = g.Count(r => !r.Owned) })
                .OrderBy(g => g.Group, StringComparer.OrdinalIgnoreCase).ToList();
            page.HasSeries = rows.Any(r => r.SeriesKey != null);
            page.PreviewRatio = PreviewRatioOf(source);

            IEnumerable<DumpRow> scope = rows;
            if (!string.IsNullOrWhiteSpace(query.Group))
            {
                scope = scope.Where(r => string.Equals(r.Group, query.Group.Trim(), StringComparison.OrdinalIgnoreCase));
            }

            var words = DumpStore.Normalize(query.Q ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var cards = new List<DumpSeriesCard>();
            foreach (var g in scope.GroupBy(r => r.SeriesKey ?? AloneKey))
            {
                var members = g.ToList();
                if (words.Length > 0 && !members.Any(r => words.All(w => r.Search.Contains(w, StringComparison.Ordinal))))
                {
                    continue;
                }

                var alone = g.Key == AloneKey;
                var linked = members.Where(r => r.Owned && !string.IsNullOrWhiteSpace(r.LibrarySeries)).ToList();
                var withImage = members.Where(r => r.HasImage).OrderBy(r => r.Owned).FirstOrDefault();
                cards.Add(new DumpSeriesCard
                {
                    Key = g.Key,
                    Alone = alone,
                    Name = alone ? "Épisodes seuls" : members[0].Series,
                    LibrarySeries = alone ? null : linked.GroupBy(r => r.LibrarySeries).OrderByDescending(x => x.Count()).Select(x => x.Key).FirstOrDefault(),
                    LibrarySeasons = linked.Where(r => r.LibrarySeason.HasValue).Select(r => r.LibrarySeason.Value).Distinct().OrderBy(x => x).ToList(),
                    // Only when every entry has the same group: otherwise the card would claim a brand it does not belong to
                    Group = members.Select(r => r.Group).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 && !string.IsNullOrWhiteSpace(members[0].Group) && members[0].Group != "(sans groupe)" ? members[0].Group : null,
                    Total = members.Count,
                    Owned = members.Count(r => r.Owned),
                    Missing = members.Count(r => !r.Owned),
                    ImageId = withImage?.Id
                });
            }

            if (query.OnlyIncomplete)
            {
                cards = cards.Where(c => c.Missing > 0).ToList();
            }

            IEnumerable<DumpSeriesCard> ordered = (query.Sort ?? "missing").ToLowerInvariant() switch
            {
                "name" => cards.OrderBy(c => c.Alone).ThenBy(c => c.LibrarySeries ?? c.Name, StringComparer.OrdinalIgnoreCase),
                "progress" => cards.OrderBy(c => c.Alone).ThenBy(c => c.Total == 0 ? 1d : (double)c.Owned / c.Total).ThenByDescending(c => c.Missing),
                _ => cards.OrderBy(c => c.Alone).ThenByDescending(c => c.Missing).ThenBy(c => c.LibrarySeries ?? c.Name, StringComparer.OrdinalIgnoreCase)
            };
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

            var rows = RowsOf(catalog, source);
            LinkOwned(rows, OwnedIds(source));

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
            var mappingKey = string.Join("|", mapping.PremiereDate, mapping.Tags, mapping.PosterUrl, mapping.ThumbUrl, mapping.BackdropUrl, previewPath, source.Dump?.SeriesMinEntries, source.Prefix);
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
                    Search = DumpStore.Normalize($"{entry.Name} {entry.Slug} {group} {string.Join(' ', tags)} {entry.Id}"),
                    Number = SplitNumber(entry.Name, out var seriesBase),
                    Series = seriesBase
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

            void Add(string tag, BaseItem item)
            {
                var dash = (tag ?? string.Empty).IndexOf('-');
                if (dash > 0 && string.Equals(tag.Substring(0, dash), prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var info = new OwnedInfo();
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
                Add(CustomId.Get(item.ProviderIds), item);
                if (!string.IsNullOrEmpty(item.Path))
                {
                    foreach (var tag in TagParser.ExtractAll(TagParser.GetOwnName(item.Path)))
                    {
                        Add(tag, item);
                    }
                }
            }

            return owned;
        }

        private static void LinkOwned(List<DumpRow> rows, Dictionary<string, OwnedInfo> owned)
        {
            foreach (var row in rows)
            {
                if (owned.TryGetValue(row.Id, out var info))
                {
                    row.Owned = true;
                    row.LibrarySeries = info.Series;
                    row.LibrarySeason = info.Season;
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
        /// Entries that share a name once the trailing number is removed form a "series" when there are at least
        /// <paramref name="min"/> of them. The display name is the one of the unnumbered entry when there is one.
        /// </summary>
        private static void GroupSeries(List<DumpRow> rows, int min)
        {
            if (min <= 0)
            {
                foreach (var r in rows) { r.Series = null; }
                return;
            }

            foreach (var group in rows.GroupBy(r => DumpStore.Normalize(r.Series ?? string.Empty)))
            {
                var members = group.ToList();
                if (group.Key.Length < 3 || members.Count < min)
                {
                    foreach (var r in members) { r.Series = null; }
                    continue;
                }

                var name = (members.FirstOrDefault(r => r.Number == null) ?? members[0]).Series;
                foreach (var r in members)
                {
                    r.SeriesKey = group.Key;
                    r.Series = name;
                }
            }
        }

        private static List<DumpSeriesCoverage> SeriesOf(List<DumpRow> rows)
        {
            return rows.Where(r => r.SeriesKey != null).GroupBy(r => r.SeriesKey).Select(g =>
            {
                var linked = g.Where(r => r.Owned && !string.IsNullOrWhiteSpace(r.LibrarySeries)).ToList();
                return new DumpSeriesCoverage
                {
                    Key = g.Key,
                    Name = g.First().Series,
                    LibrarySeries = linked.GroupBy(r => r.LibrarySeries).OrderByDescending(x => x.Count()).Select(x => x.Key).FirstOrDefault(),
                    LibrarySeasons = linked.Where(r => r.LibrarySeason.HasValue).Select(r => r.LibrarySeason.Value).Distinct().OrderBy(x => x).ToList(),
                    Total = g.Count(),
                    Owned = g.Count(r => r.Owned),
                    Missing = g.Count(r => !r.Owned)
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

        /// <summary>Key of an inferred series</summary>
        public string Series { get; set; }

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

        /// <summary>Inferred series (entries whose names only differ by a trailing number); null when alone</summary>
        public string SeriesKey { get; set; }
        public string Series { get; set; }

        /// <summary>The trailing number of the name: a season OR an episode, the title cannot tell. A hint only</summary>
        public int? Number { get; set; }

        /// <summary>Owned entries: the series and season Jellyfin files this episode under (null for a movie or an untagged file)</summary>
        public string LibrarySeries { get; set; }
        public int? LibrarySeason { get; set; }

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

        /// <summary>missing (default) | name | progress</summary>
        public string Sort { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
        public bool Refresh { get; set; }
    }

    public class DumpSeriesCard
    {
        public string Key { get; set; }

        /// <summary>The pseudo-series of the entries that belong to no series</summary>
        public bool Alone { get; set; }
        public string Name { get; set; }
        public string LibrarySeries { get; set; }
        public List<int> LibrarySeasons { get; set; } = new List<int>();

        /// <summary>The group (brand…) shared by all its entries; null when they differ</summary>
        public string Group { get; set; }
        public int Total { get; set; }
        public int Owned { get; set; }
        public int Missing { get; set; }

        /// <summary>An entry whose preview picture stands for the series</summary>
        public string ImageId { get; set; }
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
