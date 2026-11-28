using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>
    /// The photos an episode takes from the photo albums carrying its identifier (SourceConfiguration.LinkEpisodeAlbums).
    /// An episode's identifiers are every tag of its file name and its "custom" id; an album's is the tag of its folder's
    /// own name, else its "custom" id. Only the photos directly inside the album count (no sub-album, no video). Reads the
    /// library only: no source is asked, nothing is written.
    /// </summary>
    public class EpisodeAlbumPhotos
    {
        // neither a folder photo (no leading dot) nor a local source's (".local-")
        private const string KeyPrefix = ".album-";

        private readonly ILibraryManager _library;

        public EpisodeAlbumPhotos(ILibraryManager library)
        {
            _library = library;
        }

        public static bool IsAlbumKey(string key) => key != null && key.StartsWith(KeyPrefix, StringComparison.Ordinal);

        /// <summary>The photos of the episode's albums, album by album, that <paramref name="canSee"/> accepts</summary>
        public List<EpisodePhoto> List(Episode episode, Func<BaseItem, bool> canSee)
        {
            var result = new List<EpisodePhoto>();
            var ids = LinkedIds(episode);
            if (ids.Count == 0)
            {
                return result;
            }

            var albums = _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.PhotoAlbum, BaseItemKind.Folder },
                Recursive = true
            })
                .Where(a => SourceRefreshService.IsAlbum(a) && !a.IsVirtualItem)
                .Select(a => (Album: a, Rank: ids.FindIndex(id => string.Equals(id, IdentifierOf(a), StringComparison.OrdinalIgnoreCase))))
                .Where(a => a.Rank >= 0)
                .OrderBy(a => a.Rank)   // the order of the file name's tags
                .ThenBy(a => a.Album.Path ?? string.Empty, Comparer<string>.Create(EpisodePhotoFolders.CompareNatural))
                .Select(a => a.Album)
                .Where(canSee);

            foreach (var album in albums)
            {
                var photos = _library.GetItemList(new InternalItemsQuery
                {
                    ParentId = album.Id,
                    IncludeItemTypes = new[] { BaseItemKind.Photo }
                })
                    .OrderBy(p => Path.GetFileName(p.Path) ?? string.Empty, Comparer<string>.Create(EpisodePhotoFolders.CompareNatural))
                    .Where(canSee);
                result.AddRange(photos.Select(ToPhoto).Where(p => p != null));
            }

            return result;
        }

        /// <summary>
        /// The album photo with this (untrusted) key, when it still belongs to an album of the episode and
        /// <paramref name="canSee"/> accepts both; null otherwise. No album search: one thumbnail costs two item reads.
        /// </summary>
        public EpisodePhoto Find(Episode episode, string key, Func<BaseItem, bool> canSee)
        {
            if (!IsAlbumKey(key) || !Guid.TryParseExact(key.AsSpan(KeyPrefix.Length), "N", out var photoId) || photoId == Guid.Empty)
            {
                return null;
            }

            var ids = LinkedIds(episode);
            if (ids.Count == 0
                || _library.GetItemById(photoId) is not Photo item
                || _library.GetItemById(item.ParentId) is not { } album
                || !SourceRefreshService.IsAlbum(album)
                || !ids.Contains(IdentifierOf(album), StringComparer.OrdinalIgnoreCase)
                || !canSee(album)
                || !canSee(item))
            {
                return null;
            }

            var photo = ToPhoto(item);
            return photo?.Key == key ? photo : null;
        }

        /// <summary>The episode's identifiers whose prefix belongs to an enabled source that links albums</summary>
        private static List<string> LinkedIds(Episode episode)
        {
            var sources = Plugin.Instance?.Configuration?.Sources?
                .Where(s => s.Enabled && !s.IsPeople && s.LinkEpisodeAlbums && !string.IsNullOrEmpty(s.Prefix))
                .ToList();
            if (sources == null || sources.Count == 0)
            {
                return new List<string>();
            }

            return EpisodePhotoService.IdentifiersOf(episode)
                .Where(id => sources.Any(s => id.StartsWith(s.Prefix + "-", StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        private static string IdentifierOf(BaseItem album)
        {
            var tag = string.IsNullOrEmpty(album.Path) ? null : TagParser.Extract(TagParser.GetOwnName(album.Path));
            return tag ?? CustomId.Get(album.ProviderIds);
        }

        // null for a file the gallery cannot show (format a browser does not read, file gone)
        private static EpisodePhoto ToPhoto(BaseItem item)
        {
            if (item.IsVirtualItem || string.IsNullOrEmpty(item.Path) || EpisodePhotoFolders.ContentTypeOf(item.Path) == null)
            {
                return null;
            }

            var info = new FileInfo(item.Path);
            if (info.LinkTarget != null)
            {
                // the library already shows this file: size and date are the target's
                info = info.ResolveLinkTarget(returnFinalTarget: true) as FileInfo;
            }

            if (info == null || !info.Exists)
            {
                return null;
            }

            return new EpisodePhoto
            {
                Key = KeyPrefix + item.Id.ToString("N", CultureInfo.InvariantCulture),
                Name = Path.GetFileName(item.Path),
                File = new PhotoFile(Path.GetFileName(item.Path), info.FullName, info.Length, info.LastWriteTimeUtc)
            };
        }
    }
}
