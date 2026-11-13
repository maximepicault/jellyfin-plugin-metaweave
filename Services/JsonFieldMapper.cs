using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.CustomMetadata.Api;
using Jellyfin.Plugin.CustomMetadata.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>
    /// Service mapping the JSON fields of your API to Jellyfin objects
    /// Supports nested JSON paths (e.g. "data.info.title")
    /// </summary>
    public class JsonFieldMapper
    {
        private readonly ILogger _logger;

        public JsonFieldMapper(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Maps a JSON response to CustomMetadataResponse
        /// </summary>
        /// <param name="jsonDoc">JSON document to map</param>
        /// <param name="mapping">Mapping to use (optional). If null, uses the default VideoFieldMappings</param>
        public CustomMetadataResponse MapToMetadata(JsonDocument jsonDoc, FieldMapping mapping = null)
        {
            _logger.LogInformation("🔄 [JSON MAPPER] ========== Starting JSON mapping ==========");
            
            // Uses the supplied mapping, otherwise the default VideoFieldMappings
            var config = mapping 
                ?? Plugin.Instance?.Configuration?.VideoFieldMappings 
                ?? Plugin.Instance?.Configuration?.FieldMappings 
                ?? new FieldMapping();
            
            _logger.LogDebug("🔄 [JSON MAPPER] Configuration mapping:");
            _logger.LogDebug("  - Title: {TitlePath}", config.Title ?? "(not configured)");
            _logger.LogDebug("  - Overview: {OverviewPath}", config.Overview ?? "(not configured)");
            _logger.LogDebug("  - PremiereDate: {PremiereDatePath}", config.PremiereDate ?? "(not configured)");
            _logger.LogDebug("  - CommunityRating: {RatingPath}", config.CommunityRating ?? "(not configured)");
            _logger.LogDebug("  - Tags: {TagsPath}", config.Tags ?? "(not configured)");
            _logger.LogDebug("  - Genres: {GenresPath}", config.Genres ?? "(not configured)");
            
            var root = jsonDoc.RootElement;
            
            // Log of the received JSON (truncated)
            var jsonString = jsonDoc.RootElement.GetRawText();
            var preview = jsonString.Length > 500 ? jsonString.Substring(0, 500) + "..." : jsonString;
            _logger.LogDebug("🔄 [JSON MAPPER] Raw JSON received: {JsonPreview}", preview);

            var response = new CustomMetadataResponse
            {
                Title = GetStringValue(root, config.Title, "Title"),
                Overview = GetStringValue(root, config.Overview, "Overview"),
                PremiereDate = GetDateTimeValue(root, config.PremiereDate, "PremiereDate"),
                CommunityRating = GetFloatValue(root, config.CommunityRating, "CommunityRating"),
                SeriesName = GetStringValue(root, config.SeriesName, "SeriesName"),                Tags = GetStringArrayValue(root, config.Tags, config.TagsItemField, "Tags"),
                Genres = GetStringArrayValue(root, config.Genres, config.GenresItemField, "Genres"),
                Cast = GetCastArray(root, config),
                IndexNumber = GetIntValue(root, config.IndexNumber)
            };

            // HTML entities ("&amp;") → text, before detecting a list-overview
            HtmlText.DecodeInPlace(response);
            ApplyOverviewList(response, config.OverviewListTarget);

            _logger.LogInformation("✅ [JSON MAPPER] Mapping completed:");
            _logger.LogInformation("  - Title: {Title}", response.Title ?? "(empty)");
            _logger.LogInformation("  - Overview: {Overview}", response.Overview?.Substring(0, Math.Min(50, response.Overview?.Length ?? 0)) + "..." ?? "(empty)");
            _logger.LogInformation("  - PremiereDate: {PremiereDate}", response.PremiereDate?.ToString("yyyy-MM-dd") ?? "(empty)");
            _logger.LogInformation("  - CommunityRating: {Rating}", response.CommunityRating?.ToString() ?? "(empty)");
            _logger.LogInformation("  - Tags: {TagCount} tags", response.Tags?.Count ?? 0);
            if (response.Tags != null && response.Tags.Any())
            {
                _logger.LogInformation("    Tags: [{Tags}]", string.Join(", ", response.Tags));
            }
            _logger.LogInformation("  - Genres: {GenreCount} genres", response.Genres?.Count ?? 0);
            if (response.Genres != null && response.Genres.Any())
            {
                _logger.LogInformation("    Genres: [{Genres}]", string.Join(", ", response.Genres));
            }
            _logger.LogInformation("  - Cast: {CastCount} members", response.Cast?.Count ?? 0);
            _logger.LogInformation("  - IndexNumber: {IndexNumber}", response.IndexNumber?.ToString() ?? "(empty)");

            return response;
        }

        /// <summary>
        /// PersonEndpoint response → name, photo, bio (HTML entities decoded). Missing name = null
        /// </summary>
        public PersonData MapToPerson(JsonDocument jsonDoc, PersonMapping mapping)
        {
            mapping ??= new PersonMapping();
            var root = jsonDoc.RootElement;
            string Read(string path) => string.IsNullOrWhiteSpace(path) ? null : HtmlText.Decode(GetStringValue(root, path));
            var name = Read(mapping.Name)?.Trim();
            return string.IsNullOrEmpty(name)
                ? null
                : new PersonData { Name = name, Photo = GetStringValue(root, mapping.Photo)?.Trim(), Bio = Read(mapping.Bio)?.Trim() };
        }

        /// <summary>
        /// Overview that is only a list of tags/genres: added to the target list, overview cleared
        /// </summary>
        private void ApplyOverviewList(CustomMetadataResponse response, string target)
        {
            if (!OverviewListDetector.IsEnabled(target))
            {
                return;
            }

            var items = OverviewListDetector.TryParse(response.Overview);
            if (items == null)
            {
                _logger.LogDebug("🏷️ [JSON MAPPER] Overview is regular text, kept as description");
                return;
            }

            var genres = OverviewListDetector.IsGenres(target);
            var list = (genres ? response.Genres : response.Tags) ?? new List<string>();
            foreach (var item in items)
            {
                if (!list.Contains(item, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(item);
                }
            }

            if (genres)
            {
                response.Genres = list;
            }
            else
            {
                response.Tags = list;
            }

            _logger.LogInformation("🏷️ [JSON MAPPER] Overview is a list → moved {Count} items to {Target}: [{Items}] (description cleared)",
                items.Count, genres ? "genres" : "tags", string.Join(", ", items));
            response.Overview = null;
        }

        /// <summary>
        /// Maps a JSON response to a list of images
        /// </summary>
        /// <summary>
        /// Maps the EpisodeListEndpoint response. Returns null if the array is not found
        /// (bad config): not to be confused with an empty array (source with no episode).
        /// </summary>
        public List<EpisodeListItem> MapToEpisodeList(JsonDocument jsonDoc, EpisodeListMapping mapping)
        {
            mapping ??= new EpisodeListMapping();
            var result = new List<EpisodeListItem>();

            if (!JsonPathExtractor.TryGetArray(jsonDoc.RootElement, mapping.ItemsPath, out var items))
            {
                _logger.LogWarning("📃 [EPISODE LIST] ⚠️ No array found at path '{ItemsPath}' (empty = root) - check 'Tableau d'épisodes' mapping", mapping.ItemsPath ?? string.Empty);
                return null;
            }

            foreach (var item in items)
            {
                var id = GetListString(item, mapping.Id);
                if (string.IsNullOrWhiteSpace(id))
                {
                    _logger.LogDebug("📃 [EPISODE LIST] Item without ID at path '{IdPath}' - skipped", mapping.Id);
                    continue;
                }

                var indexText = GetListString(item, mapping.IndexNumber);
                var durationText = GetListString(item, mapping.Duration);
                var duration = DurationParser.Parse(durationText);
                if (durationText != null && duration == null)
                {
                    _logger.LogWarning("📃 [EPISODE LIST] ⚠️ Unrecognized duration '{Duration}' at path '{Path}' for episode {Id} (expected mm:ss, hh:mm:ss, seconds or ISO 8601)",
                        durationText, mapping.Duration, id);
                }

                result.Add(new EpisodeListItem
                {
                    Id = id.Trim(),
                    Title = HtmlText.Decode(GetListString(item, mapping.Title)),
                    Overview = HtmlText.Decode(GetListString(item, mapping.Overview)),
                    PremiereDate = string.IsNullOrWhiteSpace(mapping.PremiereDate) ? null : JsonPathExtractor.GetDateTime(item, mapping.PremiereDate),
                    IndexNumber = int.TryParse(indexText, out var index) ? index : null,
                    RunTimeTicks = duration?.Ticks,
                    ThumbUrl = GetListString(item, mapping.ThumbUrl),
                    PreviewUrl = GetListString(item, mapping.PreviewUrl),
                    RawJson = item.GetRawText()
                });
            }

            _logger.LogInformation("📃 [EPISODE LIST] Mapped {Count} episodes from {Total} items", result.Count, items.Count);
            return result;
        }

        /// <summary>
        /// Empty path = field ignored (JsonPathExtractor.GetString would return the whole element)
        /// </summary>
        private static string GetListString(JsonElement item, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var value = JsonPathExtractor.GetString(item, path, null);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        public List<ImageInfo> MapToImages(JsonDocument jsonDoc, FieldMapping mapping = null)
        {
            _logger.LogInformation("🖼️ [IMAGE MAPPER] ========== Starting image mapping ==========");
            
            // If no mapping is specified, use the default video mapping
            var config = mapping ?? Plugin.Instance?.Configuration?.VideoFieldMappings ?? new FieldMapping();
            var images = new List<ImageInfo>();

            // Log of the received JSON (preview)
            var jsonString = jsonDoc.RootElement.GetRawText();
            var preview = jsonString.Length > 500 ? jsonString.Substring(0, 500) + "..." : jsonString;
            _logger.LogDebug("🖼️ [IMAGE MAPPER] JSON preview: {JsonPreview}", preview);

            _logger.LogInformation("🖼️ [IMAGE MAPPER] === FULL IMAGE CONFIGURATION ===");
            _logger.LogInformation("🖼️ [IMAGE MAPPER] Direct URLs:");
            _logger.LogInformation("  - PosterUrl: '{PosterUrl}'", config.PosterUrl ?? "(null)");
            _logger.LogInformation("  - BackdropUrl: '{BackdropUrl}'", config.BackdropUrl ?? "(null)");
            _logger.LogInformation("  - ThumbUrl: '{ThumbUrl}'", config.ThumbUrl ?? "(null)");
            _logger.LogInformation("  - LogoUrl: '{LogoUrl}'", config.LogoUrl ?? "(null)");
            _logger.LogInformation("  - BannerUrl: '{BannerUrl}'", config.BannerUrl ?? "(null)");
            _logger.LogInformation("🖼️ [IMAGE MAPPER] Array configuration:");
            _logger.LogInformation("  - ImagesArray: '{ImagesArray}'", config.ImagesArray ?? "(null)");
            _logger.LogInformation("  - ImageUrl: '{ImageUrl}'", config.ImageUrl ?? "(null)");
            _logger.LogInformation("  - ImageType: '{ImageType}'", config.ImageType ?? "(null)");
            _logger.LogInformation("  - ImageWidth: '{ImageWidth}'", config.ImageWidth ?? "(null)");
            _logger.LogInformation("  - ImageHeight: '{ImageHeight}'", config.ImageHeight ?? "(null)");
            _logger.LogInformation("🖼️ [IMAGE MAPPER] === END CONFIGURATION ===");

            try
            {
                var root = jsonDoc.RootElement;

                // 1. Look for direct URLs in the root JSON
                _logger.LogInformation("🖼️ [IMAGE MAPPER] === Step 1: Checking direct image URLs ===");
                AddDirectImageUrl(root, config.PosterUrl, "Primary", images);
                AddDirectImageUrl(root, config.BackdropUrl, "Backdrop", images);
                AddDirectImageUrl(root, config.ThumbUrl, "Thumb", images);
                AddDirectImageUrl(root, config.LogoUrl, "Logo", images);
                AddDirectImageUrl(root, config.BannerUrl, "Banner", images);

                // 2. Look for an images array (if configured)
                _logger.LogInformation("🖼️ [IMAGE MAPPER] === Step 2: Checking images array ===");
                if (!string.IsNullOrWhiteSpace(config.ImagesArray))
                {
                    _logger.LogDebug("🖼️ [IMAGE MAPPER] Step 2: Checking images array at path '{ImagesArray}'", config.ImagesArray);
                    if (TryNavigateToPath(root, config.ImagesArray, out var imagesArrayElement))
                    {
                        if (imagesArrayElement.ValueKind == JsonValueKind.Array)
                        {
                            _logger.LogDebug("🖼️ [IMAGE MAPPER] Found array with {Count} items", imagesArrayElement.GetArrayLength());
                            foreach (var item in imagesArrayElement.EnumerateArray())
                            {
                                var image = new ImageInfo
                                {
                                    Url = GetStringValue(item, config.ImageUrl),
                                    Type = GetStringValue(item, config.ImageType),
                                    Width = GetIntValue(item, config.ImageWidth),
                                    Height = GetIntValue(item, config.ImageHeight)
                                };

                                if (!string.IsNullOrEmpty(image.Url))
                                {
                                    _logger.LogDebug("🖼️ [IMAGE MAPPER] Added image: Type={Type}, Url={Url}", image.Type ?? "(none)", image.Url);
                                    images.Add(image);
                                }
                            }
                        }
                        else
                        {
                            _logger.LogWarning("🖼️ [IMAGE MAPPER] ⚠️ Path '{ImagesArray}' exists but is NOT an array (type: {Type})", 
                                config.ImagesArray, imagesArrayElement.ValueKind);
                        }
                    }
                    else
                    {
                        _logger.LogWarning("🖼️ [IMAGE MAPPER] ⚠️ Images array path '{ImagesArray}' NOT FOUND in JSON", config.ImagesArray);
                    }
                }
                else
                {
                    _logger.LogDebug("🖼️ [IMAGE MAPPER] Step 2: Skipped (ImagesArray not configured)");
                }

                // 3. If the root itself is an array (legacy)
                if (root.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in root.EnumerateArray())
                    {
                        var image = new ImageInfo
                        {
                            Url = GetStringValue(item, config.ImageUrl),
                            Type = GetStringValue(item, config.ImageType),
                            Width = GetIntValue(item, config.ImageWidth),
                            Height = GetIntValue(item, config.ImageHeight)
                        };

                        if (!string.IsNullOrEmpty(image.Url))
                        {
                            images.Add(image);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "🖼️ [IMAGE MAPPER] ❌ Error mapping images from JSON: {Message}", ex.Message);
            }

            _logger.LogInformation("✅ [IMAGE MAPPER] Mapping completed: {ImageCount} images found", images.Count);
            if (images.Any())
            {
                foreach (var img in images)
                {
                    _logger.LogInformation("  - Image: Type={Type}, Url={Url}", img.Type ?? "(none)", img.Url);
                }
            }
            else
            {
                _logger.LogWarning("⚠️ [IMAGE MAPPER] No images found - check your JSON and configuration paths");
            }

            return images;
        }

        /// <summary>
        /// Adds a direct image URL if it exists in the JSON
        /// </summary>
        private void AddDirectImageUrl(JsonElement root, string urlPath, string imageType, List<ImageInfo> images)
        {
            _logger.LogDebug("🖼️ [IMAGE MAPPER] Checking {ImageType} at path: '{Path}'", imageType, urlPath ?? "(null)");
            
            if (string.IsNullOrWhiteSpace(urlPath))
            {
                _logger.LogDebug("🖼️ [IMAGE MAPPER] Skipping {ImageType}: path not configured", imageType);
                return;
            }

            var url = GetStringValue(root, urlPath, $"Image-{imageType}");
            if (!string.IsNullOrEmpty(url))
            {
                _logger.LogInformation("🖼️ [IMAGE MAPPER] ✅ FOUND {ImageType} at path '{Path}': {Url}", imageType, urlPath, url);
                images.Add(new ImageInfo
                {
                    Url = url,
                    Type = imageType
                });
            }
            else
            {
                _logger.LogWarning("🖼️ [IMAGE MAPPER] ❌ {ImageType} NOT FOUND at path '{Path}'", imageType, urlPath);
            }
        }

        #region Private Methods

        /// <summary>
        /// Gets a string value from a JSON path
        /// Supporte: "title", "data.title", "metadata.info.title"
        /// If path is empty OR the path does not exist in the JSON, returns null
        /// </summary>
        private string GetStringValue(JsonElement element, string path, string fieldName = null)
        {
            // If path is empty, skip the field
            if (string.IsNullOrWhiteSpace(path))
            {
                if (fieldName != null)
                {
                    _logger.LogDebug("🔄 [JSON MAPPER] {FieldName}: path not configured, skipping", fieldName);
                }
                return null;
            }

            // Try to navigate to the path
            if (!TryNavigateToPath(element, path, out var value))
            {
                // The path does not exist in the JSON - normal, returns null
                if (fieldName != null)
                {
                    _logger.LogWarning("⚠️ [JSON MAPPER] {FieldName}: path '{Path}' NOT FOUND in JSON", fieldName, path);
                }
                return null;
            }

            var result = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            
            if (fieldName != null)
            {
                if (result != null)
                {
                    var preview = result.Length > 100 ? result.Substring(0, 100) + "..." : result;
                    _logger.LogDebug("✅ [JSON MAPPER] {FieldName}: extracted '{Value}' from path '{Path}'", fieldName, preview, path);
                }
                else
                {
                    _logger.LogWarning("⚠️ [JSON MAPPER] {FieldName}: path '{Path}' exists but value is NOT A STRING (type: {Type})", 
                        fieldName, path, value.ValueKind);
                }
            }
            
            return result;
        }

        /// <summary>
        /// Gets a DateTime value from a JSON path
        /// If path is empty OR the path does not exist in the JSON, returns null
        /// </summary>
        private DateTime? GetDateTimeValue(JsonElement element, string path, string fieldName = null)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                if (fieldName != null)
                {
                    _logger.LogDebug("🔄 [JSON MAPPER] {FieldName}: path not configured, skipping", fieldName);
                }
                return null;
            }

            if (!TryNavigateToPath(element, path, out var value))
            {
                if (fieldName != null)
                {
                    _logger.LogWarning("⚠️ [JSON MAPPER] {FieldName}: path '{Path}' NOT FOUND in JSON", fieldName, path);
                }
                return null;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                var stringValue = value.GetString();
                if (DateTime.TryParse(stringValue, out var date))
                {
                    if (fieldName != null)
                    {
                        _logger.LogDebug("✅ [JSON MAPPER] {FieldName}: extracted date '{Date}' from path '{Path}'", 
                            fieldName, date.ToString("yyyy-MM-dd"), path);
                    }
                    return date;
                }
                else
                {
                    if (fieldName != null)
                    {
                        _logger.LogWarning("⚠️ [JSON MAPPER] {FieldName}: path '{Path}' has value '{Value}' which is NOT A VALID DATE", 
                            fieldName, path, stringValue);
                    }
                }
            }
            else
            {
                if (fieldName != null)
                {
                    _logger.LogWarning("⚠️ [JSON MAPPER] {FieldName}: path '{Path}' exists but value is NOT A STRING (type: {Type})", 
                        fieldName, path, value.ValueKind);
                }
            }

            return null;
        }

        /// <summary>
        /// Gets a float value from a JSON path
        /// If path is empty OR the path does not exist in the JSON, returns null
        /// </summary>
        private float? GetFloatValue(JsonElement element, string path, string fieldName = null)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                if (fieldName != null)
                {
                    _logger.LogDebug("🔄 [JSON MAPPER] {FieldName}: path not configured, skipping", fieldName);
                }
                return null;
            }

            if (!TryNavigateToPath(element, path, out var value))
            {
                if (fieldName != null)
                {
                    _logger.LogWarning("⚠️ [JSON MAPPER] {FieldName}: path '{Path}' NOT FOUND in JSON", fieldName, path);
                }
                return null;
            }
            
            if (value.ValueKind == JsonValueKind.Number)
            {
                var result = value.GetSingle();
                if (fieldName != null)
                {
                    _logger.LogDebug("✅ [JSON MAPPER] {FieldName}: extracted number {Value} from path '{Path}'", 
                        fieldName, result, path);
                }
                return result;
            }
            
            if (value.ValueKind == JsonValueKind.String)
            {
                var stringValue = value.GetString();
                if (float.TryParse(stringValue, out var floatValue))
                {
                    if (fieldName != null)
                    {
                        _logger.LogDebug("✅ [JSON MAPPER] {FieldName}: parsed string '{StringValue}' as number {Value} from path '{Path}'", 
                            fieldName, stringValue, floatValue, path);
                    }
                    return floatValue;
                }
                else
                {
                    if (fieldName != null)
                    {
                        _logger.LogWarning("⚠️ [JSON MAPPER] {FieldName}: path '{Path}' has value '{Value}' which is NOT A VALID NUMBER", 
                            fieldName, path, stringValue);
                    }
                }
            }
            else
            {
                if (fieldName != null)
                {
                    _logger.LogWarning("⚠️ [JSON MAPPER] {FieldName}: path '{Path}' exists but value is NOT A NUMBER or STRING (type: {Type})", 
                        fieldName, path, value.ValueKind);
                }
            }
            
            return null;
        }

        /// <summary>
        /// Gets an int value from a JSON path
        /// If path is empty OR the path does not exist in the JSON, returns null
        /// </summary>
        private int? GetIntValue(JsonElement element, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            if (!TryNavigateToPath(element, path, out var value))
            {
                return null;
            }
            
            if (value.ValueKind == JsonValueKind.Number)
            {
                // TryGetInt32: "3.0" or an out-of-range number no longer throw
                if (value.TryGetInt32(out var number))
                {
                    return number;
                }

                return value.TryGetDouble(out var real) && real >= int.MinValue && real <= int.MaxValue ? (int)real : null;
            }
            
            if (value.ValueKind == JsonValueKind.String)
            {
                if (int.TryParse(value.GetString(), out var intValue))
                {
                    return intValue;
                }
            }
            
            return null;
        }

        /// <summary>
        /// Gets a string array from a JSON path
        /// Supporte plusieurs formats :
        /// 1. Notation with []: "tagList[].label" → extracts "label" from each element
        /// 2. Tableau de strings: ["tag1", "tag2"]
        /// 3. Array of objects with a separate itemField: tagList + label
        /// 4. String simple: "tag1, tag2"
        /// </summary>
        private List<string> GetStringArrayValue(JsonElement element, string path, string itemField = null, string fieldName = null)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                if (fieldName != null)
                {
                    _logger.LogDebug("🔄 [JSON MAPPER] {FieldName}: path not configured, skipping", fieldName);
                }
                return new List<string>();
            }

            // Detection of the array[].field notation
            string arrayPath = path;
            string extractField = itemField;
            
            if (path.Contains("[]"))
            {
                var parts = path.Split(new[] { "[]" }, StringSplitOptions.None);
                arrayPath = parts[0].TrimEnd('.');
                
                if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]))
                {
                    extractField = parts[1].TrimStart('.');
                }
                
                if (fieldName != null)
                {
                    _logger.LogDebug("🔄 [JSON MAPPER] {FieldName}: detected [] notation - array path: '{ArrayPath}', extract field: '{ExtractField}'", 
                        fieldName, arrayPath, extractField ?? "(none)");
                }
            }

            // Navigation to the array
            if (!TryNavigateToPath(element, arrayPath, out var value))
            {
                if (fieldName != null)
                {
                    _logger.LogWarning("⚠️ [JSON MAPPER] {FieldName}: path '{Path}' NOT FOUND in JSON", fieldName, arrayPath);
                }
                return new List<string>();
            }
            
            if (value.ValueKind == JsonValueKind.Array)
            {
                var results = new List<string>();
                
                foreach (var item in value.EnumerateArray())
                {
                    // Cas 1: Tableau de strings simples
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var strValue = item.GetString();
                        if (!string.IsNullOrEmpty(strValue))
                        {
                            results.Add(strValue);
                        }
                    }
                    // Case 2: array of objects - extract a specific field
                    else if (item.ValueKind == JsonValueKind.Object && !string.IsNullOrWhiteSpace(extractField))
                    {
                        var fieldValue = GetStringValue(item, extractField);
                        if (!string.IsNullOrEmpty(fieldValue))
                        {
                            results.Add(fieldValue);
                        }
                    }
                }
                
                if (fieldName != null)
                {
                    if (results.Any())
                    {
                        _logger.LogDebug("✅ [JSON MAPPER] {FieldName}: extracted {Count} items from array at path '{Path}': [{Items}]", 
                            fieldName, results.Count, arrayPath, string.Join(", ", results.Take(10)));
                    }
                    else
                    {
                        _logger.LogWarning("⚠️ [JSON MAPPER] {FieldName}: array at path '{Path}' is EMPTY or items don't match expected format", 
                            fieldName, arrayPath);
                    }
                }
                
                return results;
            }
            
            // Case 3: simple comma-separated string
            if (value.ValueKind == JsonValueKind.String)
            {
                var str = value.GetString();
                if (!string.IsNullOrEmpty(str))
                {
                    var results = str.Split(',')
                        .Select(s => s.Trim())
                        .Where(s => !string.IsNullOrEmpty(s))
                        .ToList();
                    
                    if (fieldName != null)
                    {
                        _logger.LogDebug("✅ [JSON MAPPER] {FieldName}: parsed comma-separated string into {Count} items: [{Items}]", 
                            fieldName, results.Count, string.Join(", ", results));
                    }
                    
                    return results;
                }
            }
            
            if (fieldName != null)
            {
                _logger.LogWarning("⚠️ [JSON MAPPER] {FieldName}: path '{Path}' exists but value is NOT AN ARRAY or STRING (type: {Type})", 
                    fieldName, arrayPath, value.ValueKind);
            }
            
            return new List<string>();
        }

        /// <summary>
        /// Gets a cast array from a JSON path
        /// If path is empty OR the path does not exist in the JSON, returns an empty list
        /// </summary>
        private List<CastMember> GetCastArray(JsonElement element, FieldMapping config)
        {
            if (string.IsNullOrWhiteSpace(config.Cast))
            {
                return new List<CastMember>();
            }

            if (!TryNavigateToPath(element, config.Cast, out var value))
            {
                return new List<CastMember>();
            }
            
            if (value.ValueKind == JsonValueKind.Array)
            {
                return value.EnumerateArray()
                    .Select(castElement => new CastMember
                    {
                        Name = GetStringValue(castElement, config.CastName),
                        Role = GetStringValue(castElement, config.CastRole),
                        Type = GetStringValue(castElement, config.CastType),
                        Order = GetIntValue(castElement, config.CastOrder),
                        Image = string.IsNullOrWhiteSpace(config.CastImage) ? null : GetStringValue(castElement, config.CastImage)
                    })
                    .Where(c => !string.IsNullOrEmpty(c.Name))
                    .ToList();
            }
            
            return new List<CastMember>();
        }

        /// <summary>
        /// Navigates a JSON object following a path
        /// Exemples:
        /// - "title" → element["title"]
        /// - "data.title" → element["data"]["title"]
        /// - "metadata.info.title" → element["metadata"]["info"]["title"]
        /// </summary>
        /// <returns>true if the path exists, false otherwise</returns>
        private bool TryNavigateToPath(JsonElement element, string path, out JsonElement result)
        {
            result = default;

            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var parts = path.Split('.');
            var current = element;

            foreach (var part in parts)
            {
                if (current.ValueKind != JsonValueKind.Object)
                {
                    // The path cannot be followed (not an object)
                    return false;
                }

                if (!current.TryGetProperty(part, out var next))
                {
                    // The property does not exist in the JSON
                    return false;
                }

                current = next;
            }

            result = current;
            return true;
        }

        /// <summary>
        /// Legacy version for compatibility (throws exceptions)
        /// </summary>
        [Obsolete("Use TryNavigateToPath instead")]
        private JsonElement NavigateToPath(JsonElement element, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Path cannot be null or empty");
            }

            var parts = path.Split('.');
            var current = element;

            foreach (var part in parts)
            {
                if (current.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException($"Cannot navigate to '{part}' - current element is not an object");
                }

                if (!current.TryGetProperty(part, out var next))
                {
                    throw new KeyNotFoundException($"Property '{part}' not found in JSON");
                }

                current = next;
            }

            return current;
        }

        #endregion
    }
}
