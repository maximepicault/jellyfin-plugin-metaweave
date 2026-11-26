using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>
    /// Sizes and WebP thumbnails of episode photos, with the server's SkiaSharp. Thumbnails are cached on disk under a key
    /// holding the file's path, size and date, so an edited photo gets a new one. EXIF rotation is applied to both.
    /// </summary>
    public class EpisodePhotoThumbnails
    {
        private const int MinWidth = 64;
        private const int MaxWidth = 1920;
        private const int WidthStep = 80;   // bounds the number of cached variants
        private const int DefaultWidth = 480;
        private const int Quality = 82;

        private readonly ILogger<EpisodePhotoThumbnails> _logger;
        private readonly SemaphoreSlim _renderSlots = new(Math.Max(1, Environment.ProcessorCount / 2));
        private readonly ConcurrentDictionary<string, (int Width, int Height)> _sizes = new(StringComparer.Ordinal);
        private readonly string _cacheDirectory;

        public EpisodePhotoThumbnails(IApplicationPaths paths, ILogger<EpisodePhotoThumbnails> logger)
        {
            _logger = logger;
            _cacheDirectory = Path.Combine(paths.CachePath, "custom-metadata", "photo-thumbs");
        }

        public static int NormalizeWidth(int? requested)
        {
            var width = Math.Clamp(requested is > 0 ? requested.Value : DefaultWidth, MinWidth, MaxWidth);
            return Math.Min(MaxWidth, (width + WidthStep - 1) / WidthStep * WidthStep);
        }

        /// <summary>The displayed size of a photo (header only is read), or (0, 0) when the format cannot be read</summary>
        public (int Width, int Height) SizeOf(PhotoFile photo)
        {
            var key = string.Create(CultureInfo.InvariantCulture, $"{photo.FullPath}|{photo.Length}|{photo.LastWriteTimeUtc.Ticks}");
            return _sizes.GetOrAdd(key, _ =>
            {
                try
                {
                    using var codec = SKCodec.Create(photo.FullPath);
                    if (codec == null)
                    {
                        return (0, 0);
                    }

                    return SwapsAxes(codec.EncodedOrigin) ? (codec.Info.Height, codec.Info.Width) : (codec.Info.Width, codec.Info.Height);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "📷 [PHOTOS] Size of {Path} unreadable", photo.FullPath);
                    return (0, 0);
                }
            });
        }

        /// <summary>The cached thumbnail's path, rendered if needed; null when the image cannot be decoded</summary>
        public async Task<string> GetAsync(PhotoFile photo, int width, CancellationToken cancellationToken)
        {
            var key = string.Create(CultureInfo.InvariantCulture, $"{photo.FullPath}|{photo.Length}|{photo.LastWriteTimeUtc.Ticks}|{width}|{Quality}");
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
            var cached = Path.Combine(_cacheDirectory, hash.Substring(0, 2), hash + ".webp");
            if (File.Exists(cached))
            {
                return cached;
            }

            await _renderSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            var temp = cached + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                if (File.Exists(cached))
                {
                    return cached;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(cached));
                if (!Render(photo.FullPath, temp, width))
                {
                    return null;
                }

                File.Move(temp, cached, overwrite: true);
                return cached;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "📷 [PHOTOS] ⚠️ No thumbnail for {Path}", photo.FullPath);
                return null;
            }
            finally
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }

                _renderSlots.Release();
            }
        }

        private static bool Render(string source, string output, int width)
        {
            using var codec = SKCodec.Create(source);
            if (codec == null)
            {
                return false;
            }

            var origin = codec.EncodedOrigin;
            var info = codec.Info;
            var scale = Math.Min(1f, (float)width / (SwapsAxes(origin) ? info.Height : info.Width));

            // target size in the encoded (not yet rotated) orientation
            var targetWidth = Math.Max(1, (int)Math.Round(info.Width * scale));
            var targetHeight = Math.Max(1, (int)Math.Round(info.Height * scale));

            // the codec downsamples while decoding (JPEG: 1/2, 1/4, 1/8), never below the target
            var decodeSize = codec.GetScaledDimensions(scale);
            if (decodeSize.Width < targetWidth || decodeSize.Height < targetHeight)
            {
                decodeSize = new SKSizeI(info.Width, info.Height);
            }

            var decodeInfo = new SKImageInfo(decodeSize.Width, decodeSize.Height, SKImageInfo.PlatformColorType, SKAlphaType.Premul);
            using var decoded = new SKBitmap(decodeInfo);
            if (codec.GetPixels(decodeInfo, decoded.GetPixels()) is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            {
                return false;
            }

            using var resized = decodeSize.Width == targetWidth && decodeSize.Height == targetHeight
                ? decoded.Copy()
                : decoded.Resize(new SKImageInfo(targetWidth, targetHeight, decodeInfo.ColorType, decodeInfo.AlphaType), new SKSamplingOptions(SKCubicResampler.Mitchell));
            if (resized == null)
            {
                return false;
            }

            using var upright = Orient(resized, origin);
            using var image = SKImage.FromBitmap(upright);
            using var data = image.Encode(SKEncodedImageFormat.Webp, Quality);
            if (data == null)
            {
                return false;
            }

            using var file = File.Create(output);
            data.SaveTo(file);
            return true;
        }

        private static bool SwapsAxes(SKEncodedOrigin origin)
            => origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

        private static SKBitmap Orient(SKBitmap bitmap, SKEncodedOrigin origin)
        {
            if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
            {
                return bitmap.Copy();
            }

            int w = bitmap.Width, h = bitmap.Height;
            var result = new SKBitmap(SwapsAxes(origin) ? h : w, SwapsAxes(origin) ? w : h, bitmap.ColorType, bitmap.AlphaType);
            using var canvas = new SKCanvas(result);
            switch (origin)
            {
                case SKEncodedOrigin.TopRight: canvas.Translate(w, 0); canvas.Scale(-1, 1); break;
                case SKEncodedOrigin.BottomRight: canvas.Translate(w, h); canvas.RotateDegrees(180); break;
                case SKEncodedOrigin.BottomLeft: canvas.Translate(0, h); canvas.Scale(1, -1); break;
                case SKEncodedOrigin.LeftTop: canvas.RotateDegrees(90); canvas.Scale(1, -1); break;
                case SKEncodedOrigin.RightTop: canvas.Translate(h, 0); canvas.RotateDegrees(90); break;
                case SKEncodedOrigin.RightBottom: canvas.Translate(h, w); canvas.RotateDegrees(90); canvas.Scale(-1, 1); break;
                case SKEncodedOrigin.LeftBottom: canvas.Translate(0, w); canvas.RotateDegrees(-90); break;
            }

            canvas.DrawBitmap(bitmap, 0, 0);
            canvas.Flush();
            return result;
        }
    }
}
