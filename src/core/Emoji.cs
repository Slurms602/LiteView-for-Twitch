// Finding emoji in chat text, so the chat can draw them in colour (GDI+, which draws the rest, has no colour fonts).
// A simplified Unicode emoji grammar (UTS #51): a pictograph with emoji presentation, or any pictograph followed by
// U+FE0F, optionally a skin tone, joined by U+200D into sequences; flags (two regional indicators), keycaps (0-9 # *
// U+FE0F U+20E3) and tag sequences (black flag + tags + cancel tag). Anything else stays text.
using System.Collections.Generic;
using System.Text;

namespace LiteViewCore
{
    public static class EmojiText
    {
        public const int MaxSequence = 40;   // UTF-16 units; longer runs (spam) are split

        // characters that are emoji without U+FE0F (Emoji_Presentation=Yes), Unicode 15.1
        static readonly int[] Presentation =
        {
            0x231A, 0x231B, 0x23E9, 0x23EC, 0x23F0, 0x23F0, 0x23F3, 0x23F3, 0x25FD, 0x25FE, 0x2614, 0x2615, 0x2648, 0x2653,
            0x267F, 0x267F, 0x2693, 0x2693, 0x26A1, 0x26A1, 0x26AA, 0x26AB, 0x26BD, 0x26BE, 0x26C4, 0x26C5, 0x26CE, 0x26CE,
            0x26D4, 0x26D4, 0x26EA, 0x26EA, 0x26F2, 0x26F3, 0x26F5, 0x26F5, 0x26FA, 0x26FA, 0x26FD, 0x26FD, 0x2705, 0x2705,
            0x270A, 0x270B, 0x2728, 0x2728, 0x274C, 0x274C, 0x274E, 0x274E, 0x2753, 0x2755, 0x2757, 0x2757, 0x2795, 0x2797,
            0x27B0, 0x27B0, 0x27BF, 0x27BF, 0x2B1B, 0x2B1C, 0x2B50, 0x2B50, 0x2B55, 0x2B55, 0x1F004, 0x1F004, 0x1F0CF, 0x1F0CF,
            0x1F18E, 0x1F18E, 0x1F191, 0x1F19A, 0x1F201, 0x1F201, 0x1F21A, 0x1F21A, 0x1F22F, 0x1F22F, 0x1F232, 0x1F236,
            0x1F238, 0x1F23A, 0x1F250, 0x1F251, 0x1F300, 0x1F320, 0x1F32D, 0x1F335, 0x1F337, 0x1F37C, 0x1F37E, 0x1F393,
            0x1F3A0, 0x1F3CA, 0x1F3CF, 0x1F3D3, 0x1F3E0, 0x1F3F0, 0x1F3F4, 0x1F3F4, 0x1F3F8, 0x1F43E, 0x1F440, 0x1F440,
            0x1F442, 0x1F4FC, 0x1F4FF, 0x1F53D, 0x1F54B, 0x1F54E, 0x1F550, 0x1F567, 0x1F57A, 0x1F57A, 0x1F595, 0x1F596,
            0x1F5A4, 0x1F5A4, 0x1F5FB, 0x1F64F, 0x1F680, 0x1F6C5, 0x1F6CC, 0x1F6CC, 0x1F6D0, 0x1F6D2, 0x1F6D5, 0x1F6D7,
            0x1F6DC, 0x1F6DF, 0x1F6EB, 0x1F6EC, 0x1F6F4, 0x1F6FC, 0x1F7E0, 0x1F7EB, 0x1F7F0, 0x1F7F0, 0x1F90C, 0x1F93A,
            0x1F93C, 0x1F945, 0x1F947, 0x1F9FF, 0x1FA70, 0x1FA7C, 0x1FA80, 0x1FA89, 0x1FA8F, 0x1FAC6, 0x1FACE, 0x1FADC,
            0x1FADF, 0x1FAE9, 0x1FAF0, 0x1FAF8,
        };

        // pictographs that become emoji with U+FE0F, or inside a joined sequence (Extended_Pictographic, approximated)
        static readonly int[] Pictographic =
        {
            0x00A9, 0x00A9, 0x00AE, 0x00AE, 0x203C, 0x203C, 0x2049, 0x2049, 0x2122, 0x2122, 0x2139, 0x2139, 0x2194, 0x2199,
            0x21A9, 0x21AA, 0x231A, 0x231B, 0x2328, 0x2328, 0x2388, 0x2388, 0x23CF, 0x23CF, 0x23E9, 0x23F3, 0x23F8, 0x23FA,
            0x24C2, 0x24C2, 0x25AA, 0x25AB, 0x25B6, 0x25B6, 0x25C0, 0x25C0, 0x25FB, 0x25FE, 0x2600, 0x27BF, 0x2934, 0x2935,
            0x2B05, 0x2B07, 0x2B1B, 0x2B1C, 0x2B50, 0x2B50, 0x2B55, 0x2B55, 0x3030, 0x3030, 0x303D, 0x303D, 0x3297, 0x3297,
            0x3299, 0x3299, 0x1F000, 0x1F0FF, 0x1F10D, 0x1F10F, 0x1F12F, 0x1F12F, 0x1F16C, 0x1F171, 0x1F17E, 0x1F17F,
            0x1F18E, 0x1F18E, 0x1F191, 0x1F19A, 0x1F1AD, 0x1F1E5, 0x1F201, 0x1F20F, 0x1F21A, 0x1F21A, 0x1F22F, 0x1F22F,
            0x1F232, 0x1F23A, 0x1F23C, 0x1F23F, 0x1F249, 0x1F3FA, 0x1F400, 0x1F53D, 0x1F546, 0x1F64F, 0x1F680, 0x1F6FF,
            0x1F774, 0x1F77F, 0x1F7D5, 0x1F7FF, 0x1F80C, 0x1F80F, 0x1F848, 0x1F84F, 0x1F85A, 0x1F85F, 0x1F888, 0x1F88F,
            0x1F8AE, 0x1F8FF, 0x1F90C, 0x1F93A, 0x1F93C, 0x1F945, 0x1F947, 0x1FAFF, 0x1FC00, 0x1FFFD,
        };

        static bool In(int[] ranges, int cp)
        {
            int lo = 0, hi = ranges.Length / 2 - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (cp < ranges[mid * 2]) hi = mid - 1;
                else if (cp > ranges[mid * 2 + 1]) lo = mid + 1;
                else return true;
            }
            return false;
        }

        public static bool IsPresentation(int cp) { return In(Presentation, cp); }
        public static bool IsPictographic(int cp) { return In(Pictographic, cp) || IsPresentation(cp); }
        static bool IsRegional(int cp) { return cp >= 0x1F1E6 && cp <= 0x1F1FF; }
        static bool IsTone(int cp) { return cp >= 0x1F3FB && cp <= 0x1F3FF; }
        static bool IsTag(int cp) { return cp >= 0xE0020 && cp <= 0xE007E; }

        static int At(string s, int i, out int len)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { len = 2; return char.ConvertToUtf32(c, s[i + 1]); }
            len = 1; return c;
        }

        /// <summary>True when the text has at least one emoji (a quick check before Split).</summary>
        public static bool Has(string? s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (var part in Split(s!)) if (part.Value) return true;
            return false;
        }

        /// <summary>The text in runs: (text, false) and (one emoji sequence, true), in order; together exactly the text.</summary>
        public static List<KeyValuePair<string, bool>> Split(string s)
        {
            var list = new List<KeyValuePair<string, bool>>();
            var text = new StringBuilder();
            int i = 0;
            while (i < s.Length)
            {
                int n = Emoji(s, i);
                if (n == 0) { text.Append(s[i]); i++; continue; }
                if (text.Length > 0) { list.Add(new KeyValuePair<string, bool>(text.ToString(), false)); text.Clear(); }
                list.Add(new KeyValuePair<string, bool>(s.Substring(i, n), true));
                i += n;
            }
            if (text.Length > 0) list.Add(new KeyValuePair<string, bool>(text.ToString(), false));
            return list;
        }

        // the length (UTF-16 units) of the emoji sequence starting at i, or 0
        static int Emoji(string s, int i)
        {
            int len, cp = At(s, i, out len), j = i + len;
            // keycap: 0-9 # * [FE0F] 20E3
            if (cp < 0x80)
            {
                if (!(cp >= '0' && cp <= '9' || cp == '#' || cp == '*')) return 0;
                int k = j;
                if (k < s.Length && s[k] == '️') k++;
                return k < s.Length && s[k] == '⃣' ? k + 1 - i : 0;
            }
            // flag: two regional indicators
            if (IsRegional(cp))
            {
                if (j >= s.Length) return 0;
                int l2, cp2 = At(s, j, out l2);
                return IsRegional(cp2) ? j + l2 - i : 0;
            }
            int end = Element(s, i, cp, len, true);
            if (end == 0) return 0;
            // tag sequence (subdivision flags): black flag, tags, cancel tag
            if (cp == 0x1F3F4)
            {
                int k = end, lt;
                while (k < s.Length && IsTag(At(s, k, out lt))) k += lt;
                if (k > end && k < s.Length && At(s, k, out lt) == 0xE007F) end = k + lt;
            }
            // joined: ZWJ pictograph ...
            while (end < s.Length && s[end] == '‍' && end + 1 < s.Length && end - i < MaxSequence)
            {
                int l3, cp3 = At(s, end + 1, out l3);
                int next = IsPictographic(cp3) ? Element(s, end + 1, cp3, l3, false) : 0;
                if (next == 0) break;
                end = next;
            }
            return end - i;
        }

        // one pictograph with its FE0F / skin tone; first: it must have emoji presentation on its own (or FE0F)
        static int Element(string s, int i, int cp, int len, bool first)
        {
            if (!IsPictographic(cp) || IsTone(cp)) return 0;
            int k = i + len, lt;
            bool vs = k < s.Length && s[k] == '️';
            if (vs) k++;
            else if (k < s.Length && s[k] == '︎') return 0;   // text presentation asked for
            if (first && !vs && !IsPresentation(cp))
            {
                if (!(k < s.Length && IsTone(At(s, k, out lt)))) return 0;   // a text symbol (©, ☺) stays text
            }
            if (k < s.Length && IsTone(At(s, k, out lt))) k += lt;
            return k;
        }
    }
}
