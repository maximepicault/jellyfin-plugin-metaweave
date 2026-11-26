using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>A photo file of an episode folder</summary>
    public sealed record PhotoFile(string Name, string FullPath, long Length, DateTime LastWriteTimeUtc);

    /// <summary>
    /// The photo folder of an episode: a sub-folder of the video's directory named after the episode number ("3", "03",
    /// "003", "E03") or an episode range ("03-04"). Photos are read straight from disk, no library item is created.
    /// This is the only place that turns a client-supplied file name into a path: the name is looked up in the real
    /// directory listing, never concatenated.
    /// </summary>
    public static class EpisodePhotoFolders
    {
        private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".png"] = "image/png",
            [".webp"] = "image/webp",
            [".gif"] = "image/gif",
            [".avif"] = "image/avif"
        };

        // Leading zeros are free ("0000003"): a tag's digits can give an episode a large number, up to 9 digits fit an int
        private static readonly Regex FolderName = new(
            @"^[Ee]?0*(?<start>[0-9]{1,9})(?:\s*-\s*[Ee]?0*(?<end>[0-9]{1,9}))?$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>The MIME type of an accepted photo file name, null for any other file</summary>
        public static string ContentTypeOf(string fileName)
            => ContentTypes.TryGetValue(Path.GetExtension(fileName ?? string.Empty), out var type) ? type : null;

        /// <summary>"3", "03", "E03", "03-04", "E03 - E04" → the episode range a folder name stands for</summary>
        public static bool TryParseFolderName(string name, out int start, out int end)
        {
            start = end = 0;
            var match = FolderName.Match((name ?? string.Empty).Trim());
            if (!match.Success || !int.TryParse(match.Groups["start"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out start))
            {
                return false;
            }

            end = start;
            return !match.Groups["end"].Success
                || (int.TryParse(match.Groups["end"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out end) && end >= start);
        }

        private static readonly HashSet<string> VideoExtensions = new(new[]
        {
            ".3gp", ".asf", ".avi", ".divx", ".flv", ".img", ".iso", ".m2ts", ".m2v", ".m4v", ".mk3d", ".mkv", ".mov", ".mp4", ".mpeg",
            ".mpg", ".mts", ".ogm", ".ogv", ".rm", ".rmvb", ".strm", ".ts", ".vob", ".webm", ".wmv", ".wtv"
        }, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// A folder named like a photo folder, holding at least one photo, no video and no sub-folder (hidden ones aside):
        /// never a season folder with something to play
        /// </summary>
        public static bool IsPhotoFolderWithoutVideo(string folder)
        {
            if (!TryParseFolderName(Path.GetFileName(folder), out _, out _))
            {
                return false;
            }

            try
            {
                var directory = new DirectoryInfo(folder);
                if (!directory.Exists || directory.EnumerateDirectories().Any(d => !d.Name.StartsWith('.')))
                {
                    return false;
                }

                var files = directory.EnumerateFiles().Where(f => !f.Name.StartsWith('.')).ToList();
                return files.Any(f => ContentTypeOf(f.Name) != null) && !files.Any(f => VideoExtensions.Contains(f.Extension));
            }
            catch (Exception ex) when (IsAccessError(ex))
            {
                return false;
            }
        }

        /// <summary>The photo folder of the episode(s) of a video file (Jellyfin's IndexNumber / IndexNumberEnd), or null</summary>
        public static string Find(string videoPath, int? indexNumber, int? indexNumberEnd)
        {
            if (string.IsNullOrEmpty(videoPath) || indexNumber is not int first || first < 0)
            {
                return null;
            }

            var last = indexNumberEnd is int e && e > first ? e : first;
            var parent = Path.GetDirectoryName(videoPath);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            {
                return null;
            }

            DirectoryInfo[] folders;
            try
            {
                folders = new DirectoryInfo(parent).GetDirectories();
            }
            catch (Exception ex) when (IsAccessError(ex))
            {
                return null;
            }

            return folders
                .Where(d => d.LinkTarget == null)
                .Select(d => (Folder: d, Rank: TryParseFolderName(d.Name, out var s, out var en) ? Rank(s, en, first, last) : int.MaxValue, Start: s))
                .Where(c => c.Rank < int.MaxValue)
                .OrderBy(c => c.Rank)
                .ThenBy(c => c.Start)
                .ThenBy(c => c.Folder.Name.Length)
                .ThenBy(c => c.Folder.Name, StringComparer.Ordinal)
                .Select(c => c.Folder.FullName)
                .FirstOrDefault();
        }

        /// <summary>
        /// The accepted photos directly inside a folder, naturally sorted ("img2" before "img10"). Hidden files,
        /// sub-folders and symbolic links that leave the folder are skipped.
        /// </summary>
        public static List<PhotoFile> List(string folder)
        {
            var photos = new List<PhotoFile>();
            if (string.IsNullOrEmpty(folder))
            {
                return photos;
            }

            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            var directory = new DirectoryInfo(root);
            if (!directory.Exists || directory.LinkTarget != null)
            {
                return photos;
            }

            try
            {
                foreach (var file in directory.GetFiles())
                {
                    if (file.Name.StartsWith('.') || ContentTypeOf(file.Name) == null)
                    {
                        continue;
                    }

                    var target = ResolveInside(file, root);
                    if (target != null)
                    {
                        photos.Add(new PhotoFile(file.Name, target.FullName, target.Length, target.LastWriteTimeUtc));
                    }
                }
            }
            catch (Exception ex) when (IsAccessError(ex))
            {
                return new List<PhotoFile>();
            }

            photos.Sort((a, b) => CompareNatural(a.Name, b.Name));
            return photos;
        }

        /// <summary>Compares names so that the numbers inside them sort as numbers</summary>
        public static int CompareNatural(string x, string y)
        {
            x ??= string.Empty;
            y ??= string.Empty;
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
                {
                    int si = i, sj = j;
                    while (i < x.Length && char.IsAsciiDigit(x[i])) { i++; }
                    while (j < y.Length && char.IsAsciiDigit(y[j])) { j++; }
                    var a = x.AsSpan(si, i - si).TrimStart('0');
                    var b = y.AsSpan(sj, j - sj).TrimStart('0');
                    var number = a.Length != b.Length ? a.Length.CompareTo(b.Length) : a.SequenceCompareTo(b);
                    if (number != 0) { return number; }
                }
                else
                {
                    var letter = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                    if (letter != 0) { return letter; }
                    i++;
                    j++;
                }
            }

            var rest = (x.Length - i).CompareTo(y.Length - j);
            return rest != 0 ? rest : string.CompareOrdinal(x, y);
        }

        // Lower is better; int.MaxValue: the folder does not match
        private static int Rank(int start, int end, int first, int last)
        {
            if (start == first && end == last && first != last) { return 0; }   // "03-04" for a file holding E03-E04
            if (start == first && end == first) { return 1; }                   // "03"
            if (start <= first && first <= end) { return 2; }                   // "03-04" for E03 or E04 alone
            if (start <= last && first <= end) { return 3; }                    // "04" for a file holding E03-E04
            return int.MaxValue;
        }

        // The file itself, or the final target of a symbolic link when it is a regular file inside the folder
        private static FileInfo ResolveInside(FileInfo file, string root)
        {
            if (file.LinkTarget == null)
            {
                return file;
            }

            try
            {
                if (file.ResolveLinkTarget(returnFinalTarget: true) is not FileInfo target || !target.Exists)
                {
                    return null;
                }

                var path = Path.GetFullPath(target.FullName);
                var parent = Path.GetDirectoryName(path);
                return parent != null && string.Equals(Path.TrimEndingDirectorySeparator(parent), root, StringComparison.Ordinal)
                    ? new FileInfo(path)
                    : null;
            }
            catch (Exception ex) when (IsAccessError(ex))
            {
                return null;
            }
        }

        private static bool IsAccessError(Exception ex)
            => ex is IOException or UnauthorizedAccessException or System.Security.SecurityException;
    }
}
