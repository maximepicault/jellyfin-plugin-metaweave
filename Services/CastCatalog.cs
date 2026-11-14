using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    public class CastCatalogEpisode
    {
        /// <summary>"{prefix-id}" tag without braces: key of the cast distribution (CastOverlayStore)</summary>
        public string Key { get; set; }

        public string Title { get; set; }

        public int? IndexNumber { get; set; }

        /// <summary>Missing episode (no file)</summary>
        public bool IsMissing { get; set; }
    }

    public class CastCatalogSeason
    {
        /// <summary>Tag of the season (folder) without braces, null if the folder carries none</summary>
        public string Key { get; set; }

        public string SeasonId { get; set; }

        public string Name { get; set; }

        public int? IndexNumber { get; set; }

        public List<CastCatalogEpisode> Episodes { get; set; } = new List<CastCatalogEpisode>();
    }

    public class CastChildEntry
    {
        public string Key { get; set; }

        public string Title { get; set; }

        public bool IsMissing { get; set; }
    }

    /// <summary>Seasons and episodes (tags) below a series or season</summary>
    public class CastChildren
    {
        /// <summary>The series / season exists in the library</summary>
        public bool Found { get; set; }

        public List<CastChildEntry> Seasons { get; set; } = new List<CastChildEntry>();

        public List<CastChildEntry> Episodes { get; set; } = new List<CastChildEntry>();
    }

    public class CastCatalogSeries
    {
        /// <summary>Tag of the series (folder) without braces, null if the folder carries none</summary>
        public string Key { get; set; }

        public string SeriesId { get; set; }

        public string Name { get; set; }

        public List<CastCatalogSeason> Seasons { get; set; } = new List<CastCatalogSeason>();
    }

    /// <summary>
    /// Library episodes that carry an API source tag, to choose which episodes get a cast distribution
    /// (People screen). Episodes of a local source are excluded: their cast is set in the source.
    /// </summary>
    public class CastCatalog
    {
        private readonly ILibraryManager _libraryManager;

        public CastCatalog(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        public List<CastCatalogSeries> Build()
        {
            var sources = Plugin.Instance?.Configuration?.Sources ?? new List<Configuration.SourceConfiguration>();
            // A local source sharing its prefix with an API source only lays its data over it: that prefix stays listed
            var apiPrefixes = new HashSet<string>(
                sources.Where(s => !s.IsLocal && !s.IsPeople).Select(s => (s.Prefix ?? string.Empty).Trim().ToLowerInvariant()),
                StringComparer.Ordinal);
            var skipPrefixes = new HashSet<string>(
                sources.Where(s => s.IsLocal || s.IsPeople).Select(s => (s.Prefix ?? string.Empty).Trim().ToLowerInvariant()).Where(p => !apiPrefixes.Contains(p)),
                StringComparer.Ordinal);

            var episodes = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                Recursive = true
            }).OfType<Episode>().ToList();

            // Tags of the series and seasons (folders)
            string FolderKey(BaseItem item)
            {
                var key = Keys(item).FirstOrDefault(k => !skipPrefixes.Contains(Prefix(k)));
                return key?.ToLowerInvariant();
            }

            var seriesItems = _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.Series }, Recursive = true })
                .GroupBy(i => i.Id).Select(g => g.First()).ToList();
            var seasonItems = _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.Season }, Recursive = true })
                .GroupBy(i => i.Id).Select(g => g.First()).ToList();
            var seriesKeys = seriesItems.ToDictionary(i => i.Id, i => FolderKey(i));
            var seasonKeys = seasonItems.ToDictionary(i => i.Id, i => FolderKey(i));

            var result = new List<CastCatalogSeries>();
            foreach (var bySeries in episodes.GroupBy(e => e.SeriesId))
            {
                var series = new CastCatalogSeries
                {
                    Key = seriesKeys.TryGetValue(bySeries.Key, out var seriesKey) ? seriesKey : null,
                    SeriesId = bySeries.Key.ToString("N"),
                    Name = bySeries.First().SeriesName
                };
                foreach (var bySeason in bySeries.GroupBy(e => e.SeasonId))
                {
                    var first = bySeason.First();
                    var season = new CastCatalogSeason
                    {
                        Key = seasonKeys.TryGetValue(bySeason.Key, out var seasonKey) ? seasonKey : null,
                        SeasonId = bySeason.Key.ToString("N"),
                        Name = first.SeasonName,
                        IndexNumber = first.ParentIndexNumber
                    };

                    foreach (var episode in bySeason.OrderBy(e => e.IndexNumber ?? int.MaxValue).ThenBy(e => e.Name))
                    {
                        var key = Keys(episode).FirstOrDefault(k => !skipPrefixes.Contains(Prefix(k)));
                        if (key == null)
                        {
                            continue;
                        }

                        season.Episodes.Add(new CastCatalogEpisode
                        {
                            Key = key.ToLowerInvariant(),
                            Title = episode.Name,
                            IndexNumber = episode.IndexNumber,
                            IsMissing = episode.IsVirtualItem
                        });
                    }

                    if (season.Episodes.Count > 0 || season.Key != null)
                    {
                        series.Seasons.Add(season);
                    }
                }

                if (series.Seasons.Count > 0 || series.Key != null)
                {
                    series.Seasons = series.Seasons.OrderBy(s => s.IndexNumber ?? int.MaxValue).ThenBy(s => s.Name).ToList();
                    result.Add(series);
                }
            }

            // The catalog started from the episodes: a series or a season that carries a tag but has no episode in the library
            // (an empty folder, a series whose files are not scanned yet) would never be listed, so its cast could not be set.
            // (only with a real tag: an empty element without identifier, like Jellyfin's "Season Unknown", has nothing to attach to)
            foreach (var seriesItem in seriesItems.Where(i => seriesKeys[i.Id] != null && !IsSynthetic(seriesKeys[i.Id])))
            {
                var id = seriesItem.Id.ToString("N");
                if (!result.Any(s => s.SeriesId == id))
                {
                    result.Add(new CastCatalogSeries { Key = seriesKeys[seriesItem.Id], SeriesId = id, Name = seriesItem.Name });
                }
            }

            foreach (var seasonItem in seasonItems.Where(i => seasonKeys[i.Id] != null && !IsSynthetic(seasonKeys[i.Id])))
            {
                var seasonId = seasonItem.Id.ToString("N");
                var parent = (seasonItem as Season)?.SeriesId ?? Guid.Empty;
                var series = result.FirstOrDefault(s => s.SeriesId == parent.ToString("N"));
                if (series == null || series.Seasons.Any(x => x.SeasonId == seasonId))
                {
                    continue;
                }

                series.Seasons.Add(new CastCatalogSeason { Key = seasonKeys[seasonItem.Id], SeasonId = seasonId, Name = seasonItem.Name, IndexNumber = seasonItem.IndexNumber });
                series.Seasons = series.Seasons.OrderBy(x => x.IndexNumber ?? int.MaxValue).ThenBy(x => x.Name).ToList();
            }

            return result.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        /// <summary>
        /// Tags below ONE series or season (what its "cascade" reaches), without listing the whole library:
        /// used by the preview next to the cascade checkbox.
        /// </summary>
        public CastChildren Children(string type, string key)
        {
            var wanted = CastOverlayStore.NormalizeKey(key);
            var result = new CastChildren();
            if (wanted.Length == 0)
            {
                return result;
            }

            var kind = string.Equals(type, "series", StringComparison.OrdinalIgnoreCase) ? BaseItemKind.Series : BaseItemKind.Season;
            var parents = _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { kind }, Recursive = true })
                .Where(i => Keys(i).Any(k => string.Equals(k, wanted, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            result.Found = parents.Count > 0;
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var parent in parents)
            {
                var below = kind == BaseItemKind.Series ? new[] { BaseItemKind.Season, BaseItemKind.Episode } : new[] { BaseItemKind.Episode };
                foreach (var child in _libraryManager.GetItemList(new InternalItemsQuery { AncestorIds = new[] { parent.Id }, IncludeItemTypes = below, Recursive = true }))
                {
                    var childKey = Keys(child).FirstOrDefault();
                    if (childKey == null || !seenKeys.Add(child.GetType().Name + ":" + childKey))
                    {
                        continue;
                    }

                    var entry = new CastChildEntry { Key = childKey.ToLowerInvariant(), Title = child.Name, IsMissing = child.IsVirtualItem };
                    (child is Episode ? result.Episodes : result.Seasons).Add(entry);
                }
            }

            result.Seasons = result.Seasons.OrderBy(s => s.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
            result.Episodes = result.Episodes.OrderBy(e => e.Key, StringComparer.Ordinal).ToList();
            return result;
        }

        private static string Prefix(string key)
        {
            var dash = key.IndexOf('-');
            return (dash > 0 ? key.Substring(0, dash) : key).ToLowerInvariant();
        }

        // Tags of the file (all of them, a file may bundle several episodes), otherwise the "custom" id set by the plugin
        public static IEnumerable<string> Keys(BaseItem item)
        {
            if (!string.IsNullOrEmpty(item.Path))
            {
                var tags = TagParser.ExtractAll(TagParser.GetOwnName(item.Path));
                if (tags.Count > 0)
                {
                    return tags;
                }
            }

            var id = CustomId.Get(item.ProviderIds);
            if (id != null)
            {
                return new[] { id };
            }

            // No tag at all: a stable key derived from the Jellyfin item itself, so that a cast can still be attached to it
            return item is Series || item is Season || item is Episode ? new[] { SyntheticKey(item) } : Array.Empty<string>();
        }

        /// <summary>
        /// Key of an element that carries no {prefix-id} tag: "jf-" + its Jellyfin id (stable while the folder or file keeps its path).
        /// It lets a series, a season or an episode WITHOUT identifier get a cast; that cast is written straight into Jellyfin
        /// (CastApplyService), since no provider of the plugin ever sees such an element.
        /// </summary>
        public static string SyntheticKey(BaseItem item) => SyntheticPrefix + item.Id.ToString("N");

        public const string SyntheticPrefix = "jf-";

        public static bool IsSynthetic(string key)
            => key != null && System.Text.RegularExpressions.Regex.IsMatch(key.Trim().Trim('{', '}'), "^jf-[0-9a-f]{32}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}
