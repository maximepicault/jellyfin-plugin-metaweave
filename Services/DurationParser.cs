using System;
using System.Globalization;
using System.Xml;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>
    /// Converts an API duration to a TimeSpan. Accepted formats:
    /// - "mm:ss" (ex: "12:34", "125:07")
    /// - "hh:mm:ss" (e.g. "1:02:03"), fractions of a second tolerated ("1:02:03.500", "12:34,5")
    /// - number of seconds, as a JSON number or as text (e.g. 754, "754", "754.2")
    /// - ISO 8601 (ex: "PT1H2M3S")
    /// Careful: TimeSpan.Parse("12:34") would give 12 h 34 min, hence this dedicated parser.
    /// </summary>
    public static class DurationParser
    {
        public static TimeSpan? Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var text = value.Trim();

            if (text.Contains(':'))
            {
                return ParseColonFormat(text);
            }

            if (double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                return seconds > 0 && seconds < TimeSpan.MaxValue.TotalSeconds ? TimeSpan.FromSeconds(seconds) : null;
            }

            if (text.StartsWith("P", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var iso = XmlConvert.ToTimeSpan(text.ToUpperInvariant());
                    return iso > TimeSpan.Zero ? iso : null;
                }
                catch (FormatException)
                {
                    return null;
                }
            }

            return null;
        }

        /// <summary>
        /// 2 parts = mm:ss, 3 parts = hh:mm:ss. Only the last part (seconds) may have a fraction.
        /// </summary>
        private static TimeSpan? ParseColonFormat(string text)
        {
            var parts = text.Split(':');
            if (parts.Length < 2 || parts.Length > 3)
            {
                return null;
            }

            if (!double.TryParse(parts[^1].Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var secs) || secs >= 60)
            {
                return null;
            }

            if (!int.TryParse(parts[^2], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
            {
                return null;
            }

            var hours = 0;
            if (parts.Length == 3)
            {
                // in hh:mm:ss the minutes are bounded; in mm:ss they may exceed 59 ("125:07")
                if (minutes >= 60 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hours))
                {
                    return null;
                }
            }

            var total = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(secs);
            return total > TimeSpan.Zero ? total : null;
        }
    }
}
