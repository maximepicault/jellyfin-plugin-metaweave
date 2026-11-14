using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>An image found in a sub-folder of a series or season folder</summary>
    public class FolderImage
    {
        /// <summary>Primary, Backdrop, Logo, Banner or Thumb</summary>
        public string Type { get; set; }

        /// <summary>Name of the sub-folder as found on disk</summary>
        public string Folder { get; set; }

        public string File { get; set; }

        public string FullPath { get; set; }

        /// <summary>"Folder/File": identifies the image when it is imported into a gallery</summary>
        public string Name => Folder + "/" + File;
    }

    /// <summary>
    /// Images kept in sub-folders of a series or season folder, named after the Jellyfin image types
    /// (Primary, Backdrop, Logo, Banner, Thumb; a few usual French names too). Offered in Jellyfin's "Modifier les images"
    /// so that the images do not have to sit, unreadable, among the files of the series folder.
    /// </summary>
    public static class FolderImages
    {
        private static readonly Dictionary<string, string> FolderTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["primary"] = "Primary", ["principal"] = "Primary", ["poster"] = "Primary", ["posters"] = "Primary", ["affiche"] = "Primary", ["affiches"] = "Primary",
            ["backdrop"] = "Backdrop", ["backdrops"] = "Backdrop", ["background"] = "Backdrop", ["backgrounds"] = "Backdrop", ["fond"] = "Backdrop", ["fonds"] = "Backdrop",
            ["logo"] = "Logo", ["logos"] = "Logo",
            ["banner"] = "Banner", ["banners"] = "Banner", ["bannière"] = "Banner", ["banniere"] = "Banner", ["bannières"] = "Banner", ["bannieres"] = "Banner",
            ["thumb"] = "Thumb", ["thumbs"] = "Thumb", ["thumbnail"] = "Thumb", ["thumbnails"] = "Thumb", ["miniature"] = "Thumb", ["miniatures"] = "Thumb"
        };

        private static readonly HashSet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp", ".gif" };

        /// <summary>The type a sub-folder name stands for, null when it is not an image folder</summary>
        public static string TypeOfFolder(string folderName) => folderName != null && FolderTypes.TryGetValue(folderName.Trim(), out var type) ? type : null;

        /// <summary>Every image of the image sub-folders of a folder (sorted by folder then file name). Empty when the folder is missing.</summary>
        public static List<FolderImage> Discover(string directory)
        {
            var result = new List<FolderImage>();
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return result;
            }

            try
            {
                foreach (var sub in Directory.GetDirectories(directory).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    var folder = Path.GetFileName(sub);
                    var type = TypeOfFolder(folder);
                    if (type == null)
                    {
                        continue;
                    }

                    foreach (var file in Directory.GetFiles(sub).Where(f => Extensions.Contains(Path.GetExtension(f))).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    {
                        result.Add(new FolderImage { Type = type, Folder = folder, File = Path.GetFileName(file), FullPath = file });
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // unreadable folder: nothing offered
            }

            return result;
        }

        /// <summary>
        /// The file behind a (folder, file) pair of a directory, only if it really is an image in an image sub-folder
        /// (no path separators, no ".." : the address is public, so it must not reach anything else).
        /// </summary>
        public static bool TryResolve(string directory, string folder, string file, out string fullPath, out string mime)
        {
            fullPath = null;
            mime = null;
            if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(file) || TypeOfFolder(folder) == null
                || folder.IndexOfAny(new[] { '/', '\\' }) >= 0 || file.IndexOfAny(new[] { '/', '\\' }) >= 0 || folder == ".." || file == "..")
            {
                return false;
            }

            var found = Discover(directory).FirstOrDefault(i => string.Equals(i.Folder, folder, StringComparison.OrdinalIgnoreCase) && string.Equals(i.File, file, StringComparison.OrdinalIgnoreCase));
            if (found == null)
            {
                return false;
            }

            fullPath = found.FullPath;
            mime = MimeOf(found.File);
            return true;
        }

        public static string MimeOf(string file)
        {
            switch (Path.GetExtension(file).ToLowerInvariant())
            {
                case ".png": return "image/png";
                case ".webp": return "image/webp";
                case ".gif": return "image/gif";
                default: return "image/jpeg";
            }
        }
    }
}
