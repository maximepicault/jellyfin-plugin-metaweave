using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>
    /// A small copy of an API answer for the "click a value to fill a mapping" tree of the source test: every list is cut to ONE
    /// example element. The example is the most complete element (the one with the most filled fields: the first can lack a synopsis,
    /// tags, a thumbnail…), COMPLETED with the fields only other elements have (a "sponsor" that exists on one episode out of a
    /// hundred must still be offered), long texts are shortened. The answer itself can be far larger than the raw
    /// text the test screen shows (a list of episodes easily passes 20 KB), so the tree must not depend on that truncated text.
    /// The lengths of the cut lists are kept apart, under the path the screen builds for them ("data.videos", "data.videos[0].tags",
    /// "" for a list at the root).
    /// </summary>
    public static class JsonPickSample
    {
        // Wide and deep enough that a real answer is not cut: these only protect the page from an absurd one
        private const int MaxDepth = 12;
        private const int MaxProperties = 2000;
        private const int MaxText = 2000;
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

        /// <summary>The most complete element of a list, plus every field that only other elements have (a field filled somewhere is a field to map)</summary>
        private static JsonElement Example(JsonElement list)
        {
            var best = MostComplete(list);
            if (best.ValueKind != JsonValueKind.Object || list.GetArrayLength() < 2)
            {
                return best;
            }

            var merged = JsonNode.Parse(best.GetRawText()) as JsonObject;
            var scanned = 0;
            foreach (var element in list.EnumerateArray())
            {
                if (scanned++ >= MaxScanned) { break; }
                if (element.ValueKind == JsonValueKind.Object && JsonNode.Parse(element.GetRawText()) is JsonObject other)
                {
                    MergeMissing(merged, other, 0);
                }
            }

            using var document = JsonDocument.Parse(merged.ToJsonString());
            return document.RootElement.Clone();
        }

        private static bool IsEmpty(JsonNode node)
        {
            return node == null
                || (node is JsonValue v && v.TryGetValue<string>(out var text) && string.IsNullOrWhiteSpace(text))
                || (node is JsonArray a && a.Count == 0)
                || (node is JsonObject o && o.Count == 0);
        }

        /// <summary>Adds to {target} the fields {source} has and it lacks (or holds empty), nested ones included; never overwrites a filled value</summary>
        private static void MergeMissing(JsonObject target, JsonObject source, int depth)
        {
            if (depth >= MaxDepth)
            {
                return;
            }

            foreach (var property in source.ToList())
            {
                if (property.Value == null) { continue; }
                if (!target.ContainsKey(property.Key) || IsEmpty(target[property.Key]))
                {
                    if (!IsEmpty(property.Value)) { target[property.Key] = property.Value.DeepClone(); }
                }
                else if (target[property.Key] is JsonObject targetObject && property.Value is JsonObject sourceObject)
                {
                    MergeMissing(targetObject, sourceObject, depth + 1);
                }
                else if (target[property.Key] is JsonArray targetList && property.Value is JsonArray sourceList
                         && targetList.Count > 0 && sourceList.Count > 0 && targetList[0] is JsonObject firstTarget && sourceList[0] is JsonObject firstSource)
                {
                    // A list inside the example: its first element is the example of that list, completed with the other one's
                    MergeMissing(firstTarget, firstSource, depth + 1);
                }
            }
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
                        Write(writer, Example(value), path + "[0]", depth + 1, counts);
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
