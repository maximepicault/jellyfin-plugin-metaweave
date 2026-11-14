using System;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>
    /// Converts a PORTRAIT thumbnail into a 16:9 landscape image: the whole photo centred on a
    /// blurred, darkened copy of itself. jellyfin-web (and native clients) display episode
    /// thumbnails in a landscape frame in "cover" mode: a portrait photo is cropped at the top and bottom there.
    /// SkiaSharp is the Jellyfin server's (not shipped in the plugin).
    /// </summary>
    public static class PortraitThumbConverter
    {
        private const int MaxOutputHeight = 1080;
        private const int JpegQuality = 90;

        /// <summary>
        /// Converted image (JPEG), or null if it is not portrait or unreadable (the original is then kept)
        /// </summary>
        public static byte[] ToLandscapeIfPortrait(byte[] data, ILogger logger, string url)
        {
            if (data == null || data.Length == 0)
            {
                return null;
            }

            try
            {
                using var codec = SKCodec.Create(new SKMemoryStream(data));
                if (codec == null)
                {
                    logger.LogWarning("📐 [PORTRAIT] ⚠️ Image format not readable by SkiaSharp ({Format}, {Length} bytes) for {Url} - kept as is",
                        DetectFormat(data), data.Length, url);
                    return null;
                }

                using var decoded = SKBitmap.Decode(codec);
                if (decoded == null)
                {
                    return null;
                }

                // EXIF orientation (phone photos stored "lying down") applied BEFORE judging portrait/landscape
                var source = ApplyOrigin(decoded, codec.EncodedOrigin);
                try
                {
                    // Portrait photo already "framed" in a landscape image (uniform bars left and right,
                    // typical of vertical-video thumbnails): keep the content, the blurred background replaces the bars
                    if (source.Height <= source.Width && FindPillarboxContent(source) is SKRectI content)
                    {
                        var cropped = new SKBitmap();
                        if (source.ExtractSubset(cropped, content))
                        {
                            logger.LogInformation("📐 [PORTRAIT] {Width}x{Height} image contains a portrait picture between side bars ({ContentW}x{ContentH}) - converting it: {Url}",
                                source.Width, source.Height, content.Width, content.Height, url);
                            using (cropped)
                            {
                                return Compose(cropped);
                            }
                        }

                        cropped.Dispose();
                    }

                    if (source.Height <= source.Width)
                    {
                        logger.LogInformation("📐 [PORTRAIT] {Width}x{Height} ({Format}, EXIF {Origin}) is not portrait - kept as is: {Url}",
                            source.Width, source.Height, codec.EncodedFormat, codec.EncodedOrigin, url);
                        return null;
                    }

                    var result = Compose(source);
                    logger.LogInformation("📐 [PORTRAIT] Portrait thumbnail {SrcW}x{SrcH} converted to 16:9 with blurred background for {Url}",
                        source.Width, source.Height, url);
                    return result;
                }
                finally
                {
                    if (!ReferenceEquals(source, decoded))
                    {
                        source.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning("📐 [PORTRAIT] ⚠️ Could not convert {Url}, keeping original image: {Message}", url, ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Conversion decision, converting nothing ("Tester" button of the configuration page)
        /// </summary>
        /// <summary>
        /// Small JPEG of the picture (longest side at most maxSide, EXIF orientation applied) for the source test screen.
        /// Null when the format cannot be decoded (AVIF, HEIC…): the caller then shows the original or says so.
        /// </summary>
        public static byte[] Thumbnail(byte[] data, int maxSide)
        {
            try
            {
                using var codec = SKCodec.Create(new SKMemoryStream(data));
                if (codec == null)
                {
                    return null;
                }

                using var decoded = SKBitmap.Decode(codec);
                if (decoded == null)
                {
                    return null;
                }

                using var oriented = ApplyOrigin(decoded, codec.EncodedOrigin);
                var scale = Math.Min(1.0, (double)maxSide / Math.Max(oriented.Width, oriented.Height));
                using var resized = scale < 1.0
                    ? oriented.Resize(new SKImageInfo(Math.Max(1, (int)(oriented.Width * scale)), Math.Max(1, (int)(oriented.Height * scale))), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
                    : oriented.Copy();
                if (resized == null)
                {
                    return null;
                }

                using var image = SKImage.FromBitmap(resized);
                using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 80);
                return encoded?.ToArray();
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static string Describe(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return "image vide";
            }

            try
            {
                using var codec = SKCodec.Create(new SKMemoryStream(data));
                if (codec == null)
                {
                    return $"format illisible par Jellyfin ({DetectFormat(data)}) → ne sera pas converti";
                }

                using var decoded = SKBitmap.Decode(codec);
                if (decoded == null)
                {
                    return $"image illisible ({codec.EncodedFormat})";
                }

                var source = ApplyOrigin(decoded, codec.EncodedOrigin);
                try
                {
                    var size = $"{source.Width}x{source.Height} ({codec.EncodedFormat}"
                        + (codec.EncodedOrigin == SKEncodedOrigin.TopLeft ? ")" : $", EXIF {codec.EncodedOrigin})");
                    if (source.Height > source.Width)
                    {
                        return size + " → portrait : sera converti en 16:9";
                    }

                    return FindPillarboxContent(source) is SKRectI content
                        ? size + $" → photo portrait encadrée de bandes ({content.Width}x{content.Height}) : sera convertie en 16:9"
                        : size + " → paysage : inchangée";
                }
                finally
                {
                    if (!ReferenceEquals(source, decoded))
                    {
                        source.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                return "erreur d'analyse : " + ex.Message;
            }
        }

        // Bars: uniform columns (difference ≤ tolerance per channel) of at least 8% of the width on each side,
        // symmetric within 6%; remaining content taller than wide
        private const int BarColorTolerance = 24;
        private const double MinBarRatio = 0.08;
        private const double MaxBarAsymmetry = 0.06;
        private const int SampledRows = 40;

        /// <summary>
        /// Content area between two uniform side bars, if that content is portrait; otherwise null
        /// </summary>
        private static SKRectI? FindPillarboxContent(SKBitmap bitmap)
        {
            var w = bitmap.Width;
            var h = bitmap.Height;
            if (w < 50 || h < 50)
            {
                return null;
            }

            var left = 0;
            var leftColor = bitmap.GetPixel(0, h / 2);
            while (left < w / 2 && IsUniformColumn(bitmap, left, leftColor))
            {
                left++;
            }

            var right = w - 1;
            var rightColor = bitmap.GetPixel(w - 1, h / 2);
            while (right > w / 2 && IsUniformColumn(bitmap, right, rightColor))
            {
                right--;
            }

            var leftBar = left;
            var rightBar = w - 1 - right;

            // Fully uniform image, bars too thin or asymmetric: not a framed portrait
            if (left >= w / 2 || right <= w / 2
                || leftBar < w * MinBarRatio || rightBar < w * MinBarRatio
                || Math.Abs(leftBar - rightBar) > w * MaxBarAsymmetry)
            {
                return null;
            }

            var contentWidth = right - left + 1;
            return contentWidth < h ? new SKRectI(left, 0, right + 1, h) : null;
        }

        private static bool IsUniformColumn(SKBitmap bitmap, int x, SKColor reference)
        {
            var h = bitmap.Height;
            for (var i = 0; i < SampledRows; i++)
            {
                var c = bitmap.GetPixel(x, (int)((i + 0.5) * h / SampledRows));
                if (Math.Abs(c.Red - reference.Red) > BarColorTolerance
                    || Math.Abs(c.Green - reference.Green) > BarColorTolerance
                    || Math.Abs(c.Blue - reference.Blue) > BarColorTolerance)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Format from the file signature (diagnostic when SkiaSharp cannot read it)
        /// </summary>
        private static string DetectFormat(byte[] data)
        {
            if (data.Length >= 12)
            {
                var brand = System.Text.Encoding.ASCII.GetString(data, 4, 8);
                if (brand.StartsWith("ftypavi", StringComparison.Ordinal)) return "AVIF";
                if (brand.StartsWith("ftyphei", StringComparison.Ordinal) || brand.StartsWith("ftypmif", StringComparison.Ordinal) || brand.StartsWith("ftyphe", StringComparison.Ordinal)) return "HEIC";
                if (System.Text.Encoding.ASCII.GetString(data, 0, 4) == "RIFF") return "WEBP";
            }

            if (data.Length >= 5 && (data[0] == (byte)'<' || System.Text.Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 100)).Contains("<html", StringComparison.OrdinalIgnoreCase)))
            {
                return "HTML/text (not an image: error page?)";
            }

            return "unknown";
        }

        private static byte[] Compose(SKBitmap source)
        {
            var height = Math.Min(source.Height, MaxOutputHeight);
            var width = (int)Math.Round(height * 16.0 / 9.0);

            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Black);

            using var image = SKImage.FromBitmap(source);
            var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);

            // Background: the photo enlarged to cover the whole frame, heavily blurred (Clamp: no dark edges)
            // +15%: the edges of the blur (which darken) fall outside the frame
            var cover = Math.Max((float)width / source.Width, (float)height / source.Height) * 1.15f;
            var backgroundRect = CenteredRect(width, height, source.Width * cover, source.Height * cover);
            var sigma = height / 25f;
            using (var blur = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp, null) })
            {
                canvas.DrawImage(image, backgroundRect, sampling, blur);
            }

            // Darkened to set the photo apart from the background
            using (var dim = new SKPaint { Color = new SKColor(0, 0, 0, 80) })
            {
                canvas.DrawRect(0, 0, width, height, dim);
            }

            // Foreground: the WHOLE photo, height = frame
            var fit = (float)height / source.Height;
            canvas.DrawImage(image, CenteredRect(width, height, source.Width * fit, height), sampling);

            using var snapshot = surface.Snapshot();
            using var encoded = snapshot.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
            return encoded.ToArray();
        }

        private static SKRect CenteredRect(float frameWidth, float frameHeight, float width, float height)
        {
            return SKRect.Create((frameWidth - width) / 2f, (frameHeight - height) / 2f, width, height);
        }

        /// <summary>
        /// Applies the EXIF orientation. Returns the original bitmap if no transformation is needed.
        /// </summary>
        private static SKBitmap ApplyOrigin(SKBitmap bitmap, SKEncodedOrigin origin)
        {
            if (origin == SKEncodedOrigin.TopLeft)
            {
                return bitmap;
            }

            var w = bitmap.Width;
            var h = bitmap.Height;
            var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
            var result = new SKBitmap(swap ? h : w, swap ? w : h);

            using (var canvas = new SKCanvas(result))
            {
                switch (origin)
                {
                    case SKEncodedOrigin.TopRight:      // miroir horizontal
                        canvas.Translate(w, 0);
                        canvas.Scale(-1, 1);
                        break;
                    case SKEncodedOrigin.BottomRight:   // 180°
                        canvas.Translate(w, h);
                        canvas.RotateDegrees(180);
                        break;
                    case SKEncodedOrigin.BottomLeft:    // miroir vertical
                        canvas.Translate(0, h);
                        canvas.Scale(1, -1);
                        break;
                    case SKEncodedOrigin.LeftTop:       // transposition
                        canvas.RotateDegrees(90);
                        canvas.Scale(1, -1);
                        break;
                    case SKEncodedOrigin.RightTop:      // 90° clockwise (typical case of a phone photo)
                        canvas.Translate(h, 0);
                        canvas.RotateDegrees(90);
                        break;
                    case SKEncodedOrigin.RightBottom:   // transverse
                        canvas.Translate(h, w);
                        canvas.RotateDegrees(-90);
                        canvas.Scale(1, -1);
                        break;
                    case SKEncodedOrigin.LeftBottom:    // 90° anti-horaire
                        canvas.Translate(0, w);
                        canvas.RotateDegrees(-90);
                        break;
                }

                canvas.DrawBitmap(bitmap, 0, 0);
            }

            return result;
        }
    }
}
