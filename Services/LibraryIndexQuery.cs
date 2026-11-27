using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>What the browser asks the library index for: filters, then one page of results and/or the filters on offer.</summary>
    public class IndexRequest
    {
        /// <summary>A library to stay in; empty for everything the user sees</summary>
        public Guid? ParentId { get; set; }

        /// <summary>Chosen values by facet: genres, tags, studios, people, years, state, letter</summary>
        public Dictionary<string, string[]> Filters { get; set; }

        /// <summary>Series, Movie, Season, Episode, Video; empty for all</summary>
        public string Type { get; set; }

        /// <summary>serie (default), name, date, oldest, added, rating, random</summary>
        public string Sort { get; set; }

        /// <summary>Keeps a random order the same from one page to the next</summary>
        public int Seed { get; set; }

        public int StartIndex { get; set; }

        /// <summary>Size of the page of results; 0 for none</summary>
        public int Limit { get; set; }

        /// <summary>Also the counts per type, the useful refinements and the letters</summary>
        public bool Summary { get; set; }

        /// <summary>Facets to list with their counts</summary>
        public string[] Facets { get; set; }

        /// <summary>Only the facet values containing this text (accents and case ignored)</summary>
        public string FacetQuery { get; set; }

        /// <summary>At most this many values per facet; 0 for all</summary>
        public int FacetLimit { get; set; }

        /// <summary>"all" or "shuffle": the ids to play</summary>
        public string Play { get; set; }
    }

    public class IndexResponse
    {
        public int Total { get; set; }

        /// <summary>Number of series the results belong to</summary>
        public int SeriesCount { get; set; }

        /// <summary>There is something to play under these filters</summary>
        public bool Playable { get; set; }

        public List<IndexRow> Items { get; set; }

        /// <summary>The sections of the rows of this page, by key</summary>
        public Dictionary<string, IndexGroup> Groups { get; set; }

        /// <summary>Results per type under these filters ("" for all of them)</summary>
        public Dictionary<string, int> Types { get; set; }

        public List<IndexValue> Suggestions { get; set; }

        public Dictionary<string, int> Letters { get; set; }

        public Dictionary<string, IndexFacet> Facets { get; set; }

        /// <summary>The person items of the cast names in this answer</summary>
        public Dictionary<string, IndexPerson> People { get; set; }

        public List<Guid> PlayIds { get; set; }
    }

    public class IndexRow
    {
        public Guid Id { get; set; }

        public string Group { get; set; }
    }

    public class IndexGroup
    {
        public string Type { get; set; }

        /// <summary>Set for the episodes of one series</summary>
        public Guid? SeriesId { get; set; }

        public string SeriesName { get; set; }

        public int Count { get; set; }
    }

    public class IndexFacet
    {
        /// <summary>Number of distinct values, before the limit</summary>
        public int Total { get; set; }

        public List<IndexValue> Values { get; set; }
    }

    public class IndexValue
    {
        public string Facet { get; set; }

        public string Value { get; set; }

        /// <summary>The results one gets by choosing it</summary>
        public int Count { get; set; }

        /// <summary>An item whose backdrop stands for a genre</summary>
        public Guid? Picture { get; set; }
    }

    public class IndexPerson
    {
        public Guid Id { get; set; }

        public bool HasImage { get; set; }
    }

    /// <summary>
    /// The filtering, counting, ordering and paging of the theme's lists, over what one user sees. Genres, tags, studios
    /// and people narrow each other (an item holds all the chosen ones); years, playback states and the letter widen
    /// within their own kind.
    /// </summary>
    public sealed class LibraryIndexQuery
    {
        public const int MaxPage = 600;
        public const int PlayLimit = 200;

        private static readonly string[] Narrowing = { "genres", "tags", "studios", "people" };
        private static readonly string[] Widening = { "years", "state", "letter" };
        private static readonly string[] TypeOrder = { "Series", "Movie", "Season", "Episode", "Video" };
        private static readonly StringComparer Text = StringComparer.Create(CultureInfo.GetCultureInfo("fr-FR"), CompareOptions.IgnoreCase);

        private readonly IReadOnlyList<IndexEntry> _entries;
        private readonly IReadOnlyDictionary<Guid, IndexEntry> _byId;
        private readonly Dictionary<Guid, string> _states = new();
        private readonly Dictionary<string, HashSet<string>> _chosen = new();
        private readonly string _type;

        public LibraryIndexQuery(IReadOnlyList<IndexEntry> visible, IReadOnlyDictionary<Guid, IndexEntry> byId, Dictionary<Guid, bool> watched, IndexRequest request)
        {
            _entries = visible;
            _byId = byId;
            _type = request.Type ?? string.Empty;
            foreach (var pair in request.Filters ?? new Dictionary<string, string[]>())
            {
                var values = pair.Value?.Where(v => !string.IsNullOrEmpty(v)).ToList();
                if (values?.Count > 0 && (Narrowing.Contains(pair.Key) || Widening.Contains(pair.Key)))
                {
                    _chosen[pair.Key] = new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
                }
            }

            ReadStates(watched);
        }

        // an episode or a film: virtual = missing, else finished / started / untouched. A series or a season is judged
        // by the episodes it has a file for
        private void ReadStates(Dictionary<Guid, bool> watched)
        {
            var folders = new Dictionary<Guid, (int Files, int Played, bool Started)>();
            foreach (var e in _entries)
            {
                if (e.Type == "Series" || e.Type == "Season")
                {
                    continue;
                }

                var known = watched.TryGetValue(e.Id, out var played);
                _states[e.Id] = e.IsVirtual ? "miss" : known ? (played ? "seen" : "resume") : "unseen";
                if (e.Type != "Episode" || e.IsVirtual)
                {
                    continue;
                }

                foreach (var folder in new[] { e.SeriesId, e.SeasonId })
                {
                    if (folder.HasValue)
                    {
                        folders.TryGetValue(folder.Value, out var f);
                        folders[folder.Value] = (f.Files + 1, f.Played + (known && played ? 1 : 0), f.Started || known);
                    }
                }
            }

            foreach (var e in _entries.Where(e => e.Type == "Series" || e.Type == "Season"))
            {
                folders.TryGetValue(e.Id, out var f);
                _states[e.Id] = f.Files == 0 ? (e.IsVirtual ? "miss" : "unseen") : f.Played == f.Files ? "seen" : f.Started ? "resume" : "unseen";
            }
        }

        private IEnumerable<string> ValuesOf(IndexEntry e, string facet) => facet switch
        {
            "genres" => e.Genres,
            "tags" => e.Tags,
            "studios" => e.Studios,
            "people" => e.People,
            "years" => e.Year.HasValue ? new[] { e.Year.Value.ToString(CultureInfo.InvariantCulture) } : Array.Empty<string>(),
            "state" => new[] { _states[e.Id] },
            "letter" => new[] { Letter(e) },
            _ => Array.Empty<string>()
        };

        private static string Letter(IndexEntry e)
        {
            var plain = Plain(e.SortName);
            var first = plain.Length > 0 ? char.ToUpperInvariant(plain[0]) : '#';
            return first is >= 'A' and <= 'Z' ? first.ToString() : "#";
        }

        private bool Passes(IndexEntry e, string skip = null)
        {
            foreach (var (facet, wanted) in _chosen)
            {
                if (facet == skip)
                {
                    continue;
                }

                if (Narrowing.Contains(facet))
                {
                    var held = (string[])ValuesOf(e, facet);
                    foreach (var value in wanted)
                    {
                        if (!held.Contains(value, StringComparer.OrdinalIgnoreCase))
                        {
                            return false;
                        }
                    }
                }
                else if (!ValuesOf(e, facet).Any(wanted.Contains))
                {
                    return false;
                }
            }

            return true;
        }

        private bool OfType(IndexEntry e) => _type.Length == 0 || e.Type == _type;

        public List<IndexEntry> Results() => _entries.Where(e => OfType(e) && Passes(e)).ToList();

        public Dictionary<string, int> Types()
        {
            var types = new Dictionary<string, int>();
            foreach (var e in _entries.Where(e => Passes(e)))
            {
                types[e.Type] = types.GetValueOrDefault(e.Type) + 1;
                types[string.Empty] = types.GetValueOrDefault(string.Empty) + 1;
            }

            return types;
        }

        /// <summary>Value → the number of results one gets by choosing it; the chosen values are always listed.</summary>
        public Dictionary<string, int> Counts(string facet)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var skip = Narrowing.Contains(facet) ? null : facet;
            foreach (var e in _entries)
            {
                if (!OfType(e) || !Passes(e, skip))
                {
                    continue;
                }

                foreach (var value in ValuesOf(e, facet))
                {
                    counts[value] = counts.GetValueOrDefault(value) + 1;
                }
            }

            if (_chosen.TryGetValue(facet, out var chosen))
            {
                foreach (var value in chosen)
                {
                    counts.TryAdd(value, 0);
                }
            }

            return counts;
        }

        private bool IsChosen(string facet, string value) => _chosen.TryGetValue(facet, out var set) && set.Contains(value);

        public IndexFacet Facet(string facet, string query, int limit)
        {
            IEnumerable<KeyValuePair<string, int>> values = Counts(facet);
            if (!string.IsNullOrWhiteSpace(query))
            {
                var wanted = Plain(query.Trim());
                values = values.Where(v => Plain(v.Key).Contains(wanted, StringComparison.Ordinal));
            }

            var ordered = facet == "years"
                ? values.OrderBy(v => v.Key, StringComparer.Ordinal).ToList()
                : values.OrderByDescending(v => IsChosen(facet, v.Key)).ThenByDescending(v => v.Value).ThenBy(v => v.Key, Text).ToList();
            // years and playback states are always whole: a histogram or a set of buttons, never a long list
            if (facet == "years" || facet == "state")
            {
                limit = 0;
            }

            return new IndexFacet
            {
                Total = ordered.Count,
                Values = (limit > 0 ? ordered.Take(limit) : ordered).Select(v => new IndexValue { Facet = facet, Value = v.Key, Count = v.Value }).ToList()
            };
        }

        /// <summary>The refinements worth proposing: frequent values that would leave some results out.</summary>
        public List<IndexValue> Suggestions(int total)
        {
            return new[] { ("tags", 4), ("genres", 3), ("people", 3) }
                .SelectMany(f => Counts(f.Item1)
                    .Where(v => !IsChosen(f.Item1, v.Key) && v.Value > 0 && v.Value < total)
                    .OrderByDescending(v => v.Value).ThenBy(v => v.Key, Text)
                    .Take(f.Item2)
                    .Select(v => new IndexValue { Facet = f.Item1, Value = v.Key, Count = v.Value }))
                .OrderByDescending(v => v.Count)
                .ToList();
        }

        /// <summary>Gives each genre an item whose backdrop can stand for it: a series or a film first.</summary>
        public void Pictures(List<IndexValue> genres)
        {
            var wanted = genres.ToDictionary(g => g.Value, StringComparer.OrdinalIgnoreCase);
            var strong = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in _entries)
            {
                var own = e.HasBackdrop ? e.Id : (Guid?)null;
                var series = own == null && e.SeriesId.HasValue && _byId.TryGetValue(e.SeriesId.Value, out var s) && s.HasBackdrop ? e.SeriesId : null;
                if (own == null && series == null)
                {
                    continue;
                }

                foreach (var genre in e.Genres)
                {
                    if (wanted.TryGetValue(genre, out var value) && !strong.Contains(genre))
                    {
                        value.Picture = own ?? series;
                        if (own != null && (e.Type == "Series" || e.Type == "Movie"))
                        {
                            strong.Add(genre);
                        }
                    }
                }
            }
        }

        public List<IndexEntry> Ordered(IEnumerable<IndexEntry> results, string sort, int seed)
        {
            var typed = results.OrderBy(e => Array.IndexOf(TypeOrder, e.Type));
            IOrderedEnumerable<IndexEntry> ordered = sort switch
            {
                "name" => typed.ThenBy(e => e.SortName, Text),
                "date" => typed.ThenByDescending(Released),
                "oldest" => typed.ThenBy(e => Released(e) == DateTime.MinValue ? DateTime.MaxValue : Released(e)),
                "added" => typed.ThenByDescending(e => e.Created ?? DateTime.MinValue),
                "rating" => typed.ThenByDescending(e => e.Rating ?? -1),
                "random" => typed.ThenBy(e => Shuffle(e.Id, seed)),
                // by series: its episodes together, season by season
                _ => typed.ThenBy(SeriesSortName, Text).ThenBy(e => e.SeriesId ?? e.Id).ThenBy(e => e.SeasonOrder)
                    .ThenBy(e => e.Number is >= 0 ? e.Number.Value : int.MaxValue).ThenBy(Released)
            };
            return ordered.ThenBy(e => e.SortName, Text).ThenBy(e => e.Id).ToList();
        }

        private string SeriesSortName(IndexEntry e)
            => e.Type == "Episode" || e.Type == "Season"
                ? (e.SeriesId.HasValue && _byId.TryGetValue(e.SeriesId.Value, out var series) ? series.SortName : e.SeriesName) ?? string.Empty
                : e.SortName;

        private static DateTime Released(IndexEntry e)
            => e.Premiere ?? (e.Year is > 0 and < 10000 ? new DateTime(e.Year.Value, 1, 1) : DateTime.MinValue);

        private static int Shuffle(Guid id, int seed)
        {
            var bytes = id.ToByteArray();
            unchecked
            {
                var hash = (int)2166136261 ^ seed;
                foreach (var b in bytes)
                {
                    hash = (hash ^ b) * 16777619;
                }

                return hash;
            }
        }

        // the section a result is shown in: one per type, and one per series for episodes sorted by series
        private static string GroupKey(IndexEntry e, string sort)
            => e.Type == "Episode" && IsBySeries(sort) ? "s:" + (e.SeriesId?.ToString("N") ?? e.SeriesName) : e.Type;

        private static bool IsBySeries(string sort) => string.IsNullOrEmpty(sort) || sort == "serie";

        public static (List<IndexRow> Rows, Dictionary<string, IndexGroup> Groups) Page(List<IndexEntry> ordered, string sort, int start, int limit)
        {
            var page = ordered.Skip(Math.Max(0, start)).Take(Math.Clamp(limit, 0, MaxPage)).ToList();
            var keys = page.Select(e => GroupKey(e, sort)).ToHashSet();
            var groups = new Dictionary<string, IndexGroup>();
            foreach (var e in ordered)
            {
                var key = GroupKey(e, sort);
                if (!keys.Contains(key))
                {
                    continue;
                }

                if (!groups.TryGetValue(key, out var group))
                {
                    var bySeries = key.StartsWith("s:", StringComparison.Ordinal);
                    groups[key] = group = new IndexGroup { Type = e.Type, SeriesId = bySeries ? e.SeriesId : null, SeriesName = bySeries ? e.SeriesName : null };
                }

                group.Count++;
            }

            return (page.Select(e => new IndexRow { Id = e.Id, Group = GroupKey(e, sort) }).ToList(), groups);
        }

        private bool CanPlay(IndexEntry e) => (e.Type == "Episode" || e.Type == "Movie" || e.Type == "Video") && !e.IsVirtual;

        /// <summary>What can be played: the results themselves, else (series or seasons shown) what passes the filters.</summary>
        public List<IndexEntry> Playable(List<IndexEntry> results)
        {
            var shown = results.Where(CanPlay).ToList();
            return shown.Count > 0 ? shown : _entries.Where(e => CanPlay(e) && Passes(e)).ToList();
        }

        public IEnumerable<string> Chosen(string facet) => _chosen.TryGetValue(facet, out var set) ? set : Enumerable.Empty<string>();

        private static string Plain(string value)
        {
            var decomposed = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
            var plain = new StringBuilder(decomposed.Length);
            foreach (var c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                {
                    plain.Append(char.ToLowerInvariant(c));
                }
            }

            return plain.ToString();
        }
    }
}
