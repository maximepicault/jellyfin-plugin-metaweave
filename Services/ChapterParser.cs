using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using Jellyfin.Plugin.CustomMetadata.Api;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>
    /// Time codes of an episode's chapters. A time code is NOT a duration: "00:00" and 0 are valid (the first chapter),
    /// which DurationParser refuses. Accepted forms:
    /// - "mm:ss", "m:ss", "hh:mm:ss", a fraction of a second tolerated ("12:34.5", "1:02:03,500"); minutes may pass 59 in "mm:ss" ("125:07")
    /// - seconds, as a JSON number or as text: 359, "359", "359.5", "359,5" (milliseconds when the source says so: 359000)
    /// - with units: "6m12s", "1h02m03s", "6 min 12 s", "359s"
    /// - ISO 8601: "PT6M12S"
    /// </summary>
    public static class ChapterParser
    {
        /// <summary>No chapter beyond a day: a value that large is a unit mistake (milliseconds read as seconds)</summary>
        private const double MaxSeconds = 24 * 3600;

        private static readonly Regex Units = new Regex(
            @"^(?:(?<h>\d+)\s*(?:h|heures?|hours?)\s*)?(?:(?<m>\d+)\s*(?:m|mn|min|minutes?)\s*)?(?:(?<s>\d+(?:[.,]\d+)?)\s*(?:s|sec|secs|secondes?|seconds?)?\s*)?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private const string ColonToken = @"\d{1,3}(?::\d{2}){1,2}(?:[.,]\d+)?";
        // In a line, only the short units ("6m12s", "1h02m03s", "6 min 12 s", "359s") and never one that is the start of a word:
        // "359 Suite" is 359 and the title "Suite" (not "359 s" and "uite"), "0 Minutes de pause" is 0 and "Minutes de pause"
        private const string AnyToken = ColonToken + @"|\d+(?:[.,]\d+)?\s*(?:s|sec|secs)(?!\p{L})|(?:\d+\s*h(?!\p{L})\s*)?\d+\s*(?:m|mn|min)(?!\p{L})(?:\s*\d+(?:[.,]\d+)?\s*(?:s|sec|secs)(?!\p{L}))?|\d+(?:[.,]\d+)?";
        private const string Lead = @"^[\s\-•►▶>*·]*[\[\(]?\s*";

        // After the time code: an optional end ("0:00-12:34", "[0:00 - 12:34]"), then the separators, then the title
        private const string Tail = @"\s*[\]\)]?\s*(?:[\-–—]\s*[\[\(]?\s*(?<e>{0})\s*[\]\)]?)?\s*[\-–—:|.·»>]*\s*(?<rest>.*)$";

        // A line of a description: "12:34 Title", "[12:34] Title", "00:12:34 - Title", "- 12:34 : Title", "0:00-12:34 Title"
        private static readonly Regex ColonLine = new Regex(Lead + "(?<t>" + ColonToken + ")" + string.Format(Tail, ColonToken), RegexOptions.Compiled);

        // Same with a number of seconds or unit form first, for the field the user types the chapters in
        private static readonly Regex AnyLine = new Regex(Lead + "(?<t>" + AnyToken + ")" + string.Format(Tail, AnyToken), RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>A time code as text → seconds (0 allowed), or null</summary>
        public static double? ParseSeconds(string text, bool millisecondsForBareNumbers = false)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            text = text.Trim();
            double? result = null;
            if (text.Contains(':'))
            {
                result = ParseColon(text);
            }
            else if (Regex.IsMatch(text, @"^\d+(?:[.,]\d+)?$"))
            {
                var value = double.Parse(text.Replace(',', '.'), CultureInfo.InvariantCulture);
                result = millisecondsForBareNumbers ? value / 1000 : value;
            }
            else if (text.StartsWith("P", StringComparison.OrdinalIgnoreCase))
            {
                try { result = XmlConvert.ToTimeSpan(text.ToUpperInvariant()).TotalSeconds; }
                catch (FormatException) { result = null; }
            }
            else
            {
                var match = Units.Match(text);
                if (match.Success && (match.Groups["h"].Success || match.Groups["m"].Success || match.Groups["s"].Success))
                {
                    var h = match.Groups["h"].Success ? double.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture) : 0;
                    var m = match.Groups["m"].Success ? double.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
                    var s = match.Groups["s"].Success ? double.Parse(match.Groups["s"].Value.Replace(',', '.'), CultureInfo.InvariantCulture) : 0;
                    result = h * 3600 + m * 60 + s;
                }
            }

            return result.HasValue && result.Value >= 0 && result.Value <= MaxSeconds ? result : null;
        }

        private static double? ParseColon(string text)
        {
            var parts = text.Split(':');
            if (parts.Length < 2 || parts.Length > 3)
            {
                return null;
            }

            if (!double.TryParse(parts[^1].Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) || seconds >= 60)
            {
                return null;
            }

            if (!int.TryParse(parts[^2], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
            {
                return null;
            }

            var hours = 0;
            if (parts.Length == 3 && (minutes >= 60 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hours)))
            {
                return null;
            }

            return hours * 3600.0 + minutes * 60.0 + seconds;
        }

        /// <summary>
        /// The lines of a text that start with a time code. {anyNumber}: a bare number or a unit form ("359 Title", "6m12s Title")
        /// counts too (the field the user types in); false for a synopsis, where a line starting with a year or a count must not.
        /// </summary>
        public static List<ChapterMark> ParseLines(string text, bool anyNumber)
        {
            var marks = new List<ChapterMark>();
            if (string.IsNullOrWhiteSpace(text))
            {
                return marks;
            }

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim('\r', ' ', '\t');
                if (line.Length == 0)
                {
                    continue;
                }

                var match = (anyNumber ? AnyLine : ColonLine).Match(line);
                if (!match.Success)
                {
                    continue;
                }

                var seconds = ParseSeconds(match.Groups["t"].Value);
                if (seconds.HasValue)
                {
                    marks.Add(new ChapterMark
                    {
                        Seconds = seconds.Value,
                        EndSeconds = match.Groups["e"].Success ? ParseSeconds(match.Groups["e"].Value) : null,
                        Title = match.Groups["rest"].Value.Trim()
                    });
                }
            }

            return Normalize(marks);
        }

        /// <summary>Sorted by time, one chapter per time code (the first wins), a name for each ("Chapitre 3" when none was given)</summary>
        public static List<ChapterMark> Normalize(IEnumerable<ChapterMark> marks)
        {
            var seen = new HashSet<long>();
            var result = new List<ChapterMark>();
            foreach (var mark in (marks ?? Enumerable.Empty<ChapterMark>()).Where(m => m != null && m.Seconds >= 0 && m.Seconds <= MaxSeconds).OrderBy(m => m.Seconds))
            {
                if (seen.Add((long)Math.Round(mark.Seconds * 1000)))
                {
                    result.Add(new ChapterMark
                    {
                        Seconds = mark.Seconds,
                        EndSeconds = mark.EndSeconds.HasValue && mark.EndSeconds.Value > mark.Seconds && mark.EndSeconds.Value <= MaxSeconds ? mark.EndSeconds : null,
                        Title = mark.Title?.Trim() ?? string.Empty
                    });
                }
            }

            // An end past the next chapter's start is an overlap, not a gap: cut it there
            for (var i = 0; i < result.Count - 1; i++)
            {
                if (result[i].EndSeconds.HasValue && result[i].EndSeconds.Value > result[i + 1].Seconds)
                {
                    result[i].EndSeconds = result[i + 1].Seconds;
                }
            }

            for (var i = 0; i < result.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(result[i].Title))
                {
                    result[i].Title = "Chapitre " + (i + 1);
                }
            }

            return result;
        }

        /// <summary>"4:05", "1:02:03": how a time code is written back in a synopsis</summary>
        public static string Format(double seconds)
        {
            var total = (long)Math.Floor(seconds);
            var h = total / 3600;
            var m = total % 3600 / 60;
            var s = total % 60;
            return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
        }

        public static string ToText(IEnumerable<ChapterMark> marks)
            => string.Join("\n", marks.Select(m => Format(m.Seconds) + (m.EndSeconds.HasValue ? "\u2013" + Format(m.EndSeconds.Value) : string.Empty) + " " + m.Title));

        /// <summary>Name of the chapter that closes a stretch no chapter covers (Jellyfin has a start per chapter, no end)</summary>
        public const string GapTitle = "Hors chapitre";

        /// <summary>
        /// The chapters as Jellyfin stores them: a start and a name each, a chapter lasting until the next one. A chapter that ENDS before the
        /// next one starts (or before the end of the video, for the last) would otherwise swallow the stretch after it: a closing chapter is
        /// added at its end. {runtimeSeconds}: length of the video when known (0 = unknown: the last chapter is then left open).
        /// </summary>
        public static List<ChapterMark> WithGaps(IReadOnlyList<ChapterMark> marks, double runtimeSeconds)
        {
            var result = new List<ChapterMark>();
            for (var i = 0; i < marks.Count; i++)
            {
                result.Add(new ChapterMark { Seconds = marks[i].Seconds, Title = marks[i].Title });
                if (!marks[i].EndSeconds.HasValue)
                {
                    continue;
                }

                var end = marks[i].EndSeconds.Value;
                var limit = i + 1 < marks.Count ? marks[i + 1].Seconds : runtimeSeconds;
                // Less than half a second from the limit is a rounding, not a gap
                if (limit > 0 && end < limit - 0.5 && (runtimeSeconds <= 0 || end < runtimeSeconds))
                {
                    result.Add(new ChapterMark { Seconds = end, Title = GapTitle });
                }
            }

            return result;
        }
    }
}
