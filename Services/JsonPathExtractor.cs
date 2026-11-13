using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>
    /// JSON value extractor using dot notation
    /// Exemples :
    /// - "title" → root.title
    /// - "data.video.name" → root.data.video.name
    /// - "metadata.tags" → root.metadata.tags
    /// - "[0].name" → name of the first element of a root list; "results[0].name" → of the first element of "results"
    /// </summary>
    public static class JsonPathExtractor
    {
        /// <summary>
        /// Extracts a string value from a JSON path
        /// </summary>
        public static string GetString(JsonElement root, string path, string defaultValue = "")
        {
            try
            {
                var element = NavigateToElement(root, path);
                if (element.HasValue)
                {
                    return element.Value.ValueKind == JsonValueKind.String 
                        ? element.Value.GetString() 
                        : element.Value.ToString();
                }
            }
            catch
            {
                // If the path does not exist, return the default value
            }

            return defaultValue;
        }

        /// <summary>
        /// Extracts an int value from a JSON path
        /// </summary>
        public static int? GetInt(JsonElement root, string path)
        {
            try
            {
                var element = NavigateToElement(root, path);
                if (element.HasValue && element.Value.ValueKind == JsonValueKind.Number)
                {
                    return element.Value.GetInt32();
                }
            }
            catch
            {
                // Ignore parsing errors
            }

            return null;
        }

        /// <summary>
        /// Extracts a float value from a JSON path
        /// </summary>
        public static float? GetFloat(JsonElement root, string path)
        {
            try
            {
                var element = NavigateToElement(root, path);
                if (element.HasValue && element.Value.ValueKind == JsonValueKind.Number)
                {
                    return element.Value.GetSingle();
                }
            }
            catch
            {
                // Ignore parsing errors
            }

            return null;
        }

        /// <summary>
        /// Extracts a date from a JSON path
        /// </summary>
        public static DateTime? GetDateTime(JsonElement root, string path)
        {
            try
            {
                var element = NavigateToElement(root, path);
                if (element.HasValue && element.Value.ValueKind == JsonValueKind.String)
                {
                    var dateStr = element.Value.GetString();
                    if (DateTime.TryParse(dateStr, out var date))
                    {
                        return date;
                    }
                }
            }
            catch
            {
                // Ignore parsing errors
            }

            return null;
        }

        /// <summary>
        /// Extracts a string array from a JSON path
        /// </summary>
        public static List<string> GetStringArray(JsonElement root, string path)
        {
            var result = new List<string>();

            try
            {
                var element = NavigateToElement(root, path);
                if (element.HasValue && element.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in element.Value.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            result.Add(item.GetString());
                        }
                        else
                        {
                            result.Add(item.ToString());
                        }
                    }
                }
            }
            catch
            {
                // Return an empty list on error
            }

            return result;
        }

        /// <summary>
        /// Extracts an object array from a JSON path
        /// </summary>
        public static List<JsonElement> GetObjectArray(JsonElement root, string path)
        {
            var result = new List<JsonElement>();

            try
            {
                var element = NavigateToElement(root, path);
                if (element.HasValue && element.Value.ValueKind == JsonValueKind.Array)
                {
                    result.AddRange(element.Value.EnumerateArray());
                }
            }
            catch
            {
                // Return an empty list on error
            }

            return result;
        }

        /// <summary>
        /// Like GetObjectArray, but tells "empty array" (true) from "path missing / not an array" (false)
        /// Empty path = the root
        /// </summary>
        public static bool TryGetArray(JsonElement root, string path, out List<JsonElement> items)
        {
            items = new List<JsonElement>();
            var element = NavigateToElement(root, path);
            if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            items.AddRange(element.Value.EnumerateArray());
            return true;
        }

        /// <summary>
        /// The single place that follows a path ("a.b.c", with positions "[0]" / "items[0]"). False when the path is
        /// empty or does not exist; JsonFieldMapper uses it too, so every mapped field reads lists the same way.
        /// </summary>
        public static bool TryNavigate(JsonElement root, string path, out JsonElement result)
        {
            result = default;
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var element = NavigateToElement(root, path);
            if (!element.HasValue)
            {
                return false;
            }

            result = element.Value;
            return true;
        }

        // "name", "name[0]", "[0]" or "name[0][1]": a property (optional) followed by one or more array positions
        private static readonly System.Text.RegularExpressions.Regex IndexedPart =
            new System.Text.RegularExpressions.Regex(@"^(?<name>[^\[\]]*)(?<idx>(\[\d+\])+)$", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Walks the JSON tree following the path. Besides "a.b.c", a position in a list is written
        /// "[0]" (first element of the root list) or "items[0]" (first element of the list "items"), so
        /// "[0].name", "results[0].user.name" and "data.items[1].tags" work. "[]" (all the elements) is not handled
        /// here: the callers that accept it split the path before.
        /// </summary>
        private static JsonElement? NavigateToElement(JsonElement root, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return root;
            }

            JsonElement current = root;
            foreach (var part in path.Split('.'))
            {
                var indexed = IndexedPart.Match(part);
                var name = indexed.Success ? indexed.Groups["name"].Value : part;

                if (name.Length > 0)
                {
                    if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out var next))
                    {
                        return null;
                    }

                    current = next;
                }

                if (!indexed.Success)
                {
                    continue;
                }

                foreach (System.Text.RegularExpressions.Capture position in indexed.Groups["idx"].Captures)
                {
                    var text = position.Value.Trim('[', ']');
                    if (current.ValueKind != JsonValueKind.Array || !int.TryParse(text, out var index) || index >= current.GetArrayLength())
                    {
                        return null;
                    }

                    current = current[index];
                }
            }

            return current;
        }
    }
}
