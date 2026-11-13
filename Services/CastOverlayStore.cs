using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>
    /// Cast added to ONE element, designated by its type and its "{prefix-id}" tag (without braces).
    /// The type is part of the key: the same identifier can exist for a series and for an episode of the same source.
    /// </summary>
    public class EpisodeCastEntry
    {
        /// <summary>"series", "season" or "episode" (default: episode)</summary>
        public string Type { get; set; } = "episode";

        public string Key { get; set; }

        public List<LocalCastEntry> Cast { get; set; } = new List<LocalCastEntry>();
    }

    public class EpisodeCastFile
    {
        public int Version { get; set; } = 1;

        public List<EpisodeCastEntry> Episodes { get; set; } = new List<EpisodeCastEntry>();
    }

    /// <summary>
    /// Cast of episodes that come from an API source: the API is never modified, the cast is
    /// attached to the episode's tag ("dm-a1v1") in DataFolderPath/episode-cast.json. It is added to the API's
    /// cast, if any (without duplicates), when the episode is enriched. The people come from the directory (PeopleStore).
    /// </summary>
    public class CastOverlayStore
    {
        private static readonly JsonSerializerOptions FileOptions = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };

        private readonly ILogger _logger;
        private readonly object _lock = new object();
        private (DateTime Stamp, Dictionary<string, List<LocalCastEntry>> Map, EpisodeCastFile File)? _cached;

        public CastOverlayStore(ILogger logger)
        {
            _logger = logger;
        }

        private static string FilePath()
        {
            var root = Plugin.Instance?.DataFolderPath ?? Path.Combine(Path.GetTempPath(), "CustomMetadata");
            return Path.Combine(root, "episode-cast.json");
        }

        public static string NormalizeKey(string key) => (key ?? string.Empty).Trim().Trim('{', '}').Trim().ToLowerInvariant();

        public static string NormalizeType(string type)
        {
            var t = (type ?? string.Empty).Trim().ToLowerInvariant();
            return t == "series" || t == "season" ? t : "episode";
        }

        /// <summary>Index key: "episode:dm-a1v1"</summary>
        public static string TypedKey(string type, string key) => NormalizeType(type) + ":" + NormalizeKey(key);

        public EpisodeCastFile Load()
        {
            lock (_lock)
            {
                var path = FilePath();
                if (!File.Exists(path))
                {
                    return new EpisodeCastFile();
                }

                var stamp = File.GetLastWriteTimeUtc(path);
                if (_cached.HasValue && _cached.Value.Stamp == stamp)
                {
                    return _cached.Value.File;
                }

                try
                {
                    var file = JsonSerializer.Deserialize<EpisodeCastFile>(File.ReadAllText(path), FileOptions) ?? new EpisodeCastFile();
                    file.Episodes ??= new List<EpisodeCastEntry>();
                    _cached = (stamp, Index(file), file);
                    return file;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "👥 [CAST OVERLAY] ❌ Cannot read {Path}", path);
                    return new EpisodeCastFile();
                }
            }
        }

        private static Dictionary<string, List<LocalCastEntry>> Index(EpisodeCastFile file)
        {
            return file.Episodes.GroupBy(e => TypedKey(e.Type, e.Key)).ToDictionary(g => g.Key, g => g.First().Cast ?? new List<LocalCastEntry>());
        }

        /// <summary>Normalises (keys, valid types, people without duplicates, empty entries removed) and saves</summary>
        public EpisodeCastFile Save(EpisodeCastFile file)
        {
            file ??= new EpisodeCastFile();
            file.Episodes = (file.Episodes ?? new List<EpisodeCastEntry>())
                .Where(e => !string.IsNullOrWhiteSpace(e?.Key))
                .GroupBy(e => TypedKey(e.Type, e.Key))
                .Select(g => new EpisodeCastEntry
                {
                    Type = NormalizeType(g.First().Type),
                    Key = NormalizeKey(g.First().Key),
                    Cast = LocalSourceStore.NormalizeCast(g.SelectMany(e => e.Cast ?? new List<LocalCastEntry>()))
                })
                .Where(e => e.Cast.Count > 0)
                .OrderBy(e => e.Type, StringComparer.Ordinal).ThenBy(e => e.Key, StringComparer.Ordinal)
                .ToList();

            lock (_lock)
            {
                var path = FilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(file, FileOptions));
                if (File.Exists(path))
                {
                    File.Copy(path, path + ".bak", true);
                }

                File.Move(tmp, path, true);
                _cached = (File.GetLastWriteTimeUtc(path), Index(file), file);
            }

            _logger.LogInformation("👥 [CAST OVERLAY] 💾 Saved cast for {Count} episodes", file.Episodes.Count);
            return file;
        }

        /// <summary>Cast attached to these tags for this element type (file with several tags: each one's, first occurrence of a person kept)</summary>
        public List<LocalCastEntry> Get(string type, IEnumerable<string> keys)
        {
            Load();
            var result = new List<LocalCastEntry>();
            Dictionary<string, List<LocalCastEntry>> map;
            lock (_lock)
            {
                map = _cached?.Map;
            }

            if (map == null)
            {
                return result;
            }

            foreach (var key in (keys ?? Enumerable.Empty<string>()).Select(k => TypedKey(type, k)).Distinct())
            {
                if (map.TryGetValue(key, out var entries))
                {
                    foreach (var entry in entries.Where(e => !result.Any(r => string.Equals(r.PersonId, e.PersonId, StringComparison.OrdinalIgnoreCase))))
                    {
                        result.Add(entry);
                    }
                }
            }

            return result;
        }
    }
}
