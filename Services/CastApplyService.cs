using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>What "Appliquer aux épisodes" must update. All empty = every episode that has a cast.</summary>
    public class CastApplyRequest
    {
        /// <summary>Directory people that changed (name, photo…): the episodes that contain them</summary>
        public List<string> PersonIds { get; set; } = new List<string>();

        /// <summary>API episodes whose cast changed ("dm-a1v1" tags)</summary>
        public List<string> EpisodeKeys { get; set; } = new List<string>();

        /// <summary>Seasons whose cast changed (folder tags "dm-a1")</summary>
        public List<string> SeasonKeys { get; set; } = new List<string>();

        /// <summary>Series whose cast changed (folder tags "dm-serie")</summary>
        public List<string> SeriesKeys { get; set; } = new List<string>();

        /// <summary>
        /// Typed keys ("series:dm-x", "season:dm-a1") whose children must be refreshed even if the element is not (or no longer)
        /// marked "cascade": the screen sends the ones whose cascade was on before or after the change, so that turning it off cleans the children.
        /// </summary>
        public List<string> ExpandKeys { get; set; } = new List<string>();

        /// <summary>Local sources whose content changed (prefixes): their episodes that have a cast</summary>
        public List<string> Prefixes { get; set; } = new List<string>();
    }

    public class CastApplyResult
    {
        /// <summary>Episodes whose enrichment was restarted (in the background, by Jellyfin)</summary>
        public int Queued { get; set; }

        public int Missing { get; set; }
    }

    /// <summary>
    /// Names, photos and casts are copied into Jellyfin when an episode is enriched. After a change to
    /// the directory or to a cast, episodes that are ALREADY enriched keep the old version: this service restarts their
    /// enrichment (metadata only, images untouched) for the affected ones, without touching the rest of the series.
    /// </summary>
    public class CastApplyService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IFileSystem _fileSystem;
        private readonly LocalSourceStore _local;
        private readonly CastOverlayStore _overlay;
        private readonly CastInheritance _inheritance;
        private readonly ILogger<CastApplyService> _logger;

        public CastApplyService(
            ILibraryManager libraryManager,
            IFileSystem fileSystem,
            LocalSourceStore local,
            CastOverlayStore overlay,
            CastInheritance inheritance,
            ILogger<CastApplyService> logger)
        {
            _inheritance = inheritance;
            _libraryManager = libraryManager;
            _fileSystem = fileSystem;
            _local = local;
            _overlay = overlay;
            _logger = logger;
        }

        /// <summary>Typed keys ("episode:dm-a1v1", "season:dm-a1", "series:dm-x") of the elements concerned by the request</summary>
        public HashSet<string> AffectedKeys(CastApplyRequest request)
        {
            request ??= new CastApplyRequest();
            var persons = new HashSet<string>((request.PersonIds ?? new List<string>()).Select(p => p.Trim()), StringComparer.OrdinalIgnoreCase);
            var keys = new HashSet<string>(
                (request.EpisodeKeys ?? new List<string>()).Select(k => CastOverlayStore.TypedKey("episode", k))
                    .Concat((request.SeasonKeys ?? new List<string>()).Select(k => CastOverlayStore.TypedKey("season", k)))
                    .Concat((request.SeriesKeys ?? new List<string>()).Select(k => CastOverlayStore.TypedKey("series", k))),
                StringComparer.OrdinalIgnoreCase);
            var prefixes = new HashSet<string>((request.Prefixes ?? new List<string>()).Select(p => p.Trim().ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);
            var everything = persons.Count == 0 && keys.Count == 0 && prefixes.Count == 0;
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            bool Touches(IEnumerable<LocalCastEntry> cast) => cast != null && cast.Any(c => persons.Count > 0 && persons.Contains(c.PersonId ?? string.Empty));

            // Cast of API episodes (People screen)
            foreach (var entry in _overlay.Load().Episodes)
            {
                var key = CastOverlayStore.TypedKey(entry.Type, entry.Key);
                if (everything || keys.Contains(key) || Touches(entry.Cast))
                {
                    result.Add(key);
                }
            }

            // Keys named by the request even when they no longer have an entry (a distribution that was emptied must be cleaned too)
            foreach (var key in keys)
            {
                result.Add(key);
            }

            // Cast typed in the local sources
            foreach (var source in Plugin.Instance?.Configuration?.Sources?.Where(s => s.IsLocal && !string.IsNullOrEmpty(s.LocalDataId)) ?? Enumerable.Empty<Configuration.SourceConfiguration>())
            {
                var prefix = (source.Prefix ?? string.Empty).Trim().ToLowerInvariant();
                var wholeSource = everything || prefixes.Contains(prefix);
                foreach (var episode in _local.Load(source.LocalDataId).Series.SelectMany(s => s.Seasons).SelectMany(s => s.Episodes))
                {
                    if (episode.Cast != null && episode.Cast.Count > 0 && (wholeSource || Touches(episode.Cast)))
                    {
                        result.Add(CastOverlayStore.TypedKey("episode", $"{prefix}-{episode.Id}"));
                    }
                }
            }

            return result;
        }

        // One run at a time: two successive "Appliquer" runs follow each other instead of overlapping
        private static readonly System.Threading.SemaphoreSlim RunLock = new System.Threading.SemaphoreSlim(1, 1);

        /// <summary>
        /// Restarts the enrichment of the library episodes concerned, one by one, in the background.
        /// Not Jellyfin's QueueRefresh queue: several requests at once trigger an internal exception there
        /// ("Guid can't be empty") that drops some of the episodes.
        /// </summary>
        public CastApplyResult Apply(CastApplyRequest request)
        {
            var wanted = AffectedKeys(request);
            var result = new CastApplyResult();
            if (wanted.Count == 0)
            {
                return result;
            }

            // Episodes, seasons and series with a tag matching a concerned key
            bool Wanted(BaseItem item, string type) => CastCatalog.Keys(item).Any(k => wanted.Contains(CastOverlayStore.TypedKey(type, k)));
            var expand = new HashSet<string>((request?.ExpandKeys ?? new List<string>()).Select(k => k.Trim().ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);
            var episodes = new List<BaseItem>();
            var seen = new HashSet<Guid>();
            foreach (var (kind, type) in new[] { (BaseItemKind.Series, "series"), (BaseItemKind.Season, "season"), (BaseItemKind.Episode, "episode") })
            {
                foreach (var item in _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { kind }, Recursive = true }).Where(i => Wanted(i, type)))
                {
                    if (seen.Add(item.Id))
                    {
                        episodes.Add(item);
                    }

                    // A series / season marked "cascade" (or that was, see ExpandKeys): everything below it inherits its cast
                    if (type != "episode" && CastCatalog.Keys(item).Any(k => _overlay.IsCascading(type, k) || expand.Contains(CastOverlayStore.TypedKey(type, k))))
                    {
                        var below = type == "series" ? new[] { BaseItemKind.Season, BaseItemKind.Episode } : new[] { BaseItemKind.Episode };
                        foreach (var child in _libraryManager.GetItemList(new InternalItemsQuery { AncestorIds = new[] { item.Id }, IncludeItemTypes = below, Recursive = true }))
                        {
                            if (seen.Add(child.Id))
                            {
                                episodes.Add(child);
                            }
                        }
                    }
                }
            }

            result.Queued = episodes.Count;
            result.Missing = episodes.Count(e => e.IsVirtualItem);
            if (episodes.Count > 0)
            {
                _ = System.Threading.Tasks.Task.Run(() => RefreshAsync(episodes));
            }

            _logger.LogInformation("👥 [CAST APPLY] {Queued} episodes queued for refresh ({Missing} missing, {Keys} keys wanted)", result.Queued, result.Missing, wanted.Count);
            return result;
        }

        private async System.Threading.Tasks.Task RefreshAsync(List<BaseItem> episodes)
        {
            await RunLock.WaitAsync();
            try
            {
                var done = 0;
                foreach (var episode in episodes)
                {
                    try
                    {
                        // A missing episode has no path: tell the provider which season it belongs to (cascade)
                        if (episode is Episode virtualEpisode && virtualEpisode.IsVirtualItem)
                        {
                            _inheritance.Register(CustomId.Get(virtualEpisode.ProviderIds), virtualEpisode.SeasonId);
                        }

                        await episode.RefreshMetadata(
                            new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                            {
                                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                                ReplaceAllMetadata = true,
                                ImageRefreshMode = MetadataRefreshMode.None
                            },
                            System.Threading.CancellationToken.None);
                        done++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "👥 [CAST APPLY] ⚠️ Refresh failed for episode '{Name}'", episode.Name);
                    }
                }

                _logger.LogInformation("👥 [CAST APPLY] ✅ {Done}/{Total} episodes refreshed", done, episodes.Count);
            }
            finally
            {
                RunLock.Release();
            }
        }
    }
}
