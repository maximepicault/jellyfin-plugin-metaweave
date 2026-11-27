using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Jellyfin.Plugin.CustomMetadata.Api;
using MediaBrowser.Model.Entities;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    public class SourceRefreshRequest
    {
        /// <summary>Prefix of the sources to refresh: an API source and a local source that share it are one group</summary>
        public string Prefix { get; set; }

        /// <summary>"series", "season", "episode", "album", "photo" (empty = everything the sources of the prefix hold). A dump source: "episode" and "movie" (empty = both)</summary>
        public List<string> Types { get; set; } = new List<string>();

        /// <summary>Dump sources only: the libraries to look into (ids from Library/VirtualFolders). Empty = every library</summary>
        public List<string> LibraryIds { get; set; } = new List<string>();

        /// <summary>Also replace the images (default: metadata only)</summary>
        public bool ReplaceImages { get; set; }

        /// <summary>Optional: only the elements with this exact tag ("fny-1234")</summary>
        public string Tag { get; set; }

        /// <summary>With Tag: also the elements below the matching ones that carry the prefix (a season's episodes)</summary>
        public bool IncludeChildren { get; set; }
    }

    public class SourceRefreshStatus
    {
        public bool Running { get; set; }
        public string Prefix { get; set; }
        public int Total { get; set; }
        public int Done { get; set; }
        public int Failed { get; set; }
        public int Series { get; set; }
        public int Seasons { get; set; }
        public int Episodes { get; set; }
        public int Albums { get; set; }
        public int Photos { get; set; }

        /// <summary>Videos of albums (refreshed with their photos)</summary>
        public int Videos { get; set; }
        public int Movies { get; set; }

        /// <summary>Dump sources: "scan" (looking for the files the dump recognises) then "refresh"; empty for the other sources</summary>
        public string Phase { get; set; }

        /// <summary>Dump sources: the files looked at, and how many of them the dump recognised</summary>
        public int Scanned { get; set; }
        public int Matched { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? FinishedAt { get; set; }

        /// <summary>Image files left in media folders (poster.jpg…) although the plugin applied another image: they come back at the next scan</summary>
        public int LocalFilesKept { get; set; }

        /// <summary>Why nothing was started (a refresh is already running…)</summary>
        public string Message { get; set; }
    }

    /// <summary>
    /// Targeted refresh: finds, in the library, the series / seasons / episodes that carry a source's prefix and refreshes only
    /// those (no scan of the media folders). The API source is called first by the providers and the local source of the same
    /// prefix is laid over its answer, so one refresh of the prefix updates both. Runs in the background, one item at a time
    /// (IProviderManager.QueueRefresh dropped items when several were queued at once: see CastApplyService).
    /// </summary>
    public class SourceRefreshService
    {
        private static readonly SemaphoreSlim RunLock = new SemaphoreSlim(1, 1);
        private static readonly object StatusLock = new object();
        private static SourceRefreshStatus _status = new SourceRefreshStatus();

        private readonly ILibraryManager _libraryManager;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger _logger;
        private readonly CustomApiClient _api;
        private readonly SourceResolver _resolver;
        private readonly IProviderManager _providerManager;
        private readonly DumpStore _dump;

        public SourceRefreshService(ILibraryManager libraryManager, IFileSystem fileSystem, ILogger logger, CustomApiClient api, SourceResolver resolver, IProviderManager providerManager, DumpStore dump = null)
        {
            _dump = dump;
            _api = api;
            _resolver = resolver;
            _providerManager = providerManager;
            _libraryManager = libraryManager;
            _fileSystem = fileSystem;
            _logger = logger;
        }

        public SourceRefreshStatus Status()
        {
            lock (StatusLock)
            {
                return Copy(_status);
            }
        }

        private static SourceRefreshStatus Copy(SourceRefreshStatus s) => new SourceRefreshStatus
        {
            Running = s.Running, Prefix = s.Prefix, Total = s.Total, Done = s.Done, Failed = s.Failed,
            Series = s.Series, Seasons = s.Seasons, Episodes = s.Episodes, Albums = s.Albums, Photos = s.Photos, Videos = s.Videos, Movies = s.Movies, Phase = s.Phase, Scanned = s.Scanned, Matched = s.Matched, StartedAt = s.StartedAt, FinishedAt = s.FinishedAt, Message = s.Message, LocalFilesKept = s.LocalFilesKept
        };

        /// <summary>Finds the items, then refreshes them in the background. Returns at once with the counts.</summary>
        public SourceRefreshStatus Start(SourceRefreshRequest request)
        {
            var prefix = (request?.Prefix ?? string.Empty).Trim();
            if (prefix.Length == 0)
            {
                return new SourceRefreshStatus { Message = "Préfixe manquant" };
            }

            if (!RunLock.Wait(0))
            {
                var current = Status();
                current.Message = "Une actualisation est déjà en cours";
                return current;
            }

            // A dump recognises files by their NAME: the files that carry no tag are the point, so they cannot be found by tag
            var dumpSource = (Plugin.Instance?.Configuration?.Sources ?? new List<Configuration.SourceConfiguration>())
                .FirstOrDefault(s => s.IsDump && s.Enabled && string.Equals(s.Prefix, prefix, StringComparison.OrdinalIgnoreCase));
            if (dumpSource != null && _dump != null)
            {
                try
                {
                    return StartDump(request, prefix, dumpSource);
                }
                catch
                {
                    RunLock.Release();
                    throw;
                }
            }

            List<BaseItem> items;
            try
            {
                items = Find(request, prefix);
            }
            catch
            {
                RunLock.Release();
                throw;
            }

            lock (StatusLock)
            {
                _status = new SourceRefreshStatus
                {
                    Running = items.Count > 0, Prefix = prefix, Total = items.Count,
                    Series = items.Count(i => i is Series), Seasons = items.Count(i => i is Season), Episodes = items.Count(i => i is Episode),
                    Albums = items.Count(IsAlbum), Photos = items.Count(i => i is Photo), Videos = items.Count(i => i.GetType() == typeof(Video)),
                    StartedAt = DateTime.UtcNow, FinishedAt = items.Count > 0 ? null : DateTime.UtcNow
                };
            }

            if (items.Count == 0)
            {
                RunLock.Release();
                _logger.LogInformation("🔁 [SOURCE REFRESH] Nothing to refresh for prefix '{Prefix}'", prefix);
                return Status();
            }

            var replaceImages = request.ReplaceImages;
            _logger.LogInformation("🔁 [SOURCE REFRESH] Refreshing {Total} item(s) of prefix '{Prefix}' ({Series} series, {Seasons} seasons, {Episodes} episodes, {Albums} albums, {Photos} photos, {Videos} videos), {Images}",
                items.Count, prefix, Status().Series, Status().Seasons, Status().Episodes, Status().Albums, Status().Photos, Status().Videos, replaceImages ? "metadata and images" : "metadata only");
            _ = Task.Run(async () =>
            {
                try
                {
                    // Series first, then seasons, then episodes (albums, then photos): a child refreshed after its parent sees the parent's new data
                    await RefreshItemsAsync(items.OrderBy(i => i is Series || IsAlbum(i) ? 0 : (i is Season ? 1 : 2)).ThenBy(i => i.Path?.Length ?? 0).ToList(), replaceImages);
                }
                finally
                {
                    lock (StatusLock)
                    {
                        _status.Running = false;
                        _status.FinishedAt = DateTime.UtcNow;
                    }

                    _logger.LogInformation("🔁 [SOURCE REFRESH] ✅ Done for prefix '{Prefix}': {Done}/{Total} refreshed, {Failed} failed", Status().Prefix, Status().Done, Status().Total, Status().Failed);
                    RunLock.Release();
                }
            });

            return Status();
        }

        /// <summary>Refreshes the items one at a time (full refresh, plugin cache bypassed); a failure is counted and logged, never fatal</summary>
        private async Task RefreshItemsAsync(List<BaseItem> items, bool replaceImagesWanted)
        {
            foreach (var item in items)
            {
                try
                {
                    // A photo's image is its own file, a video's comes from its frames: the source gives none, nothing is replaced
                    var replaceImages = replaceImagesWanted && !IsAlbumMedia(item);
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    long jellyfinMs, imagesMs = 0;
                    var imagesBefore = replaceImages ? ImageSignature(item) : null;
                    await item.RefreshMetadata(
                        new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                        {
                            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                            ReplaceAllMetadata = true,
                            ImageRefreshMode = replaceImages ? MetadataRefreshMode.FullRefresh : MetadataRefreshMode.None,
                            ReplaceAllImages = replaceImages,
                            IsAutomated = false   // a user request: the plugin's cache is bypassed
                        },
                        CancellationToken.None);
                    if (replaceImages)
                    {
                        var imagesAfter = ImageSignature(item);
                        _logger.LogInformation("🔁 [SOURCE REFRESH] 🖼️ '{Name}': Jellyfin refresh {State} the images (before: {Before} | after: {After})",
                            item.Name, imagesBefore == imagesAfter ? "left UNCHANGED" : "replaced", imagesBefore, imagesAfter);
                        jellyfinMs = watch.ElapsedMilliseconds;
                        await ApplyImagesAsync(item);
                        imagesMs = watch.ElapsedMilliseconds - jellyfinMs;
                    }
                    else
                    {
                        jellyfinMs = watch.ElapsedMilliseconds;
                    }

                    // Where the time goes: Jellyfin's own refresh (every provider of the library, probing the video file, images…)
                    // against the plugin's image step. The plugin's own part (matching + reading the dump in memory) is a few milliseconds.
                    _logger.LogInformation("🔁 [SOURCE REFRESH] ⏱️ '{Name}': {Total} ms (Jellyfin refresh {Jellyfin} ms{Images})",
                        item.Name, watch.ElapsedMilliseconds, jellyfinMs, replaceImages ? $", plugin images {imagesMs} ms" : ", images not replaced");
                    lock (StatusLock) { _status.Done++; }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "🔁 [SOURCE REFRESH] ⚠️ Could not refresh '{Name}': {Message}", item.Name, ex.Message);
                    lock (StatusLock) { _status.Failed++; }
                }
            }
        }

        /// <summary>
        /// Writes what the dump says straight onto the items, WITHOUT Jellyfin's own refresh. Everything a dump gives is already in
        /// memory, so nothing has to be fetched: a full Jellyfin refresh would call every other provider of the library again
        /// (TMDb… over the network), probe the video file and rebuild the images, seconds per item for no gain.
        /// The plugin's own provider is asked for the metadata (same code as a refresh: mapping, rules on genres and tags, cast
        /// overlay, stored id), then the fields it returns are set on the item and saved. A field the dump does not give is left as
        /// it is (what another provider wrote stays); tags, genres and studios are added to the existing ones.
        /// </summary>
        private async Task ApplyDumpAsync(List<BaseItem> items, bool replaceImages)
        {
            foreach (var item in items)
            {
                try
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    var libraryOptions = _libraryManager.GetLibraryOptions(item);
                    BaseItem found = null;
                    IReadOnlyList<PersonInfo> people = null;
                    var enabled = true;

                    if (item is Episode episode)
                    {
                        var provider = _providerManager.GetMetadataProviders<Episode>(item, libraryOptions).OfType<Providers.CustomEpisodeProvider>().FirstOrDefault();
                        enabled = provider != null;
                        if (provider != null)
                        {
                            var info = episode.GetLookupInfo();
                            info.IsAutomated = false;
                            var result = await provider.GetMetadata(info, CancellationToken.None);
                            found = result.HasMetadata ? result.Item : null;
                            people = result.People;
                        }
                    }
                    else if (item is MediaBrowser.Controller.Entities.Movies.Movie movie)
                    {
                        var provider = _providerManager.GetMetadataProviders<MediaBrowser.Controller.Entities.Movies.Movie>(item, libraryOptions).OfType<Providers.CustomMetadataProvider>().FirstOrDefault();
                        enabled = provider != null;
                        if (provider != null)
                        {
                            var info = movie.GetLookupInfo();
                            info.IsAutomated = false;
                            var result = await provider.GetMetadata(info, CancellationToken.None);
                            found = result.HasMetadata ? result.Item : null;
                            people = result.People;
                        }
                    }

                    if (!enabled)
                    {
                        _logger.LogWarning("🔁 [SOURCE REFRESH] ⚠️ '{Name}': \"Custom Metadata Provider\" is not ticked for this type in its library - nothing applied", item.Name);
                        lock (StatusLock) { _status.Failed++; }
                        continue;
                    }

                    if (found == null)
                    {
                        _logger.LogWarning("🔁 [SOURCE REFRESH] ⚠️ '{Name}': the dump returned nothing for it - left as it is", item.Name);
                        lock (StatusLock) { _status.Failed++; }
                        continue;
                    }

                    if (!item.IsLocked)
                    {
                        CopyMetadata(found, item);
                        if (people != null && people.Count > 0)
                        {
                            // The people other providers gave stay; ours are added (same name and kind = the same person)
                            var existing = _libraryManager.GetPeople(item) ?? new List<PersonInfo>();
                            var merged = existing.Concat(people.Where(p => !existing.Any(e => string.Equals(e.Name, p.Name, StringComparison.OrdinalIgnoreCase) && e.Type == p.Type))).ToList();
                            if (merged.Count != existing.Count)
                            {
                                await _libraryManager.UpdatePeopleAsync(item, merged, CancellationToken.None);
                            }
                        }

                        await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, CancellationToken.None);
                    }

                    var metadataMs = watch.ElapsedMilliseconds;
                    if (replaceImages)
                    {
                        await ApplyImagesAsync(item);
                    }

                    _logger.LogInformation("🔁 [SOURCE REFRESH] ⏱️ '{Name}': {Total} ms, written directly from the dump (metadata {Metadata} ms{Images}){Locked}",
                        item.Name, watch.ElapsedMilliseconds, metadataMs, replaceImages ? $", plugin images {watch.ElapsedMilliseconds - metadataMs} ms" : ", images not replaced",
                        item.IsLocked ? " - the item is locked in Jellyfin: metadata left untouched" : string.Empty);
                    lock (StatusLock) { _status.Done++; }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "🔁 [SOURCE REFRESH] ⚠️ Could not apply the dump to '{Name}': {Message}", item.Name, ex.Message);
                    lock (StatusLock) { _status.Failed++; }
                }
            }
        }

        /// <summary>The fields the plugin's provider filled in, copied onto the library item; what it left empty is not touched</summary>
        private static void CopyMetadata(BaseItem from, BaseItem to)
        {
            if (!string.IsNullOrWhiteSpace(from.Name)) { to.Name = from.Name; }
            if (!string.IsNullOrWhiteSpace(from.Overview)) { to.Overview = from.Overview; }
            if (from.PremiereDate.HasValue)
            {
                to.PremiereDate = from.PremiereDate;
                to.ProductionYear = from.PremiereDate.Value.Year;
            }

            if (from.CommunityRating.HasValue) { to.CommunityRating = from.CommunityRating; }
            if (from.IndexNumber.HasValue && to is Episode) { to.IndexNumber = from.IndexNumber; }

            static string[] Union(string[] current, string[] added) => (current ?? Array.Empty<string>()).Concat(added ?? Array.Empty<string>())
                .Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            if (from.Tags != null && from.Tags.Length > 0) { to.Tags = Union(from.Tags, to.Tags); }
            if (from.Genres != null && from.Genres.Length > 0) { to.Genres = Union(from.Genres, to.Genres); }
            if (from.Studios != null && from.Studios.Length > 0) { to.Studios = Union(from.Studios, to.Studios); }

            foreach (var id in from.ProviderIds ?? new Dictionary<string, string>())
            {
                if (!string.IsNullOrWhiteSpace(id.Value))
                {
                    to.ProviderIds[id.Key] = id.Value;
                }
            }
        }

        /// <summary>
        /// Targeted refresh of a DUMP source. A dump recognises files by their name, so the files to refresh are NOT found by a tag:
        /// every episode / movie file of the chosen libraries is looked at (phase "scan"), the ones the dump recognises are kept
        /// and only those are refreshed (phase "refresh"). A file tagged for another source, or that matches nothing, is left alone.
        /// </summary>
        private SourceRefreshStatus StartDump(SourceRefreshRequest request, string prefix, Configuration.SourceConfiguration source)
        {
            var wanted = new HashSet<string>((request.Types == null || request.Types.Count == 0 ? new List<string> { "episode", "movie" } : request.Types)
                .Select(t => (t ?? string.Empty).Trim().ToLowerInvariant()));
            var kinds = new List<BaseItemKind>();
            if (wanted.Contains("episode")) { kinds.Add(BaseItemKind.Episode); }
            if (wanted.Contains("movie")) { kinds.Add(BaseItemKind.Movie); }

            var libraryIds = (request.LibraryIds ?? new List<string>()).Select(i => Guid.TryParse(i, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToArray();
            var queryWatch = System.Diagnostics.Stopwatch.StartNew();
            var candidates = new List<BaseItem>();
            if (kinds.Count > 0)
            {
                var query = new InternalItemsQuery { IncludeItemTypes = kinds.ToArray(), Recursive = true, IsVirtualItem = false };
                if (libraryIds.Length > 0)
                {
                    // One query per library: AncestorIds means "below ALL of these"
                    foreach (var id in libraryIds)
                    {
                        query.AncestorIds = new[] { id };
                        candidates.AddRange(_libraryManager.GetItemList(query));
                    }
                }
                else
                {
                    candidates.AddRange(_libraryManager.GetItemList(query));
                }

                candidates = candidates.Where(i => !string.IsNullOrEmpty(i.Path)).GroupBy(i => i.Id).Select(g => g.First()).ToList();
            }

            lock (StatusLock)
            {
                _status = new SourceRefreshStatus
                {
                    Running = candidates.Count > 0, Prefix = prefix, Total = candidates.Count, Scanned = candidates.Count, Phase = "scan",
                    StartedAt = DateTime.UtcNow, FinishedAt = candidates.Count > 0 ? null : DateTime.UtcNow
                };
            }

            if (candidates.Count == 0)
            {
                RunLock.Release();
                _logger.LogInformation("🔁 [SOURCE REFRESH] 📦 Dump '{Source}': no episode or movie file in the chosen libraries", source.Name);
                return Status();
            }

            var replaceImages = request.ReplaceImages;
            _logger.LogInformation("🔁 [SOURCE REFRESH] 📦 Dump '{Source}' (prefix '{Prefix}'): looking at {Count} file(s) in {Libraries} (listed in {ListMs} ms), then refreshing the ones the dump recognises ({Images})",
                source.Name, prefix, candidates.Count, libraryIds.Length == 0 ? "every library" : libraryIds.Length + " librarie(s)", queryWatch.ElapsedMilliseconds, replaceImages ? "metadata and images" : "metadata only");
            var runWatch = System.Diagnostics.Stopwatch.StartNew();

            _ = Task.Run(async () =>
            {
                try
                {
                    var matched = new List<BaseItem>();
                    foreach (var item in candidates)
                    {
                        try
                        {
                            var match = await _dump.ResolveAsync(item.Path, item.Name, item.ProviderIds, CancellationToken.None);
                            if (match != null && string.Equals(match.Source.Prefix, prefix, StringComparison.OrdinalIgnoreCase))
                            {
                                matched.Add(item);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "🔁 [SOURCE REFRESH] ⚠️ Could not look at '{Name}': {Message}", item.Name, ex.Message);
                        }

                        int done;
                        lock (StatusLock) { done = ++_status.Done; _status.Matched = matched.Count; }
                        if (done % 500 == 0)
                        {
                            _logger.LogInformation("🔁 [SOURCE REFRESH] 📦 Looked at {Done}/{Total} file(s), {Matched} recognised so far", done, candidates.Count, matched.Count);
                        }
                    }

                    lock (StatusLock)
                    {
                        _status.Phase = "refresh";
                        _status.Total = matched.Count;
                        _status.Done = 0;
                        _status.Episodes = matched.Count(i => i is Episode);
                        _status.Movies = matched.Count(i => i is MediaBrowser.Controller.Entities.Movies.Movie);
                    }

                    _logger.LogInformation("🔁 [SOURCE REFRESH] 📦 {Matched} of {Total} file(s) recognised by the dump in {ScanMs} ms ({Left} left alone: no match, or tagged for another source)",
                        matched.Count, candidates.Count, runWatch.ElapsedMilliseconds, candidates.Count - matched.Count);
                    await ApplyDumpAsync(matched, replaceImages);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "🔁 [SOURCE REFRESH] ❌ The dump refresh stopped: {Message}", ex.Message);
                }
                finally
                {
                    lock (StatusLock)
                    {
                        _status.Running = false;
                        _status.FinishedAt = DateTime.UtcNow;
                    }

                    _logger.LogInformation("🔁 [SOURCE REFRESH] ✅ Done for dump prefix '{Prefix}': {Done}/{Total} refreshed, {Failed} failed, in {TotalMs} ms", Status().Prefix, Status().Done, Status().Total, Status().Failed, runWatch.ElapsedMilliseconds);
                    RunLock.Release();
                }
            });

            return Status();
        }

        /// <summary>
        /// Applies the plugin's images itself after the refresh, because Jellyfin's "replace all images" does not always win: an image file
        /// already sitting in the media folder (poster.jpg…) is a local image and beats the remote ones, so the series kept its old poster.
        /// For each type the FIRST image of the plugin (API + local source, in that order) is saved when it differs from the item's current
        /// one (compared by content, so nothing is rewritten when the refresh already did it). Backdrops are a carousel: all of them are
        /// applied (API, local gallery, Backdrop/ sub-folder, de-duplicated) and the extra old ones removed when there are at least two.
        /// </summary>
        private async Task ApplyImagesAsync(BaseItem item)
        {
            try
            {
                var tag = TagsOf(item).FirstOrDefault();
                var source = tag == null ? null : _resolver.GetSourceByPrefix(_resolver.ExtractPrefixFromId(tag), IsAlbum(item) ? SourceContent.Albums : SourceContent.Video);
                var kind = item is Series ? "series" : (item is Season ? "season" : (IsAlbum(item) ? "album" : "episode"));
                var offered = source == null ? null : await _api.GetImagesAsync(_resolver.ExtractIdWithoutPrefix(tag), kind, source, CancellationToken.None);
                var images = offered ?? new List<ImageInfo>();
                var changed = new List<string>();
                var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // SHA-256 of every image applied (or already there) now
                var unreadable = offered == null;                                       // then nothing is cleaned up: the source may only be down

                async Task<(byte[] Data, string Mime)> Fetch(string url, bool landscape)
                {
                    var (data, mime, _) = await _api.DownloadImageAsync(url, source, landscape, CancellationToken.None);
                    if (data == null || data.Length == 0) { unreadable = true; }
                    return (data, mime);
                }

                static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
                string CurrentSha(ImageType type, int index)
                {
                    var path = item.GetImageInfo(type, index)?.Path;
                    return !string.IsNullOrEmpty(path) && File.Exists(path) ? Sha(File.ReadAllBytes(path)) : null;
                }

                async Task Apply(ImageType type, int index, byte[] data, string mime)
                {
                    // a rotation only in the EXIF tag: Jellyfin's resizer ignores it, so the pixels are turned here (ExifOrientation)
                    data = ExifOrientation.Normalize(data, _logger, item.Name + " " + type);
                    if (data == null || data.Length == 0)
                    {
                        return;
                    }

                    var sha = Sha(data);
                    wanted.Add(sha);
                    if (sha == CurrentSha(type, index))
                    {
                        return;
                    }

                    using var stream = new MemoryStream(data);
                    await _providerManager.SaveImage(item, stream, mime ?? "image/jpeg", type, type == ImageType.Backdrop ? index : (int?)null, CancellationToken.None);
                    changed.Add(type + (type == ImageType.Backdrop ? "#" + index : string.Empty));
                }

                bool IsType(ImageInfo i, string name) => string.Equals(i.Type, name, StringComparison.OrdinalIgnoreCase) || (name == "Primary" && string.Equals(i.Type, "Poster", StringComparison.OrdinalIgnoreCase));
                var folderImages = item is Series || item is Season ? FolderImages.Discover(item.Path) : new List<FolderImage>();
                var keptLocalFiles = new List<string>();
                foreach (var (type, name) in new[] { (ImageType.Primary, "Primary"), (ImageType.Thumb, "Thumb"), (ImageType.Logo, "Logo"), (ImageType.Banner, "Banner") })
                {
                    // Candidates in order: the API and the local source, then (episode without a poster) its thumbnail like the image
                    // provider does, then the matching sub-folder (Primary/, Logo/…) of a series or season folder. The first one that
                    // can be read wins: an image that no longer exists must not leave the item without any image.
                    var urls = images.Where(i => IsType(i, name) && !i.NoDefault).Select(i => i.Url).ToList();
                    if (urls.Count == 0 && item is Episode && name == "Primary")
                    {
                        urls = images.Where(i => IsType(i, "Thumb") && !i.NoDefault).Select(i => i.Url).ToList();
                    }

                    byte[] data = null;
                    string mime = null;
                    foreach (var url in urls.Where(u => !string.IsNullOrEmpty(u)).Distinct())
                    {
                        (data, mime) = await Fetch(url, item is Episode && type is ImageType.Primary or ImageType.Thumb && source.LandscapePortraitThumbs);
                        if (data != null && data.Length > 0)
                        {
                            break;
                        }

                        _logger.LogWarning("🔁 [SOURCE REFRESH] ⚠️ '{Name}': {Type} image {Url} could not be read - trying the next one", item.Name, name, url);
                    }

                    if (data == null || data.Length == 0)
                    {
                        var fromFolder = folderImages.FirstOrDefault(i => i.Type == name);
                        if (fromFolder != null)
                        {
                            data = await File.ReadAllBytesAsync(fromFolder.FullPath);
                            mime = FolderImages.MimeOf(fromFolder.File);
                        }
                    }

                    if (data == null || data.Length == 0)
                    {
                        continue;
                    }

                    var previous = item.GetImageInfo(type, 0)?.Path;
                    await Apply(type, 0, data, mime);

                    // An image file left in the media folder (poster.jpg…) is not deleted by Jellyfin and is picked again at the next
                    // library scan: say so, the person has to remove it (or move it into the Primary/… sub-folder)
                    var current = item.GetImageInfo(type, 0)?.Path;
                    if (!string.IsNullOrEmpty(previous) && !string.Equals(previous, current, StringComparison.OrdinalIgnoreCase) && File.Exists(previous)
                        && !previous.StartsWith(item.GetInternalMetadataPath(), StringComparison.OrdinalIgnoreCase))
                    {
                        keptLocalFiles.Add(previous);
                    }
                }

                // Episodes too: Jellyfin's default library options fetch no backdrop for an episode, so a Backdrop of the gallery would never show up
                if (item is Series || item is Season || item is Episode)
                {
                    var backdrops = new List<(byte[] Data, string Mime)>();
                    var seen = new HashSet<string>();
                    void AddBackdrop(byte[] data, string mime)
                    {
                        if (data != null && data.Length > 0 && seen.Add(Sha(data))) { backdrops.Add((data, mime)); }
                    }

                    foreach (var url in images.Where(i => IsType(i, "Backdrop")).Select(i => i.Url).Distinct())
                    {
                        var (data, mime) = await Fetch(url, false);
                        AddBackdrop(data, mime);
                    }

                    foreach (var file in folderImages.Where(i => i.Type == "Backdrop"))
                    {
                        AddBackdrop(await File.ReadAllBytesAsync(file.FullPath), FolderImages.MimeOf(file.File));
                    }

                    var previousBackdrops = item.GetImages(ImageType.Backdrop).Select(i => i.Path).ToList();
                    for (var i = 0; i < backdrops.Count; i++)
                    {
                        await Apply(ImageType.Backdrop, i, backdrops[i].Data, backdrops[i].Mime);
                    }

                    // Same as above for an old backdrop.jpg / fanart.jpg left in the media folder and no longer used
                    if (backdrops.Count > 0)
                    {
                        var currentBackdrops = new HashSet<string>(item.GetImages(ImageType.Backdrop).Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
                        keptLocalFiles.AddRange(previousBackdrops.Where(path => !string.IsNullOrEmpty(path) && !currentBackdrops.Contains(path) && File.Exists(path)
                            && !path.StartsWith(item.GetInternalMetadataPath(), StringComparison.OrdinalIgnoreCase)));
                    }

                    var extra = backdrops.Count >= 2 ? item.GetImages(ImageType.Backdrop).Skip(backdrops.Count).ToList() : new List<ItemImageInfo>();
                    if (extra.Count > 0)
                    {
                        item.RemoveImages(extra);
                        changed.Add("-" + extra.Count + " old backdrop(s)");
                    }
                }

                // Images the plugin applied before and the source no longer gives (removed from the local source, the API or the folder)
                var removed = unreadable ? 0 : await RemoveDroppedImagesAsync(item, wanted);
                if (removed > 0)
                {
                    changed.Add("-" + removed + " image(s) no longer given by the source");
                }

                // ImageSaver only sets the new paths on the item in memory: without this the database keeps the old image list, and the
                // images vanish as soon as Jellyfin reloads the item (restart, or cache eviction in a big library)
                if (changed.Count > 0)
                {
                    await item.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, CancellationToken.None);
                }

                _logger.LogInformation("🔁 [SOURCE REFRESH] 🖼️ '{Name}': {Result}", item.Name, changed.Count == 0 ? "images already up to date" : "images applied by the plugin: " + string.Join(", ", changed));
                if (keptLocalFiles.Count > 0)
                {
                    lock (StatusLock) { _status.LocalFilesKept += keptLocalFiles.Count; }
                    _logger.LogWarning("🔁 [SOURCE REFRESH] ⚠️ '{Name}': old image file(s) still in the media folder, Jellyfin will pick them again at the next library scan - remove them or move them into the image sub-folder: {Files}", item.Name, string.Join(", ", keptLocalFiles));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "🔁 [SOURCE REFRESH] ⚠️ Could not apply the images of '{Name}': {Message}", item.Name, ex.Message);
            }
        }

        private static readonly object AppliedLock = new object();

        private static string AppliedPath => Path.Combine(Plugin.Instance?.DataFolderPath ?? Path.Combine(Path.GetTempPath(), "CustomMetadata"), "applied-images.json");

        /// <summary>
        /// Removes the images of the item that the plugin applied at an earlier "Remplacer les images" and that the source no longer
        /// gives, then remembers what it applied now. Only those: an image the plugin never applied (Jellyfin's own episode
        /// thumbnail, another provider's, one set by hand) is never touched, which is why the SHA-256 of the applied images is kept
        /// per item in applied-images.json (the item's image list says nothing about where an image came from). Files of the
        /// media folder are left alone too (reported as kept files). Images applied before this record existed are not known:
        /// they stay until removed by hand once.
        /// </summary>
        private async Task<int> RemoveDroppedImagesAsync(BaseItem item, HashSet<string> wanted)
        {
            var key = item.Id.ToString("N");
            Dictionary<string, List<string>> record;
            lock (AppliedLock)
            {
                try { record = File.Exists(AppliedPath) ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(AppliedPath)) ?? new() : new(); }
                catch (Exception) { record = new Dictionary<string, List<string>>(); }
            }

            var before = new HashSet<string>(record.TryGetValue(key, out var known) ? known : new List<string>(), StringComparer.OrdinalIgnoreCase);
            var removed = 0;
            var metadataPath = item.GetInternalMetadataPath();
            foreach (var type in new[] { ImageType.Primary, ImageType.Thumb, ImageType.Logo, ImageType.Banner, ImageType.Backdrop })
            {
                // highest index first: deleting an image shifts the ones after it
                var list = item.GetImages(type).ToList();
                for (var index = list.Count - 1; index >= 0; index--)
                {
                    var path = list[index].Path;
                    if (before.Count == 0 || string.IsNullOrEmpty(path) || !File.Exists(path) || !path.StartsWith(metadataPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var sha = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
                    if (before.Contains(sha) && !wanted.Contains(sha))
                    {
                        await item.DeleteImageAsync(type, index);
                        removed++;
                        _logger.LogInformation("🔁 [SOURCE REFRESH] 🗑️ '{Name}': {Type} image removed, the source no longer gives it ({File})", item.Name, type, Path.GetFileName(path));
                    }
                }
            }

            lock (AppliedLock)
            {
                try { record = File.Exists(AppliedPath) ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(AppliedPath)) ?? new() : new(); }
                catch (Exception) { record = new Dictionary<string, List<string>>(); }
                if (wanted.Count > 0) { record[key] = wanted.ToList(); } else { record.Remove(key); }
                Directory.CreateDirectory(Path.GetDirectoryName(AppliedPath));
                File.WriteAllText(AppliedPath, System.Text.Json.JsonSerializer.Serialize(record));
            }

            return removed;
        }

        /// <summary>One line per image of the item (type, file, last write): shows in the log whether a refresh really replaced them</summary>
        private static string ImageSignature(BaseItem item)
        {
            try
            {
                return string.Join("; ", item.ImageInfos.Select(i => $"{i.Type}#{Path.GetFileName(i.Path)}@{i.DateModified:HH:mm:ss}"));
            }
            catch (Exception)
            {
                return "?";
            }
        }

        private List<BaseItem> Find(SourceRefreshRequest request, string prefix)
        {
            var wanted = new HashSet<string>(
                (request.Types == null || request.Types.Count == 0 ? DefaultTypes(prefix) : request.Types).Select(t => (t ?? string.Empty).Trim().ToLowerInvariant()));
            var kinds = new List<BaseItemKind>();
            if (wanted.Contains("series")) { kinds.Add(BaseItemKind.Series); }
            if (wanted.Contains("season")) { kinds.Add(BaseItemKind.Season); }
            if (wanted.Contains("episode")) { kinds.Add(BaseItemKind.Episode); }
            // A tagged folder holding only sub-folders is a plain Folder for Jellyfin: it is an album too
            if (wanted.Contains("album")) { kinds.Add(BaseItemKind.PhotoAlbum); kinds.Add(BaseItemKind.Folder); }
            var photos = wanted.Contains("photo");
            if (kinds.Count == 0 && !photos)
            {
                return new List<BaseItem>();
            }

            // Photos and videos are mostly known by their file name in their album: they come with their album, or by their own tag
            List<BaseItem> WithPhotos(List<BaseItem> found, bool exact)
            {
                if (!photos)
                {
                    return found;
                }

                var all = _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.Photo, BaseItemKind.Video }, Recursive = true })
                    .Where(IsAlbumMedia);
                var albums = found.Where(IsAlbum).Select(a => a.Id).ToHashSet();
                return found.Concat(all.Where(p => albums.Contains(p.ParentId) || (!exact && TagsOf(p).Any(t => HasPrefix(t, prefix))))).ToList();
            }

            var tag = (request.Tag ?? string.Empty).Trim().Trim('{', '}');

            // Every candidate of the chosen types: the library query is cheap, the tag test is made here
            var candidates = kinds.Count == 0
                ? new List<BaseItem>()
                : _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = kinds.ToArray(), Recursive = true })
                    .Where(i => i is Series || i is Season || i is Episode || IsAlbum(i)).ToList();
            bool Carries(BaseItem item, bool exact)
            {
                var tags = TagsOf(item);
                return exact
                    ? tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))
                    : tags.Any(t => HasPrefix(t, prefix));
            }

            if (tag.Length == 0)
            {
                return WithPhotos(candidates.Where(i => Carries(i, false)).ToList(), false);
            }

            var roots = candidates.Where(i => Carries(i, true)).ToList();
            if (!request.IncludeChildren)
            {
                return WithPhotos(roots, true);
            }

            var rootIds = roots.Select(r => r.Id).ToHashSet();
            var children = candidates.Where(i => !rootIds.Contains(i.Id) && Carries(i, false) && i.GetAncestorIds().Any(rootIds.Contains));
            return WithPhotos(roots.Concat(children).ToList(), true);
        }

        /// <summary>A photo album, or a plain folder (an album holding only sub-albums): never a series, a season or a library</summary>
        internal static bool IsAlbum(BaseItem item) => item is PhotoAlbum || item?.GetType() == typeof(Folder);

        /// <summary>What an album holds besides sub-albums: a photo, or a plain video (not a film or an episode, which are types of their own, nor an extra)</summary>
        internal static bool IsAlbumMedia(BaseItem item)
            => item is Photo || (item?.GetType() == typeof(Video) && item.OwnerId == Guid.Empty && item.ExtraType == null);

        private static List<string> DefaultTypes(string prefix)
        {
            var sources = (Plugin.Instance?.Configuration?.Sources ?? new List<Configuration.SourceConfiguration>())
                .Where(s => s.Enabled && !s.IsPeople && string.Equals(s.Prefix, prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            var types = new List<string>();
            if (sources.Count == 0 || sources.Any(s => s.ContentVideo)) { types.AddRange(new[] { "series", "season", "episode" }); }
            if (sources.Any(s => s.ContentAlbums)) { types.AddRange(new[] { "album", "photo" }); }
            return types;
        }

        private static bool HasPrefix(string tag, string prefix)
        {
            var dash = tag.IndexOf('-');
            return dash > 0 && string.Equals(tag.Substring(0, dash), prefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The tags of an item: those of its own file or folder name (never of its parents), else its "custom" id (virtual episodes)</summary>
        internal static List<string> TagsOf(BaseItem item)
        {
            var tags = new List<string>();
            if (!string.IsNullOrEmpty(item.Path))
            {
                var name = Path.GetFileName(item.Path.TrimEnd('/', '\\'));
                tags.AddRange(item is Episode ? TagParser.ExtractAll(name) : TagParser.ExtractAll(name).Take(1));
            }

            if (tags.Count == 0)
            {
                var custom = CustomId.Get(item.ProviderIds);
                if (!string.IsNullOrEmpty(custom))
                {
                    tags.Add(custom);
                }
            }

            return tags;
        }
    }
}
