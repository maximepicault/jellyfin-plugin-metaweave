using System;
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
                if (source.SendOriginReferer)
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

            logger.Log(detailLevel, "{Tag} Adding {Count} custom headers from source '{SourceName}'", tag, source.CustomHeaders.Count, source.Name ?? "(unnamed)");

            foreach (var header in source.CustomHeaders)
            {
                if (string.IsNullOrWhiteSpace(header.Key) || string.IsNullOrWhiteSpace(header.Value))
                {
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
                    headers.Add(header.Key, header.Value);
                    logger.Log(detailLevel, "{Tag} ✅ Added custom header: '{Key}' = '{Value}'", tag, header.Key, Mask(header.Key, header.Value));
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
