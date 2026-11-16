// Where you stopped in each video (owner's request: "see which ones I have watched and where I left off, like a little
// red bar at the bottom of the card"): the position and length of the last MaxEntries videos watched, newest first,
// kept in liteview.videos next to LiteView.exe (one line "id position length"). A video plays on from its position,
// unless it was nearly finished or barely started.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LiteViewCore
{
    public sealed class WatchedVideos
    {
        public const int MaxEntries = 500;
        /// <summary>Less than this from the start or the end: the video starts from the beginning (it was barely started, or finished).</summary>
        public const int ResumeMargin = 30;
        readonly List<string> order = new List<string>();   // newest first
        readonly Dictionary<string, KeyValuePair<int, int>> map = new Dictionary<string, KeyValuePair<int, int>>(StringComparer.Ordinal);

        public int Count { get { return order.Count; } }

        /// <summary>Records where a video is (numeric ids only; length 0 = unknown).</summary>
        public void Set(string id, int position, int length)
        {
            if (!ChannelName.IsNumericId(id) || position < 0) return;
            if (length < 0) length = 0;
            if (length > 0 && position > length) position = length;
            map[id] = new KeyValuePair<int, int>(position, length);
            order.Remove(id); order.Insert(0, id);
            while (order.Count > MaxEntries) { map.Remove(order[order.Count - 1]); order.RemoveAt(order.Count - 1); }
        }

        public bool TryGet(string id, out int position, out int length)
        {
            KeyValuePair<int, int> e;
            if (id != null && map.TryGetValue(id, out e)) { position = e.Key; length = e.Value; return true; }
            position = length = 0; return false;
        }

        /// <summary>How much of it was watched, 0 to 1 (0: never, or its length is not known).</summary>
        public double Fraction(string id, int knownLength)
        {
            int pos, len;
            if (!TryGet(id, out pos, out len)) return 0;
            if (len <= 0) len = knownLength;
            if (len <= 0) return 0;
            return Math.Max(0, Math.Min(1, pos / (double)len));
        }

        /// <summary>Where the video should start: its position, or 0 when it was barely started or nearly finished.</summary>
        public int ResumeAt(string id, int knownLength)
        {
            int pos, len;
            if (!TryGet(id, out pos, out len)) return 0;
            if (len <= 0) len = knownLength;
            if (pos < ResumeMargin || (len > 0 && pos > len - ResumeMargin)) return 0;
            return pos;
        }

        public void Forget(string id) { if (id != null && map.Remove(id)) order.Remove(id); }

        public string Save()
        {
            var sb = new StringBuilder();
            foreach (var id in order)
            {
                var e = map[id];
                sb.Append(id).Append(' ').Append(e.Key.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(e.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>The file's lines (newest first); anything malformed is skipped.</summary>
        public static WatchedVideos Load(string? text)
        {
            var w = new WatchedVideos();
            if (string.IsNullOrEmpty(text)) return w;
            var lines = text!.Split('\n');
            for (int i = Math.Min(lines.Length, MaxEntries * 2) - 1; i >= 0; i--)   // oldest first, so the newest ends up first
            {
                var p = lines[i].Trim().Split(' ');
                int pos, len;
                if (p.Length != 3 || !ChannelName.IsNumericId(p[0])) continue;
                if (!int.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out pos) || !int.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out len)) continue;
                w.Set(p[0], pos, len);
            }
            return w;
        }
    }
}
