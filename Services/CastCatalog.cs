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
            var skipPrefixes = new HashSet<string>(
                sources.Where(s => s.IsLocal || s.IsPeople).Select(s => (s.Prefix ?? string.Empty).Trim().ToLowerInvariant()),
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

            var seriesKeys = _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.Series }, Recursive = true })
                .GroupBy(i => i.Id).ToDictionary(g => g.Key, g => FolderKey(g.First()));
            var seasonKeys = _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.Season }, Recursive = true })
                .GroupBy(i => i.Id).ToDictionary(g => g.Key, g => FolderKey(g.First()));

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

            return result.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
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
            return id != null ? new[] { id } : Array.Empty<string>();
        }
    }
}
