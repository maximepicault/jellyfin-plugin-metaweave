using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>One photo of an episode's gallery</summary>
    public sealed class EpisodePhoto
    {
        /// <summary>Opaque identifier inside the episode: the file name for a folder photo, ".local-…" for a local source's</summary>
        public string Key { get; init; }

        public string Name { get; init; }

        /// <summary>The file on disk (folder photo, uploaded local image); null for a web address</summary>
        public PhotoFile File { get; init; }

        /// <summary>The web address typed in a local source; null for a file</summary>
        public string Url { get; init; }
    }

    /// <summary>
    /// The photos of an episode: first the ones of type "Photo" in the local source(s) naming the episode's tag (gallery
    /// order), then the numbered folder next to its video file (<see cref="EpisodePhotoFolders"/>). A missing episode has
    /// no file but still shows its local source's photos. Nothing here ever creates or changes a library item.
    /// </summary>
    public class EpisodePhotoService
    {
        /// <summary>The local source gallery type that is only shown in the photo gallery, never applied as an image</summary>
        public const string PhotoType = LocalSourceStore.PhotoType;

        private const string LocalKeyPrefix = ".local-";   // a folder photo can't start with a dot (hidden files are skipped)

        private readonly ILibraryManager _library;
        private readonly IUserManager _users;
        private readonly LocalSourceStore _localStore;
        private readonly ILogger<EpisodePhotoService> _logger;

        public EpisodePhotoService(ILibraryManager library, IUserManager users, LocalSourceStore localStore, ILogger<EpisodePhotoService> logger)
        {
            _library = library;
            _users = users;
            _localStore = localStore;
            _logger = logger;
        }

        public static bool Enabled => Plugin.Instance?.Configuration?.EnableEpisodePhotos ?? false;

        /// <summary>The item when the caller may see it (library access, parental rating, tags), else null</summary>
        public BaseItem GetVisibleItem(Guid itemId, Guid userId, bool isApiKey)
        {
            if (itemId == Guid.Empty)
            {
                return null;
            }

            if (isApiKey)
            {
                return _library.GetItemById<BaseItem>(itemId);
            }

            // ILibraryManager skips the access check when the user is null: an empty or unknown user must be refused here
            var user = userId == Guid.Empty ? null : _users.GetUserById(userId);
            return user == null ? null : _library.GetItemById<BaseItem>(itemId, user);
        }

        public List<EpisodePhoto> List(Episode episode)
        {
            if (episode == null || !Enabled)
            {
                return new List<EpisodePhoto>();
            }

            var photos = LocalPhotos(episode);
            var folder = EpisodePhotoFolders.Find(episode.Path, episode.IndexNumber, episode.IndexNumberEnd);
            photos.AddRange(EpisodePhotoFolders.List(folder).Select(f => new EpisodePhoto { Key = f.Name, Name = f.Name, File = f }));
            _logger.LogDebug("📷 [PHOTOS] '{Episode}': {Count} photo(s), folder: {Folder}", episode.Name, photos.Count, folder ?? "(none)");
            return photos;
        }

        /// <summary>The photo of the episode with exactly this key (the key is untrusted), or null</summary>
        public EpisodePhoto Find(Episode episode, string key)
            => string.IsNullOrEmpty(key) ? null : List(episode).FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.Ordinal));

        private List<EpisodePhoto> LocalPhotos(Episode episode)
        {
            var result = new List<EpisodePhoto>();
            var keys = string.IsNullOrEmpty(episode.Path) ? new List<string>() : TagParser.ExtractAll(Path.GetFileName(episode.Path)).ToList();
            var ownId = CustomId.Get(episode.ProviderIds);
            if (!string.IsNullOrEmpty(ownId) && !keys.Contains(ownId, StringComparer.OrdinalIgnoreCase))
            {
                keys.Add(ownId);
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tag in keys)
            {
                if (_localStore.FindOverride(tag, "episode")?.Node is not LocalEpisode node)
                {
                    continue;
                }

                foreach (var image in (node.Gallery ?? new List<LocalImage>()).Where(g => g.Type == PhotoType))
                {
                    if (!seen.Add(image.Url))
                    {
                        continue;
                    }

                    if (LocalSourceStore.IsLocalImageUrl(image.Url))
                    {
                        var path = LocalSourceStore.ImageFile(image.Url);
                        if (path == null)
                        {
                            continue;
                        }

                        var info = new FileInfo(path);
                        result.Add(new EpisodePhoto
                        {
                            Key = LocalKeyPrefix + info.Name,
                            Name = image.Name ?? info.Name,
                            File = new PhotoFile(info.Name, info.FullName, info.Length, info.LastWriteTimeUtc)
                        });
                    }
                    else if (Uri.TryCreate(image.Url, UriKind.Absolute, out var address) && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps))
                    {
                        result.Add(new EpisodePhoto { Key = LocalKeyPrefix + "web-" + result.Count, Name = image.Name ?? Path.GetFileName(address.LocalPath), Url = image.Url });
                    }
                }
            }

            return result;
        }
    }
}
