using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>
    /// Cache service storing the custom API's responses
    /// Uses a memory cache with a disk fallback
    /// </summary>
    public class MetadataCacheService
    {
        private readonly IMemoryCache _memoryCache;
        private readonly ILogger _logger;
        private readonly string _cacheDirectory;
        private static readonly SemaphoreSlim _fileLock = new SemaphoreSlim(1, 1);

        public MetadataCacheService(
            IMemoryCache memoryCache,
            ILogger logger)
        {
            _memoryCache = memoryCache;
            _logger = logger;

            try
            {
                // Cache directory: /var/lib/jellyfin/plugins/CustomMetadata/cache
                if (Plugin.Instance == null)
                {
                    _logger.LogError("❌ [CACHE] CRITICAL: Plugin.Instance is NULL! Cache will not work!");
                    _cacheDirectory = Path.Combine(Path.GetTempPath(), "jellyfin-custom-metadata-cache");
                    _logger.LogWarning("⚠️ [CACHE] Using fallback cache directory: {CacheDirectory}", _cacheDirectory);
                }
                else if (string.IsNullOrEmpty(Plugin.Instance.DataFolderPath))
                {
                    _logger.LogError("❌ [CACHE] CRITICAL: Plugin.Instance.DataFolderPath is NULL or empty!");
                    _cacheDirectory = Path.Combine(Path.GetTempPath(), "jellyfin-custom-metadata-cache");
                    _logger.LogWarning("⚠️ [CACHE] Using fallback cache directory: {CacheDirectory}", _cacheDirectory);
                }
                else
                {
                    _cacheDirectory = Path.Combine(
                        Plugin.Instance.DataFolderPath,
                        "cache"
                    );
                }

                _logger.LogInformation("💾 [CACHE] Cache directory: {CacheDirectory}", _cacheDirectory);
                EnsureCacheDirectoryExists();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ [CACHE] CRITICAL ERROR during initialization!");
                _cacheDirectory = Path.Combine(Path.GetTempPath(), "jellyfin-custom-metadata-cache-emergency");
                _logger.LogWarning("⚠️ [CACHE] Using emergency cache directory: {CacheDirectory}", _cacheDirectory);
            }
        }

        /// <summary>
        /// Gets a value from the cache (memory then disk)
        /// </summary>
        public async Task<T> GetOrCreateAsync<T>(
            string key,
            Func<Task<T>> factory,
            CancellationToken cancellationToken = default) where T : class
        {
            _logger.LogDebug("💾 [CACHE] GetOrCreateAsync called for key: {Key}", key);
            
            var cacheKey = GetCacheKey(key);

            // 1. Check the memory cache
            if (_memoryCache.TryGetValue(cacheKey, out T cachedValue))
            {
                _logger.LogInformation("💾 [CACHE HIT - RAM] Key: {OriginalKey} → Using cached data from memory", key);
                return cachedValue;
            }

            // 2. Check the disk cache
            _logger.LogDebug("💾 [CACHE] Checking disk cache for key: {Key}", key);
            var diskValue = await GetFromDiskAsync<T>(cacheKey);
            if (diskValue != null)
            {
                _logger.LogInformation("💾 [CACHE HIT - DISK] Key: {OriginalKey} → Loaded from disk cache file: {CacheKey}.json", key, cacheKey);
                
                // Put in the memory cache for the next accesses
                SetInMemoryCache(cacheKey, diskValue);
                return diskValue;
            }

            // 3. Cache miss - call the factory
            _logger.LogInformation("❌ [CACHE MISS] Key: {OriginalKey} → Fetching fresh data from API", key);
            _logger.LogDebug("💾 [CACHE] Calling factory function...");
            
            var value = await factory();
            
            _logger.LogDebug("💾 [CACHE] Factory returned: {IsNull}", value == null ? "NULL" : "DATA");

            if (value != null)
            {
                _logger.LogDebug("💾 [CACHE] Storing value to cache...");
                
                // Store in memory and on disk
                await SetAsync(cacheKey, value, cancellationToken);
                
                _logger.LogInformation("✅ [CACHE STORED] Key: {OriginalKey} → Saved to cache file: {CacheKey}.json", key, cacheKey);
            }
            else
            {
                _logger.LogWarning("⚠️ [CACHE] Factory returned NULL for key: {Key} - NOT caching", key);
            }

            return value;
        }

        /// <summary>
        /// Stores a value in the cache (memory + disk)
        /// </summary>
        public async Task SetAsync<T>(
            string key,
            T value,
            CancellationToken cancellationToken = default) where T : class
        {
            _logger.LogDebug("💾 [CACHE] SetAsync called for key: {Key}", key);
            
            if (value == null)
            {
                _logger.LogWarning("💾 [CACHE] SetAsync: value is NULL, skipping");
                return;
            }

            var cacheKey = GetCacheKey(key);
            
            _logger.LogDebug("💾 [CACHE] SetAsync: cacheKey = {CacheKey}", cacheKey);

            // Store in memory
            _logger.LogDebug("💾 [CACHE] Storing in memory...");
            SetInMemoryCache(cacheKey, value);

            // Store on disk
            _logger.LogDebug("💾 [CACHE] Storing on disk...");
            await SaveToDiskAsync(cacheKey, value, cancellationToken);

            _logger.LogDebug("💾 [CACHE] SetAsync completed for key: {CacheKey}", cacheKey);
        }

        /// <summary>
        /// Invalidates a cache entry
        /// </summary>
        public async Task InvalidateAsync(string key)
        {
            var cacheKey = GetCacheKey(key);

            // Remove from the memory cache
            _memoryCache.Remove(cacheKey);

            // Remove from disk
            var filePath = GetCacheFilePath(cacheKey);
            if (File.Exists(filePath))
            {
                await _fileLock.WaitAsync();
                try
                {
                    File.Delete(filePath);
                    _logger.LogDebug("Cache invalidated: {CacheKey}", cacheKey);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error deleting cache file: {FilePath}", filePath);
                }
                finally
                {
                    _fileLock.Release();
                }
            }
        }

        /// <summary>
        /// Clears the whole cache
        /// </summary>
        public async Task ClearAllAsync()
        {
            _logger.LogInformation("Clearing all cache");

            // Note: IMemoryCache does not support a global Clear()
            // We only delete the files on disk
            await _fileLock.WaitAsync();
            try
            {
                if (Directory.Exists(_cacheDirectory))
                {
                    var files = Directory.GetFiles(_cacheDirectory, "*.json");
                    foreach (var file in files)
                    {
                        try
                        {
                            File.Delete(file);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error deleting cache file: {File}", file);
                        }
                    }
                    _logger.LogInformation("Cleared {Count} cache files", files.Length);
                }
            }
            finally
            {
                _fileLock.Release();
            }
        }

        /// <summary>
        /// Gets the cache statistics
        /// </summary>
        public CacheStats GetStats()
        {
            var stats = new CacheStats();

            if (Directory.Exists(_cacheDirectory))
            {
                var files = Directory.GetFiles(_cacheDirectory, "*.json");
                stats.DiskCacheCount = files.Length;
                
                foreach (var file in files)
                {
                    try
                    {
                        var info = new FileInfo(file);
                        stats.TotalSizeBytes += info.Length;
                    }
                    catch { }
                }
            }

            return stats;
        }

        #region Private Methods

        private void SetInMemoryCache<T>(string cacheKey, T value)
        {
            var ttl = GetCacheTTL();
            var cacheOptions = new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = ttl,
                SlidingExpiration = ttl / 2 // Renews if accessed before expiry
            };

            _memoryCache.Set(cacheKey, value, cacheOptions);
        }

        private async Task<T> GetFromDiskAsync<T>(string cacheKey) where T : class
        {
            var filePath = GetCacheFilePath(cacheKey);
            
            if (!File.Exists(filePath))
                return null;

            await _fileLock.WaitAsync();
            try
            {
                // Check whether the disk cache has expired
                var fileInfo = new FileInfo(filePath);
                var ttl = GetCacheTTL();
                
                if (DateTime.UtcNow - fileInfo.LastWriteTimeUtc > ttl)
                {
                    _logger.LogDebug("Disk cache expired: {CacheKey}", cacheKey);
                    File.Delete(filePath);
                    return null;
                }

                var json = await File.ReadAllTextAsync(filePath);
                var value = JsonSerializer.Deserialize<T>(json);
                return value;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading cache file: {FilePath}", filePath);
                return null;
            }
            finally
            {
                _fileLock.Release();
            }
        }

        private async Task SaveToDiskAsync<T>(
            string cacheKey,
            T value,
            CancellationToken cancellationToken)
        {
            var filePath = GetCacheFilePath(cacheKey);
            
            _logger.LogDebug("💾 [CACHE DISK] SaveToDiskAsync called for cacheKey: {CacheKey}", cacheKey);
            _logger.LogDebug("💾 [CACHE DISK] Target file path: {FilePath}", filePath);

            await _fileLock.WaitAsync(cancellationToken);
            try
            {
                _logger.LogDebug("💾 [CACHE DISK] Acquired file lock, serializing...");
                
                var json = JsonSerializer.Serialize(value, new JsonSerializerOptions
                {
                    WriteIndented = false
                });

                _logger.LogDebug("💾 [CACHE DISK] Serialized {Bytes} bytes of JSON", json.Length);
                _logger.LogDebug("💾 [CACHE DISK] Writing to file: {FilePath}", filePath);
                
                await File.WriteAllTextAsync(filePath, json, cancellationToken);
                
                _logger.LogInformation("💾 [CACHE DISK] ✅ SUCCESSFULLY saved to file: {FilePath} ({Size} bytes)", filePath, json.Length);
                
                // Check that the file exists
                if (File.Exists(filePath))
                {
                    var fileInfo = new FileInfo(filePath);
                    _logger.LogDebug("💾 [CACHE DISK] File verified: {FilePath} (size: {Size} bytes)", filePath, fileInfo.Length);
                }
                else
                {
                    _logger.LogError("💾 [CACHE DISK] ❌ FILE NOT FOUND after write: {FilePath}", filePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ [CACHE DISK ERROR] Error writing cache file: {FilePath} - {Message}", filePath, ex.Message);
                _logger.LogError("❌ [CACHE DISK ERROR] Exception type: {ExceptionType}", ex.GetType().Name);
                _logger.LogError("❌ [CACHE DISK ERROR] Stack trace: {StackTrace}", ex.StackTrace);
            }
            finally
            {
                _fileLock.Release();
                _logger.LogDebug("💾 [CACHE DISK] Released file lock");
            }
        }

        private string GetCacheKey(string key)
        {
            // Convert the key to a readable file name
            // Multi-source:
            //   "metadata:series:mv1-123" → "MV1-SERIES-123"
            //   "metadata:episode:yt-456" → "YT-EPISODE-456"
            //   "images:mv1-123:series" → "MV1-IMAGES-SERIES-123"
            // Mono-source (legacy):
            //   "metadata:series:1004855103" → "SERIES-1004855103"
            
            var parts = key.Split(':');
            
            if (parts.Length >= 3)
            {
                // Format: "metadata:TYPE:ID" or "images:ID:TYPE"
                if (parts[0] == "metadata")
                {
                    // metadata:series:mv1-123 → MV1-SERIES-123
                    var type = parts[1].ToUpperInvariant();
                    var idWithPrefix = parts[2];
                    
                    // Extract the ID prefix (e.g. "mv1-123" → prefix="mv1", id="123")
                    var (prefix, id) = SplitPrefixFromId(idWithPrefix);
                    
                    if (!string.IsNullOrEmpty(prefix))
                    {
                        return $"{prefix.ToUpperInvariant()}-{type}-{id}";
                    }
                    else
                    {
                        // No prefix (legacy single-source)
                        return $"{type}-{idWithPrefix}";
                    }
                }
                else if (parts[0] == "images")
                {
                    // images:mv1-123:series → MV1-IMAGES-SERIES-123
                    var idWithPrefix = parts[1];
                    var type = parts[2].ToUpperInvariant();
                    
                    // Extract the ID prefix
                    var (prefix, id) = SplitPrefixFromId(idWithPrefix);
                    
                    if (!string.IsNullOrEmpty(prefix))
                    {
                        return $"{prefix.ToUpperInvariant()}-IMAGES-{type}-{id}";
                    }
                    else
                    {
                        // No prefix (legacy single-source)
                        return $"IMAGES-{type}-{idWithPrefix}";
                    }
                }
            }
            
            // Fallback: replace the forbidden characters and keep the key readable
            // Remplace : / \ : * ? " < > |
            var safe = key.Replace(":", "-")
                          .Replace("/", "-")
                          .Replace("\\", "-")
                          .Replace("*", "-")
                          .Replace("?", "-")
                          .Replace("\"", "")
                          .Replace("<", "")
                          .Replace(">", "")
                          .Replace("|", "-");
            
            return safe;
        }

        /// <summary>
        /// Splits the prefix from the ID
        /// Ex: "mv1-123" → ("mv1", "123")
        /// Ex: "1004855103" → (null, "1004855103")
        /// </summary>
        private (string prefix, string id) SplitPrefixFromId(string idWithPrefix)
        {
            if (string.IsNullOrEmpty(idWithPrefix))
                return (null, idWithPrefix);

            var dashIndex = idWithPrefix.IndexOf('-');
            if (dashIndex > 0 && dashIndex < idWithPrefix.Length - 1)
            {
                var prefix = idWithPrefix.Substring(0, dashIndex);
                var id = idWithPrefix.Substring(dashIndex + 1);
                return (prefix, id);
            }

            // No hyphen found, return the full ID without prefix
            return (null, idWithPrefix);
        }

        private string GetCacheFilePath(string cacheKey)
        {
            return Path.Combine(_cacheDirectory, $"{cacheKey}.json");
        }

        private void EnsureCacheDirectoryExists()
        {
            try
            {
                if (!Directory.Exists(_cacheDirectory))
                {
                    _logger.LogInformation("💾 [CACHE] Creating cache directory: {Directory}", _cacheDirectory);
                    Directory.CreateDirectory(_cacheDirectory);
                    _logger.LogInformation("💾 [CACHE] ✅ Cache directory created successfully");
                }
                else
                {
                    _logger.LogDebug("💾 [CACHE] Cache directory already exists: {Directory}", _cacheDirectory);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ [CACHE] Failed to create cache directory: {Directory}", _cacheDirectory);
                throw;
            }
        }

        private TimeSpan GetCacheTTL()
        {
            var config = Plugin.Instance?.Configuration;
            var hours = config?.CacheTTLHours ?? 24; // 24h by default
            return TimeSpan.FromHours(hours);
        }

        private static string CreateHash(string input)
        {
            using var md5 = MD5.Create();
            var inputBytes = Encoding.UTF8.GetBytes(input);
            var hashBytes = md5.ComputeHash(inputBytes);
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        #endregion
    }

    /// <summary>
    /// Cache statistics
    /// </summary>
    public class CacheStats
    {
        public int DiskCacheCount { get; set; }
        public long TotalSizeBytes { get; set; }
        
        public string TotalSizeFormatted => FormatBytes(TotalSizeBytes);

        private static string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            int order = 0;
            
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }

            return $"{len:0.##} {sizes[order]}";
        }
    }
}
