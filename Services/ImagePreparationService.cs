using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>
    /// Makes in advance the picture sizes the Projection theme's photo screens ask for, so that a first visit waits for
    /// nothing: Jellyfin's own image processor writes them in its cache, under the same names as for a browser's request.
    /// The sizes and the quality must stay those of theme/projection-photos.js (CARD, TILE, SCREEN, WIDE_SCREEN).
    /// </summary>
    public class ImagePreparationService
    {
        private const int Quality = 90;
        private static readonly (int Width, int? Height) Card = (640, null);
        private static readonly (int Width, int? Height) Tile = (1200, 360);
        private static readonly (int Width, int? Height) Screen = (1920, 1080);
        private static readonly (int Width, int? Height) WideScreen = (2560, 1440);

        // What a current browser accepts: Jellyfin picks the format of its answer among these
        private static readonly ImageFormat[] BrowserFormats = { ImageFormat.Webp, ImageFormat.Jpg, ImageFormat.Png };

        // Jellyfin's cache cleaning removes files that have not been written for a month: a prepared one is dated again
        private static readonly TimeSpan RedateAfter = TimeSpan.FromDays(20);

        private readonly ILibraryManager _library;
        private readonly IImageProcessor _images;
        private readonly IApplicationPaths _paths;
        private readonly ILogger _logger;

        public ImagePreparationService(ILibraryManager library, IImageProcessor images, IApplicationPaths paths, ILogger logger)
        {
            _library = library;
            _images = images;
            _paths = paths;
            _logger = logger;
        }

        private record Picture(BaseItem Item, ImageType Type, (int Width, int? Height) Size);

        /// <summary>The pictures of every photo library (« home videos and photos » holding at least one photo)</summary>
        private List<Picture> Wanted(bool wideScreens)
        {
            var wanted = new Dictionary<(Guid, ImageType, int), Picture>();
            void Add(BaseItem item, ImageType type, (int Width, int? Height) size)
            {
                if (item?.GetImageInfo(type, 0) is { IsLocalFile: true })
                {
                    wanted[(item.Id, type, size.Width * 10000 + (size.Height ?? 0))] = new Picture(item, type, size);
                }
            }

            void Large(BaseItem item, ImageType type)
            {
                Add(item, type, Screen);
                if (wideScreens)
                {
                    Add(item, type, WideScreen);
                }
            }

            foreach (var library in _library.GetVirtualFolders().Where(f => f.CollectionType == CollectionTypeOptions.homevideos))
            {
                if (!Guid.TryParse(library.ItemId, out var id) || _library.GetItemById(id) is not Folder root)
                {
                    continue;
                }

                var items = root.GetItemList(new InternalItemsQuery
                {
                    Recursive = true,
                    IsVirtualItem = false,
                    IncludeItemTypes = new[] { BaseItemKind.Photo, BaseItemKind.Video, BaseItemKind.PhotoAlbum, BaseItemKind.Folder }
                });
                // A library of videos only keeps jellyfin-web's own screens, which ask for other sizes
                if (!items.Any(i => i is Photo))
                {
                    continue;
                }

                var media = items.Where(i => !i.IsFolder && i.OwnerId == Guid.Empty).OrderBy(i => i is Photo ? 0 : 1).ThenBy(i => i.SortName, StringComparer.OrdinalIgnoreCase).ToList();
                // The albums' cards and head pictures first: they are what a visit starts with
                foreach (var album in items.Where(i => i.IsFolder))
                {
                    var cover = album.GetImageInfo(ImageType.Primary, 0) != null;
                    var backdrop = album.GetImageInfo(ImageType.Backdrop, 0) != null;
                    if (cover)
                    {
                        Add(album, ImageType.Primary, Card);
                    }

                    if (backdrop)
                    {
                        Add(album, ImageType.Backdrop, Card);
                        Large(album, ImageType.Backdrop);
                    }
                    else if (cover)
                    {
                        Large(album, ImageType.Primary);
                    }

                    // Without a cover the screens show a photo from inside, in the card's size
                    if (!cover && !string.IsNullOrEmpty(album.Path))
                    {
                        var inside = album.Path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                        Add(media.FirstOrDefault(m => m.Path?.StartsWith(inside, StringComparison.Ordinal) == true && m.GetImageInfo(ImageType.Primary, 0) != null), ImageType.Primary, Card);
                    }
                }

                foreach (var item in media)
                {
                    Add(item, ImageType.Primary, Tile);
                }

                foreach (var item in media)
                {
                    Large(item, ImageType.Primary);
                }
            }

            return wanted.Values.ToList();
        }

        public async Task PrepareAsync(bool wideScreens, IProgress<double> progress, CancellationToken cancellationToken)
        {
            var pictures = Wanted(wideScreens);
            var started = DateTime.UtcNow.AddSeconds(-2);
            var cache = Path.GetFullPath(_paths.CachePath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            int done = 0, made = 0, failed = 0;
            long bytes = 0;
            // A share of the processor only: the server stays usable meanwhile
            var parallel = Math.Max(1, Environment.ProcessorCount / 4);
            _logger.LogInformation("🏞️ [IMAGE PREPARE] {Count} picture(s) to have ready, {Parallel} at a time (wide screens: {Wide})", pictures.Count, parallel, wideScreens);

            await Parallel.ForEachAsync(pictures, new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = cancellationToken }, async (picture, token) =>
            {
                try
                {
                    var (path, _, _) = await _images.ProcessImage(new ImageProcessingOptions
                    {
                        Item = picture.Item,
                        ItemId = picture.Item.Id,
                        Image = picture.Item.GetImageInfo(picture.Type, 0),
                        ImageIndex = 0,
                        MaxWidth = picture.Size.Width,
                        MaxHeight = picture.Size.Height,
                        Quality = Quality,
                        SupportedOutputFormats = BrowserFormats
                    });

                    // The original comes back when there is nothing to make: only the cache's own files are looked at, never a photo
                    if (path != null && Path.GetFullPath(path).StartsWith(cache, StringComparison.Ordinal))
                    {
                        var file = new FileInfo(path);
                        Interlocked.Add(ref bytes, file.Length);
                        if (file.LastWriteTimeUtc >= started)
                        {
                            Interlocked.Increment(ref made);
                        }
                        else if (DateTime.UtcNow - file.LastWriteTimeUtc > RedateAfter)
                        {
                            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Interlocked.Increment(ref failed);
                    _logger.LogWarning("🏞️ [IMAGE PREPARE] ⚠️ '{Name}' ({Type} {Width}): {Message}", picture.Item.Name, picture.Type, picture.Size.Width, ex.Message);
                }

                progress?.Report(Interlocked.Increment(ref done) * 100.0 / pictures.Count);
            });

            progress?.Report(100);
            _logger.LogInformation("🏞️ [IMAGE PREPARE] ✅ {Done} picture(s) ready, {Made} newly made, {Failed} failed, {Size} MB in the cache", done - failed, made, failed, bytes / (1024 * 1024));
        }
    }
}
