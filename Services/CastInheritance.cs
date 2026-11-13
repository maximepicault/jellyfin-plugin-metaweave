using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>
    /// Cast a season or an episode inherits from its season / series when that distribution is marked "cascade"
    /// (People screen). The parents are found in the library (exact types, never by guessing from a folder name), so the same
    /// identifier used for a series and for a season cannot be mixed up. The result is added to the element's own cast.
    /// </summary>
    public class CastInheritance
    {
        // A virtual (missing) episode has no path: the analysis tells us which season it belongs to ("dm-a1v3" -> season id)
        private static readonly ConcurrentDictionary<string, Guid> VirtualSeasons = new ConcurrentDictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        private readonly ILibraryManager _library;
        private readonly CastOverlayStore _overlay;
        private readonly ILogger _logger;

        public CastInheritance(ILibraryManager library, CastOverlayStore overlay, ILogger<CastInheritance> logger)
        {
            _library = library;
            _overlay = overlay;
            _logger = logger;
        }

        public void Register(string customKey, Guid seasonId)
        {
            if (!string.IsNullOrEmpty(customKey) && seasonId != Guid.Empty)
            {
                VirtualSeasons[customKey] = seasonId;
            }
        }

        /// <summary>Cast inherited by an episode: from its season, then from its series (people already present once only)</summary>
        public List<LocalCastEntry> ForEpisode(string path, string customKey)
        {
            if (!_overlay.AnyCascade())
            {
                return new List<LocalCastEntry>();
            }

            BaseItem season = null;
            BaseItem series = null;
            try
            {
                if (!string.IsNullOrEmpty(path))
                {
                    var dir = Path.GetDirectoryName(path);
                    var parent = string.IsNullOrEmpty(dir) ? null : _library.FindByPath(dir, true);
                    if (parent is Season)
                    {
                        season = parent;
                        series = parent.GetParent() as Series;
                    }
                    else if (parent is Series)
                    {
                        series = parent;
                    }
                }
                else if (!string.IsNullOrEmpty(customKey) && VirtualSeasons.TryGetValue(customKey, out var seasonId))
                {
                    season = _library.GetItemById(seasonId);
                    series = season?.GetParent() as Series;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "👥 [CAST INHERIT] ⚠️ Cannot find the parents of '{Path}' / '{Key}'", path, customKey);
            }

            return Inherited(season, series);
        }

        /// <summary>Cast inherited by a season from its series</summary>
        public List<LocalCastEntry> ForSeason(string path)
        {
            if (!_overlay.AnyCascade() || string.IsNullOrEmpty(path))
            {
                return new List<LocalCastEntry>();
            }

            try
            {
                return Inherited(null, _library.FindByPath(path, true)?.GetParent() as Series);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "👥 [CAST INHERIT] ⚠️ Cannot find the series of '{Path}'", path);
                return new List<LocalCastEntry>();
            }
        }

        /// <summary>Same for an episode whose season item is already known (missing episodes analysis)</summary>
        public List<LocalCastEntry> ForSeasonItem(BaseItem season) => _overlay.AnyCascade() ? Inherited(season, season?.GetParent() as Series) : new List<LocalCastEntry>();

        private List<LocalCastEntry> Inherited(BaseItem season, BaseItem series)
        {
            var result = new List<LocalCastEntry>();
            foreach (var entry in _overlay.GetCascading("season", season == null ? Array.Empty<string>() : CastCatalog.Keys(season))
                .Concat(_overlay.GetCascading("series", series == null ? Array.Empty<string>() : CastCatalog.Keys(series))))
            {
                if (!result.Any(r => string.Equals(r.PersonId, entry.PersonId, StringComparison.OrdinalIgnoreCase)))
                {
                    result.Add(entry);
                }
            }

            return result;
        }

        /// <summary>Own entries first, then inherited ones for people not already there</summary>
        public static List<LocalCastEntry> Combine(List<LocalCastEntry> own, List<LocalCastEntry> inherited)
        {
            var result = new List<LocalCastEntry>(own ?? new List<LocalCastEntry>());
            foreach (var entry in inherited ?? new List<LocalCastEntry>())
            {
                if (!result.Any(r => string.Equals(r.PersonId, entry.PersonId, StringComparison.OrdinalIgnoreCase)))
                {
                    result.Add(entry);
                }
            }

            return result;
        }
    }
}
