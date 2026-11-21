using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Plugin.CustomMetadata.Api;
using Jellyfin.Plugin.CustomMetadata.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>
    /// Downloads the video of a missing episode into its season folder (source option AutoDownloadVideos), one at a time,
    /// in the background. The file is named "E{number} {title} {prefix-id}.mp4": the tag makes the next library scan pair it with the
    /// API episode, which then stops being reported as missing.
    /// The format is detected, not assumed: a direct file (MP4, WebM, MKV) is streamed to disk as it is, an HLS playlist
    /// (.m3u8) or a DASH manifest is remuxed to MP4 by the server's own ffmpeg (re-encoded only if the streams cannot be copied).
    /// </summary>
    public class VideoDownloadService : BackgroundService
    {
        private static readonly TimeSpan FailureBackoff = TimeSpan.FromHours(6);
        private static readonly TimeSpan FfmpegTimeout = TimeSpan.FromHours(6);
        private const long MinFreeBytesAfterDownload = 1L << 30;

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IMediaEncoder _mediaEncoder;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;
        private bool _scanNeeded;
        private readonly Channel<DownloadJob> _queue = Channel.CreateUnbounded<DownloadJob>();
        private readonly ConcurrentDictionary<string, byte> _pending = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> _failed = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        public VideoDownloadService(
            IHttpClientFactory httpClientFactory,
            IMediaEncoder mediaEncoder,
            ILibraryManager libraryManager,
            ILogger<VideoDownloadService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _mediaEncoder = mediaEncoder;
            _libraryManager = libraryManager;
            _logger = logger;
        }

        public sealed class DownloadJob
        {
            public string Key { get; set; }
            public string Title { get; set; }
            public int? IndexNumber { get; set; }
            public string Url { get; set; }
            public string Folder { get; set; }
            public SourceConfiguration Source { get; set; }

            // Follow-up state, shown by the downloads page
            public string Status { get; set; } = StatusQueued;
            public string Kind { get; set; }
            public string Error { get; set; }
            public string File { get; set; }
            public string Variant { get; set; }
            public long BytesDone { get; set; }
            public long? BytesTotal { get; set; }
            public string PartPath { get; set; }
            public DateTime QueuedAt { get; set; } = DateTime.UtcNow;
            public DateTime? StartedAt { get; set; }
            public DateTime? FinishedAt { get; set; }
            internal CancellationTokenSource Cts { get; set; }
        }

        public const string StatusQueued = "queued";
        public const string StatusDownloading = "downloading";
        public const string StatusConverting = "converting";
        public const string StatusDone = "done";
        public const string StatusFailed = "failed";
        public const string StatusCancelled = "cancelled";
        private const int MaxFinishedKept = 200;

        private readonly ConcurrentDictionary<string, DownloadJob> _jobs = new ConcurrentDictionary<string, DownloadJob>(StringComparer.OrdinalIgnoreCase);

        /// <summary>What the downloads page shows: one row per download, running first, then queued, then the finished ones (newest first).</summary>
        public sealed class DownloadInfo
        {
            public string Key { get; set; }
            public string Title { get; set; }
            public int? IndexNumber { get; set; }
            public string Series { get; set; }
            public string Season { get; set; }
            public string Source { get; set; }
            public string Host { get; set; }
            public string Status { get; set; }
            public string Kind { get; set; }
            public string Quality { get; set; }
            public long BytesDone { get; set; }
            public long? BytesTotal { get; set; }

            /// <summary>Average speed since the download started (0 until it has run for a second)</summary>
            public long BytesPerSecond { get; set; }
            public string Message { get; set; }
            public string File { get; set; }
            public DateTime QueuedAt { get; set; }
            public DateTime? StartedAt { get; set; }
            public DateTime? FinishedAt { get; set; }
        }

        public List<DownloadInfo> Snapshot()
        {
            static int Rank(string status) => status == StatusDownloading || status == StatusConverting ? 0 : status == StatusQueued ? 1 : 2;
            var list = new List<DownloadInfo>();
            foreach (var job in _jobs.Values)
            {
                var done = job.BytesDone;
                if (job.Status == StatusConverting && job.PartPath != null)
                {
                    try { var part = new FileInfo(job.PartPath); done = part.Exists ? part.Length : 0; } catch (IOException) { }
                }

                string host = null;
                try { host = new Uri(job.Url).Host; } catch (UriFormatException) { }
                list.Add(new DownloadInfo
                {
                    Key = job.Key,
                    Title = job.Title,
                    IndexNumber = job.IndexNumber,
                    Series = TagParser.Strip(Path.GetFileName(Path.GetDirectoryName(job.Folder.TrimEnd('/', '\\')) ?? string.Empty)),
                    Season = TagParser.Strip(Path.GetFileName(job.Folder.TrimEnd('/', '\\'))),
                    Source = job.Source?.Name,
                    Host = host,
                    Status = job.Status,
                    Kind = job.Kind,
                    Quality = job.Source?.VideoQuality,
                    BytesDone = done,
                    BytesTotal = job.BytesTotal,
                    BytesPerSecond = job.StartedAt.HasValue && (job.Status == StatusDownloading || job.Status == StatusConverting) && (DateTime.UtcNow - job.StartedAt.Value).TotalSeconds > 1 ? (long)(done / (DateTime.UtcNow - job.StartedAt.Value).TotalSeconds) : 0,
                    Message = job.Error,
                    File = job.File,
                    QueuedAt = job.QueuedAt,
                    StartedAt = job.StartedAt,
                    FinishedAt = job.FinishedAt
                });
            }

            return list.OrderBy(i => Rank(i.Status))
                .ThenBy(i => Rank(i.Status) == 2 ? 0 : 1).ThenByDescending(i => Rank(i.Status) == 2 ? (i.FinishedAt ?? i.QueuedAt) : DateTime.MinValue)
                .ThenBy(i => i.QueuedAt).ToList();
        }

        /// <summary>Stops a queued or running download (a running one is interrupted and its partial file deleted). It is not retried on its own for the failure backoff.</summary>
        public bool Cancel(string key)
        {
            if (!_jobs.TryGetValue(key, out var job) || (job.Status != StatusQueued && job.Status != StatusDownloading && job.Status != StatusConverting))
            {
                return false;
            }

            if (job.Status == StatusQueued)
            {
                job.Status = StatusCancelled;
                job.Error = "Annulé avant le début.";
                job.FinishedAt = DateTime.UtcNow;
                _failed[key] = DateTime.UtcNow;
                return true;
            }

            try { job.Cts?.Cancel(); } catch (ObjectDisposedException) { }
            return true;
        }

        /// <summary>Queues again a failed or cancelled download, without waiting for the failure backoff</summary>
        public bool Retry(string key)
        {
            if (!_jobs.TryGetValue(key, out var job) || (job.Status != StatusFailed && job.Status != StatusCancelled))
            {
                return false;
            }

            _failed.TryRemove(key, out _);
            var fresh = new DownloadJob { Key = job.Key, Title = job.Title, IndexNumber = job.IndexNumber, Url = job.Url, Folder = job.Folder, Source = job.Source };
            return Enqueue(fresh);
        }

        /// <summary>Forgets the finished rows (done, failed, cancelled), and the failure backoff of those that failed or were cancelled.</summary>
        public int ClearFinished()
        {
            var removed = 0;
            foreach (var pair in _jobs)
            {
                if ((pair.Value.Status == StatusDone || pair.Value.Status == StatusFailed || pair.Value.Status == StatusCancelled) && _jobs.TryRemove(pair.Key, out _))
                {
                    // Forgetting a failure also forgets its backoff: the next analysis tries that episode again
                    _failed.TryRemove(pair.Key, out _);
                    removed++;
                }
            }

            return removed;
        }

        /// <summary>Number of downloads waiting or running (the tab counter)</summary>
        public int ActiveCount => _jobs.Values.Count(j => j.Status == StatusQueued || j.Status == StatusDownloading || j.Status == StatusConverting);

        public int FailedCount => _jobs.Values.Count(j => j.Status == StatusFailed);

        private void TrimFinished()
        {
            var finished = _jobs.Values.Where(j => j.Status == StatusDone || j.Status == StatusFailed || j.Status == StatusCancelled).OrderByDescending(j => j.FinishedAt ?? j.QueuedAt).Skip(MaxFinishedKept).ToList();
            foreach (var job in finished)
            {
                _jobs.TryRemove(job.Key, out _);
            }
        }

        /// <summary>
        /// Queues a download unless the episode is already queued, already on disk, or failed recently.
        /// Returns true when it was queued.
        /// </summary>
        public bool Enqueue(DownloadJob job)
        {
            if (string.IsNullOrWhiteSpace(job.Url) || string.IsNullOrWhiteSpace(job.Folder) || !Directory.Exists(job.Folder))
            {
                return false;
            }

            // The tag must survive in the file name, otherwise the scan could never pair the file with the episode
            if (!string.Equals(TagParser.Extract("{" + job.Key + "}"), job.Key, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("⬇️ [DOWNLOAD] ⚠️ '{Key}' cannot be written as a {{PREFIX-ID}} tag in a file name: skipped", job.Key);
                return false;
            }

            if (HasFileFor(job.Folder, job.Key))
            {
                return false;
            }

            if (_failed.TryGetValue(job.Key, out var when) && DateTime.UtcNow - when < FailureBackoff)
            {
                return false;
            }

            if (!_pending.TryAdd(job.Key, 0))
            {
                return false;
            }

            job.Status = StatusQueued;
            job.QueuedAt = DateTime.UtcNow;
            _jobs[job.Key] = job;
            _queue.Writer.TryWrite(job);
            _logger.LogInformation("⬇️ [DOWNLOAD] Queued '{Title}' ({Key}) from {Url}", job.Title, job.Key, job.Url);
            return true;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
                {
                    if (job.Status == StatusCancelled)
                    {
                        _pending.TryRemove(job.Key, out _);
                        continue;
                    }

                    job.StartedAt = DateTime.UtcNow;
                    job.Status = StatusDownloading;
                    using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    job.Cts = jobCts;
                    try
                    {
                        if (await DownloadAsync(job, jobCts.Token))
                        {
                            _failed.TryRemove(job.Key, out _);
                            _scanNeeded = true;
                            job.Status = StatusDone;
                        }
                        else
                        {
                            _failed[job.Key] = DateTime.UtcNow;
                            job.Status = StatusFailed;
                            job.Error ??= "Échec (voir le journal du serveur).";
                        }
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        _failed[job.Key] = DateTime.UtcNow;
                        job.Status = StatusCancelled;
                        job.Error = "Annulé.";
                        _logger.LogInformation("⬇️ [DOWNLOAD] '{Title}' ({Key}) cancelled", job.Title, job.Key);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _failed[job.Key] = DateTime.UtcNow;
                        job.Status = StatusFailed;
                        job.Error = ex.Message;
                        _logger.LogWarning(ex, "⬇️ [DOWNLOAD] ⚠️ '{Title}' ({Key}) failed: {Message}", job.Title, job.Key, ex.Message);
                    }
                    finally
                    {
                        job.Cts = null;
                        job.FinishedAt = DateTime.UtcNow;
                        _pending.TryRemove(job.Key, out _);
                        TrimFinished();
                    }

                    if (_scanNeeded && _queue.Reader.Count == 0)
                    {
                        // One scan once the queue is empty (whatever the last job did): the files become real episodes
                        _scanNeeded = false;
                        _logger.LogInformation("⬇️ [DOWNLOAD] Queue empty: library scan requested");
                        try
                        {
                            _libraryManager.QueueLibraryScan();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "⬇️ [DOWNLOAD] ⚠️ could not request the library scan: {Message}", ex.Message);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Server stopping
            }
            catch (Exception ex)
            {
                // A background service that throws would stop the whole server: log and give up on downloads instead
                _logger.LogError(ex, "⬇️ [DOWNLOAD] ❌ the download service stopped on an unexpected error: {Message}", ex.Message);
            }
        }

        private async Task<bool> DownloadAsync(DownloadJob job, CancellationToken cancellationToken)
        {
            // "E02 Title {prefix-id}": the number first, like the other files of the season, so the folder sorts in episode order
            var number = job.IndexNumber is int index && index > 0 ? $"E{index:D2} " : string.Empty;
            var baseName = number + SafeFileName(job.Title, job.Key) + " {" + job.Key + "}";
            if (HasFileFor(job.Folder, job.Key))
            {
                job.Error = "Un fichier avec ce tag existe déjà dans le dossier.";
                return true;
            }

            var client = _httpClientFactory.CreateClient();
            SourceHeaders.Apply(client, job.Source, null, _logger, LogLevel.Debug, "⬇️ [DOWNLOAD]");
            client.Timeout = Timeout.InfiniteTimeSpan;

            // 1. Detect the format from the response headers and the first bytes
            using var response = await client.GetAsync(job.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("⬇️ [DOWNLOAD] ⚠️ '{Title}': HTTP {Status} {Reason}", job.Title, (int)response.StatusCode, response.ReasonPhrase);
                job.Error = $"Le serveur a répondu HTTP {(int)response.StatusCode} {response.ReasonPhrase}.";
                return false;
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            var head = new byte[64];
            var read = 0;
            while (read < head.Length)
            {
                var n = await body.ReadAsync(head.AsMemory(read, head.Length - read), cancellationToken);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            var kind = DetectKind(job.Url, contentType, head, read);
            job.Kind = kind.ToString();
            _logger.LogInformation("⬇️ [DOWNLOAD] '{Title}': detected {Kind} (content-type '{Type}')", job.Title, kind, contentType);

            if (kind == VideoKind.Unknown)
            {
                _logger.LogWarning("⬇️ [DOWNLOAD] ⚠️ '{Title}': not a video (content-type '{Type}'): skipped", job.Title, contentType);
                job.Error = $"Le lien ne mène pas à une vidéo (type « {(string.IsNullOrEmpty(contentType) ? "inconnu" : contentType)} »).";
                return false;
            }

            if (kind == VideoKind.Hls || kind == VideoKind.Dash)
            {
                if (kind == VideoKind.Hls)
                {
                    var variant = await PickVariantAsync(job, head, read, body, cancellationToken);
                    if (variant != null)
                    {
                        job.Variant = variant;
                    }
                }

                var target = Path.Combine(job.Folder, baseName + ".mp4");
                job.Status = StatusConverting;
                job.PartPath = target + ".part";
                return await RemuxAsync(job, client, target, cancellationToken);
            }

            // 2. Direct file: streamed as it is
            var extension = kind == VideoKind.Webm ? ".webm" : kind == VideoKind.Matroska ? ".mkv" : ".mp4";
            var final = Path.Combine(job.Folder, baseName + extension);
            var part = final + ".part";
            var length = response.Content.Headers.ContentLength;
            job.BytesTotal = length;
            if (!HasRoomFor(job.Folder, length ?? 0))
            {
                _logger.LogWarning("⬇️ [DOWNLOAD] ⚠️ '{Title}': not enough free disk space in {Folder}", job.Title, job.Folder);
                job.Error = "Pas assez d'espace disque libre dans le dossier (1 Go de marge est exigé).";
                return false;
            }

            try
            {
                await using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    await file.WriteAsync(head.AsMemory(0, read), cancellationToken);
                    job.BytesDone = read;
                    var buffer = new byte[1 << 16];
                    int got;
                    while ((got = await body.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                    {
                        await file.WriteAsync(buffer.AsMemory(0, got), cancellationToken);
                        job.BytesDone += got;
                    }
                }

                if (length.HasValue && new FileInfo(part).Length != length.Value)
                {
                    _logger.LogWarning("⬇️ [DOWNLOAD] ⚠️ '{Title}': incomplete download ({Got}/{Expected} bytes)", job.Title, new FileInfo(part).Length, length.Value);
                    job.Error = $"Téléchargement incomplet ({new FileInfo(part).Length} octets reçus sur {length.Value}).";
                    File.Delete(part);
                    return false;
                }

                File.Move(part, final, true);
                job.File = Path.GetFileName(final);
            }
            catch
            {
                TryDelete(part);
                throw;
            }

            _logger.LogInformation("⬇️ [DOWNLOAD] ✅ '{Title}' saved as {File} ({Size} MB)", job.Title, Path.GetFileName(final), new FileInfo(final).Length / (1024 * 1024));
            return true;
        }

        /// <summary>Reads the master playlist (the first bytes are already in hand) and returns the URL of the variant that matches the source's quality, or null to leave the playlist as it is (ffmpeg then takes the first variant listed, not the best).</summary>
        private async Task<string> PickVariantAsync(DownloadJob job, byte[] head, int headLength, Stream body, CancellationToken cancellationToken)
        {
            try
            {
                using var buffer = new MemoryStream();
                buffer.Write(head, 0, headLength);
                var chunk = new byte[1 << 14];
                while (buffer.Length < 1 << 20)
                {
                    var n = await body.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken);
                    if (n == 0) { break; }
                    buffer.Write(chunk, 0, n);
                }

                var chosen = ChooseVariant(Encoding.UTF8.GetString(buffer.ToArray()), job.Source?.VideoQuality, job.Url);
                _logger.LogInformation("⬇️ [DOWNLOAD] '{Title}': quality '{Quality}' → {Variant}", job.Title, job.Source?.VideoQuality, chosen ?? "(playlist as it is)");
                return chosen;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "⬇️ [DOWNLOAD] ⚠️ '{Title}': could not read the playlist to choose a quality; ffmpeg picks", job.Title);
                return null;
            }
        }

        /// <summary>
        /// Picks a variant of an HLS master playlist. quality: "best" or empty (largest bandwidth), "lowest" (smallest bandwidth) or a maximum height such as "720" or "1080"
        /// (the tallest variant not above it, else the smallest one above it). Null when the playlist is not a master, has no usable
        /// variant, or relies on separate audio renditions (a bare variant URL would then lose the sound).
        /// </summary>
        internal static string ChooseVariant(string playlist, string quality, string playlistUrl)
        {
            if (string.IsNullOrEmpty(playlist) || !playlist.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal))
            {
                return null;
            }

            var lines = playlist.Replace("\r", string.Empty).Split('\n');
            if (lines.Any(l => l.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal) && l.Contains("TYPE=AUDIO", StringComparison.Ordinal) && l.Contains("URI=", StringComparison.Ordinal)))
            {
                return null;
            }

            var variants = new List<(string Uri, long Bandwidth, int Height)>();
            for (var i = 0; i < lines.Length - 1; i++)
            {
                if (!lines[i].StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal)) { continue; }
                var uri = lines.Skip(i + 1).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));
                if (uri == null) { continue; }
                var bandwidth = System.Text.RegularExpressions.Regex.Match(lines[i], @"(?:^|[:,])BANDWIDTH=(\d+)");
                var resolution = System.Text.RegularExpressions.Regex.Match(lines[i], @"RESOLUTION=\d+x(\d+)");
                variants.Add((uri, bandwidth.Success ? long.Parse(bandwidth.Groups[1].Value) : 0, resolution.Success ? int.Parse(resolution.Groups[1].Value) : 0));
            }

            if (variants.Count < 2) { return null; }

            quality = (quality ?? string.Empty).Trim();
            (string Uri, long Bandwidth, int Height) pick;
            if (quality.Length == 0 || quality.Equals("best", StringComparison.OrdinalIgnoreCase))
            {
                pick = variants.OrderByDescending(v => v.Bandwidth).ThenByDescending(v => v.Height).First();
            }
            else if (quality.Equals("lowest", StringComparison.OrdinalIgnoreCase))
            {
                pick = variants.OrderBy(v => v.Bandwidth).ThenBy(v => v.Height).First();
            }
            else if (int.TryParse(quality.Trim(), out var maxHeight) && maxHeight > 0)
            {
                var sized = variants.Where(v => v.Height > 0).ToList();
                if (sized.Count == 0) { return null; }
                var fitting = sized.Where(v => v.Height <= maxHeight).ToList();
                pick = fitting.Count > 0
                    ? fitting.OrderByDescending(v => v.Height).ThenByDescending(v => v.Bandwidth).First()
                    : sized.OrderBy(v => v.Height).ThenBy(v => v.Bandwidth).First();
            }
            else
            {
                return null;
            }

            return Uri.TryCreate(new Uri(playlistUrl), pick.Uri, out var absolute) ? absolute.ToString() : null;
        }

        /// <summary>HLS/DASH → MP4 through ffmpeg. Stream copy first; if the codecs do not fit MP4, a re-encode.</summary>
        private async Task<bool> RemuxAsync(DownloadJob job, HttpClient headersSource, string target, CancellationToken cancellationToken)
        {
            var ffmpeg = _mediaEncoder.EncoderPath;
            if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg))
            {
                _logger.LogWarning("⬇️ [DOWNLOAD] ⚠️ '{Title}': ffmpeg not found (Jellyfin's encoder path is empty): cannot convert the stream to MP4", job.Title);
                job.Error = "ffmpeg est introuvable (le chemin de Jellyfin est vide) : impossible de convertir le flux en MP4.";
                return false;
            }

            if (!HasRoomFor(job.Folder, 0))
            {
                _logger.LogWarning("⬇️ [DOWNLOAD] ⚠️ '{Title}': not enough free disk space in {Folder}", job.Title, job.Folder);
                job.Error = "Pas assez d'espace disque libre dans le dossier (1 Go de marge est exigé).";
                return false;
            }

            var part = target + ".part";
            var headerBlock = new StringBuilder();
            string userAgent = null;
            foreach (var h in headersSource.DefaultRequestHeaders)
            {
                var value = string.Join(", ", h.Value);
                if (h.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) { userAgent = value; continue; }
                if (h.Key.Equals("Accept", StringComparison.OrdinalIgnoreCase)) { continue; }
                headerBlock.Append(h.Key).Append(": ").Append(value).Append("\r\n");
            }

            foreach (var attempt in new[] { "copy", "encode" })
            {
                var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-allowed_extensions", "ALL" };
                if (userAgent != null) { args.Add("-user_agent"); args.Add(userAgent); }
                if (headerBlock.Length > 0) { args.Add("-headers"); args.Add(headerBlock.ToString()); }
                args.AddRange(new[] { "-i", job.Variant ?? job.Url, "-map", "0:v:0?", "-map", "0:a?" });
                if (attempt == "copy")
                {
                    args.AddRange(new[] { "-c", "copy", "-bsf:a", "aac_adtstoasc" });
                }
                else
                {
                    args.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-c:a", "aac", "-b:a", "160k" });
                }

                args.AddRange(new[] { "-movflags", "+faststart", "-f", "mp4", part });

                _logger.LogInformation("⬇️ [DOWNLOAD] '{Title}': ffmpeg {Mode} → {File}", job.Title, attempt, Path.GetFileName(target));
                int code;
                string error;
                try
                {
                    (code, error) = await RunAsync(ffmpeg, args, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    TryDelete(part);
                    throw;
                }

                if (code == 0 && File.Exists(part) && new FileInfo(part).Length > 0)
                {
                    File.Move(part, target, true);
                    job.File = Path.GetFileName(target);
                    job.BytesDone = new FileInfo(target).Length;
                    _logger.LogInformation("⬇️ [DOWNLOAD] ✅ '{Title}' saved as {File} ({Size} MB)", job.Title, Path.GetFileName(target), new FileInfo(target).Length / (1024 * 1024));
                    return true;
                }

                TryDelete(part);
                _logger.LogWarning("⬇️ [DOWNLOAD] ⚠️ '{Title}': ffmpeg {Mode} failed (exit {Code}): {Error}", job.Title, attempt, code, Truncate(error, 500));
                job.Error = "ffmpeg a échoué : " + (code == -1 && error == "timeout" ? "délai dépassé (6 h)." : Truncate(string.Join(" ", (error ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(2)), 300));
            }

            return false;
        }

        private static async Task<(int Code, string Error)> RunAsync(string exe, List<string> args, CancellationToken cancellationToken)
        {
            var info = new ProcessStartInfo(exe)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var a in args)
            {
                info.ArgumentList.Add(a);
            }

            using var process = new Process { StartInfo = info };
            var error = new StringBuilder();
            process.ErrorDataReceived += (_, e) => { if (e.Data != null && error.Length < 4000) { error.AppendLine(e.Data); } };
            process.Start();
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            using var timeout = new CancellationTokenSource(FfmpegTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch (InvalidOperationException) { }
                if (cancellationToken.IsCancellationRequested) { throw; }
                return (-1, "timeout");
            }

            return (process.ExitCode, error.ToString());
        }

        internal enum VideoKind { Unknown, Mp4, Webm, Matroska, Hls, Dash }

        /// <summary>Format from the URL, the Content-Type and the first bytes (the bytes win over a wrong header)</summary>
        internal static VideoKind DetectKind(string url, string contentType, byte[] head, int length)
        {
            var type = (contentType ?? string.Empty).ToLowerInvariant();
            string path;
            try { path = new Uri(url).AbsolutePath.ToLowerInvariant(); } catch (UriFormatException) { path = url.ToLowerInvariant(); }

            var text = Encoding.ASCII.GetString(head, 0, Math.Min(length, 16)).TrimStart('﻿', ' ', '\r', '\n');
            if (text.StartsWith("#EXTM3U", StringComparison.Ordinal) || type.Contains("mpegurl") || path.EndsWith(".m3u8", StringComparison.Ordinal))
            {
                return VideoKind.Hls;
            }

            if (type.Contains("dash+xml") || path.EndsWith(".mpd", StringComparison.Ordinal))
            {
                return VideoKind.Dash;
            }

            if (length >= 12 && head[4] == 'f' && head[5] == 't' && head[6] == 'y' && head[7] == 'p')
            {
                return VideoKind.Mp4;
            }

            if (length >= 4 && head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3)
            {
                // EBML: WebM or Matroska, told apart by the DocType
                return Encoding.ASCII.GetString(head, 0, length).Contains("webm", StringComparison.Ordinal) ? VideoKind.Webm : VideoKind.Matroska;
            }

            if (type.StartsWith("video/mp4", StringComparison.Ordinal) || type.Contains("quicktime")) { return VideoKind.Mp4; }
            if (type.StartsWith("video/webm", StringComparison.Ordinal)) { return VideoKind.Webm; }
            if (type.Contains("matroska")) { return VideoKind.Matroska; }

            // Generic type and no known signature: trust a video extension
            if (path.EndsWith(".mp4", StringComparison.Ordinal) || path.EndsWith(".m4v", StringComparison.Ordinal) || path.EndsWith(".mov", StringComparison.Ordinal)) { return type.StartsWith("text/") ? VideoKind.Unknown : VideoKind.Mp4; }
            return VideoKind.Unknown;
        }

        /// <summary>A finished file of the folder already carries this tag (a leftover .part does not count)</summary>
        private static bool HasFileFor(string folder, string key)
        {
            try
            {
                var tag = "{" + key + "}";
                return Directory.EnumerateFiles(folder).Any(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(f).Contains(tag, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static bool HasRoomFor(string folder, long bytes)
        {
            try
            {
                var drive = new DriveInfo(Path.GetFullPath(folder));
                return drive.AvailableFreeSpace > bytes + MinFreeBytesAfterDownload;
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is UnauthorizedAccessException)
            {
                return true;
            }
        }

        internal static string SafeFileName(string title, string fallback)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string((title ?? string.Empty).Select(c => invalid.Contains(c) || char.IsControl(c) ? ' ' : c).ToArray());
            cleaned = string.Join(" ", cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim(' ', '.');
            // A brace would be read as a second tag
            cleaned = cleaned.Replace('{', '(').Replace('}', ')');
            if (cleaned.Length > 120)
            {
                cleaned = cleaned.Substring(0, 120).TrimEnd(' ', '.');
            }

            return cleaned.Length == 0 ? fallback : cleaned;
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private static string Truncate(string text, int max) => string.IsNullOrEmpty(text) || text.Length <= max ? text : text.Substring(0, max) + "…";
    }
}
