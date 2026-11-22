using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.CustomMetadata.Api;
using Jellyfin.Plugin.CustomMetadata.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>
    /// What the dump screens work out from a downloaded dump and the library: the light rows of the entries, which ones the library owns,
    /// the series (by the dump's series id or by title) and which library season or series they belong to. Shared by the coverage
    /// endpoints (<see cref="DumpController"/>) and the missing-episodes report. Results are kept by <see cref="DumpCoverageCache"/>.
    /// </summary>
    public class DumpCoverageService
    {
        private readonly DumpStore _dump;
        private readonly ILibraryManager _libraryManager;
        private readonly DumpCoverageCache _coverage;

        public DumpCoverageService(DumpStore dump, ILibraryManager libraryManager, DumpCoverageCache coverage)
        {
            _dump = dump;
            _libraryManager = libraryManager;
            _coverage = coverage;
        }

        /// <summary>id → preview image address, one index per copy of the dump (no library involved)</summary>
        internal Dictionary<string, string> ImageIndex(SourceConfiguration source, DumpCatalog catalog)
        {
            return _coverage.GetOrBuild(SlotOf(source, "index"), catalog, MappingKeyOf(source),
                () => RowsOf(catalog, source).ToDictionary(r => r.Id, r => r.ImageUrl, StringComparer.OrdinalIgnoreCase), libraryDependent: false);
        }

        // ---- what the coverage screen computes is kept (DumpCoverageCache), shared and read-only ----

        internal static string SlotOf(SourceConfiguration source, string what) => (source.Prefix ?? string.Empty).Trim().ToLowerInvariant() + ":" + what;

        /// <summary>What a kept result must still equal: the mapping, and every setting of the dump (the matching uses them)</summary>
        internal static string SettingsKey(SourceConfiguration source) => MappingKeyOf(source) + "|" + JsonSerializer.Serialize(source.Dump ?? new DumpSettings());

        /// <summary>The entries of the dump linked to the library (owned, series, seasons). Shared: never modify them.</summary>
        internal List<DumpRow> LinkedRows(SourceConfiguration source, DumpCatalog catalog)
        {
            return _coverage.GetOrBuild(SlotOf(source, "rows"), catalog, SettingsKey(source), () =>
            {
                var rows = RowsOf(catalog, source);
                LinkOwned(rows, OwnedIds(source), catalog, source);
                LinkContainers(rows, source);
                return rows;
            });
        }

        internal sealed class CoverageSummary
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
        internal CoverageSummary SummaryOf(SourceConfiguration source, DumpCatalog catalog, List<DumpRow> rows)
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
                    SeriesGroups = rows.GroupBy(r => r.SeriesKey ?? DumpController.AloneKey).Select(g => new KeyValuePair<string, List<DumpRow>>(g.Key, g.ToList())).ToList()
                };
            });
        }

        // The light view of each entry (computed once per downloaded copy: parsing thousands of entries at every click would be slow)
        private static readonly ConditionalWeakTable<DumpCatalog, Tuple<string, List<DumpRow>>> RowCache = new ConditionalWeakTable<DumpCatalog, Tuple<string, List<DumpRow>>>();

        private static readonly JsonFieldMapper ListReader = new JsonFieldMapper(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        /// <summary>What the light rows (and everything computed from them) depend on besides the downloaded copy: the mapping paths used and the series settings</summary>
        internal static string MappingKeyOf(SourceConfiguration source)
        {
            var mapping = source.VideoFieldMappings ?? new FieldMapping();
            // The catalogue has its own date path, so the date can stay unmapped (kept out of Jellyfin) and still be used here
            var datePath = string.IsNullOrWhiteSpace(source.Dump?.ReleaseDatePath) ? mapping.PremiereDate : source.Dump.ReleaseDatePath;
            return string.Join("|", datePath, mapping.Tags, mapping.PosterUrl, mapping.ThumbUrl, mapping.BackdropUrl, source.Dump?.PreviewImagePath, mapping.TagsItemField, source.Dump?.SeriesMinEntries, source.Dump?.SeriesIdPath, source.Dump?.SeriesNamePath, source.Prefix);
        }

        internal static List<DumpRow> RowsOf(DumpCatalog catalog, SourceConfiguration source)
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

        internal static List<DumpSeriesCoverage> SeriesOf(List<DumpRow> rows)
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


        /// <summary>
        /// The episode list of a SEASON whose folder tag is "prefix-seriesId": the dump entries that carry that series id, in the shape the
        /// missing-episodes analysis reads from an API source (so a dump season gets the same virtual episodes, thumbnails and "ignore").
        /// (null, null) = the dump has no series with this id (not a season of this dump); (null, error) = the dump could not be loaded.
        /// </summary>
        public async Task<(List<EpisodeListItem> Items, string Error)> EpisodeListAsync(SourceConfiguration source, string seriesId, CancellationToken cancellationToken)
        {
            if (source == null || !source.IsDump || string.IsNullOrWhiteSpace(source.Dump?.SeriesIdPath) || string.IsNullOrWhiteSpace(seriesId))
            {
                return (null, null);
            }

            var (catalog, error) = await _dump.GetCatalogAsync(source, false, cancellationToken).ConfigureAwait(false);
            if (catalog == null)
            {
                return (null, error ?? "Dump indisponible");
            }

            var bySeries = _coverage.GetOrBuild(SlotOf(source, "seriesids"), catalog, MappingKeyOf(source),
                () => RowsOf(catalog, source).Where(r => r.SeriesId != null).GroupBy(r => r.SeriesId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase), libraryDependent: false);
            if (!bySeries.TryGetValue(seriesId.Trim(), out var rows) || rows.Count == 0)
            {
                return (null, null);
            }

            var video = source.VideoFieldMappings ?? new FieldMapping();
            var settings = source.Dump;
            var mapping = new EpisodeListMapping
            {
                ItemsPath = string.Empty,
                Id = settings.IdPath,
                Title = video.Title,
                Overview = video.Overview,
                PremiereDate = string.IsNullOrWhiteSpace(settings.ReleaseDatePath) ? video.PremiereDate : settings.ReleaseDatePath,
                IndexNumber = video.IndexNumber,
                Duration = string.Empty,
                PreviewUrl = string.Empty,
                VideoUrl = string.Empty,
                ThumbUrl = string.Join(" || ", new[] { settings.PreviewImagePath, video.ThumbUrl, video.PosterUrl, video.BackdropUrl }.Where(p => !string.IsNullOrWhiteSpace(p)))
            };

            var raws = rows.Where(r => catalog.ById.ContainsKey(r.Id)).Select(r => catalog.ById[r.Id].Raw);
            using var doc = JsonDocument.Parse("[" + string.Join(",", raws) + "]");
            var items = new JsonFieldMapper(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance).MapToEpisodeList(doc, mapping) ?? new List<EpisodeListItem>();
            var dates = rows.ToDictionary(r => r.Id, r => r.Date, StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                // The row's date also reads Unix timestamps and alternatives: it wins over the plain mapping
                if (dates.TryGetValue(item.Id, out var date) && date.HasValue)
                {
                    item.PremiereDate = date;
                }

                if (video.OverviewIsHtml)
                {
                    item.Overview = HtmlText.ToPlainText(item.Overview);
                }
            }

            return (items, null);
        }
    }
}
