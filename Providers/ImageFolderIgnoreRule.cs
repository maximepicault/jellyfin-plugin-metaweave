using System;
using System.IO;
using Jellyfin.Plugin.CustomMetadata.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Resolvers;
using MediaBrowser.Model.IO;

namespace Jellyfin.Plugin.CustomMetadata.Providers
{
    /// <summary>
    /// Keeps the image sub-folders (Primary, Backdrop, Logo, Banner, Thumb…) of a series or season folder out of the library:
    /// without this rule Jellyfin turns each of them into an empty "season". The plugin reads them itself (FolderImages).
    /// Only applies below a series or season folder whose own name carries a {prefix-id} tag, so nobody else's folders are touched.
    /// With the episode photo gallery on, a numbered folder of photos without any video ("03", "03-04") is kept out too,
    /// below any series or season: it would become an empty season (episodes directly in the series folder) or a folder item.
    /// Discovered by Jellyfin like the providers.
    /// </summary>
    public class ImageFolderIgnoreRule : IResolverIgnoreRule
    {
        public bool ShouldIgnore(FileSystemMetadata fileInfo, BaseItem parent)
        {
            if (fileInfo == null || !fileInfo.IsDirectory || !(parent is Series || parent is Season) || string.IsNullOrEmpty(parent.Path))
            {
                return false;
            }

            if (EpisodePhotoService.Enabled && EpisodePhotoFolders.IsPhotoFolderWithoutVideo(fileInfo.FullName))
            {
                return true;
            }

            return FolderImages.TypeOfFolder(fileInfo.Name) != null
                && !string.IsNullOrEmpty(TagParser.Extract(TagParser.GetOwnName(parent.Path)));
        }
    }
}
