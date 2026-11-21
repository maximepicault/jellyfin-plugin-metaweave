using System;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>
    /// Crops a LANDSCAPE poster to the 2:3 portrait of a poster, at the centre and at the full resolution of the source.
    /// jellyfin-web asks the server for a resized copy of the poster (limited by width) and then crops it to portrait in the browser:
    /// a 1080p landscape picture is shrunk first, then zoomed on its middle third, and ends up blurry. Handing Jellyfin the portrait
    /// crop makes it resize a picture that already has the right shape. SkiaSharp is the Jellyfin server's (not shipped in the plugin).
    /// </summary>
    public static class LandscapePosterConverter
    {
        private const double PortraitRatio = 2.0 / 3.0;
        private const int JpegQuality = 92;

        /// <summary>The 2:3 centre crop (JPEG), or null when the picture is not landscape or unreadable (the original is then kept)</summary>
        public static byte[] ToPortraitIfLandscape(byte[] data, ILogger logger, string url)
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
                    logger.LogWarning("📐 [POSTER] ⚠️ Image format not readable by SkiaSharp ({Length} bytes) for {Url} - kept as is", data.Length, url);
                    return null;
                }

                using var decoded = SKBitmap.Decode(codec);
                if (decoded == null)
                {
                    return null;
                }

                var source = PortraitThumbConverter.ApplyOrigin(decoded, codec.EncodedOrigin);
                try
                {
                    if (source.Width <= source.Height * PortraitRatio + 1)
                    {
                        logger.LogInformation("📐 [POSTER] {Width}x{Height} is already portrait - kept as is: {Url}", source.Width, source.Height, url);
                        return null;
                    }

                    var width = (int)Math.Round(source.Height * PortraitRatio);
                    var left = (source.Width - width) / 2;
                    using var cropped = new SKBitmap();
                    if (!source.ExtractSubset(cropped, new SKRectI(left, 0, left + width, source.Height)))
                    {
                        return null;
                    }

                    using var image = SKImage.FromBitmap(cropped);
                    using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
                    logger.LogInformation("📐 [POSTER] Landscape poster {SrcW}x{SrcH} cropped to portrait {W}x{H} for {Url}", source.Width, source.Height, width, source.Height, url);
                    return encoded.ToArray();
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
                logger.LogWarning("📐 [POSTER] ⚠️ Could not crop {Url}, keeping original image: {Message}", url, ex.Message);
                return null;
            }
        }
    }
}
