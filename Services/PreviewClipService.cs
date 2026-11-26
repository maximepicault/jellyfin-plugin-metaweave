using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>
    /// Small preview clips of real episodes for the Projection theme's hover preview, when the source gives no preview video:
    /// <see cref="Segments"/> clips of <see cref="ClipSeconds"/> s spread evenly over the episode, joined into one silent MP4
    /// (360 px on its short side, about 0.5 MB). Evenly spread, the clip's own time is the episode's position: the preview's
    /// progress bar runs straight through the episode. Built by the "Générer les aperçus des épisodes" task, kept in
    /// {DataFolderPath}/episode-previews/{itemId}-{key}.mp4, where the key changes with the file (path, size, date).
    /// </summary>
    public class PreviewClipService
    {
        public const int Segments = 8;
        public const double ClipSeconds = 1.5;
        private static readonly TimeSpan FfmpegTimeout = TimeSpan.FromMinutes(3);

        private readonly ILibraryManager _library;
        private readonly IMediaEncoder _mediaEncoder;
        private readonly EpisodePreviewService _sourcePreviews;
        private readonly ILogger _logger;

        public PreviewClipService(ILibraryManager library, IMediaEncoder mediaEncoder, EpisodePreviewService sourcePreviews, ILogger logger)
        {
            _library = library;
            _mediaEncoder = mediaEncoder;
            _sourcePreviews = sourcePreviews;
            _logger = logger;
        }

        private static string Folder => Path.Combine(Plugin.Instance?.DataFolderPath ?? Path.GetTempPath(), "episode-previews");

        /// <summary>The clip of this episode if it was built for its current file, else null</summary>
        public string GetClip(Episode episode)
        {
            var path = ClipPath(episode);
            return path != null && File.Exists(path) ? path : null;
        }

        private static string ClipPath(Episode episode)
        {
            if (episode == null || string.IsNullOrEmpty(episode.Path) || !File.Exists(episode.Path))
            {
                return null;
            }

            var info = new FileInfo(episode.Path);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(episode.Path + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks))).Substring(0, 12).ToLowerInvariant();
            return Path.Combine(Folder, episode.Id.ToString("N") + "-" + key + ".mp4");
        }

        /// <summary>Builds the missing clips of every real episode that has no source preview; removes the clips of gone files</summary>
        public async Task BuildAllAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var ffmpeg = _mediaEncoder.EncoderPath;
            if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg))
            {
                _logger.LogWarning("🎞️ [PREVIEW CLIP] ⚠️ ffmpeg not found (Jellyfin's encoder path is empty): no preview clip built");
                return;
            }

            Directory.CreateDirectory(Folder);
            var episodes = _library.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.Episode }, IsVirtualItem = false, Recursive = true })
                .OfType<Episode>().Where(e => !string.IsNullOrEmpty(e.Path) && e.RunTimeTicks > 0).ToList();
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int built = 0, skipped = 0, failed = 0, tooShort = 0;
            for (var i = 0; i < episodes.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(100.0 * i / Math.Max(1, episodes.Count));
                var episode = episodes[i];
                var target = ClipPath(episode);
                if (target == null) { continue; }
                wanted.Add(Path.GetFileName(target));
                if (File.Exists(target)) { skipped++; continue; }

                // the source's own preview is preferred on hover: no clip needed
                var (sourceUrl, _) = await _sourcePreviews.GetAsync(episode, cancellationToken);
                if (!string.IsNullOrEmpty(sourceUrl)) { skipped++; continue; }

                if (TimeSpan.FromTicks(episode.RunTimeTicks ?? 0).TotalSeconds < ClipSeconds * 4) { tooShort++; continue; }
                if (await BuildAsync(ffmpeg, episode, target, cancellationToken)) { built++; } else { failed++; }
            }

            // clips of files that changed or left the library
            foreach (var file in Directory.EnumerateFiles(Folder, "*.mp4"))
            {
                if (!wanted.Contains(Path.GetFileName(file))) { try { File.Delete(file); } catch (IOException) { } }
            }

            progress?.Report(100);
            _logger.LogInformation("🎞️ [PREVIEW CLIP] {Built} built, {Skipped} already there or with a source preview, {Short} too short, {Failed} failed ({Total} episodes)", built, skipped, tooShort, failed, episodes.Count);
        }

        private async Task<bool> BuildAsync(string ffmpeg, Episode episode, string target, CancellationToken cancellationToken)
        {
            var duration = TimeSpan.FromTicks(episode.RunTimeTicks ?? 0).TotalSeconds;
            if (duration < ClipSeconds * 2)
            {
                return false;
            }

            // one input per clip, each opened at its own point (fast seek), then scaled, joined, encoded small
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y" };
            var count = duration >= Segments * ClipSeconds * 2 ? Segments : Math.Max(2, (int)(duration / (ClipSeconds * 2)));
            for (var k = 0; k < count; k++)
            {
                var at = duration * (k + 0.5) / count - ClipSeconds / 2;
                args.AddRange(new[] { "-ss", Math.Max(0, at).ToString("0.###", CultureInfo.InvariantCulture), "-t", ClipSeconds.ToString(CultureInfo.InvariantCulture), "-i", episode.Path });
            }

            var filter = new StringBuilder();
            for (var k = 0; k < count; k++)
            {
                filter.Append(CultureInfo.InvariantCulture, $"[{k}:v:0]scale='if(gte(iw,ih),-2,360)':'if(gte(iw,ih),360,-2)',setsar=1,fps=24,format=yuv420p[v{k}];");
            }

            for (var k = 0; k < count; k++) { filter.Append(CultureInfo.InvariantCulture, $"[v{k}]"); }
            filter.Append(CultureInfo.InvariantCulture, $"concat=n={count}:v=1:a=0[out]");
            var part = target + ".part.mp4";
            args.AddRange(new[] { "-filter_complex", filter.ToString(), "-map", "[out]", "-an", "-c:v", "libx264", "-preset", "veryfast", "-crf", "30", "-movflags", "+faststart", part });

            var info = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) { info.ArgumentList.Add(a); }
            using var process = new Process { StartInfo = info };
            var error = new StringBuilder();
            process.ErrorDataReceived += (_, e) => { if (e.Data != null && error.Length < 2000) { error.AppendLine(e.Data); } };
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
                try { File.Delete(part); } catch (IOException) { }
                if (cancellationToken.IsCancellationRequested) { throw; }
                _logger.LogWarning("🎞️ [PREVIEW CLIP] ⚠️ '{Episode}': ffmpeg timed out", episode.Name);
                return false;
            }

            if (process.ExitCode == 0 && File.Exists(part) && new FileInfo(part).Length > 0)
            {
                File.Move(part, target, true);
                _logger.LogInformation("🎞️ [PREVIEW CLIP] '{Episode}': {Size} KB", episode.Name, new FileInfo(target).Length / 1024);
                return true;
            }

            try { File.Delete(part); } catch (IOException) { }
            _logger.LogWarning("🎞️ [PREVIEW CLIP] ⚠️ '{Episode}': ffmpeg failed ({Code}): {Error}", episode.Name, process.ExitCode, error.ToString().Trim());
            return false;
        }
    }
}
