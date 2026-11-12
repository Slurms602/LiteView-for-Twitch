// Small pure helpers: bounded reads of network data, CPU topology decisions, diagnostics text.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LiteViewCore
{
    public sealed class TooLargeException : IOException
    {
        public TooLargeException(long limit) : base("response larger than " + limit + " bytes") { }
    }

    public static class Bounded
    {
        /// <summary>Largest JSON document we accept from Twitch or an emote service.</summary>
        public const int MaxJsonBytes = 16 * 1024 * 1024;

        /// <summary>Reads the whole stream, but throws TooLargeException past max bytes.</summary>
        public static async Task<byte[]> ReadAllAsync(Stream s, int max, CancellationToken ct = default)
        {
            var ms = new MemoryStream();
            var buf = new byte[81920];
            int n;
            while ((n = await s.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
            {
                if (ms.Length + n > max) throw new TooLargeException(max);
                ms.Write(buf, 0, n);
            }
            return ms.ToArray();
        }
    }

    /// <summary>One L3 cache as Windows reports it (GetLogicalProcessorInformationEx, RelationCache).</summary>
    public struct L3Cache
    {
        public uint Size; public ulong Mask; public ushort Group;
        public L3Cache(uint size, ulong mask, ushort group) { Size = size; Mask = mask; Group = group; }
    }

    public static class CpuTopology
    {
        /// <summary>
        /// The logical processors that share the SMALLEST L3 cache: on Ryzen X3D parts with two dies, the die
        /// without 3D V-Cache. 0 (= do not pin) when every L3 is the same size (9900X, 9950X, Intel, one die),
        /// when the CPU spans several processor groups (over 64 threads: a group-0 mask would be wrong), or when
        /// the result would leave fewer than 2 logical processors.
        /// </summary>
        public static ulong SmallestL3Mask(IList<L3Cache> l3)
        {
            if (l3 == null || l3.Count < 2) return 0;
            uint min = uint.MaxValue, max = 0;
            foreach (var c in l3)
            {
                if (c.Group != 0) return 0;
                if (c.Size < min) min = c.Size;
                if (c.Size > max) max = c.Size;
            }
            if (min == max) return 0;
            ulong m = 0;
            foreach (var c in l3) if (c.Size == min) m |= c.Mask;
            return PopCount(m) >= 2 ? m : 0;
        }

        public static int PopCount(ulong v) { int n = 0; while (v != 0) { v &= v - 1; n++; } return n; }

        /// <summary>
        /// Parses the RelationCache records of a GetLogicalProcessorInformationEx buffer (layout from winnt.h:
        /// Relationship at 0, Size at 4, Level at 8, CacheSize at 12, GroupMask.Mask at 40, GroupMask.Group at 48).
        /// </summary>
        public static List<L3Cache> ParseL3(byte[] buf, int len)
        {
            var r = new List<L3Cache>();
            for (int off = 0; off + 8 <= len;)
            {
                int rel = BitConverter.ToInt32(buf, off), size = BitConverter.ToInt32(buf, off + 4);
                if (size <= 0 || off + size > len) break;
                if (rel == 2 && size >= 50 && buf[off + 8] == 3)
                    r.Add(new L3Cache(BitConverter.ToUInt32(buf, off + 12), BitConverter.ToUInt64(buf, off + 40), BitConverter.ToUInt16(buf, off + 48)));
                off += size;
            }
            return r;
        }
    }

    public static class Diagnostics
    {
        /// <summary>Settings keys that are never copied into a diagnostics report.</summary>
        static readonly string[] Private = { "token", "secret", "password", "code", "client_id" };

        /// <summary>A plain-text report: sections of name/value pairs, then log lines, all redacted for sharing.</summary>
        public static string Report(IEnumerable<KeyValuePair<string, string>> facts, IDictionary<string, string> settings, IEnumerable<string> logTail)
        {
            var sb = new StringBuilder();
            sb.Append("LiteView Enhanced diagnostics\r\n");
            sb.Append("(credentials, codes and channel names are removed; chat is never included)\r\n\r\n");
            foreach (var kv in facts) sb.Append(kv.Key).Append(": ").Append(Redact.ForSharing(kv.Value)).Append("\r\n");
            sb.Append("\r\n[settings]\r\n");
            foreach (var kv in settings)
            {
                bool hide = false;
                foreach (var p in Private) if (kv.Key.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) hide = true;
                if (kv.Key.Equals("last", StringComparison.OrdinalIgnoreCase) || kv.Key.Equals("channel", StringComparison.OrdinalIgnoreCase)) hide = true;
                sb.Append(kv.Key).Append('=').Append(hide ? "[redacted]" : Redact.ForSharing(kv.Value)).Append("\r\n");
            }
            sb.Append("\r\n[log]\r\n");
            foreach (var l in logTail) sb.Append(Redact.ForSharing(l)).Append("\r\n");
            return sb.ToString();
        }
    }
}
