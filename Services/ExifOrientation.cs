using System;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>
    /// Bakes the EXIF rotation of a JPEG or PNG into its pixels. A picture rotated in a viewer often only gets an "Orientation" tag
    /// (2 to 8): browsers honour it (the original shows as rotated), but Jellyfin's image resizer IGNORES it and drops it, so
    /// every resized copy (the thumbnails of "Modifier les images", the Projection theme's header, the apps) comes out with
    /// the pixels as stored: a picture turned landscape shows portrait again once selected. Reproduced in Jellyfin 10.11.6.
    /// The plugin therefore hands Jellyfin such an image already rotated, re-encoded in the same format (JPEG, or PNG to keep
    /// transparency) without the tag; any other image (no tag, tag 1, another format, unreadable) is passed through untouched.
    /// Chrome honours the tag in JPEG and in a PNG's eXIf chunk, not in WebP (checked), so WebP is left alone.
    /// </summary>
    public static class ExifOrientation
    {
        private const int JpegQuality = 92;

        /// <summary>The image as browsers show it (rotation applied, tag gone) when it carries a rotation; else the input</summary>
        public static byte[] Normalize(byte[] data, ILogger logger, string name)
        {
            var png = IsPng(data);
            if (!(png ? TryReadPng(data, out var orientation) : TryRead(data, out orientation)) || orientation < 2 || orientation > 8)
            {
                return data;
            }

            try
            {
                using var codec = SKCodec.Create(new SKMemoryStream(data));
                using var source = codec == null ? null : SKBitmap.Decode(codec);
                if (source == null)
                {
                    logger?.LogWarning("🖼️ [IMAGE ORIENTATION] ⚠️ {Name}: EXIF rotation {Orientation} but the image cannot be decoded - kept as is", name, orientation);
                    return data;
                }

                int w = source.Width, h = source.Height;
                var swap = orientation >= 5;
                var matrix = orientation switch
                {
                    2 => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),     // mirrored
                    3 => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),    // upside down
                    4 => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),     // mirrored upside down
                    5 => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),      // transposed
                    6 => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),     // rotate 90° clockwise
                    7 => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),    // transversed
                    _ => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1)      // 8: rotate 90° counter-clockwise
                };

                using var target = new SKBitmap(new SKImageInfo(swap ? h : w, swap ? w : h, SKColorType.Rgba8888, SKAlphaType.Premul));
                using (var canvas = new SKCanvas(target))
                {
                    canvas.SetMatrix(in matrix);
                    canvas.DrawBitmap(source, 0, 0);
                }

                using var image = SKImage.FromBitmap(target);
                using var encoded = image.Encode(png ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg, png ? 100 : JpegQuality);
                if (encoded == null)
                {
                    return data;
                }

                logger?.LogInformation("🖼️ [IMAGE ORIENTATION] {Name}: EXIF rotation {Orientation} applied to the pixels ({W}x{H} → {TW}x{TH})", name, orientation, w, h, target.Width, target.Height);
                return encoded.ToArray();
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "🖼️ [IMAGE ORIENTATION] ⚠️ {Name}: rotation failed - kept as is", name);
                return data;
            }
        }

        /// <summary>The EXIF Orientation of a JPEG (APP1 "Exif", IFD0 tag 0x0112); false when not a JPEG or no tag. Reads headers only.</summary>
        internal static bool TryRead(byte[] d, out int orientation)
        {
            orientation = 0;
            if (d == null || d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8)
            {
                return false;
            }

            var pos = 2;
            while (pos + 4 <= d.Length)
            {
                if (d[pos] != 0xFF) { return false; }
                var marker = d[pos + 1];
                if (marker == 0xFF) { pos++; continue; }                  // fill byte
                if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD8)) { pos += 2; continue; }  // no length
                if (marker == 0xDA || marker == 0xD9) { return false; }   // image data starts: no EXIF after it
                var length = (d[pos + 2] << 8) | d[pos + 3];
                if (length < 2 || pos + 2 + length > d.Length) { return false; }
                var body = pos + 4;
                if (marker == 0xE1 && length >= 16 && d[body] == 'E' && d[body + 1] == 'x' && d[body + 2] == 'i' && d[body + 3] == 'f' && d[body + 4] == 0 && d[body + 5] == 0)
                {
                    return ReadOrientation(d, body + 6, pos + 2 + length, out orientation);
                }

                pos += 2 + length;
            }

            return false;
        }

        private static bool IsPng(byte[] d) => d != null && d.Length > 8 && d[0] == 0x89 && d[1] == 'P' && d[2] == 'N' && d[3] == 'G';

        /// <summary>The EXIF Orientation of a PNG (its "eXIf" chunk holds a TIFF header); false when there is none</summary>
        internal static bool TryReadPng(byte[] d, out int orientation)
        {
            orientation = 0;
            var pos = 8;
            while (pos + 12 <= d.Length)
            {
                var length = (int)(((uint)d[pos] << 24) | ((uint)d[pos + 1] << 16) | ((uint)d[pos + 2] << 8) | d[pos + 3]);
                if (length < 0 || pos + 12 + length > d.Length) { return false; }
                var type = System.Text.Encoding.ASCII.GetString(d, pos + 4, 4);
                if (type == "eXIf") { return ReadOrientation(d, pos + 8, pos + 8 + length, out orientation); }
                if (type == "IDAT" || type == "IEND") { return false; }   // eXIf comes before the image data
                pos += 12 + length;
            }

            return false;
        }

        private static bool ReadOrientation(byte[] d, int tiff, int end, out int orientation)
        {
            orientation = 0;
            if (tiff + 8 > end)
            {
                return false;
            }

            var be = d[tiff] == 'M' && d[tiff + 1] == 'M';
            if (!be && !(d[tiff] == 'I' && d[tiff + 1] == 'I'))
            {
                return false;
            }

            int U16(int at) => be ? (d[at] << 8) | d[at + 1] : d[at] | (d[at + 1] << 8);
            long U32(int at) => be ? ((long)d[at] << 24) | ((long)d[at + 1] << 16) | ((long)d[at + 2] << 8) | d[at + 3]
                                   : d[at] | ((long)d[at + 1] << 8) | ((long)d[at + 2] << 16) | ((long)d[at + 3] << 24);

            var ifd = U32(tiff + 4);
            if (ifd < 8 || tiff + ifd + 2 > end)
            {
                return false;
            }

            var count = U16(tiff + (int)ifd);
            for (var i = 0; i < count; i++)
            {
                var entry = tiff + (int)ifd + 2 + i * 12;
                if (entry + 12 > end) { return false; }
                if (U16(entry) == 0x0112 && U16(entry + 2) == 3)          // Orientation, SHORT
                {
                    orientation = U16(entry + 8);
                    return true;
                }
            }

            return false;
        }
    }
}
