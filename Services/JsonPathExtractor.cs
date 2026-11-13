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
        /// Walks the JSON tree following the path
        /// </summary>
        private static JsonElement? NavigateToElement(JsonElement root, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return root;
            }

            var parts = path.Split('.');
            JsonElement current = root;

            foreach (var part in parts)
            {
                if (current.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                if (!current.TryGetProperty(part, out var next))
                {
                    return null;
                }

                current = next;
            }

            return current;
        }
    }
}
