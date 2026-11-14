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
    /// Discovered by Jellyfin like the providers.
    /// </summary>
    public class ImageFolderIgnoreRule : IResolverIgnoreRule
    {
        public bool ShouldIgnore(FileSystemMetadata fileInfo, BaseItem parent)
        {
            if (fileInfo == null || !fileInfo.IsDirectory || FolderImages.TypeOfFolder(fileInfo.Name) == null)
            {
                return false;
            }

            if (!(parent is Series || parent is Season) || string.IsNullOrEmpty(parent.Path))
            {
                return false;
            }

            return !string.IsNullOrEmpty(TagParser.Extract(TagParser.GetOwnName(parent.Path)));
        }
    }
}
