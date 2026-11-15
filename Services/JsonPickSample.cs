using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>
    /// A small copy of an API answer for the "click a value to fill a mapping" tree of the source test: every list is cut to ONE
    /// element, the most complete one (the one with the most filled fields: the first can lack a synopsis, tags, a thumbnail…, and
    /// the example would then hide fields the user wants to map), long texts are shortened. The answer itself can be far larger than the raw
    /// text the test screen shows (a list of episodes easily passes 20 KB), so the tree must not depend on that truncated text.
    /// The lengths of the cut lists are kept apart, under the path the screen builds for them ("data.videos", "data.videos[0].tags",
    /// "" for a list at the root).
    /// </summary>
    public static class JsonPickSample
    {
        private const int MaxDepth = 8;
        private const int MaxProperties = 200;
        private const int MaxText = 200;
        private const int MaxScanned = 1000;   // elements looked at per list, to keep a huge answer cheap

        public static string Build(JsonElement root, out Dictionary<string, int> counts)
        {
            counts = new Dictionary<string, int>();
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                Write(writer, root, string.Empty, 0, counts);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        /// <summary>The element of a list with the most filled fields (the first one on a tie, and for a list of plain values)</summary>
        private static JsonElement MostComplete(JsonElement list)
        {
            var best = list[0];
            if (best.ValueKind != JsonValueKind.Object)
            {
                return best;
            }

            var bestScore = Score(best, 0);
            var scanned = 0;
            foreach (var element in list.EnumerateArray())
            {
                if (scanned++ >= MaxScanned) { break; }
                if (element.ValueKind != JsonValueKind.Object) { continue; }
                var score = Score(element, 0);
                if (score > bestScore) { best = element; bestScore = score; }
            }

            return best;
        }

        /// <summary>Number of fields that hold something (a null, an empty text or an empty list counts for nothing), nested ones included</summary>
        private static int Score(JsonElement value, int depth)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    if (depth >= MaxDepth) { return 0; }
                    var total = 0;
                    foreach (var property in value.EnumerateObject())
                    {
                        // A filled value counts for 1, an object or a list for what it holds
                        total += Score(property.Value, depth + 1);
                    }

                    return total;
                case JsonValueKind.Array:
                    // A list holds something if its most filled element does
                    return value.GetArrayLength() == 0 || depth >= MaxDepth ? 0 : System.Math.Max(1, Score(value[0], depth + 1));
                case JsonValueKind.String:
                    return string.IsNullOrWhiteSpace(value.GetString()) ? 0 : 1;
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return 0;
                default:
                    return 1;
            }
        }

        private static void Write(Utf8JsonWriter writer, JsonElement value, string path, int depth, Dictionary<string, int> counts)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    if (depth < MaxDepth)
                    {
                        var written = 0;
                        foreach (var property in value.EnumerateObject())
                        {
                            if (written++ >= MaxProperties) { break; }
                            writer.WritePropertyName(property.Name);
                            Write(writer, property.Value, path.Length == 0 ? property.Name : path + "." + property.Name, depth + 1, counts);
                        }
                    }

                    writer.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    var length = value.GetArrayLength();
                    if (length > 1) { counts[path] = length; }
                    if (length > 0 && depth < MaxDepth)
                    {
                        // Shown (and addressed by the page) as the element "[0]": a mapping path is written for the first element,
                        // and any element of a list has the same fields when the API is regular
                        Write(writer, MostComplete(value), path + "[0]", depth + 1, counts);
                    }

                    writer.WriteEndArray();
                    break;
                case JsonValueKind.String:
                    var text = value.GetString() ?? string.Empty;
                    writer.WriteStringValue(text.Length > MaxText ? text.Substring(0, MaxText) + "…" : text);
                    break;
                default:
                    value.WriteTo(writer);
                    break;
            }
        }
    }
}
