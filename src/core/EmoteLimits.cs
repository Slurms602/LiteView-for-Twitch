// Hard limits for emote, badge and avatar images. Animated emotes are the most expensive thing chat
// handles (every frame is kept decoded), so size, dimensions, frame count and total bytes are bounded,
// and anything larger is scaled down, thinned out, or rejected.
using System;
using System.Collections.Generic;

namespace LiteViewCore
{
    public static class EmoteLimits
    {
        /// <summary>Largest image file we download (7TV / BTTV animated 1x files are well under 1 MB).</summary>
        public const int MaxDownloadBytes = 4 * 1024 * 1024;
        /// <summary>Largest source image we decode (each GIF frame is expanded to this canvas).</summary>
        public const int MaxSourceSide = 2048, MaxSourcePixels = 2048 * 1024;
        /// <summary>Frames kept per animation, and decoded bytes per image (frames x width x height x 4).</summary>
        public const int MaxFrames = 400, MaxDecodedBytes = 8 * 1024 * 1024;
        /// <summary>How wide an emote may be drawn, in multiples of its line height (7TV wide emotes are ~4:1).</summary>
        public const int MaxAspect = 5;
        /// <summary>Downloads running at the same time.</summary>
        public const int MaxConcurrentDownloads = 4;

        /// <summary>Whether a decoded source of this size may be processed at all.</summary>
        public static bool SourceOk(int w, int h)
        {
            return w > 0 && h > 0 && w <= MaxSourceSide && h <= MaxSourceSide && (long)w * h <= MaxSourcePixels;
        }

        /// <summary>
        /// The drawn size of an image. Upstream rule: scale to fixedH when given, otherwise down to the
        /// 32 px line cap when taller. New: the width is capped too (at MaxAspect x the height).
        /// </summary>
        public static void Fit(int w, int h, int fixedH, out int outW, out int outH, int cap = 32)
        {
            if (w <= 0 || h <= 0) { outW = outH = fixedH > 0 ? fixedH : 28; return; }
            int c = fixedH > 0 ? fixedH : cap;
            if (h > c || fixedH > 0) { w = (int)Math.Max(1, (long)w * c / h); h = c; }
            int maxW = Math.Max(1, h * MaxAspect);
            if (w > maxW) { h = (int)Math.Max(1, (long)h * maxW / w); w = maxW; }
            outW = w; outH = h;
        }

        /// <summary>How many frames to keep for an animation of n source frames drawn at w x h.</summary>
        public static int FramesToKeep(int sourceFrames, int w, int h)
        {
            if (sourceFrames <= 1) return 1;
            long per = Math.Max(1L, (long)w * h * 4);
            long byBudget = Math.Max(1L, MaxDecodedBytes / per);
            return (int)Math.Min(Math.Min(sourceFrames, MaxFrames), byBudget);
        }

        /// <summary>
        /// Which source frames to keep (evenly spread) when only `keep` of n fit. Each kept frame shows for
        /// the delays of the frames dropped after it, so the animation keeps its speed.
        /// </summary>
        public static int[] FramePlan(int sourceFrames, int keep)
        {
            if (sourceFrames <= 0) return Array.Empty<int>();
            keep = Math.Max(1, Math.Min(keep, sourceFrames));
            var idx = new int[keep];
            for (int i = 0; i < keep; i++) idx[i] = (int)((long)i * sourceFrames / keep);
            return idx;
        }

        /// <summary>Per kept frame, its display time: the sum of the source delays it stands for (20 ms+ each; under 20 ms means 100 ms, like browsers).</summary>
        public static int[] MergeDelays(int[] sourceDelaysMs, int[] plan)
        {
            var d = new int[plan.Length];
            for (int k = 0; k < plan.Length; k++)
            {
                int from = plan[k], to = k + 1 < plan.Length ? plan[k + 1] : sourceDelaysMs.Length;
                long sum = 0;
                for (int i = from; i < to && i < sourceDelaysMs.Length; i++) sum += sourceDelaysMs[i] < 20 ? 100 : sourceDelaysMs[i];
                d[k] = (int)Math.Min(int.MaxValue, Math.Max(20, sum));
            }
            return d;
        }
    }

    /// <summary>
    /// Least-recently-used bookkeeping with a byte budget. It only tracks keys and sizes; the owner frees
    /// the actual objects for the keys Evict returns. Not thread-safe: use it from one thread (the UI thread).
    /// </summary>
    public sealed class ByteBudget<TKey> where TKey : notnull
    {
        readonly LinkedList<KeyValuePair<TKey, long>> order = new LinkedList<KeyValuePair<TKey, long>>();
        readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, long>>> map;
        public long Budget { get; set; }
        public long Total { get; private set; }
        public int Count { get { return map.Count; } }

        public ByteBudget(long budget, IEqualityComparer<TKey>? cmp = null)
        {
            Budget = budget; map = new Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, long>>>(cmp ?? EqualityComparer<TKey>.Default);
        }

        /// <summary>Records a use of key (most recent now), adding it with its size if new.</summary>
        public void Touch(TKey key, long bytes)
        {
            if (map.TryGetValue(key, out var n))
            {
                if (n.Value.Value != bytes) { Total += bytes - n.Value.Value; n.Value = new KeyValuePair<TKey, long>(key, bytes); }
                if (n != order.Last) { order.Remove(n); order.AddLast(n); }
                return;
            }
            map[key] = order.AddLast(new KeyValuePair<TKey, long>(key, bytes));
            Total += bytes;
        }

        public bool Contains(TKey key) { return map.ContainsKey(key); }

        public void Remove(TKey key)
        {
            if (!map.TryGetValue(key, out var n)) return;
            order.Remove(n); map.Remove(key); Total -= n.Value.Value;
        }

        /// <summary>Removes and returns least recently used keys until the total fits the budget.
        /// Keys for which keep(key) is true (in use right now) are left, and moved to the recent end.</summary>
        public List<TKey> Evict(Func<TKey, bool>? keep = null)
        {
            var gone = new List<TKey>();
            int guard = map.Count;
            while (Total > Budget && order.First != null && guard-- > 0)
            {
                var n = order.First;
                if (keep != null && keep(n.Value.Key)) { order.Remove(n); order.AddLast(n); continue; }
                order.Remove(n); map.Remove(n.Value.Key); Total -= n.Value.Value; gone.Add(n.Value.Key);
            }
            return gone;
        }

        public void Clear() { order.Clear(); map.Clear(); Total = 0; }
    }
}
