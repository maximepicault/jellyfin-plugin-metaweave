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

        /// <summary>"series", "season", "episode" (empty = all three)</summary>
        public List<string> Types { get; set; } = new List<string>();

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
        public DateTime? StartedAt { get; set; }
        public DateTime? FinishedAt { get; set; }

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

        public SourceRefreshService(ILibraryManager libraryManager, IFileSystem fileSystem, ILogger logger, CustomApiClient api, SourceResolver resolver, IProviderManager providerManager)
        {
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
            Series = s.Series, Seasons = s.Seasons, Episodes = s.Episodes, StartedAt = s.StartedAt, FinishedAt = s.FinishedAt, Message = s.Message
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
            _logger.LogInformation("🔁 [SOURCE REFRESH] Refreshing {Total} item(s) of prefix '{Prefix}' ({Series} series, {Seasons} seasons, {Episodes} episodes), {Images}",
                items.Count, prefix, Status().Series, Status().Seasons, Status().Episodes, replaceImages ? "metadata and images" : "metadata only");
            _ = Task.Run(async () =>
            {
                try
                {
                    // Series first, then seasons, then episodes: a child refreshed after its parent sees the parent's new data
                    foreach (var item in items.OrderBy(i => i is Series ? 0 : (i is Season ? 1 : 2)))
                    {
                        try
                        {
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
                            }

                            if (replaceImages)
                            {
                                await ApplyImagesAsync(item);
                            }

                            lock (StatusLock) { _status.Done++; }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "🔁 [SOURCE REFRESH] ⚠️ Could not refresh '{Name}': {Message}", item.Name, ex.Message);
                            lock (StatusLock) { _status.Failed++; }
                        }
                    }
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
                var source = tag == null ? null : _resolver.GetSourceByPrefix(_resolver.ExtractPrefixFromId(tag));
                var kind = item is Series ? "series" : (item is Season ? "season" : "episode");
                var images = source == null ? new List<ImageInfo>() : (await _api.GetImagesAsync(_resolver.ExtractIdWithoutPrefix(tag), kind, source, CancellationToken.None) ?? new List<ImageInfo>());
                var changed = new List<string>();

                async Task<(byte[] Data, string Mime)> Fetch(string url, bool landscape)
                {
                    var (data, mime, _) = await _api.DownloadImageAsync(url, source, landscape, CancellationToken.None);
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
                    if (data == null || data.Length == 0 || Sha(data) == CurrentSha(type, index))
                    {
                        return;
                    }

                    using var stream = new MemoryStream(data);
                    await _providerManager.SaveImage(item, stream, mime ?? "image/jpeg", type, type == ImageType.Backdrop ? index : (int?)null, CancellationToken.None);
                    changed.Add(type + (type == ImageType.Backdrop ? "#" + index : string.Empty));
                }

                bool IsType(ImageInfo i, string name) => string.Equals(i.Type, name, StringComparison.OrdinalIgnoreCase) || (name == "Primary" && string.Equals(i.Type, "Poster", StringComparison.OrdinalIgnoreCase));
                foreach (var (type, name) in new[] { (ImageType.Primary, "Primary"), (ImageType.Thumb, "Thumb"), (ImageType.Logo, "Logo"), (ImageType.Banner, "Banner") })
                {
                    // An episode without a poster takes its thumbnail as Primary, like the image provider does
                    var first = images.FirstOrDefault(i => IsType(i, name)) ?? (item is Episode && name == "Primary" ? images.FirstOrDefault(i => IsType(i, "Thumb")) : null);
                    if (first != null)
                    {
                        var (data, mime) = await Fetch(first.Url, item is Episode && type is ImageType.Primary or ImageType.Thumb && source.LandscapePortraitThumbs);
                        await Apply(type, 0, data, mime);
                        continue;
                    }

                    // Nothing from the API or the local source: the first image of the matching sub-folder (Primary/, Logo/…) of a series or season folder
                    if (item is Series || item is Season)
                    {
                        var fromFolder = FolderImages.Discover(item.Path).FirstOrDefault(i => i.Type == name);
                        if (fromFolder != null)
                        {
                            await Apply(type, 0, await File.ReadAllBytesAsync(fromFolder.FullPath), FolderImages.MimeOf(fromFolder.File));
                        }
                    }
                }

                if (item is Series || item is Season)
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

                    foreach (var file in FolderImages.Discover(item.Path).Where(i => i.Type == "Backdrop"))
                    {
                        AddBackdrop(await File.ReadAllBytesAsync(file.FullPath), FolderImages.MimeOf(file.File));
                    }

                    for (var i = 0; i < backdrops.Count; i++)
                    {
                        await Apply(ImageType.Backdrop, i, backdrops[i].Data, backdrops[i].Mime);
                    }

                    var extra = backdrops.Count >= 2 ? item.GetImages(ImageType.Backdrop).Skip(backdrops.Count).ToList() : new List<ItemImageInfo>();
                    if (extra.Count > 0)
                    {
                        item.RemoveImages(extra);
                        await item.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, CancellationToken.None);
                        changed.Add("-" + extra.Count + " old backdrop(s)");
                    }
                }

                _logger.LogInformation("🔁 [SOURCE REFRESH] 🖼️ '{Name}': {Result}", item.Name, changed.Count == 0 ? "images already up to date" : "images applied by the plugin: " + string.Join(", ", changed));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "🔁 [SOURCE REFRESH] ⚠️ Could not apply the images of '{Name}': {Message}", item.Name, ex.Message);
            }
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
                (request.Types == null || request.Types.Count == 0 ? new List<string> { "series", "season", "episode" } : request.Types).Select(t => (t ?? string.Empty).Trim().ToLowerInvariant()));
            var kinds = new List<BaseItemKind>();
            if (wanted.Contains("series")) { kinds.Add(BaseItemKind.Series); }
            if (wanted.Contains("season")) { kinds.Add(BaseItemKind.Season); }
            if (wanted.Contains("episode")) { kinds.Add(BaseItemKind.Episode); }
            if (kinds.Count == 0)
            {
                return new List<BaseItem>();
            }

            var tag = (request.Tag ?? string.Empty).Trim().Trim('{', '}');

            // Every candidate of the chosen types: the library query is cheap, the tag test is made here
            var candidates = _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = kinds.ToArray(), Recursive = true });
            bool Carries(BaseItem item, bool exact)
            {
                var tags = TagsOf(item);
                return exact
                    ? tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))
                    : tags.Any(t => HasPrefix(t, prefix));
            }

            if (tag.Length == 0)
            {
                return candidates.Where(i => Carries(i, false)).ToList();
            }

            var roots = candidates.Where(i => Carries(i, true)).ToList();
            if (!request.IncludeChildren)
            {
                return roots;
            }

            var rootIds = roots.Select(r => r.Id).ToHashSet();
            var children = candidates.Where(i => !rootIds.Contains(i.Id) && Carries(i, false) && i.GetAncestorIds().Any(rootIds.Contains));
            return roots.Concat(children).ToList();
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
