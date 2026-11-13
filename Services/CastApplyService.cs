using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
        private readonly PeopleStore _people;
        private readonly SourceResolver _resolver;
        private readonly ILogger<CastApplyService> _logger;

        public CastApplyService(
            ILibraryManager libraryManager,
            IFileSystem fileSystem,
            LocalSourceStore local,
            CastOverlayStore overlay,
            CastInheritance inheritance,
            PeopleStore people,
            SourceResolver resolver,
            ILogger<CastApplyService> logger)
        {
            _people = people;
            _resolver = resolver;
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
            var direct = new List<BaseItem>();   // children without a tag of one of our sources: our providers never see them
            var seen = new HashSet<Guid>();
            foreach (var (kind, type) in new[] { (BaseItemKind.Series, "series"), (BaseItemKind.Season, "season"), (BaseItemKind.Episode, "episode") })
            {
                foreach (var item in _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { kind }, Recursive = true }).Where(i => Wanted(i, type)))
                {
                    if (seen.Add(item.Id))
                    {
                        (HandledByProvider(item) ? episodes : direct).Add(item);
                    }

                    // A series / season marked "cascade" (or that was, see ExpandKeys): everything below it inherits its cast
                    if (type != "episode" && CastCatalog.Keys(item).Any(k => _overlay.IsCascading(type, k) || expand.Contains(CastOverlayStore.TypedKey(type, k))))
                    {
                        var below = type == "series" ? new[] { BaseItemKind.Season, BaseItemKind.Episode } : new[] { BaseItemKind.Episode };
                        foreach (var child in _libraryManager.GetItemList(new InternalItemsQuery { AncestorIds = new[] { item.Id }, IncludeItemTypes = below, Recursive = true }))
                        {
                            if (seen.Add(child.Id))
                            {
                                (HandledByProvider(child) ? episodes : direct).Add(child);
                            }
                        }
                    }
                }
            }

            result.Queued = episodes.Count + direct.Count;
            result.Missing = episodes.Count(e => e.IsVirtualItem);
            if (episodes.Count > 0 || direct.Count > 0)
            {
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    await RefreshAsync(episodes);
                    await ApplyDirectAsync(direct, System.Threading.CancellationToken.None);
                });
            }

            _logger.LogInformation("👥 [CAST APPLY] {Queued} episodes queued for refresh ({Missing} missing, {Keys} keys wanted)", result.Queued, result.Missing, wanted.Count);
            return result;
        }

        // ---- Children without an identifier: the cast is written straight into Jellyfin ----
        // A file or folder with no {prefix-id} of an enabled source never goes through our providers (it is left to Jellyfin's
        // own), so the cascade cannot reach it by an enrichment. Its inherited people are added to its people list directly,
        // and what we added is remembered so that a removal or turning the cascade off takes only those back.

        private bool HandledByProvider(BaseItem item)
            => CastCatalog.Keys(item).Any(k => _resolver.GetSourceByPrefix(_resolver.ExtractPrefixFromId(k)) != null);

        private static string PersonKey(PersonInfo p) => (p.Name ?? string.Empty).Trim().ToLowerInvariant() + "|" + p.Type;

        private static readonly object RecordLock = new object();

        private static string RecordPath() => Path.Combine(Plugin.Instance?.DataFolderPath ?? Path.Combine(Path.GetTempPath(), "CustomMetadata"), "cast-inherited.json");

        private static Dictionary<string, List<string>> LoadRecord()
        {
            try
            {
                var path = RecordPath();
                return File.Exists(path)
                    ? JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(path)) ?? new Dictionary<string, List<string>>()
                    : new Dictionary<string, List<string>>();
            }
            catch (Exception)
            {
                return new Dictionary<string, List<string>>();
            }
        }

        private static void SaveRecord(Dictionary<string, List<string>> record)
        {
            var path = RecordPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(record));
            File.Move(tmp, path, true);
        }

        /// <summary>Sets the inherited cast on tag-less seasons / episodes, without touching the rest of their metadata</summary>
        private async System.Threading.Tasks.Task<int> ApplyDirectAsync(List<BaseItem> items, System.Threading.CancellationToken cancellationToken)
        {
            if (items.Count == 0 || !(Plugin.Instance?.Configuration?.EnableCasting ?? true))
            {
                return 0;
            }

            Dictionary<string, List<string>> record;
            lock (RecordLock)
            {
                record = LoadRecord();
            }

            var changed = 0;
            foreach (var item in items)
            {
                try
                {
                    // Its own distribution (the key may be the fallback "jf-…" of an element without tag) + what it inherits
                    var type = item is Series ? "series" : (item is Season ? "season" : "episode");
                    var own = _overlay.Get(type, CastCatalog.Keys(item));
                    var inherited = item is Series ? new List<LocalCastEntry>() : (item is Season ? _inheritance.ForSeason(item.Path) : _inheritance.ForEpisode(item.Path, null));
                    var wanted = _people.ResolveCast(CastInheritance.Combine(own, inherited)).Where(m => !string.IsNullOrWhiteSpace(m.Name)).Select(CastMapper.ToPersonInfo).ToList();
                    var wantedKeys = wanted.Select(PersonKey).ToHashSet();
                    var added = record.TryGetValue(item.Id.ToString("N"), out var before) ? new HashSet<string>(before) : new HashSet<string>();
                    var existing = _libraryManager.GetPeople(item) ?? new List<PersonInfo>();

                    // What stays: people that are not ours (other providers, NFO...). Ours are replaced by the current list.
                    var keep = existing.Where(p => !added.Contains(PersonKey(p)) && !wantedKeys.Contains(PersonKey(p))).ToList();
                    var merged = keep.Concat(wanted).ToList();
                    for (var i = 0; i < merged.Count; i++)
                    {
                        merged[i].SortOrder = i;
                    }

                    var same = merged.Count == existing.Count
                        && merged.Select(PersonKey).OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(existing.Select(PersonKey).OrderBy(k => k, StringComparer.Ordinal))
                        && wanted.All(w => existing.Any(e => PersonKey(e) == PersonKey(w) && string.Equals(e.Role ?? string.Empty, w.Role ?? string.Empty, StringComparison.Ordinal)));
                    if (!same)
                    {
                        await _libraryManager.UpdatePeopleAsync(item, merged, cancellationToken);
                        changed++;
                    }

                    if (wantedKeys.Count > 0)
                    {
                        record[item.Id.ToString("N")] = wantedKeys.ToList();
                    }
                    else
                    {
                        record.Remove(item.Id.ToString("N"));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "👥 [CAST APPLY] ⚠️ Cannot set the inherited cast on '{Name}'", item.Name);
                }
            }

            lock (RecordLock)
            {
                SaveRecord(record);
            }

            _logger.LogInformation("👥 [CAST APPLY] ✅ Inherited cast written on {Changed}/{Total} items without an identifier", changed, items.Count);
            return changed;
        }

        /// <summary>
        /// Keeps the tag-less children of every cascading series / season up to date (new files, changed casts).
        /// Called by the scheduled task: cheap, and touches nothing but their people list.
        /// </summary>
        public System.Threading.Tasks.Task<int> SyncUntaggedAsync(System.Threading.CancellationToken cancellationToken)
        {
            var cascading = _overlay.Load().Episodes.Where(e => e.Cascade && e.Cast != null && e.Cast.Count > 0 && CastOverlayStore.NormalizeType(e.Type) != "episode").ToList();
            // Elements without any tag that have their own distribution (key "jf-<item id>")
            var ownIds = _overlay.Load().Episodes
                .Where(e => e.Cast != null && e.Cast.Count > 0 && CastCatalog.IsSynthetic(e.Key))
                .Select(e => Guid.TryParseExact(CastOverlayStore.NormalizeKey(e.Key).Substring(CastCatalog.SyntheticPrefix.Length), "N", out var g) ? g : Guid.Empty)
                .Where(g => g != Guid.Empty)
                .ToList();
            if (cascading.Count == 0 && ownIds.Count == 0)
            {
                return System.Threading.Tasks.Task.FromResult(0);
            }

            var direct = new List<BaseItem>();
            var seen = new HashSet<Guid>();
            foreach (var ownId in ownIds)
            {
                var ownItem = _libraryManager.GetItemById(ownId);
                if ((ownItem is Series || ownItem is Season || ownItem is Episode) && seen.Add(ownItem.Id))
                {
                    direct.Add(ownItem);
                }
            }

            foreach (var (kind, type) in new[] { (BaseItemKind.Series, "series"), (BaseItemKind.Season, "season") })
            {
                var keys = new HashSet<string>(cascading.Where(e => CastOverlayStore.NormalizeType(e.Type) == type).Select(e => CastOverlayStore.NormalizeKey(e.Key)), StringComparer.OrdinalIgnoreCase);
                foreach (var item in _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { kind }, Recursive = true }).Where(i => CastCatalog.Keys(i).Any(keys.Contains)))
                {
                    var below = type == "series" ? new[] { BaseItemKind.Season, BaseItemKind.Episode } : new[] { BaseItemKind.Episode };
                    foreach (var child in _libraryManager.GetItemList(new InternalItemsQuery { AncestorIds = new[] { item.Id }, IncludeItemTypes = below, Recursive = true }))
                    {
                        if (!child.IsVirtualItem && !HandledByProvider(child) && seen.Add(child.Id))
                        {
                            direct.Add(child);
                        }
                    }
                }
            }

            return ApplyDirectAsync(direct, cancellationToken);
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
