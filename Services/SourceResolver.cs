using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.CustomMetadata.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>
    /// Resolves which source to use from a prefix extracted from a file name
    /// </summary>
    public class SourceResolver
    {
        private readonly ILogger _logger;

        public SourceResolver(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Finds the source matching a prefix
        /// </summary>
        /// <param name="prefix">The extracted prefix (e.g. "mv1", "yt")</param>
        /// <returns>The source configuration, or null if not found</returns>
        public SourceConfiguration GetSourceByPrefix(string prefix)
        {
            if (string.IsNullOrEmpty(prefix))
            {
                _logger.LogWarning("🔍 [SOURCE RESOLVER] Prefix is null or empty");
                return GetDefaultSource();
            }

            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                _logger.LogError("🔍 [SOURCE RESOLVER] ❌ Plugin.Instance.Configuration is NULL!");
                return null;
            }

            // Multi-source mode: search the list of sources
            if (config.Sources != null && config.Sources.Count > 0)
            {
                _logger.LogDebug("🔍 [SOURCE RESOLVER] Multi-source mode: {Count} sources configured", config.Sources.Count);
                
                var source = config.Sources.FirstOrDefault(s => 
                    s.Enabled && 
                    !s.IsPeople &&   // a people source does not enrich media: its tag designates no file
                    !string.IsNullOrEmpty(s.Prefix) && 
                    s.Prefix.Equals(prefix, System.StringComparison.OrdinalIgnoreCase));
                
                if (source != null)
                {
                    _logger.LogInformation("🔍 [SOURCE RESOLVER] ✅ Found source '{SourceName}' for prefix '{Prefix}'", 
                        source.Name, prefix);
                    _logger.LogDebug("🔍 [SOURCE RESOLVER]   BaseUrl: {BaseUrl}", source.BaseUrl);
                    return source;
                }
                
                _logger.LogWarning("🔍 [SOURCE RESOLVER] ⚠️ No source found for prefix '{Prefix}'", prefix);
                _logger.LogWarning("🔍 [SOURCE RESOLVER]   Available prefixes: {Prefixes}", 
                    string.Join(", ", config.Sources.Where(s => s.Enabled).Select(s => s.Prefix)));
                
                return null;
            }

            // Single-source mode (backward compatibility): use the global config
            _logger.LogDebug("🔍 [SOURCE RESOLVER] Mono-source mode (legacy): checking global prefix");
            
            if (config.PatternPrefix.Equals(prefix, System.StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("🔍 [SOURCE RESOLVER] ✅ Using global configuration for prefix '{Prefix}'", prefix);
                return GetDefaultSource();
            }

            _logger.LogWarning("🔍 [SOURCE RESOLVER] ⚠️ Prefix '{Prefix}' doesn't match global prefix '{GlobalPrefix}'", 
                prefix, config.PatternPrefix);
            
            return null;
        }

        /// <summary>
        /// Returns a source built from the global config (single-source mode)
        /// </summary>
        private SourceConfiguration GetDefaultSource()
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
                return null;

            _logger.LogDebug("🔍 [SOURCE RESOLVER] Building source from global configuration");

            return SourceConfiguration.FromLegacyConfig(config, "Default Source (Global Config)");
        }

        /// <summary>
        /// Extracts the prefix from a {PREFIX-ID} pattern
        /// Ex: "mv1-123" → "mv1", "yt-456" → "yt"
        /// </summary>
        public string ExtractPrefixFromId(string idWithPrefix)
        {
            if (string.IsNullOrEmpty(idWithPrefix))
                return null;

            // Format: "prefix-id" → on veut "prefix"
            var parts = idWithPrefix.Split('-', 2);
            if (parts.Length >= 1)
            {
                var prefix = parts[0];
                _logger.LogDebug("🔍 [SOURCE RESOLVER] Extracted prefix '{Prefix}' from '{IdWithPrefix}'", prefix, idWithPrefix);
                return prefix;
            }

            return null;
        }

        /// <summary>
        /// Extracts the ID without the prefix from a {PREFIX-ID} pattern
        /// Ex: "mv1-123" → "123", "yt-456" → "456"
        /// </summary>
        public string ExtractIdWithoutPrefix(string idWithPrefix)
        {
            if (string.IsNullOrEmpty(idWithPrefix))
                return null;

            // Format: "prefix-id" → on veut "id"
            var dashIndex = idWithPrefix.IndexOf('-');
            if (dashIndex > 0 && dashIndex < idWithPrefix.Length - 1)
            {
                var id = idWithPrefix.Substring(dashIndex + 1);
                _logger.LogDebug("🔍 [SOURCE RESOLVER] Extracted ID '{Id}' from '{IdWithPrefix}'", id, idWithPrefix);
                return id;
            }

            // No hyphen found, return the full ID
            _logger.LogDebug("🔍 [SOURCE RESOLVER] No dash found in '{IdWithPrefix}', returning as-is", idWithPrefix);
            return idWithPrefix;
        }
    }
}
