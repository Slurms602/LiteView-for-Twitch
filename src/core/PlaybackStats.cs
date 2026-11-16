// The stats overlay (I while watching): what mpv reports about the playing stream, read with one "expand-text" request
// a second, and the text drawn on the video with mpv's osd-overlay (ASS). mpv's answer is untrusted text like any
// other input: parsed field by field, clamped, and escaped before it goes into the ASS text.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LiteViewCore
{
    public sealed class MpvStats
    {
        public string Codec = "", Hwdec = "";
        public int Width, Height;
        public double Fps = -1, BufferSeconds = -1, VideoBitrate = -1;
        public long Dropped = -1, DecoderDropped = -1;
        public double DisplayFps = -1;
    }

    public static class PlaybackStats
    {
        public const int RequestId = 81;

        /// <summary>The expand-text request: raw values separated by "|", "-" when mpv does not know one yet.</summary>
        public static string Request()
        {
            return "{\"command\":[\"expand-text\",\"" + Fields + "\"],\"request_id\":" + RequestId + "}";
        }

        const string Fields = "${=video-codec:-}|${=hwdec-current:-}|${=width:-}|${=height:-}|${=estimated-vf-fps:-}|${=frame-drop-count:-}|${=decoder-frame-drop-count:-}|${=demuxer-cache-duration:-}|${=video-bitrate:-}|${=display-fps:-}";

        public const int PassesId = 82;

        /// <summary>The request for mpv's render passes and their GPU times (vo-passes).</summary>
        public static string PassesRequest() { return "{\"command\":[\"get_property\",\"vo-passes\"],\"request_id\":" + PassesId + "}"; }

        /// <summary>
        /// GPU time of one new frame in milliseconds (the sum of the "fresh" passes' averages, which mpv measures in
        /// nanoseconds with GPU timer queries), from the answer to PassesRequest; -1 when the line is not that answer
        /// or mpv measured nothing.
        /// </summary>
        public static double GpuMs(string? line)
        {
            if (line == null || line.Length > 262144) return -1;
            var v = Json.Parse(line);
            if (!(Json.V(v, "request_id") is long id) || id != PassesId || Json.S(v, "error") != "success") return -1;
            var fresh = Json.A(Json.V(v, "data"), "fresh");
            if (fresh.Length == 0 || fresh.Length > 500) return -1;
            double ns = 0;
            foreach (var pass in fresh)
            {
                long avg = Json.N(pass, "avg");
                if (avg < 0 || avg > 10_000_000_000L) return -1;
                ns += avg;
            }
            return ns <= 0 ? -1 : ns / 1e6;
        }

        /// <summary>mpv's answer line to Request(), or null when the line is something else.</summary>
        public static MpvStats? ParseAnswer(string? line)
        {
            if (line == null || line.Length > 4096) return null;
            var v = Json.Parse(line);
            if (!(Json.V(v, "request_id") is long id) || id != RequestId || Json.S(v, "error") != "success") return null;
            return Json.V(v, "data") is string data ? Parse(data) : null;
        }

        public static MpvStats Parse(string? expanded)
        {
            var s = new MpvStats();
            var f = (expanded ?? "").Split('|');
            string F(int i) { return i < f.Length && f[i] != "-" ? f[i].Trim() : ""; }
            s.Codec = Clean(F(0), 80); s.Hwdec = Clean(F(1), 30);
            s.Width = (int)Num(F(2), 0, 16384, 0); s.Height = (int)Num(F(3), 0, 16384, 0);
            s.Fps = Num(F(4), 0, 1000, -1);
            s.Dropped = (long)Num(F(5), 0, 1e12, -1); s.DecoderDropped = (long)Num(F(6), 0, 1e12, -1);
            s.BufferSeconds = Num(F(7), 0, 3600, -1);
            s.VideoBitrate = Num(F(8), 0, 1e10, -1);
            s.DisplayFps = Num(F(9), 1, 1000, -1);
            return s;
        }

        static double Num(string t, double min, double max, double none)
        {
            double d;
            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out d) || double.IsNaN(d) || double.IsInfinity(d)) return none;
            return Math.Max(min, Math.Min(max, d));
        }

        static string Clean(string t, int max)
        {
            var sb = new StringBuilder();
            foreach (char c in t) if (c >= ' ' && c < 0x7F) sb.Append(c);
            string r = sb.ToString();
            return r.Length > max ? r.Substring(0, max) : r;
        }

        /// <summary>"h264 (H.264 / AVC / ...)" or "h264" -> "H.264"; hevc -> "HEVC (H.265)"; av1 -> "AV1"; else the short name.</summary>
        public static string CodecName(string? codec)
        {
            string c = (codec ?? "").Trim();
            int sp = c.IndexOf(' ');
            string k = (sp > 0 ? c.Substring(0, sp) : c).ToLowerInvariant();
            switch (k)
            {
                case "h264": return "H.264";
                case "hevc": case "h265": return "HEVC (H.265)";
                case "av1": return "AV1";
                case "": return "";
            }
            return k.Length > 16 ? k.Substring(0, 16) : k;
        }

        /// <summary>How the video is decoded, from hwdec-current ("no" or empty: by the processor).</summary>
        public static string Decoding(string? hwdec)
        {
            string h = (hwdec ?? "").Trim();
            if (h.Length == 0) return "";
            if (h == "no") return "processor (software)";
            return "graphics card (" + h + ")";
        }

        public static string Mbps(double bitsPerSecond)
        {
            if (bitsPerSecond < 0) return "";
            return (bitsPerSecond / 1e6).ToString(bitsPerSecond < 1e7 ? "0.0" : "0", CultureInfo.InvariantCulture) + " Mb/s";
        }

        public static string Seconds(double s) { return s < 0 ? "" : s.ToString("0.0", CultureInfo.InvariantCulture) + " s"; }

        /// <summary>The overlay as mpv osd-overlay "ass-events": a dark box, then the label/value lines in a monospace font.</summary>
        public static string Ass(IList<KeyValuePair<string, string>> rows, string title)
        {
            int labelW = 0;
            foreach (var r in rows) labelW = Math.Max(labelW, r.Key.Length);
            var lines = new List<string>();
            if (title.Length > 0) lines.Add(title);
            foreach (var r in rows) lines.Add(r.Key.PadRight(labelW + 2) + r.Value);
            int cols = 0; foreach (var l in lines) cols = Math.Max(cols, l.Length);
            const int fs = 14, pad = 8, lineH = 17;
            int w = (int)Math.Ceiling(cols * fs * 0.55) + 2 * pad, h = lines.Count * lineH + 2 * pad;
            var sb = new StringBuilder();
            sb.Append("{\\an7\\pos(10,10)\\bord0\\shad0\\1c&H000000&\\1a&H50&\\p1}m 0 0 l ").Append(w).Append(" 0 ").Append(w).Append(' ').Append(h).Append(" 0 ").Append(h).Append("{\\p0}\n");
            sb.Append("{\\an7\\pos(").Append(10 + pad).Append(',').Append(10 + pad).Append(")\\fnConsolas\\fs").Append(fs).Append("\\bord0\\shad0\\1c&HF1EFEF&\\q2}");
            for (int i = 0; i < lines.Count; i++) { if (i > 0) sb.Append("\\N"); sb.Append(Escape(lines[i])); }
            return sb.ToString();
        }

        /// <summary>Text that cannot start ASS override tags or line breaks.</summary>
        public static string Escape(string? t)
        {
            var sb = new StringBuilder();
            foreach (char c in t ?? "")
            {
                if (c == '{') sb.Append('(');
                else if (c == '}') sb.Append(')');
                else if (c == '\\') sb.Append('/');
                else if (c == '\n' || c == '\r' || c < ' ') sb.Append(' ');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>
        /// The mpv command that shows (or, with null, removes) the overlay. res_y is the video area's height in pixels,
        /// so the text has the same size in a small window and in fullscreen.
        /// </summary>
        public static string OverlayCommand(string? ass, int heightPx = 720)
        {
            if (ass == null) return "{\"command\":{\"name\":\"osd-overlay\",\"id\":7,\"format\":\"none\",\"data\":\"\"}}";
            int h = Math.Max(120, Math.Min(8640, heightPx));
            return "{\"command\":{\"name\":\"osd-overlay\",\"id\":7,\"format\":\"ass-events\",\"res_x\":0,\"res_y\":" + h.ToString(CultureInfo.InvariantCulture) + ",\"data\":" + Quote(ass) + "}}";
        }

        static string Quote(string t)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in t)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c < ' ' || c == 0x2028 || c == 0x2029) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }
    }
}
