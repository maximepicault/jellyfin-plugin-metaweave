using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>
    /// Recognises an "overview" that is really just a list of tags/genres:
    /// - hashtags only: "#travel #mountain #summer"
    /// - items separated by , ; | • · or line breaks: "Action, Comedy, Drama"
    /// Each item must be short (≤ 40 characters, ≤ 4 words) and free of sentence punctuation (. ! ? :),
    /// so that a real sentence with commas is not taken for a list.
    /// </summary>
    public static class OverviewListDetector
    {
        public const string TargetTags = "tags";
        public const string TargetGenres = "genres";

        private const int MaxLength = 500;
        private const int MaxItemLength = 40;
        private const int MaxWordsPerItem = 4;
        private static readonly char[] Separators = { ',', ';', '|', '\n', '\r', '•', '·' };
        private static readonly char[] SentencePunctuation = { '.', '!', '?', ':' };

        /// <summary>
        /// Items of the list, or null if the overview looks like real text
        /// </summary>
        public static List<string> TryParse(string overview)
        {
            if (string.IsNullOrWhiteSpace(overview))
            {
                return null;
            }

            var text = overview.Trim();
            if (text.Length > MaxLength)
            {
                return null;
            }

            // 1. Hashtags only
            var words = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (words.All(w => w.Length > 1 && w[0] == '#'))
            {
                return Clean(words.Select(w => w.TrimStart('#')));
            }

            // 2. Separated list (trailing full stop tolerated)
            var items = text.TrimEnd('.').Split(Separators, StringSplitOptions.RemoveEmptyEntries)
                .Select(i => i.Trim().TrimStart('#', '-', '*').Trim())
                .Where(i => i.Length > 0)
                .ToList();

            if (items.Count < 2)
            {
                return null;
            }

            foreach (var item in items)
            {
                if (item.Length > MaxItemLength
                    || item.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > MaxWordsPerItem
                    || item.IndexOfAny(SentencePunctuation) >= 0)
                {
                    return null;
                }
            }

            return Clean(items);
        }

        public static bool IsEnabled(string target)
        {
            return string.Equals(target, TargetTags, StringComparison.OrdinalIgnoreCase)
                || string.Equals(target, TargetGenres, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsGenres(string target)
        {
            return string.Equals(target, TargetGenres, StringComparison.OrdinalIgnoreCase);
        }

        private static List<string> Clean(IEnumerable<string> items)
        {
            var list = items.Select(i => i.Trim()).Where(i => i.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return list.Count > 0 ? list : null;
        }
    }
}
