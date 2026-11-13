// Small decisions behind P5 features: which favourites just went live and whether it is quiet time (notifications),
// which settings an imported file may set, and whether a release is newer than this build (update check).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace LiteViewCore
{
    /// <summary>Channels that went live since the last look. The first look only learns who is live (no burst at start).</summary>
    public sealed class LiveWatch
    {
        HashSet<string>? live;

        public List<string> Update(IEnumerable<string> liveNow)
        {
            var now = new HashSet<string>(liveNow, StringComparer.Ordinal);
            var started = live == null ? new List<string>() : now.Where(l => !live.Contains(l)).OrderBy(l => l, StringComparer.Ordinal).ToList();
            live = now;
            return started;
        }

        public void Reset() { live = null; }
    }

    /// <summary>Quiet hours "23-8" (from 23:00 to 08:00, across midnight) or "13-14"; "" or "off" = never quiet.</summary>
    public static class QuietHours
    {
        public static bool TryParse(string? s, out int from, out int to)
        {
            from = to = 0;
            var m = Regex.Match(s ?? "", @"^\s*(\d{1,2})\s*-\s*(\d{1,2})\s*$");
            if (!m.Success) return false;
            from = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture); to = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            return from >= 0 && from < 24 && to >= 0 && to < 24 && from != to;
        }

        public static bool IsQuiet(string? setting, DateTime localTime)
        {
            int from, to;
            if (!TryParse(setting, out from, out to)) return false;
            int h = localTime.Hour;
            return from < to ? h >= from && h < to : h >= from || h < to;
        }
    }

    /// <summary>Settings import: only known keys, one-line values of a sane length. Anything else in the file is ignored.</summary>
    public static class SettingsImport
    {
        public static readonly HashSet<string> Keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "quality", "chat", "volume", "muted", "picture", "rtx", "chatmode", "sidebar", "x", "y", "w", "h", "max",
            "following", "sort", "favfirst", "favorites", "keeprecent", "recent", "keepsearches", "searches", "openlast",
            "notify", "quiet", "updates", "chatwidth", "chatsize", "timestamps", "highlights", "ignored", "cpu", "cpumask", "gaming", "gamingpic",
        };
        public const int MaxValue = 20000;
        static readonly char[] LineBreaks = { '\r', '\n', '\0' };

        public static Dictionary<string, string> Filter(IDictionary<string, string> file)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in file)
                if (Keys.Contains(kv.Key) && kv.Value != null && kv.Value.Length <= MaxValue && kv.Value.IndexOfAny(LineBreaks) < 0)
                    d[kv.Key.ToLowerInvariant()] = kv.Value;
            return d;
        }
    }

    public static class Versions
    {
        /// <summary>"v1.2.3" / "1.2.3" / "1.2.3-ci+abc" → 1.2.3; null when it is not a version.</summary>
        public static Version? Parse(string? s)
        {
            var m = Regex.Match(s ?? "", @"^\s*v?(\d{1,5})\.(\d{1,5})(?:\.(\d{1,5}))?");
            if (!m.Success) return null;
            return new Version(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0);
        }

        /// <summary>True when the release tag is a newer version than this build. A pre-release tag ("v1.3.0-beta") is never offered.</summary>
        public static bool IsNewer(string? releaseTag, string? current)
        {
            if (releaseTag == null || releaseTag.IndexOf('-') >= 0) return false;
            Version? r = Parse(releaseTag), c = Parse(current);
            return r != null && c != null && r > c;
        }
    }
}
