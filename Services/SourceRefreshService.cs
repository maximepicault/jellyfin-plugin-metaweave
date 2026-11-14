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
                            if (replaceImages && (item is Series || item is Season))
                            {
                                await ApplyAllBackdropsAsync(item);
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
        /// Backdrops are a carousel: a normal refresh keeps only as many as the library allows (usually one), so when an element has several
        /// (API, local gallery, Backdrop/ sub-folder) all of them are saved, in that order, replacing the item's backdrops.
        /// Nothing is touched when there are fewer than two (the refresh already applied the only one).
        /// </summary>
        private async Task ApplyAllBackdropsAsync(BaseItem item)
        {
            try
            {
                var tag = TagsOf(item).FirstOrDefault();
                var source = tag == null ? null : _resolver.GetSourceByPrefix(_resolver.ExtractPrefixFromId(tag));
                var found = new List<(byte[] Data, string Mime)>();
                var seen = new HashSet<string>();
                void Add(byte[] data, string mime)
                {
                    if (data != null && data.Length > 0 && seen.Add(Convert.ToHexString(SHA256.HashData(data)))) { found.Add((data, mime)); }
                }

                if (source != null)
                {
                    var images = await _api.GetImagesAsync(_resolver.ExtractIdWithoutPrefix(tag), item is Series ? "series" : "season", source, CancellationToken.None) ?? new List<ImageInfo>();
                    foreach (var url in images.Where(i => string.Equals(i.Type, "Backdrop", StringComparison.OrdinalIgnoreCase)).Select(i => i.Url).Distinct())
                    {
                        var (data, mime, _) = await _api.DownloadImageAsync(url, source, false, CancellationToken.None);
                        Add(data, mime);
                    }
                }

                foreach (var file in FolderImages.Discover(item.Path).Where(i => i.Type == "Backdrop"))
                {
                    Add(await File.ReadAllBytesAsync(file.FullPath), FolderImages.MimeOf(file.File));
                }

                if (found.Count < 2)
                {
                    return;
                }

                for (var i = 0; i < found.Count; i++)
                {
                    using var stream = new MemoryStream(found[i].Data);
                    await _providerManager.SaveImage(item, stream, found[i].Mime ?? "image/jpeg", ImageType.Backdrop, i, CancellationToken.None);
                }

                var extra = item.GetImages(ImageType.Backdrop).Skip(found.Count).ToList();
                if (extra.Count > 0)
                {
                    item.RemoveImages(extra);
                    await item.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, CancellationToken.None);
                }

                _logger.LogInformation("🔁 [SOURCE REFRESH] 🖼️ {Count} backdrops applied to '{Name}'", found.Count, item.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "🔁 [SOURCE REFRESH] ⚠️ Could not apply all the backdrops of '{Name}': {Message}", item.Name, ex.Message);
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
