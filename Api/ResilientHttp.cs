using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CustomMetadata.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Api
{
    /// <summary>
    /// Result of a GET to a source API
    /// </summary>
    public class HttpFetchResult
    {
        public string Url { get; set; }

        /// <summary>HTTP code of the last attempt (null = no response: timeout, network error)</summary>
        public int? StatusCode { get; set; }

        /// <summary>Response body, only on success (2xx)</summary>
        public string Content { get; set; }

        /// <summary>Reason for the failure of the last attempt</summary>
        public string Error { get; set; }

        public int Attempts { get; set; }

        /// <summary>HTTP version of the last response ("1.1", "2.0")</summary>
        public string HttpVersion { get; set; }

        /// <summary>Start of the last error response body (diagnosing a 403: anti-bot page, CORS message…)</summary>
        public string ErrorBody { get; set; }

        public long DurationMs { get; set; }

        public bool IsSuccess => Content != null;
    }

    /// <summary>
    /// HTTP calls to the sources: URL building, per-attempt timeout, retries
    /// with backoff on temporary errors and a limit of simultaneous requests per source.
    /// </summary>
    public class ResilientHttp
    {
        private const int DefaultTimeoutSeconds = 30;
        private const int MaxBackoffSeconds = 30;

        // Beyond that we do not wait: a Retry-After of several minutes would block the scan
        private const int MaxRetryAfterSeconds = 60;

        private const int MaxErrorBodyLength = 500;

        // Shared by all instances: the limit applies to the source, not to the caller
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> Limiters =
            new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);

        private readonly ILogger _logger;
        private readonly Services.LocalSourceStore _local;

        public ResilientHttp(ILogger logger, Services.LocalSourceStore local = null)
        {
            _logger = logger;
            _local = local;
        }

        /// <summary>
        /// BaseUrl + endpoint, whatever "/" is on either side, {id} encoded for the URL.
        /// "https://api.x/" + "videos/{id}" + "a b" → "https://api.x/videos/a%20b"
        /// </summary>
        public static string BuildUrl(string baseUrl, string endpoint, string id)
        {
            var root = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            var path = (endpoint ?? string.Empty).Trim();

            if (id != null)
            {
                path = path.Replace("{id}", Uri.EscapeDataString(id));
            }

            if (path.Length == 0)
            {
                return root;
            }

            if (path.StartsWith("?", StringComparison.Ordinal))
            {
                return root + path;
            }

            return root + "/" + path.TrimStart('/');
        }

        /// <summary>
        /// GET with retries. Does not throw for HTTP/network/timeout errors:
        /// they are described in the result. Only a cancellation requested by the caller is propagated.
        /// </summary>
        public async Task<HttpFetchResult> GetStringAsync(
            HttpClient client,
            string url,
            SourceConfiguration source,
            string logTag,
            CancellationToken cancellationToken)
        {
            // Local source: the content typed in the plugin answers in place of the API (no network)
            if (source != null && source.IsLocal && _local != null)
            {
                return _local.Serve(source, url);
            }

            var timeout = TimeSpan.FromSeconds(source != null && source.TimeoutSeconds > 0 ? source.TimeoutSeconds : DefaultTimeoutSeconds);
            var maxRetries = Math.Clamp(source?.MaxRetries ?? 0, 0, 10);
            var limiter = GetLimiter(source);

            // The timeout is applied per attempt below (HttpClient.Timeout would cut at 100s whatever happens)
            // One client can serve several calls (episode list pagination): HttpClient
            // refuses any change of Timeout after the 1st request, even to the same value.
            if (client.Timeout != Timeout.InfiniteTimeSpan)
            {
                try
                {
                    client.Timeout = Timeout.InfiniteTimeSpan;
                }
                catch (InvalidOperationException)
                {
                    _logger.LogDebug("{Tag} HttpClient already used - keeping its Timeout ({Timeout})", logTag, client.Timeout);
                }
            }

            var result = new HttpFetchResult { Url = url };
            var stopwatch = Stopwatch.StartNew();

            for (var attempt = 1; ; attempt++)
            {
                result.Attempts = attempt;
                TimeSpan? retryAfter = null;
                var retryable = false;

                if (limiter != null && !limiter.Wait(0))
                {
                    _logger.LogDebug("{Tag} ⏳ Waiting for a free slot (max {Max} concurrent requests for source '{SourceName}')",
                        logTag, source.MaxConcurrentRequests, source.Name);
                    await limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                try
                {
                    using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    attemptCts.CancelAfter(timeout);

                    using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token).ConfigureAwait(false);
                    result.StatusCode = (int)response.StatusCode;
                    result.HttpVersion = response.Version.ToString();

                    if (response.IsSuccessStatusCode)
                    {
                        result.Content = await response.Content.ReadAsStringAsync(attemptCts.Token).ConfigureAwait(false);
                        result.Error = null;
                        result.ErrorBody = null;
                        break;
                    }

                    result.Error = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                    result.ErrorBody = await ReadErrorBodyAsync(response, attemptCts.Token).ConfigureAwait(false);
                    retryable = IsRetryable(response.StatusCode);
                    retryAfter = GetRetryAfter(response);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    result.StatusCode = null;
                    result.ErrorBody = null;
                    result.Error = $"Timeout after {timeout.TotalSeconds:0}s";
                    retryable = true;
                }
                catch (HttpRequestException ex)
                {
                    result.StatusCode = null;
                    result.ErrorBody = null;
                    result.Error = ex.InnerException != null ? $"{ex.Message} ({ex.InnerException.Message})" : ex.Message;
                    retryable = true;
                }
                finally
                {
                    limiter?.Release();
                }

                if (!retryable || attempt > maxRetries)
                {
                    break;
                }

                var delay = retryAfter ?? TimeSpan.FromSeconds(Math.Min(MaxBackoffSeconds, Math.Pow(2, attempt - 1)));
                if (delay > TimeSpan.FromSeconds(MaxRetryAfterSeconds))
                {
                    _logger.LogWarning("{Tag} ⚠️ {Error} for {Url} - API asks to wait {Delay:0}s, giving up",
                        logTag, result.Error, url, delay.TotalSeconds);
                    break;
                }

                _logger.LogWarning("{Tag} 🔁 {Error} for {Url} - retry {Attempt}/{MaxRetries} in {Delay:0.#}s",
                    logTag, result.Error, url, attempt, maxRetries, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            result.DurationMs = stopwatch.ElapsedMilliseconds;
            return result;
        }

        private static async Task<string> ReadErrorBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            try
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return body.Length > MaxErrorBodyLength ? body.Substring(0, MaxErrorBodyLength) + "…" : body;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Erreurs temporaires : 408 Request Timeout, 429 Too Many Requests, 5xx
        /// </summary>
        private static bool IsRetryable(HttpStatusCode statusCode)
        {
            var code = (int)statusCode;
            return code == 408 || code == 429 || code >= 500;
        }

        private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
        {
            var retryAfter = response.Headers.RetryAfter;
            if (retryAfter == null)
            {
                return null;
            }

            if (retryAfter.Delta.HasValue)
            {
                return retryAfter.Delta.Value < TimeSpan.Zero ? TimeSpan.Zero : retryAfter.Delta.Value;
            }

            if (retryAfter.Date.HasValue)
            {
                var wait = retryAfter.Date.Value - DateTimeOffset.UtcNow;
                return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
            }

            return null;
        }

        private static SemaphoreSlim GetLimiter(SourceConfiguration source)
        {
            if (source == null || source.MaxConcurrentRequests <= 0)
            {
                return null;
            }

            // The size is part of the key: changing the limit in the config creates a new semaphore
            var key = $"{source.Prefix ?? source.BaseUrl}|{source.MaxConcurrentRequests}";
            return Limiters.GetOrAdd(key, _ => new SemaphoreSlim(source.MaxConcurrentRequests, source.MaxConcurrentRequests));
        }
    }
}
