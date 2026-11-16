using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.CustomMetadata.Api;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>
    /// Some APIs return text with HTML entities ("Q&amp;A", "L&#39;été", "caf&eacute;").
    /// Jellyfin displays plain text: we decode once, when the JSON is read (JsonFieldMapper).
    /// Reserved for text fields (title, overview, tags, genres, cast), never for URLs.
    /// </summary>
    public static class HtmlText
    {
        // That WebUtility.HtmlDecode leaves untouched: "&quot"/"&apos" WITHOUT a semicolon ("&quotTitle") and the names
        // in capitals ("&QUOT;", "&AMP;"). The semicolon stays mandatory for amp/lt/gt/nbsp: "&amplifier" is not an entity.
        private static readonly Regex LooseEntity = new Regex(@"&(?:(quot|apos);?|(amp|lt|gt|nbsp);)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // "quot;" whose "&" got lost on the way (no common word ends with "quot;")
        private static readonly Regex BareQuot = new Regex(@"(?<!&)quot;", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Entity still present after one pass: text encoded twice ("&amp;quot;")
        private static readonly Regex StillEncoded = new Regex(@"&(#\d+|#x[0-9a-f]+|quot|amp|apos|lt|gt|nbsp);", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string Decode(string text)
        {
            if (string.IsNullOrEmpty(text) || (text.IndexOf('&') < 0 && text.IndexOf("quot;", System.StringComparison.OrdinalIgnoreCase) < 0))
            {
                return text;
            }

            // Up to 3 passes: "&amp;quot;" → "&quot;" → "\""
            for (var pass = 0; pass < 3; pass++)
            {
                var decoded = WebUtility.HtmlDecode(text);
                decoded = LooseEntity.Replace(decoded, m => WebUtility.HtmlDecode("&" + (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToLowerInvariant() + ";"));
                decoded = BareQuot.Replace(decoded, "\"");
                if (decoded == text)
                {
                    break;
                }

                text = decoded;
                if (!StillEncoded.IsMatch(text))
                {
                    break;
                }
            }

            return text;
        }

        private static readonly Regex ScriptStyle = new Regex(@"<\s*(script|style)[^>]*>.*?<\s*/\s*\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex Br = new Regex(@"<\s*br\s*/?\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex BlockClose = new Regex(@"<\s*/\s*(p|div|h[1-6]|blockquote|tr|table)\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ListItem = new Regex(@"<\s*li[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ListItemClose = new Regex(@"<\s*/\s*li\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ListClose = new Regex(@"<\s*/\s*(ul|ol)\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex AnyTag = new Regex(@"<[^>]+>", RegexOptions.Compiled);
        private static readonly Regex Spaces = new Regex(@"[ \t]+", RegexOptions.Compiled);
        private static readonly Regex SpacesAroundNewline = new Regex(@" *\n *", RegexOptions.Compiled);
        private static readonly Regex ManyNewlines = new Regex(@"\n{3,}", RegexOptions.Compiled);

        /// <summary>
        /// HTML fragment → readable plain text (clients show the overview as text, markup would come out as is):
        /// line breaks kept, lists as bullets, entities decoded. Also handles JSON escaped twice (a literal "\n"),
        /// non-breaking spaces and invisible characters.
        /// </summary>
        public static string ToPlainText(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return html;
            }

            var text = html
                .Replace("\\r\\n", "\n").Replace("\\n", "\n").Replace("\\r", "\n").Replace("\\t", " ")
                .Replace("\r\n", "\n").Replace("\r", "\n");

            text = ScriptStyle.Replace(text, string.Empty);
            text = Br.Replace(text, "\n");
            text = BlockClose.Replace(text, "\n\n");
            text = ListItemClose.Replace(text, string.Empty);
            text = ListItem.Replace(text, "\n• ");
            text = ListClose.Replace(text, "\n");
            text = AnyTag.Replace(text, string.Empty);
            text = Decode(text);
            text = text.Replace('\u00A0', ' ').Replace("\u200B", string.Empty).Replace("\u200C", string.Empty)
                .Replace("\u200D", string.Empty).Replace("\uFEFF", string.Empty);
            text = Spaces.Replace(text, " ");
            text = SpacesAroundNewline.Replace(text, "\n");
            text = ManyNewlines.Replace(text, "\n\n");
            return text.Trim();
        }

        public static List<string> Decode(List<string> list)
        {
            return list?.Select(Decode).ToList();
        }

        public static void DecodeInPlace(CustomMetadataResponse response)
        {
            if (response == null)
            {
                return;
            }

            response.Title = Decode(response.Title);
            response.Overview = Decode(response.Overview);
            response.SeriesName = Decode(response.SeriesName);
            response.Tags = Decode(response.Tags);
            response.Genres = Decode(response.Genres);
            response.Studios = Decode(response.Studios);
            if (response.Cast != null)
            {
                foreach (var member in response.Cast)
                {
                    member.Name = Decode(member.Name);
                    member.Role = Decode(member.Role);
                }
            }
        }
    }
}
