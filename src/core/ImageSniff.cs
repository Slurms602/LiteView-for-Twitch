// Reads an image's format and pixel size from its first bytes, before Windows' image decoder (GDI+)
// sees it. Only PNG, GIF and JPEG are passed on - the formats Twitch, 7TV, BetterTTV and FFZ serve -
// so oversized images are refused without being decoded, and GDI+'s other parsers (EMF, WMF, TIFF,
// ICO...) never touch downloaded data.
using System;

namespace LiteViewCore
{
    public enum ImageKind { Unknown, Png, Gif, Jpeg }

    public static class ImageSniff
    {
        public static ImageKind Kind(byte[] b)
        {
            if (b == null || b.Length < 12) return ImageKind.Unknown;
            if (b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G' && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A) return ImageKind.Png;
            if (b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8' && (b[4] == '7' || b[4] == '9') && b[5] == 'a') return ImageKind.Gif;
            if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return ImageKind.Jpeg;
            return ImageKind.Unknown;
        }

        /// <summary>Width and height from the header; false when the format is not allowed or the header is broken.</summary>
        public static bool TrySize(byte[] b, out int w, out int h)
        {
            w = h = 0;
            switch (Kind(b))
            {
                case ImageKind.Png:
                    // signature, then the IHDR chunk: length(4) "IHDR" width(4, BE) height(4, BE)
                    if (b.Length < 24 || b[12] != 'I' || b[13] != 'H' || b[14] != 'D' || b[15] != 'R') return false;
                    w = Be32(b, 16); h = Be32(b, 20);
                    return w > 0 && h > 0;
                case ImageKind.Gif:
                    w = b[6] | (b[7] << 8); h = b[8] | (b[9] << 8);   // logical screen size (every frame is drawn on it)
                    return w > 0 && h > 0;
                case ImageKind.Jpeg:
                    return JpegSize(b, out w, out h);
                default:
                    return false;
            }
        }

        /// <summary>True when the bytes are an allowed image whose size passes EmoteLimits.SourceOk.</summary>
        public static bool Acceptable(byte[] b)
        {
            return b != null && b.Length <= EmoteLimits.MaxDownloadBytes && TrySize(b, out int w, out int h) && EmoteLimits.SourceOk(w, h);
        }

        static int Be32(byte[] b, int o)
        {
            long v = ((long)b[o] << 24) | ((long)b[o + 1] << 16) | ((long)b[o + 2] << 8) | b[o + 3];
            return v > int.MaxValue ? -1 : (int)v;
        }

        static bool JpegSize(byte[] b, out int w, out int h)
        {
            w = h = 0;
            int i = 2;
            while (i + 4 <= b.Length)
            {
                if (b[i] != 0xFF) return false;
                byte m = b[i + 1];
                if (m == 0xFF) { i++; continue; }                     // fill bytes
                if (m == 0xD8 || (m >= 0xD0 && m <= 0xD7) || m == 0x01) { i += 2; continue; }
                if (m == 0xD9 || m == 0xDA) return false;             // end / start of scan before any frame header
                int len = (b[i + 2] << 8) | b[i + 3];
                if (len < 2) return false;
                bool sof = m >= 0xC0 && m <= 0xCF && m != 0xC4 && m != 0xC8 && m != 0xCC;
                if (sof)
                {
                    if (i + 9 > b.Length) return false;
                    h = (b[i + 5] << 8) | b[i + 6]; w = (b[i + 7] << 8) | b[i + 8];
                    return w > 0 && h > 0;
                }
                i += 2 + len;
            }
            return false;
        }
    }
}
