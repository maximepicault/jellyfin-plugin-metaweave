using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>
    /// Extraction of the {PREFIX-ID} tag from file/folder names.
    /// Single place for the pattern: the providers and the missing-episode analysis must
    /// all recognise exactly the same tags.
    /// Always apply it to the item's OWN name (GetOwnName), never to the full path:
    /// a season or an episode would otherwise pick up the series ID from a parent folder.
    /// </summary>
    public static class TagParser
    {
        /// <summary>
        /// Generic pattern: captures ANY prefix ("mv1-123", "yt-SOURCE-YT", "custom-conf_2024")
        /// </summary>
        public const string Pattern = @"\{([\w\-]+-[\w\-]+)\}";

        private static readonly Regex TagRegex = new Regex(Pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex StripRegex = new Regex(@"\s*" + Pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Own name of a file or folder: "/media/Series/Season {mv-1}/" → "Season {mv-1}".
        /// Returns the input unchanged if no name can be extracted.
        /// </summary>
        public static string GetOwnName(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.IsNullOrEmpty(name) ? path : name;
        }

        /// <summary>
        /// "Episode 01 {mv1-123}.mkv" → "mv1-123" (without braces), null if no tag
        /// </summary>
        public static string Extract(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            var match = TagRegex.Match(name);
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>
        /// All the tags of the name, in order, without duplicates (case-insensitive):
        /// "Ep {dm-1} {dm-2}.mkv" → ["dm-1", "dm-2"]. Used for files that bundle several API episodes;
        /// the first one is the reference ID (same result as Extract).
        /// </summary>
        public static List<string> ExtractAll(string name)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(name))
            {
                return result;
            }

            foreach (Match match in TagRegex.Matches(name))
            {
                var id = match.Groups[1].Value;
                if (!result.Contains(id, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(id);
                }
            }

            return result;
        }

        /// <summary>
        /// "Source A {mv1-sourceA}" → "Source A"
        /// </summary>
        public static string Strip(string name)
        {
            return string.IsNullOrEmpty(name) ? name : StripRegex.Replace(name, string.Empty).Trim();
        }
    }
}
