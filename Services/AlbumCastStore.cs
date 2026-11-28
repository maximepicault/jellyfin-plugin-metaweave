using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.CustomMetadata.Api;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>The people linked to one album, photo or video</summary>
    public class AlbumCastRecord
    {
        public List<CastMember> Members { get; set; } = new List<CastMember>();

        /// <summary>Videos only: the "name|type" keys written into Jellyfin's own cast, to take back only those</summary>
        public List<string> Written { get; set; } = new List<string>();

        /// <summary>The "name|type" keys of the members a source links (the People screen shows them read-only)</summary>
        public List<string> FromSource { get; set; } = new List<string>();
    }

    /// <summary>
    /// People of albums, photos and videos, by Jellyfin item id (DataFolderPath/album-cast.json). Jellyfin keeps no cast on a
    /// photo or an album (SupportsPeople is false and a direct write stores nothing), so the plugin keeps the links itself; the
    /// theme shows them. Rebuilt by the providers when an item is enriched.
    /// </summary>
    public class AlbumCastStore
    {
        private readonly ILogger _logger;
        private readonly object _lock = new object();
        private Dictionary<string, AlbumCastRecord> _records;

        public AlbumCastStore(ILogger logger)
        {
            _logger = logger;
        }

        private static string FilePath()
            => Path.Combine(Plugin.Instance?.DataFolderPath ?? Path.Combine(Path.GetTempPath(), "CustomMetadata"), "album-cast.json");

        private Dictionary<string, AlbumCastRecord> Records()
        {
            if (_records != null)
            {
                return _records;
            }

            try
            {
                var path = FilePath();
                _records = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, AlbumCastRecord>>(File.ReadAllText(path)) : null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "👥 [ALBUM CAST] ❌ Cannot read {Path}", FilePath());
            }

            return _records ??= new Dictionary<string, AlbumCastRecord>();
        }

        public AlbumCastRecord Get(Guid itemId)
        {
            lock (_lock)
            {
                return Records().TryGetValue(itemId.ToString("N"), out var record) ? record : null;
            }
        }

        public List<(Guid ItemId, AlbumCastRecord Record)> All()
        {
            lock (_lock)
            {
                return Records().Select(p => (Guid.Parse(p.Key), p.Value)).ToList();
            }
        }

        /// <summary>Replaces the links of an item; an empty record removes it. Writes the file only when something changed.</summary>
        public void Set(Guid itemId, AlbumCastRecord record)
        {
            var key = itemId.ToString("N");
            lock (_lock)
            {
                var records = Records();
                var empty = record == null || (record.Members.Count == 0 && record.Written.Count == 0);
                if (records.TryGetValue(key, out var known) ? !empty && JsonSerializer.Serialize(known) == JsonSerializer.Serialize(record) : empty)
                {
                    return;
                }

                if (empty)
                {
                    records.Remove(key);
                }
                else
                {
                    records[key] = record;
                }

                var path = FilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(records));
                File.Move(tmp, path, true);
            }
        }
    }
}
