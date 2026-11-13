using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>
    /// Reads the "custom" identifier ({prefix}-{id}) CASE-INSENSITIVELY.
    /// Jellyfin creates ProviderIds with OrdinalIgnoreCase, but when it re-reads an item from the database
    /// (BaseItemRepository: Provider.ToDictionary(...)) the dictionary is case-sensitive.
    /// Seen in the database: episodes carried "Custom" → TryGetValue("custom") failed.
    /// </summary>
    public static class CustomId
    {
        public const string Key = "custom";

        public static string Get(IReadOnlyDictionary<string, string> providerIds)
        {
            if (providerIds == null)
            {
                return null;
            }

            var value = providerIds.FirstOrDefault(kv => IsCustomKey(kv.Key)).Value;
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        public static bool IsCustomKey(string key)
        {
            return string.Equals(key, Key, StringComparison.OrdinalIgnoreCase);
        }
    }
}
