// Which characters a TrueType/OpenType font has, read from its 'cmap' table. The chat draws in Inter (bundled, like
// Twitch); Windows fills in missing characters (Chinese, emoji, ...) only for system fonts such as Segoe UI, so a word
// with a character Inter lacks is drawn in Segoe UI instead. Malformed font files give null, never an exception.
using System;
using System.Collections.Generic;

namespace LiteViewCore
{
    public sealed class FontCoverage
    {
        readonly int[] starts, ends;   // sorted, non-overlapping ranges of code points with a glyph

        FontCoverage(List<KeyValuePair<int, int>> ranges)
        {
            ranges.Sort((a, b) => a.Key.CompareTo(b.Key));
            var s = new List<int>(); var e = new List<int>();
            foreach (var r in ranges)
            {
                if (s.Count > 0 && r.Key <= e[e.Count - 1] + 1) { e[e.Count - 1] = Math.Max(e[e.Count - 1], r.Value); continue; }
                s.Add(r.Key); e.Add(r.Value);
            }
            starts = s.ToArray(); ends = e.ToArray();
        }

        public int RangeCount { get { return starts.Length; } }

        public bool Has(int cp)
        {
            int lo = 0, hi = starts.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (cp < starts[mid]) hi = mid - 1;
                else if (cp > ends[mid]) lo = mid + 1;
                else return true;
            }
            return false;
        }

        /// <summary>Every character of the text has a glyph (spaces, joiners and variation selectors need none).</summary>
        public bool Covers(string? text)
        {
            if (string.IsNullOrEmpty(text)) return true;
            for (int i = 0; i < text!.Length; i++)
            {
                int cp = text[i];
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { cp = char.ConvertToUtf32(text[i], text[i + 1]); i++; }
                else if (char.IsSurrogate(text[i])) return false;
                if (cp <= 0x20 || cp == 0xA0 || (cp >= 0x200B && cp <= 0x200F) || cp == 0x2060 || (cp >= 0xFE00 && cp <= 0xFE0F)) continue;
                if (!Has(cp)) return false;
            }
            return true;
        }

        public static FontCoverage? Read(byte[]? font)
        {
            try { return font == null ? null : Parse(font); }
            catch (IndexOutOfRangeException) { return null; }
            catch (ArgumentException) { return null; }
            catch (OverflowException) { return null; }
        }

        static FontCoverage? Parse(byte[] f)
        {
            if (f.Length < 12) return null;
            int tables = U16(f, 4);
            int cmap = -1, cmapLen = 0;
            for (int i = 0; i < tables; i++)
            {
                int rec = 12 + 16 * i;
                if (rec + 16 > f.Length) return null;
                if (f[rec] == 'c' && f[rec + 1] == 'm' && f[rec + 2] == 'a' && f[rec + 3] == 'p') { cmap = I32(f, rec + 8); cmapLen = I32(f, rec + 12); break; }
            }
            if (cmap < 0 || cmapLen < 4 || (long)cmap + cmapLen > f.Length) return null;
            int subtables = U16(f, cmap + 2);
            int best = -1, bestRank = 0;
            for (int i = 0; i < subtables; i++)
            {
                int rec = cmap + 4 + 8 * i;
                if (rec + 8 > cmap + cmapLen) return null;
                int platform = U16(f, rec), encoding = U16(f, rec + 2), off = I32(f, rec + 4);
                if (off < 0 || (long)cmap + off + 2 > cmap + cmapLen) continue;
                int format = U16(f, cmap + off);
                int rank = platform == 3 && encoding == 10 && format == 12 ? 4 : platform == 0 && format == 12 ? 3 : platform == 3 && encoding == 1 && format == 4 ? 2 : platform == 0 && format == 4 ? 1 : 0;
                if (rank > bestRank) { bestRank = rank; best = cmap + off; }
            }
            if (best < 0) return null;
            int end = cmap + cmapLen;
            var ranges = new List<KeyValuePair<int, int>>();
            if (U16(f, best) == 12)
            {
                if (best + 16 > end) return null;
                long groups = (uint)I32(f, best + 12);
                if (groups > (end - best - 16) / 12) return null;
                for (int g = 0; g < groups; g++)
                {
                    int p = best + 16 + 12 * g;
                    long a = (uint)I32(f, p), z = (uint)I32(f, p + 4), glyph = (uint)I32(f, p + 8);
                    if (glyph == 0) a++;
                    if (a > z || z > 0x10FFFF) continue;
                    ranges.Add(new KeyValuePair<int, int>((int)a, (int)z));
                }
            }
            else
            {
                if (best + 14 > end) return null;
                int segs = U16(f, best + 6) / 2;
                int ends0 = best + 14, starts0 = ends0 + 2 * segs + 2, deltas0 = starts0 + 2 * segs, offs0 = deltas0 + 2 * segs;
                if (offs0 + 2 * segs > end) return null;
                for (int s = 0; s < segs; s++)
                {
                    int z = U16(f, ends0 + 2 * s), a = U16(f, starts0 + 2 * s), delta = U16(f, deltas0 + 2 * s), ro = U16(f, offs0 + 2 * s);
                    if (a > z) continue;
                    int run = -1;
                    for (int c = a; c <= z && c != 0xFFFF; c++)
                    {
                        int glyph;
                        if (ro == 0) glyph = (c + delta) & 0xFFFF;
                        else
                        {
                            long at = offs0 + 2L * s + ro + 2L * (c - a);
                            if (at + 2 > end) { glyph = 0; }
                            else { glyph = U16(f, (int)at); if (glyph != 0) glyph = (glyph + delta) & 0xFFFF; }
                        }
                        if (glyph != 0) { if (run < 0) run = c; }
                        else if (run >= 0) { ranges.Add(new KeyValuePair<int, int>(run, c - 1)); run = -1; }
                    }
                    if (run >= 0) ranges.Add(new KeyValuePair<int, int>(run, z == 0xFFFF ? 0xFFFE : z));
                }
            }
            return ranges.Count == 0 ? null : new FontCoverage(ranges);
        }

        static int U16(byte[] f, int p) { return (f[p] << 8) | f[p + 1]; }
        static int I32(byte[] f, int p) { return (f[p] << 24) | (f[p + 1] << 16) | (f[p + 2] << 8) | f[p + 3]; }
    }
}
