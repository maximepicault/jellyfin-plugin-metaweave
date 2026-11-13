using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.CustomMetadata.Api;
using Jellyfin.Plugin.CustomMetadata.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>
    /// Cache of the missing episodes already processed: one JSON file per season
    /// ({DataFolderPath}/missing-episodes-cache/{seasonId}.json), holding for each episode the JSON as
    /// returned by the API and a fingerprint. At the next analysis, an existing virtual episode whose
    /// fingerprint has not changed is skipped (no thumbnail, preview or database write).
    /// </summary>
    public class MissingEpisodeCache
    {
        private const string FolderName = "missing-episodes-cache";

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = true };

        private readonly ILogger _logger;

        public MissingEpisodeCache(ILogger logger)
        {
            _logger = logger;
        }

        public Dictionary<string, CachedMissingEpisode> Load(Guid seasonId)
        {
            var result = new Dictionary<string, CachedMissingEpisode>(StringComparer.OrdinalIgnoreCase);
            var path = GetPath(seasonId);
            if (path == null || !File.Exists(path))
            {
                return result;
            }

            try
            {
                var stored = JsonSerializer.Deserialize<Dictionary<string, CachedMissingEpisode>>(File.ReadAllText(path));
                if (stored != null)
                {
                    foreach (var (key, value) in stored)
                    {
                        result[key] = value;
                    }
                }
            }
            catch (Exception ex)
            {
                // Unreadable cache: the season is simply reprocessed
                _logger.LogWarning(ex, "🔎 [MISSING] Unreadable cache {Path} - season will be fully reprocessed", path);
            }

            return result;
        }

        public void Save(Guid seasonId, Dictionary<string, CachedMissingEpisode> entries)
        {
            var path = GetPath(seasonId);
            if (path == null)
            {
                return;
            }

            try
            {
                if (entries.Count == 0)
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }

                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonSerializer.Serialize(entries, JsonOptions));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "🔎 [MISSING] Could not save cache {Path}", path);
            }
        }

        /// <summary>
        /// Fingerprint of an episode of the list: its raw JSON + the mapping + the source URL.
        /// Changing the mapping (e.g. the thumbnail path) therefore invalidates this source's cache.
        /// </summary>
        public static string ComputeHash(EpisodeListItem item, SourceConfiguration source, string castFingerprint = null)
        {
            var input = string.Join(
                "\n",
                item.RawJson ?? string.Empty,
                JsonSerializer.Serialize(source.EpisodeListMappings ?? new EpisodeListMapping()),
                source.BaseUrl ?? string.Empty,
                "html-decoded")   // HTML entities of titles/overviews are decoded: invalidates the earlier cache
                // Added only when active: does not invalidate the existing cache of sources that do not use it
                + (source.LandscapePortraitThumbs ? "\nlandscape-portrait-thumbs" : string.Empty)
                + (OverviewListDetector.IsEnabled(source.VideoFieldMappings?.OverviewListTarget)
                    ? "\noverview-list:" + source.VideoFieldMappings.OverviewListTarget.ToLowerInvariant()
                    : string.Empty)
                // Cast added in the People screen (directory + this episode's distribution): if it changes, the episode is updated
                + (string.IsNullOrEmpty(castFingerprint) ? string.Empty : "\ncast:" + castFingerprint);

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
        }

        private static string GetPath(Guid seasonId)
        {
            var folder = Plugin.Instance?.DataFolderPath;
            return string.IsNullOrEmpty(folder) ? null : Path.Combine(folder, FolderName, $"{seasonId:N}.json");
        }
    }

    public class CachedMissingEpisode
    {
        public string Hash { get; set; }

        /// <summary>URL of the thumbnail used (resolved)</summary>
        public string ThumbUrl { get; set; }

        /// <summary>Downloading ThumbUrl failed (retried if the URL changes, or via "Tout retraiter")</summary>
        public bool ThumbFailed { get; set; }

        /// <summary>Thumbnail saved with the LandscapePortraitThumbs option on (changing it → new download)</summary>
        public bool ThumbLandscape { get; set; }

        public string PreviewUrl { get; set; }

        /// <summary>Assigned episode number (kept from one analysis to the next, see AssignIndexNumbers)</summary>
        public int? IndexNumber { get; set; }

        public DateTime UpdatedAt { get; set; }

        /// <summary>JSON of the episode as returned by the API</summary>
        public JsonElement? Json { get; set; }
    }
}
