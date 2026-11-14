using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using Jellyfin.Plugin.CustomMetadata.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Api
{
    /// <summary>
    /// Headers and HTTP version sent to a source: single place for the API calls (CustomApiClient)
    /// and the image downloads (CustomImageProvider).
    /// Order: defaults (depending on the source options) → X-API-Key → CustomHeaders (which override the defaults).
    /// </summary>
    public static class SourceHeaders
    {
        public const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        public const string PluginUserAgent = "Jellyfin.Plugin.CustomMetadata/1.0";

        // ---- CORS rejections -------------------------------------------------------------------------------
        // Some backends (Spring "Invalid CORS request"…) answer 403 to a request whose Origin they do not list, others to one
        // WITHOUT an Origin. On such a 403 the call is retried once with the opposite choice, and the working choice is kept
        // (per source and per current setting: changing the setting in the editor starts over).

        private static readonly ConcurrentDictionary<string, bool> OriginMemory = new ConcurrentDictionary<string, bool>();

        private static string MemoryKey(SourceConfiguration s) => $"{s.Prefix}|{s.BaseUrl}|{s.SendOriginReferer}|{s.OriginOverride}|{s.RefererOverride}";

        /// <summary>The source's Origin/Referer setting, unless a CORS rejection taught us the opposite works</summary>
        public static bool EffectiveSendOrigin(SourceConfiguration source)
            => source != null && (OriginMemory.TryGetValue(MemoryKey(source), out var remembered) ? remembered : source.SendOriginReferer);

        public static void RememberOrigin(SourceConfiguration source, bool send)
        {
            if (source != null)
            {
                OriginMemory[MemoryKey(source)] = send;
            }
        }

        /// <summary>403 whose body talks about CORS / a refused origin</summary>
        public static bool LooksLikeCorsRejection(int? statusCode, string body)
        {
            if (statusCode != 403 || string.IsNullOrEmpty(body))
            {
                return false;
            }

            return body.IndexOf("cors", StringComparison.OrdinalIgnoreCase) >= 0
                || (body.IndexOf("origin", StringComparison.OrdinalIgnoreCase) >= 0 && body.IndexOf("not allowed", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// Flips Origin/Referer on this client (removes them if they were sent, adds them otherwise). Returns the new state, or
        /// null when nothing can be flipped (source without BaseUrl, or the user sets Origin/Referer in the custom headers).
        /// </summary>
        public static bool? FlipOrigin(HttpClient client, SourceConfiguration source, ILogger logger, string tag)
        {
            if (source == null || string.IsNullOrEmpty(source.BaseUrl)
                || (source.CustomHeaders?.Any(h => h.Enabled && (string.Equals(h.Key, "Origin", StringComparison.OrdinalIgnoreCase) || string.Equals(h.Key, "Referer", StringComparison.OrdinalIgnoreCase))) ?? false))
            {
                return null;
            }

            var headers = client.DefaultRequestHeaders;
            var sending = headers.Contains("Origin");
            headers.Remove("Origin");
            headers.Remove("Referer");
            if (sending)
            {
                logger.LogWarning("{Tag} 🔐 CORS rejection (HTTP 403) - retrying WITHOUT Origin/Referer", tag);
                return false;
            }

            AddOriginOrReferer(headers, "Origin", source.OriginOverride, () => DeriveOrigin(source.BaseUrl), logger, LogLevel.Information, tag);
            AddOriginOrReferer(headers, "Referer", source.RefererOverride, () => source.BaseUrl, logger, LogLevel.Information, tag);
            logger.LogWarning("{Tag} 🔐 CORS rejection (HTTP 403) - retrying WITH Origin/Referer", tag);
            return true;
        }

        /// <param name="apiKey">Sent as X-API-Key if not empty (null for image downloads)</param>
        /// <param name="detailLevel">Level of the detailed logs (Information for the API, Debug for images)</param>
        public static void Apply(HttpClient client, SourceConfiguration source, string apiKey, ILogger logger, LogLevel detailLevel, string tag)
        {
            var headers = client.DefaultRequestHeaders;

            // HTTP/2: .NET generates the :method, :scheme, :authority, :path pseudo-headers itself
            if (source?.UseHttp2 == true)
            {
                client.DefaultRequestVersion = HttpVersion.Version20;
                client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
                logger.Log(detailLevel, "{Tag} ✅ HTTP/2 requested (fallback to HTTP/1.1)", tag);
            }

            var browserUserAgent = source?.SendBrowserUserAgent ?? true;
            var userAgent = browserUserAgent ? BrowserUserAgent : PluginUserAgent;
            if (!headers.Contains("User-Agent"))
            {
                headers.TryAddWithoutValidation("User-Agent", userAgent);
                logger.Log(detailLevel, "{Tag} ✅ Added default User-Agent: {UserAgent}", tag,
                    browserUserAgent ? userAgent.Substring(0, 50) + "..." : userAgent);
            }

            if (!headers.Contains("Accept"))
            {
                headers.Add("Accept", "application/json, text/plain, */*");
                logger.LogDebug("{Tag} ✅ Added default Accept header", tag);
            }

            if (source != null && !string.IsNullOrEmpty(source.BaseUrl))
            {
                if (EffectiveSendOrigin(source))
                {
                    AddOriginOrReferer(headers, "Origin", source.OriginOverride, () => DeriveOrigin(source.BaseUrl), logger, detailLevel, tag);
                    AddOriginOrReferer(headers, "Referer", source.RefererOverride, () => source.BaseUrl, logger, detailLevel, tag);
                }
                else
                {
                    logger.Log(detailLevel, "{Tag} ⏭️ Origin/Referer disabled for source '{SourceName}'", tag, source.Name);
                }
            }

            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                headers.Add("X-API-Key", apiKey);
                logger.Log(detailLevel, "{Tag} ✅ Added X-API-Key: {ApiKey}...", tag, apiKey.Substring(0, Math.Min(10, apiKey.Length)));
            }

            if (source?.CustomHeaders == null || source.CustomHeaders.Count == 0)
            {
                logger.LogDebug("{Tag} No custom headers configured for this source", tag);
                return;
            }

            logger.Log(detailLevel, "{Tag} Adding {Count} custom headers from source '{SourceName}'", tag, source.CustomHeaders.Count(h => h.Enabled), source.Name ?? "(unnamed)");

            foreach (var header in source.CustomHeaders)
            {
                if (string.IsNullOrWhiteSpace(header.Key) || string.IsNullOrWhiteSpace(header.Value))
                {
                    continue;
                }

                if (!header.Enabled)
                {
                    logger.Log(detailLevel, "{Tag} ⏭️ Custom header '{Key}' is switched off - not sent", tag, header.Key);
                    continue;
                }

                // A custom header replaces the default header of the same name
                if (headers.Contains(header.Key))
                {
                    headers.Remove(header.Key);
                    logger.Log(detailLevel, "{Tag} Overriding default header '{Key}' with custom value", tag, header.Key);
                }

                try
                {
                    // Sent exactly as typed: Add() validates known headers and refuses values such as max-age="10" or
                    // fr;q="0.9" (quotes) - the header was then silently dropped
                    if (headers.TryAddWithoutValidation(header.Key, header.Value))
                    {
                        logger.Log(detailLevel, "{Tag} ✅ Added custom header: '{Key}' = '{Value}'", tag, header.Key, Mask(header.Key, header.Value));
                    }
                    else
                    {
                        logger.LogWarning("{Tag} ⚠️ Header '{Key}' refused (invalid name, or a header that cannot be set on a request)", tag, header.Key);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning("{Tag} ⚠️ Failed to add header '{Key}': {Message}", tag, header.Key, ex.Message);
                }
            }
        }

        /// <summary>
        /// Overridden value if set, otherwise derived from BaseUrl. An invalid value is ignored (warning).
        /// </summary>
        private static void AddOriginOrReferer(
            System.Net.Http.Headers.HttpRequestHeaders headers,
            string name,
            string overrideValue,
            Func<string> derive,
            ILogger logger,
            LogLevel detailLevel,
            string tag)
        {
            if (headers.Contains(name))
            {
                return;
            }

            var isOverride = !string.IsNullOrWhiteSpace(overrideValue);
            string value;
            try
            {
                value = isOverride ? overrideValue.Trim() : derive();
            }
            catch (Exception ex)
            {
                logger.LogDebug("{Tag} Could not derive {Header} from BaseUrl: {Message}", tag, name, ex.Message);
                return;
            }

            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            try
            {
                headers.Add(name, value);
                logger.Log(detailLevel, isOverride ? "{Tag} ✅ Added overridden {Header}: {Value}" : "{Tag} ✅ Added default {Header}: {Value}", tag, name, value);
            }
            catch (Exception ex)
            {
                logger.LogWarning("{Tag} ⚠️ Invalid {Header} value '{Value}', not sent: {Message}", tag, name, value, ex.Message);
            }
        }

        /// <summary>
        /// "https://api.x:8080/v1/" → "https://api.x:8080" (GetLeftPart keeps the port if it is not the default one)
        /// </summary>
        private static string DeriveOrigin(string baseUrl)
        {
            return new Uri(baseUrl).GetLeftPart(UriPartial.Authority);
        }

        /// <summary>
        /// Headers actually configured on the client, sensitive values masked (Test button)
        /// </summary>
        public static List<string> Describe(HttpClient client)
        {
            // NonValidated: values as sent (otherwise the User-Agent is split into "products")
            return client.DefaultRequestHeaders.NonValidated
                .Select(h => $"{h.Key}: {Mask(h.Key, h.Value.ToString())}")
                .ToList();
        }

        private static string Mask(string key, string value)
        {
            var lower = key.ToLowerInvariant();
            var isSensitive = lower.Contains("token") || lower.Contains("secret") || lower.Contains("password")
                || lower.Contains("authorization") || lower.Contains("api-key") || lower.Contains("apikey") || lower.Contains("cookie");

            return isSensitive
                ? value.Substring(0, Math.Min(10, value.Length)) + "..."
                : value.Substring(0, Math.Min(200, value.Length));
        }
    }
}
