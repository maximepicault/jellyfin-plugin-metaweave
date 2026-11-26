using System;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>
    /// Keeps a landscape backdrop landscape. A JPEG can store landscape pixels with an EXIF "Orientation" tag that rotates
    /// it by 90° (5 to 8): browsers and Jellyfin's apps honour the tag and show it portrait, so the Projection theme (which
    /// never uses portrait backdrops) drops it. Typical case: a portrait picture turned landscape in an editor that rotated
    /// the pixels but kept the tag. For an image used as a backdrop, the tag is reset to 1 (pixels shown as stored), in
    /// place, without re-encoding. Any other image, or a tag that does not turn landscape pixels portrait, is left alone.
    /// </summary>
    public static class ExifOrientation
    {
        /// <summary>The same JPEG with its rotation tag reset when it would show landscape pixels portrait; else the input</summary>
        public static byte[] KeepLandscape(byte[] data, ILogger logger, string name)
        {
            if (!TryRead(data, out var tagOffset, out var bigEndian, out var orientation, out var width, out var height))
            {
                return data;
            }

            if (orientation < 5 || orientation > 8 || width <= height)
            {
                return data;
            }

            var copy = (byte[])data.Clone();
            copy[tagOffset] = bigEndian ? (byte)0 : (byte)1;
            copy[tagOffset + 1] = bigEndian ? (byte)1 : (byte)0;
            logger?.LogInformation("🖼️ [IMAGE ORIENTATION] {Name}: EXIF rotation {Orientation} removed, kept landscape ({Width}x{Height})", name, orientation, width, height);
            return copy;
        }

        /// <summary>Orientation value (and where it is stored) and the pixel size of a JPEG; false when not a JPEG or no tag</summary>
        internal static bool TryRead(byte[] d, out int tagOffset, out bool bigEndian, out int orientation, out int width, out int height)
        {
            tagOffset = -1;
            bigEndian = false;
            orientation = 0;
            width = 0;
            height = 0;
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
                if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { pos += 2; continue; }  // no length
                if (marker == 0xDA || marker == 0xD9) { break; }          // image data starts: no header after
                var length = (d[pos + 2] << 8) | d[pos + 3];
                if (length < 2 || pos + 2 + length > d.Length) { return false; }
                var body = pos + 4;

                if (marker == 0xE1 && tagOffset < 0 && length >= 16 && d[body] == 'E' && d[body + 1] == 'x' && d[body + 2] == 'i' && d[body + 3] == 'f' && d[body + 4] == 0 && d[body + 5] == 0)
                {
                    ReadOrientation(d, body + 6, pos + 2 + length, out tagOffset, out bigEndian, out orientation);
                }
                else if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC && length >= 7)
                {
                    height = (d[body + 1] << 8) | d[body + 2];
                    width = (d[body + 3] << 8) | d[body + 4];
                }

                pos += 2 + length;
            }

            return tagOffset >= 0 && width > 0 && height > 0;
        }

        private static void ReadOrientation(byte[] d, int tiff, int end, out int tagOffset, out bool bigEndian, out int orientation)
        {
            tagOffset = -1;
            orientation = 0;
            bigEndian = tiff + 8 <= end && d[tiff] == 'M' && d[tiff + 1] == 'M';
            if (tiff + 8 > end || !(bigEndian || (d[tiff] == 'I' && d[tiff + 1] == 'I')))
            {
                return;
            }

            var be = bigEndian;
            int U16(int at) => be ? (d[at] << 8) | d[at + 1] : d[at] | (d[at + 1] << 8);
            long U32(int at) => be ? ((long)d[at] << 24) | ((long)d[at + 1] << 16) | ((long)d[at + 2] << 8) | d[at + 3]
                                   : d[at] | ((long)d[at + 1] << 8) | ((long)d[at + 2] << 16) | ((long)d[at + 3] << 24);

            var ifd = U32(tiff + 4);
            if (ifd < 8 || tiff + ifd + 2 > end)
            {
                return;
            }

            var count = U16(tiff + (int)ifd);
            for (var i = 0; i < count; i++)
            {
                var entry = tiff + (int)ifd + 2 + i * 12;
                if (entry + 12 > end) { return; }
                if (U16(entry) == 0x0112 && U16(entry + 2) == 3)          // Orientation, SHORT
                {
                    tagOffset = entry + 8;
                    orientation = U16(entry + 8);
                    return;
                }
            }
        }
    }
}
