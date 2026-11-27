using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jellyfin.Plugin.CustomMetadata.Api;
using Jellyfin.Plugin.CustomMetadata.Configuration;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>One photo of an album's answer: its file name and what the source says about it</summary>
    public sealed record ListedPhoto(string File, CustomMetadataResponse Data);

    /// <summary>The list of photos inside an album's answer (SourceConfiguration.PhotoListPath / PhotoFileField)</summary>
    public static class PhotoList
    {
        /// <summary>The photos that name a file; null when the path leads to no list (or is empty)</summary>
        public static List<ListedPhoto> Read(JsonElement root, SourceConfiguration source, JsonFieldMapper mapper)
        {
            // A local source serves its photos in the default shape, whatever its saved settings say
            var path = source.IsLocal ? LocalSourceStore.PhotoListPath : source.PhotoListPath;
            var field = source.IsLocal ? LocalSourceStore.PhotoFileField : source.PhotoFileField;
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(field) || !JsonPathExtractor.TryGetArray(root, path, out var entries))
            {
                return null;
            }

            var mapping = source.IsLocal ? new FieldMapping() : source.PhotoFieldMappings ?? new FieldMapping();
            var photos = new List<ListedPhoto>();
            foreach (var entry in entries)
            {
                // The source may give a path or an address: only the file name counts
                var file = Path.GetFileName((JsonPathExtractor.GetString(entry, field) ?? string.Empty).Replace('\\', '/').Trim());
                if (file.Length == 0)
                {
                    continue;
                }

                using var document = JsonDocument.Parse(entry.GetRawText());
                photos.Add(new ListedPhoto(file, mapper.MapToMetadata(document, mapping, true) ?? new CustomMetadataResponse()));
            }

            return photos;
        }
    }
}
