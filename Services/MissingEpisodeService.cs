using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.CustomMetadata.Api;
using Jellyfin.Plugin.CustomMetadata.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>
    /// Detects missing episodes: compares the EpisodeListEndpoint list (per season/source)
    /// with the {PREFIX-ID} files present, then creates/deletes virtual episodes and writes a report.
    /// Matching by ID: case-insensitive "{prefix}-{id}" key.
    /// </summary>
    public class MissingEpisodeService
    {
        private const string ProviderKey = "custom";
        private const string ReportFileName = "missing-episodes.json";

        // State shared between the scheduled task and the API controller (separate instances)
        private static readonly SemaphoreSlim _runLock = new SemaphoreSlim(1, 1);
        private static MissingEpisodesReport _lastReport;
        private static volatile bool _fullReprocessRequested;

        private readonly ILibraryManager _libraryManager;
        private readonly IProviderManager _providerManager;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger _logger;
        private readonly CustomApiClient _apiClient;
        private readonly SourceResolver _sourceResolver;
        private readonly MissingEpisodeCache _cache;
        private readonly CastOverlayStore _castOverlay;
        private readonly PeopleStore _people;
        private readonly CastInheritance _inheritance;
        private readonly LocalSourceStore _localStore;

        public MissingEpisodeService(
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IFileSystem fileSystem,
            CustomApiClient apiClient,
            SourceResolver sourceResolver,
            CastOverlayStore castOverlay,
            PeopleStore people,
            CastInheritance inheritance,
            LocalSourceStore localStore,
            ILogger<MissingEpisodeService> logger)
        {
            _localStore = localStore;
            _inheritance = inheritance;
            _castOverlay = castOverlay;
            _people = people;
            _libraryManager = libraryManager;
            _providerManager = providerManager;
            _fileSystem = fileSystem;
            _logger = logger;
            _apiClient = apiClient;
            _sourceResolver = sourceResolver;
            _cache = new MissingEpisodeCache(logger);
        }

        /// <summary>
        /// The next analysis will ignore the cache (reprocesses every missing episode)
        /// </summary>
        public static void RequestFullReprocess()
        {
            _fullReprocessRequested = true;
        }

        public static bool IsRunning => _runLock.CurrentCount == 0;

        /// <summary>
        /// Last report (memory, otherwise the JSON file in the plugin's data folder)
        /// </summary>
        public MissingEpisodesReport GetLastReport()
        {
            if (_lastReport != null)
            {
                return _lastReport;
            }

            try
            {
                var path = GetReportPath();
                if (path != null && File.Exists(path))
                {
                    _lastReport = JsonSerializer.Deserialize<MissingEpisodesReport>(File.ReadAllText(path));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "🔎 [MISSING] Could not read previous report");
            }

            return _lastReport;
        }

        public async Task<MissingEpisodesReport> AnalyzeAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            if (!await _runLock.WaitAsync(0, cancellationToken))
            {
                _logger.LogWarning("🔎 [MISSING] Analysis already running - skipped");
                return GetLastReport();
            }

            try
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var report = new MissingEpisodesReport { GeneratedAt = DateTime.UtcNow };
                var createVirtual = Plugin.Instance?.Configuration.CreateVirtualEpisodes ?? true;
                var ignoreCache = _fullReprocessRequested;
                _fullReprocessRequested = false;
                var totals = new SyncStats();

                var seasons = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Season },
                    Recursive = true
                }).OfType<Season>().ToList();

                _logger.LogInformation(
                    "🔎 [MISSING] ========== Analysis started: {Count} seasons{Full} ==========",
                    seasons.Count, ignoreCache ? " (full reprocess, cache ignored)" : string.Empty);

                await CleanupLegacyRemoteTrailersOnceAsync(cancellationToken);

                // 3rd lookup method: all our virtual episodes of the library, by season
                var virtualBySeason = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Episode },
                    IsVirtualItem = true,
                    Recursive = true
                })
                    .OfType<Episode>()
                    .GroupBy(e => e.SeasonId)
                    .ToDictionary(g => g.Key, g => g.ToList());
                _logger.LogInformation(
                    "🔎 [MISSING] Library query: {Count} virtual episodes in {Seasons} seasons",
                    virtualBySeason.Values.Sum(l => l.Count), virtualBySeason.Count);

                for (var i = 0; i < seasons.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(100.0 * i / Math.Max(1, seasons.Count));

                    var seasonReport = await AnalyzeSeasonAsync(
                        seasons[i],
                        createVirtual,
                        ignoreCache,
                        virtualBySeason.TryGetValue(seasons[i].Id, out var seasonVirtual) ? seasonVirtual : new List<Episode>(),
                        totals,
                        cancellationToken);
                    if (seasonReport != null)
                    {
                        report.Seasons.Add(seasonReport);
                    }
                }

                report.Seasons = report.Seasons
                    .OrderBy(s => s.SeriesName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(s => s.SeasonName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                report.DurationSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 1);
                report.Created = totals.Created;
                report.Updated = totals.Updated;
                report.Unchanged = totals.Unchanged;
                report.Removed = totals.Removed;

                _logger.LogInformation(
                    "🔎 [MISSING] ========== Analysis done in {Duration}s: {Seasons} seasons, {Missing} missing — virtual episodes: {Created} created, {Updated} updated, {Unchanged} unchanged (cache), {Removed} removed ==========",
                    report.DurationSeconds, report.Seasons.Count, report.Seasons.Sum(s => s.Missing.Count),
                    totals.Created, totals.Updated, totals.Unchanged, totals.Removed);

                _lastReport = report;
                SaveReport(report);
                progress?.Report(100);
                return report;
            }
            finally
            {
                _runLock.Release();
            }
        }

        private async Task<SeasonMissingReport> AnalyzeSeasonAsync(
            Season season,
            bool createVirtual,
            bool ignoreCache,
            List<Episode> virtualFromLibrary,
            SyncStats totals,
            CancellationToken cancellationToken)
        {
            var seasonTag = GetSeasonTag(season);
            if (string.IsNullOrEmpty(seasonTag))
            {
                return null;
            }

            var prefix = _sourceResolver.ExtractPrefixFromId(seasonTag);
            var source = _sourceResolver.GetSourceByPrefix(prefix);
            var sourceItemId = _sourceResolver.ExtractIdWithoutPrefix(seasonTag);

            // A local season whose id is this season's tag brings its own episodes. Without an API list it IS the list
            // (episodes tagged with the local source's prefix). With an API list, the API stays the base and the local
            // episodes override what they name (same "prefix-id") or are added to it: the local data never removes API ones
            var overridden = source != null && !source.IsLocal ? _localStore?.FindOverride(seasonTag, "season") : null;
            List<EpisodeListItem> localList = null;
            string episodePrefix = null;
            if (overridden != null)
            {
                var overrideSource = overridden.Value.Source;
                _logger.LogInformation("🔎 [MISSING] {Tag}: local season '{Title}' of source '{Source}' overrides the episodes",
                    seasonTag, overridden.Value.Node.Title ?? overridden.Value.Node.Id, overrideSource.Name);
                if (string.IsNullOrWhiteSpace(source.EpisodeListEndpoint))
                {
                    source = overrideSource;
                    sourceItemId = overridden.Value.Node.Id;
                    episodePrefix = source.Prefix?.Trim();
                }
                else
                {
                    localList = await _apiClient.GetEpisodeListAsync(overridden.Value.Node.Id, overrideSource, cancellationToken);
                }
            }

            if (source == null || string.IsNullOrWhiteSpace(source.EpisodeListEndpoint))
            {
                // No source, or no list endpoint configured for this source: nothing to analyse
                return null;
            }

            var seasonReport = new SeasonMissingReport
            {
                SeriesId = season.SeriesId,
                SeriesName = season.SeriesName,
                SeasonId = season.Id,
                SeasonName = season.Name,
                SourceName = source.Name,
                SourceItemId = seasonTag,
                EpisodePrefix = episodePrefix
            };

            var episodes = season.GetRecursiveChildren().OfType<Episode>().ToList();
            var physical = episodes.Where(e => !e.IsVirtualItem).ToList();
            var ourVirtual = episodes.Where(e => e.IsVirtualItem && CustomId.Get(e.ProviderIds) != null).ToList();

            _logger.LogInformation(
                "🔎 [MISSING] Children of {Series} / {Season}: {Total} episodes, {Physical} physical, {Virtual} virtual ({Ours} with a 'custom' id)",
                season.SeriesName, season.Name, episodes.Count, physical.Count, episodes.Count(e => e.IsVirtualItem), ourVirtual.Count);

            var localKeys = new HashSet<string>(
                physical.SelectMany(GetEpisodeKeys),
                StringComparer.OrdinalIgnoreCase);
            seasonReport.LocalCount = physical.Count;

            var apiEpisodes = await _apiClient.GetEpisodeListAsync(sourceItemId, source, cancellationToken);
            if (apiEpisodes == null)
            {
                // List unavailable: touch nothing (no deletion of virtual episodes on an error)
                seasonReport.Error = "La liste d'épisodes n'a pas pu être récupérée (voir les logs 📃 [EPISODE LIST])";
                return seasonReport;
            }

            if (localList != null)
            {
                apiEpisodes = MergeLocalEpisodes(apiEpisodes, localList, source, overridden.Value.Source);
            }

            seasonReport.ApiCount = apiEpisodes.Count;
            DetectOverviewLists(apiEpisodes, source);

            var missing = apiEpisodes
                .Where(e => !localKeys.Contains(EpisodeKey(source, e)))
                .OrderBy(e => e.IndexNumber ?? int.MaxValue)
                .ThenBy(e => e.PremiereDate ?? DateTime.MaxValue)
                .ToList();

            seasonReport.Missing = missing.Select(e => new MissingEpisodeEntry
            {
                Id = e.Id,
                Tag = EpisodeKey(source, e),
                Title = e.Title,
                IndexNumber = e.IndexNumber,
                PremiereDate = e.PremiereDate,
                RunTimeTicks = e.RunTimeTicks,
                Overview = e.Overview != null && e.Overview.Length > 400 ? e.Overview.Substring(0, 400) + "…" : e.Overview,
                // Matching virtual episode (thumbnail + link in the Missing episodes screen)
                EpisodeId = createVirtual ? GetVirtualEpisodeId(season, EpisodeKey(source, e)) : null
            }).ToList();

            _logger.LogInformation(
                "🔎 [MISSING] {Series} / {Season}: {Local} local, {Api} in API, {Missing} missing",
                season.SeriesName, season.Name, physical.Count, apiEpisodes.Count, missing.Count);

            var stats = await SyncVirtualEpisodesAsync(season, source, createVirtual ? missing : new List<EpisodeListItem>(), episodes.Where(e => e.IsVirtualItem).ToList(), virtualFromLibrary, ignoreCache, cancellationToken);
            totals.Created += stats.Created;
            totals.Updated += stats.Updated;
            totals.Unchanged += stats.Unchanged;
            totals.Removed += stats.Removed;

            return seasonReport;
        }

        /// <summary>
        /// Creates the missing virtual episodes, updates those whose JSON changed in the API and deletes
        /// those that became useless (file added since, removed from the API, or option disabled).
        /// An existing virtual episode whose fingerprint (JSON + mapping) is identical to the cache is skipped.
        /// NEVER deletes a physical episode nor a virtual episode without a "custom" ProviderId.
        /// </summary>
        private async Task<SyncStats> SyncVirtualEpisodesAsync(
            Season season,
            SourceConfiguration source,
            List<EpisodeListItem> wanted,
            List<Episode> existingVirtual,
            List<Episode> virtualFromLibrary,
            bool ignoreCache,
            CancellationToken cancellationToken)
        {
            var stats = new SyncStats();
            var wantedByKey = new Dictionary<string, EpisodeListItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in wanted)
            {
                wantedByKey[EpisodeKey(source, item)] = item;
            }

            var cache = ignoreCache
                ? new Dictionary<string, CachedMissingEpisode>(StringComparer.OrdinalIgnoreCase)
                : _cache.Load(season.Id);

            // The virtual episodes inherit the cast of their season / series ("cascade"): tell the provider which season they belong to
            foreach (var wantedKey in wantedByKey.Keys)
            {
                _inheritance.Register(wantedKey, season.Id);
            }

            var seasonInherited = _inheritance.ForSeasonItem(season);
            var indexNumbers = AssignIndexNumbers(season, wantedByKey, cache);
            var (existing, orphans) = FindExistingVirtualEpisodes(season, existingVirtual, virtualFromLibrary, wantedByKey.Keys.Concat(cache.Keys));

            foreach (var orphan in orphans)
            {
                _logger.LogInformation("🔎 [MISSING] Removing orphan virtual episode '{Name}' ({Id})", orphan.Name, orphan.Id);
                DeleteVirtualEpisode(orphan, season);
                stats.Removed++;
            }

            var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, episode) in existing)
            {
                if (!wantedByKey.TryGetValue(key, out var item))
                {
                    _logger.LogInformation("🔎 [MISSING] Removing virtual episode '{Name}' ({Key})", episode.Name, key);
                    DeleteVirtualEpisode(episode, season);
                    stats.Removed++;
                    continue;
                }

                kept.Add(key);

                var hash = MissingEpisodeCache.ComputeHash(item, source, CastFingerprint(key, seasonInherited));
                cache.TryGetValue(key, out var cached);
                var indexNumber = indexNumbers[key];
                if (cached != null && cached.Hash == hash)
                {
                    // JSON unchanged, but a real episode may have taken this number since: only the number is revisited
                    if (episode.IndexNumber != indexNumber)
                    {
                        episode.IndexNumber = indexNumber;
                        await episode.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken);
                        cached.IndexNumber = indexNumber;
                    }

                    stats.Unchanged++;
                    continue;
                }

                _logger.LogDebug("🔎 [MISSING] Updating virtual episode '{Name}' ({Key})", episode.Name, key);
                await UpdateVirtualEpisodeAsync(episode, item, source, indexNumber, cancellationToken);
                await RefreshPeopleAsync(episode, key, cancellationToken);
                var thumb = await EnsureThumbAsync(episode, item, source, cached, true, cancellationToken);
                await SyncPreviewAsync(episode, item, source, cancellationToken);
                cache[key] = CreateCacheEntry(item, source, hash, thumb, indexNumber);
                stats.Updated++;
            }

            foreach (var (key, item) in wantedByKey)
            {
                if (kept.Contains(key))
                {
                    continue;
                }

                var episode = new Episode
                {
                    Name = string.IsNullOrWhiteSpace(item.Title) ? $"Épisode manquant ({item.Id})" : item.Title,
                    Overview = item.Overview,
                    PremiereDate = item.PremiereDate,
                    ProductionYear = item.PremiereDate?.Year,
                    RunTimeTicks = item.RunTimeTicks,
                    IndexNumber = indexNumbers[key],
                    ParentIndexNumber = season.IndexNumber,
                    Id = GetVirtualEpisodeId(season, key),
                    IsVirtualItem = true,
                    SeasonId = season.Id,
                    SeasonName = season.Name,
                    SeriesId = season.SeriesId,
                    SeriesName = season.SeriesName,
                    SeriesPresentationUniqueKey = season.SeriesPresentationUniqueKey,
                    DateLastSaved = DateTime.UtcNow
                };
                episode.SetProviderId(ProviderKey, key);
                episode.PresentationUniqueKey = episode.GetPresentationUniqueKey();
                AddOverviewItems(episode, item, source);

                _logger.LogInformation("🔎 [MISSING] Creating virtual episode '{Name}' ({Key}) in {Season}", episode.Name, key, season.Name);
                season.AddChild(episode);

                // Before the refresh: it does not replace an image already present
                var thumb = await EnsureThumbAsync(episode, item, source, null, false, cancellationToken);

                try
                {
                    // Enrichit via nos providers (ProviderIds["custom"]) : images, synopsis complet...
                    await episode.RefreshMetadata(new MetadataRefreshOptions(new DirectoryService(_fileSystem)), cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "🔎 [MISSING] Metadata refresh failed for virtual episode {Key}", key);
                }

                await SyncPreviewAsync(episode, item, source, cancellationToken);
                cache[key] = CreateCacheEntry(item, source, MissingEpisodeCache.ComputeHash(item, source, CastFingerprint(key, seasonInherited)), thumb, indexNumbers[key]);
                stats.Created++;
            }

            // Episode entries that are no longer missing
            foreach (var key in cache.Keys.Where(k => !wantedByKey.ContainsKey(k)).ToList())
            {
                cache.Remove(key);
            }

            _cache.Save(season.Id, cache);
            return stats;
        }

        /// <summary>
        /// Fingerprint of the cast added to this episode (People screen): people of the directory (names, photos) and roles.
        /// Empty if none. It goes into the episode's hash: a rename in the directory updates it at the next analysis.
        /// </summary>
        private string CastFingerprint(string key, List<LocalCastEntry> inherited)
        {
            var cast = _people.ResolveCast(CastInheritance.Combine(_castOverlay.Get("episode", new[] { key }), inherited));
            return cast.Count == 0 ? string.Empty : string.Join("|", cast.Select(c => $"{c.Name}~{c.Role}~{c.Type}~{c.Image}"));
        }

        /// <summary>
        /// Restarts the enrichment of an already created missing episode so that it picks up its cast (the fields updated
        /// by UpdateVirtualEpisodeAsync do not contain the cast). Images untouched: only the metadata part is replaced.
        /// </summary>
        private async Task RefreshPeopleAsync(Episode episode, string key, CancellationToken cancellationToken)
        {
            try
            {
                await episode.RefreshMetadata(
                    new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                    {
                        MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                        ReplaceAllMetadata = true,
                        ImageRefreshMode = MetadataRefreshMode.None
                    },
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "🔎 [MISSING] Cast refresh failed for virtual episode {Key}", key);
            }
        }

        private void DeleteVirtualEpisode(Episode episode, Season season)
        {
            // Extras are not deleted together with their owner
            foreach (var preview in GetOurPreviews(episode))
            {
                _libraryManager.DeleteItem(preview, new DeleteOptions { DeleteFileLocation = false });
            }

            _libraryManager.DeleteItem(episode, new DeleteOptions { DeleteFileLocation = false }, season, false);
        }

        /// <summary>
        /// "{prefix}-{id}" key of a listed episode. On a local source, an episode whose id is already "prefix-id" (it overrides an API
        /// episode) keeps that tag: it stands for the file tagged with the API's id.
        /// </summary>
        private static string EpisodeKey(SourceConfiguration source, EpisodeListItem item)
            => item.Key ?? (source.IsLocal && item.Id != null && item.Id.Contains('-') ? item.Id : $"{source.Prefix.Trim()}-{item.Id}");

        /// <summary>
        /// The API list with the local season's episodes laid over it: a local episode named "prefix-id" overrides the API episode
        /// with that id (its filled-in fields win), any other local episode is added under its own tag ("loc-abc").
        /// </summary>
        private List<EpisodeListItem> MergeLocalEpisodes(List<EpisodeListItem> api, List<EpisodeListItem> local, SourceConfiguration apiSource, SourceConfiguration localSource)
        {
            var apiPrefix = apiSource.Prefix.Trim();
            var merged = api.ToList();
            foreach (var item in local)
            {
                // "dm-a1v4" names the API episode "a1v4"; a plain id of a local source sharing the API's prefix does too
                var sharesPrefix = string.Equals(localSource.Prefix?.Trim(), apiPrefix, StringComparison.OrdinalIgnoreCase);
                var apiId = item.Id.StartsWith(apiPrefix + "-", StringComparison.OrdinalIgnoreCase)
                    ? item.Id.Substring(apiPrefix.Length + 1)
                    : (sharesPrefix && !item.Id.Contains('-') ? item.Id : null);
                var target = apiId == null
                    ? null
                    : merged.FirstOrDefault(e => string.Equals(e.Id, apiId, StringComparison.OrdinalIgnoreCase));
                if (target == null)
                {
                    item.Key = apiId != null ? $"{apiPrefix}-{apiId}" : (item.Id.Contains('-') ? item.Id : $"{localSource.Prefix.Trim()}-{item.Id}");
                    merged.Add(item);
                    continue;
                }

                target.Title = string.IsNullOrWhiteSpace(item.Title) ? target.Title : item.Title;
                target.Overview = string.IsNullOrWhiteSpace(item.Overview) ? target.Overview : item.Overview;
                target.PremiereDate = item.PremiereDate ?? target.PremiereDate;
                target.IndexNumber = item.IndexNumber ?? target.IndexNumber;
                target.RunTimeTicks = item.RunTimeTicks ?? target.RunTimeTicks;
                target.ThumbUrl = string.IsNullOrWhiteSpace(item.ThumbUrl) ? target.ThumbUrl : item.ThumbUrl;
                target.PreviewUrl = string.IsNullOrWhiteSpace(item.PreviewUrl) ? target.PreviewUrl : item.PreviewUrl;
                target.OverviewIsList = false;
                // The fingerprint of the episode (cache) follows the local data too (valid JSON: it is parsed back later)
                target.RawJson = "{\"api\":" + (string.IsNullOrWhiteSpace(target.RawJson) ? "null" : target.RawJson) + ",\"local\":" + (string.IsNullOrWhiteSpace(item.RawJson) ? "null" : item.RawJson) + "}";
            }

            return merged;
        }

        /// <summary>
        /// Deterministic id of the virtual episode of a "{prefix}-{id}" key in a season
        /// </summary>
        private Guid GetVirtualEpisodeId(Season season, string key)
        {
            return _libraryManager.GetNewItemId($"{season.Id:N}-custom-missing-{key.ToLowerInvariant()}", typeof(Episode));
        }

        /// <summary>
        /// Virtual episodes already created for this season, by key, + orphans.
        /// Does NOT use ProviderIds["custom"] to identify: re-read from the database, ProviderIds is case-sensitive
        /// and the key may be "Custom" (see CustomId); the deterministic Id is safer.
        /// Identification by the deterministic Id (GetVirtualEpisodeId) of the known keys (wanted + cache), among:
        /// the season's virtual children, the library's virtual episodes attached to the season
        /// (SeasonId), then direct read by Id.
        /// Orphan = a virtual episode of the season that matches no known key and carries no
        /// identifier of another provider (e.g. created by a previous version, file arrived since).
        /// </summary>
        private (Dictionary<string, Episode> Existing, List<Episode> Orphans) FindExistingVirtualEpisodes(
            Season season,
            List<Episode> virtualChildren,
            List<Episode> virtualFromLibrary,
            IEnumerable<string> candidateKeys)
        {
            var keyById = new Dictionary<Guid, string>();
            foreach (var key in candidateKeys.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                keyById.TryAdd(GetVirtualEpisodeId(season, key), key);
            }

            var result = new Dictionary<string, Episode>(StringComparer.OrdinalIgnoreCase);
            var orphans = new List<Episode>();
            var seen = new HashSet<Guid>();

            foreach (var episode in virtualChildren.Concat(virtualFromLibrary))
            {
                if (!seen.Add(episode.Id))
                {
                    continue;
                }

                if (keyById.TryGetValue(episode.Id, out var key))
                {
                    result[key] = episode;
                }
                else if (episode.ProviderIds.Keys.All(CustomId.IsCustomKey))
                {
                    orphans.Add(episode);
                }
            }

            // Direct read for the keys not found yet (independent of the season → episode relation)
            var foundById = 0;
            foreach (var (id, key) in keyById)
            {
                if (!result.ContainsKey(key)
                    && _libraryManager.GetItemById(id) is Episode episode
                    && episode.IsVirtualItem)
                {
                    foundById++;
                    result[key] = episode;
                }
            }

            _logger.LogInformation(
                "🔎 [MISSING] Lookup {Series} / {Season}: {Children} virtual children, {Library} virtual in library for this season, {ById} more by id → {Total} known, {Orphans} orphan(s)",
                season.SeriesName, season.Name, virtualChildren.Count, virtualFromLibrary.Count, foundById, result.Count, orphans.Count);

            return (result, orphans);
        }

        /// <summary>
        /// Episode number of each virtual episode. ESSENTIAL: on every series refresh,
        /// Jellyfin (SeriesMetadataService.RemoveObsoleteEpisodes) deletes virtual episodes WITHOUT a number,
        /// or whose number is held by a real episode of the same ParentIndexNumber group (the whole series).
        /// Order of preference: the API number if free, the number assigned during a previous analysis
        /// (cache) if free, otherwise the next free number after the highest real/assigned number.
        /// </summary>
        private Dictionary<string, int> AssignIndexNumbers(
            Season season,
            Dictionary<string, EpisodeListItem> wantedByKey,
            Dictionary<string, CachedMissingEpisode> cache)
        {
            var physical = (season.Series?.GetEpisodes(null, new DtoOptions(), true) ?? season.GetEpisodes())
                .OfType<Episode>()
                .Where(e => !e.IsVirtualItem && !e.IsMissingEpisode && e.ParentIndexNumber == season.IndexNumber)
                .ToList();

            return ComputeIndexNumbers(physical, wantedByKey, cache);
        }

        /// <summary>
        /// Pure part of AssignIndexNumbers (testable without a Jellyfin library)
        /// </summary>
        internal static Dictionary<string, int> ComputeIndexNumbers(
            IReadOnlyList<Episode> physical,
            Dictionary<string, EpisodeListItem> wantedByKey,
            Dictionary<string, CachedMissingEpisode> cache)
        {
            bool IsTakenByPhysical(int number) => physical.Any(e => e.ContainsEpisodeNumber(number));

            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var used = new HashSet<int>();

            // 1. Number supplied by the API
            foreach (var (key, item) in wantedByKey)
            {
                if (item.IndexNumber is int apiNumber && apiNumber > 0 && !IsTakenByPhysical(apiNumber) && used.Add(apiNumber))
                {
                    result[key] = apiNumber;
                }
            }

            // 2. Number already assigned (stable from one analysis to the next)
            foreach (var key in wantedByKey.Keys.Where(k => !result.ContainsKey(k)))
            {
                if (cache.TryGetValue(key, out var cached)
                    && cached.IndexNumber is int previous
                    && previous > 0
                    && !IsTakenByPhysical(previous)
                    && used.Add(previous))
                {
                    result[key] = previous;
                }
            }

            // 3. Next free number, in the order of the API list
            var next = Math.Max(
                physical.Select(e => e.IndexNumberEnd ?? e.IndexNumber ?? 0).DefaultIfEmpty(0).Max(),
                used.DefaultIfEmpty(0).Max()) + 1;
            foreach (var key in wantedByKey.Keys.Where(k => !result.ContainsKey(k)))
            {
                while (IsTakenByPhysical(next) || used.Contains(next))
                {
                    next++;
                }

                result[key] = next;
                used.Add(next);
                next++;
            }

            return result;
        }

        /// <summary>
        /// Copies the list's fields (title, overview, date, duration, number) onto an existing virtual episode
        /// </summary>
        private static async Task UpdateVirtualEpisodeAsync(Episode episode, EpisodeListItem item, SourceConfiguration source, int indexNumber, CancellationToken cancellationToken)
        {
            var changed = AddOverviewItems(episode, item, source);

            // List overview = tag list: an old overview that was this list is cleared,
            // never a real synopsis set since by the provider (video endpoint)
            if (item.OverviewIsList && OverviewListDetector.TryParse(episode.Overview) != null)
            {
                episode.Overview = null;
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(item.Title) && episode.Name != item.Title)
            {
                episode.Name = item.Title;
                changed = true;
            }

            if (item.Overview != null && episode.Overview != item.Overview)
            {
                episode.Overview = item.Overview;
                changed = true;
            }

            if (item.PremiereDate.HasValue && episode.PremiereDate != item.PremiereDate)
            {
                episode.PremiereDate = item.PremiereDate;
                episode.ProductionYear = item.PremiereDate.Value.Year;
                changed = true;
            }

            if (item.RunTimeTicks.HasValue && episode.RunTimeTicks != item.RunTimeTicks)
            {
                episode.RunTimeTicks = item.RunTimeTicks;
                changed = true;
            }

            if (episode.IndexNumber != indexNumber)
            {
                episode.IndexNumber = indexNumber;
                changed = true;
            }

            if (changed)
            {
                await episode.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken);
            }
        }

        /// <summary>
        /// Overview of the episode list recognised as a list of tags/genres (option of the Videos/Episodes mapping):
        /// items set aside, overview cleared
        /// </summary>
        private void DetectOverviewLists(List<EpisodeListItem> items, SourceConfiguration source)
        {
            var target = source.VideoFieldMappings?.OverviewListTarget;
            if (!OverviewListDetector.IsEnabled(target))
            {
                return;
            }

            foreach (var item in items)
            {
                var list = OverviewListDetector.TryParse(item.Overview);
                if (list == null)
                {
                    continue;
                }

                _logger.LogDebug("🏷️ [MISSING] Overview of '{Title}' is a list → {Target}: [{Items}]", item.Title, target, string.Join(", ", list));
                item.OverviewIsList = true;
                item.OverviewItems = list;
                item.Overview = null;
            }
        }

        /// <summary>
        /// Adds the items of a list-overview to the episode's tags (or genres). true if modified
        /// </summary>
        private static bool AddOverviewItems(Episode episode, EpisodeListItem item, SourceConfiguration source)
        {
            if (!item.OverviewIsList || item.OverviewItems == null)
            {
                return false;
            }

            var changed = false;
            if (OverviewListDetector.IsGenres(source.VideoFieldMappings?.OverviewListTarget))
            {
                foreach (var genre in item.OverviewItems.Where(g => !episode.Genres.Contains(g, StringComparer.OrdinalIgnoreCase)))
                {
                    episode.AddGenre(genre);
                    changed = true;
                }
            }
            else
            {
                var missing = item.OverviewItems.Where(t => !episode.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
                if (missing.Count > 0)
                {
                    episode.Tags = episode.Tags.Concat(missing).ToArray();
                    changed = true;
                }
            }

            return changed;
        }

        private static CachedMissingEpisode CreateCacheEntry(EpisodeListItem item, SourceConfiguration source, string hash, ThumbResult thumb, int indexNumber)
        {
            JsonElement? json = null;
            if (!string.IsNullOrEmpty(item.RawJson))
            {
                using var doc = JsonDocument.Parse(item.RawJson);
                json = doc.RootElement.Clone();
            }

            return new CachedMissingEpisode
            {
                Hash = hash,
                ThumbUrl = thumb.Url,
                ThumbFailed = thumb.Failed,
                ThumbLandscape = source.LandscapePortraitThumbs,
                IndexNumber = indexNumber,
                PreviewUrl = PreviewMapper.ResolveUrl(item.PreviewUrl, source.BaseUrl),
                UpdatedAt = DateTime.UtcNow,
                Json = json
            };
        }

        /// <summary>
        /// Video preview of the virtual episode = LOCAL trailer (Trailer, ExtraType.Trailer) whose Path
        /// is the preview URL, attached through ExtraIds/OwnerId. Played through the server (HTTP proxy,
        /// transcoding if needed): works on web as well as Android, unlike RemoteTrailers.
        /// Only possible for virtual episodes: with no file, Jellyfin does not recompute their
        /// extras (RefreshedOwnedItems requires IsFileProtocol) and therefore does not delete this Trailer.
        /// </summary>
        private async Task SyncPreviewAsync(Episode episode, EpisodeListItem item, SourceConfiguration source, CancellationToken cancellationToken)
        {
            var changed = false;

            // Remote trailers set by a previous version (unplayable in jellyfin-web)
            if (episode.RemoteTrailers.Any(PreviewMapper.IsLegacyRemoteTrailer))
            {
                episode.RemoteTrailers = episode.RemoteTrailers.Where(t => !PreviewMapper.IsLegacyRemoteTrailer(t)).ToArray();
                changed = true;
            }

            var url = PreviewMapper.ResolveUrl(item.PreviewUrl, source.BaseUrl);
            var existing = GetOurPreviews(episode);
            var upToDate = existing.FirstOrDefault(t => url != null && string.Equals(t.Path, url, StringComparison.Ordinal));

            // Preview removed or URL changed
            foreach (var old in existing.Where(t => t != upToDate))
            {
                _logger.LogInformation("🎞️ [PREVIEW] Removing preview {Url} of '{Name}'", old.Path, episode.Name);
                episode.ExtraIds = episode.ExtraIds.Where(id => !id.Equals(old.Id)).ToArray();
                _libraryManager.DeleteItem(old, new DeleteOptions { DeleteFileLocation = false });
                changed = true;
            }

            if (url != null && upToDate == null)
            {
                var trailer = new Trailer
                {
                    Id = _libraryManager.GetNewItemId($"{episode.Id:N}-custom-preview-{url}", typeof(Trailer)),
                    Name = PreviewMapper.GetTrailerName(episode.Name),
                    Path = url,
                    ExtraType = ExtraType.Trailer,
                    OwnerId = episode.Id,
                    ParentId = Guid.Empty,
                    // Locked: no remote provider (TMDb...) that would rename the preview.
                    // The (local) ffprobe probe still runs.
                    IsLocked = true,
                    DateCreated = DateTime.UtcNow,
                    DateModified = DateTime.UtcNow
                };
                // Marker (informative): "{prefix}-{id}" key computed from the list, never re-read from the episode
                // (an empty value would make SetProviderId throw)
                trailer.SetProviderId(PreviewMapper.ProviderKey, EpisodeKey(source, item));

                _logger.LogInformation("🎞️ [PREVIEW] Creating preview {Url} for '{Name}'", url, episode.Name);
                _libraryManager.CreateItem(trailer, null);

                try
                {
                    // ffprobe on the URL: codecs, duration... so that the server chooses direct play or transcoding
                    await trailer.RefreshMetadata(
                        new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                        {
                            EnableRemoteContentProbe = true,
                            MetadataRefreshMode = MetadataRefreshMode.FullRefresh
                        },
                        cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "🎞️ [PREVIEW] Probe failed for {Url} (the preview may still play)", url);
                }

                episode.ExtraIds = episode.ExtraIds.Append(trailer.Id).ToArray();
                changed = true;
            }

            if (changed)
            {
                await episode.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken);
            }
        }

        private List<Trailer> GetOurPreviews(Episode episode)
        {
            return PreviewMapper.GetPreviewTrailers(episode, _libraryManager);
        }

        /// <summary>
        /// Removes the remote "Aperçu…" trailers that a previous version put on REAL
        /// episodes: jellyfin-web does not play a non-YouTube URL, the button always failed.
        /// </summary>
        private async Task CleanupLegacyRemoteTrailersOnceAsync(CancellationToken cancellationToken)
        {
            // Walks the whole library: only once (marker in the plugin's data folder)
            var folder = Plugin.Instance?.DataFolderPath;
            var marker = string.IsNullOrEmpty(folder) ? null : Path.Combine(folder, "legacy-remote-trailers-cleaned");
            if (marker != null && File.Exists(marker))
            {
                return;
            }

            var episodes = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                Recursive = true
            }).OfType<Episode>().Where(e => !e.IsVirtualItem && e.RemoteTrailers.Any(PreviewMapper.IsLegacyRemoteTrailer)).ToList();

            foreach (var episode in episodes)
            {
                episode.RemoteTrailers = episode.RemoteTrailers.Where(t => !PreviewMapper.IsLegacyRemoteTrailer(t)).ToArray();
                await episode.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken);
            }

            if (episodes.Count > 0)
            {
                _logger.LogInformation("🎞️ [PREVIEW] Removed legacy remote preview trailers from {Count} episodes", episodes.Count);
            }

            if (marker != null)
            {
                Directory.CreateDirectory(folder);
                await File.WriteAllTextAsync(marker, DateTime.UtcNow.ToString("O"), cancellationToken);
            }
        }

        /// <summary>
        /// Downloads the list's thumbnail and saves it as the episode's Primary image
        /// (for an episode, Primary = the image shown in the season's episode list).
        /// No new download if the episode already has an image for the same URL.
        /// Relative URL ("/img/1.jpg") → prefixed with the source's BaseUrl.
        /// </summary>
        private async Task<ThumbResult> EnsureThumbAsync(
            Episode episode,
            EpisodeListItem item,
            SourceConfiguration source,
            CachedMissingEpisode cached,
            bool saveToRepository,
            CancellationToken cancellationToken)
        {
            var url = PreviewMapper.ResolveUrl(item.ThumbUrl, source.BaseUrl);
            if (url == null)
            {
                return new ThumbResult(null, false);
            }

            // Image already present for this URL: no new download.
            // Without a cache entry (episode older than the cache, or "Tout retraiter") the existing image is kept,
            // UNLESS the portrait conversion is on: it may never have been applied to this image.
            var keepExisting = cached == null
                ? !source.LandscapePortraitThumbs
                : string.Equals(cached.ThumbUrl, url, StringComparison.Ordinal)
                    && !cached.ThumbFailed
                    && cached.ThumbLandscape == source.LandscapePortraitThumbs;
            if (episode.HasImage(ImageType.Primary, 0) && keepExisting)
            {
                _logger.LogDebug("🔎 [MISSING] Thumbnail of '{Name}' already up to date - not downloaded again", episode.Name);
                return new ThumbResult(url, false);
            }

            try
            {
                // Downloaded by the plugin with the source's HTTP config (headers, HTTP/2…):
                // SaveImage(url) would use Jellyfin's HTTP client, without these settings
                var (data, mimeType, error) = await _apiClient.DownloadImageAsync(url, source, source.LandscapePortraitThumbs, cancellationToken);
                if (data == null)
                {
                    _logger.LogWarning("🔎 [MISSING] Could not download thumbnail {Url} for '{Name}': {Error}", url, episode.Name, error);
                    return new ThumbResult(url, true);
                }

                using (var stream = new MemoryStream(data))
                {
                    await _providerManager.SaveImage(episode, stream, mimeType, ImageType.Primary, null, cancellationToken);
                }

                if (saveToRepository)
                {
                    await episode.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken);
                }

                _logger.LogDebug("🔎 [MISSING] Thumbnail saved for '{Name}': {Url}", episode.Name, url);
                return new ThumbResult(url, false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "🔎 [MISSING] Could not download thumbnail {Url} for '{Name}'", url, episode.Name);
                return new ThumbResult(url, true);
            }
        }

        private sealed record ThumbResult(string Url, bool Failed);

        private sealed class SyncStats
        {
            public int Created { get; set; }
            public int Updated { get; set; }
            public int Unchanged { get; set; }
            public int Removed { get; set; }
        }

        /// <summary>
        /// {PREFIX-ID} tag of the season: folder name, otherwise ProviderIds["custom"] set by CustomSeasonProvider
        /// </summary>
        private static string GetSeasonTag(Season season)
        {
            if (!string.IsNullOrEmpty(season.Path))
            {
                var tag = TagParser.Extract(TagParser.GetOwnName(season.Path));
                if (tag != null)
                {
                    return tag;
                }
            }

            return CustomId.Get(season.ProviderIds);
        }

        /// <summary>
        /// "{prefix}-{id}" keys of a physical episode: ALL the tags of the file name (a file may bundle
        /// several API episodes: "Ep {dm-1} {dm-2}.mkv"), otherwise ProviderIds["custom"]
        /// </summary>
        private static IEnumerable<string> GetEpisodeKeys(Episode episode)
        {
            if (!string.IsNullOrEmpty(episode.Path))
            {
                var tags = TagParser.ExtractAll(TagParser.GetOwnName(episode.Path));
                if (tags.Count > 0)
                {
                    return tags;
                }
            }

            var id = CustomId.Get(episode.ProviderIds);
            return id != null ? new[] { id } : Array.Empty<string>();
        }

        private static string GetReportPath()
        {
            var folder = Plugin.Instance?.DataFolderPath;
            return string.IsNullOrEmpty(folder) ? null : Path.Combine(folder, ReportFileName);
        }

        private void SaveReport(MissingEpisodesReport report)
        {
            try
            {
                var path = GetReportPath();
                if (path == null)
                {
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonSerializer.Serialize(report));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "🔎 [MISSING] Could not save report");
            }
        }
    }

    public class MissingEpisodesReport
    {
        public DateTime? GeneratedAt { get; set; }

        public double DurationSeconds { get; set; }

        /// <summary>Virtual episodes created / updated / skipped thanks to the cache / deleted during the analysis</summary>
        public int Created { get; set; }
        public int Updated { get; set; }
        public int Unchanged { get; set; }
        public int Removed { get; set; }

        public List<SeasonMissingReport> Seasons { get; set; } = new List<SeasonMissingReport>();
    }

    public class SeasonMissingReport
    {
        public Guid SeriesId { get; set; }
        public string SeriesName { get; set; }
        public Guid SeasonId { get; set; }
        public string SeasonName { get; set; }
        public string SourceName { get; set; }
        public string SourceItemId { get; set; }

        /// <summary>Prefix of the episodes' tags when it is not the season's (a local season replaces the API's list), else null</summary>
        public string EpisodePrefix { get; set; }
        public int LocalCount { get; set; }
        public int ApiCount { get; set; }
        public string Error { get; set; }
        public List<MissingEpisodeEntry> Missing { get; set; } = new List<MissingEpisodeEntry>();
    }

    public class MissingEpisodeEntry
    {
        public string Id { get; set; }

        /// <summary>Full "prefix-id" tag to put in the file name (the list's source, or a local source's for an episode added there)</summary>
        public string Tag { get; set; }
        public string Title { get; set; }
        public int? IndexNumber { get; set; }
        public DateTime? PremiereDate { get; set; }
        public long? RunTimeTicks { get; set; }

        /// <summary>Synopsis of the list, truncated to 400 characters</summary>
        public string Overview { get; set; }

        /// <summary>Id of the virtual episode (null if the creation of virtual episodes is disabled)</summary>
        public Guid? EpisodeId { get; set; }
    }
}
