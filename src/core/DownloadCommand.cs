// Downloading a video or clip to the PC (owner's request: "the ability to download past broadcasts ... like yt-dlp"):
// Streamlink, which LiteView already ships, writes the stream to a file instead of a player (--output): a video's
// segments as they are (MPEG-TS, .ts: plays in VLC, mpv, the Media Player), a clip's MP4 as it is. No re-encoding.
// The progress comes from Streamlink's debug log ("First Sequence: N; Last Sequence: M", then "Writing segment K to
// output" per segment; checked in Streamlink 8.6.3's stream/hls/hls.py). The file is named ".part" until Streamlink
// ends well, so an unfinished download never looks finished.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace LiteViewCore
{
    public sealed class DownloadRequest
    {
        public string Streamlink = "", LogFile = "", Output = "", Channel = "", Video = "", Clip = "", Quality = "Best";
    }

    public sealed class DownloadProgress
    {
        public int First = -1, Last = -1, Written = -1;
        /// <summary>0 to 1, or -1 when not known (a clip, or before the playlist was read).</summary>
        public double Fraction { get { return First < 0 || Last < First || Written < First ? -1 : Math.Min(1, (Written - First + 1) / (double)(Last - First + 1)); } }
    }

    public static class DownloadCommand
    {
        public static readonly string[] Qualities = { "Best", "1080p60", "720p60", "480p", "Audio only" };
        static readonly Regex SeqRx = new Regex(@"First Sequence: (\d{1,9}); Last Sequence: (\d{1,9})", RegexOptions.CultureInvariant);
        static readonly Regex SegRx = new Regex(@"Writing segment (\d{1,9}) to output", RegexOptions.CultureInvariant);

        public static string QualityName(string q) { return q == "Best" ? "Best (source)" : q; }

        /// <summary>The Streamlink command line (program first) that writes the video or clip to r.Output.</summary>
        public static string Build(DownloadRequest r)
        {
            bool video = r.Video.Length > 0, clip = r.Clip.Length > 0;
            if (!ChannelName.IsValidLogin(r.Channel)) throw new ArgumentException("not a channel name");
            if (video == clip) throw new ArgumentException("a video or a clip");
            if (video && !ChannelName.IsNumericId(r.Video)) throw new ArgumentException("not a video id");
            if (clip && !VideoData.IsClipSlug(r.Clip)) throw new ArgumentException("not a clip id");
            if (!SafeOutput(r.Output)) throw new ArgumentException("bad output path");
            if (Array.IndexOf(Qualities, r.Quality) < 0) throw new ArgumentException("bad quality");
            var a = new List<string> { r.Streamlink, "--loglevel", "debug", "--logfile", r.LogFile, "--output", r.Output, "--stream-segment-threads", "2" };
            a.Add("--twitch-supported-codecs"); a.Add("h264");   // a file that plays everywhere
            a.Add(video ? "twitch.tv/videos/" + r.Video : "clips.twitch.tv/" + r.Clip);
            a.Add(LiteViewCore.Quality.Selector(r.Quality));
            return WinArgs.Join(a);
        }

        /// <summary>An absolute path ending in ".part", without Streamlink's {placeholders} or characters a file name cannot have.</summary>
        public static bool SafeOutput(string? path)
        {
            if (string.IsNullOrEmpty(path) || path!.Length > 400 || !path.EndsWith(".part", StringComparison.Ordinal)) return false;
            if (path.IndexOf('{') >= 0 || path.IndexOf('}') >= 0 || path.IndexOf('"') >= 0) return false;
            foreach (char c in path) if (c < ' ') return false;
            return path.Length > 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');   // a drive path, like C:\Users\...
        }

        /// <summary>"channel 2026-11-14 title.ts" (or .mp4 for a clip): only letters, digits and harmless punctuation, at most 120 characters.</summary>
        public static string FileName(string channel, string title, DateTimeOffset? created, bool clip)
        {
            var sb = new StringBuilder();
            foreach (char c in (title ?? "").Normalize(NormalizationForm.FormC))
            {
                if (char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_' || c == '.' || c == ',' || c == '!' || c == '(' || c == ')' || c == '\'' || c == '&' || c == '#') sb.Append(c);
                else if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
                if (sb.Length >= 90) break;
            }
            string t = Regex.Replace(sb.ToString(), " {2,}", " ").Trim(' ', '.');
            string date = created.HasValue ? created.Value.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";
            string name = (ChannelName.IsValidLogin(channel) ? channel : "twitch") + (date.Length > 0 ? " " + date : "") + (t.Length > 0 ? " " + t : "");
            return name + (clip ? ".mp4" : ".ts");
        }

        /// <summary>Reads Streamlink's log lines (new ones added to p).</summary>
        public static void Read(DownloadProgress p, IEnumerable<string> lines)
        {
            foreach (var l in lines)
            {
                var m = SegRx.Match(l);
                if (m.Success) { int n; if (int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out n) && n > p.Written) p.Written = n; continue; }
                m = SeqRx.Match(l);
                if (m.Success)
                {
                    int f, z;
                    if (int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out f) && int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out z) && z >= f)
                    { if (p.First < 0) p.First = f; if (z > p.Last) p.Last = z; }
                }
            }
        }

        /// <summary>About how big a download of this length is (Twitch's usual bitrates); 0 when the length is not known.</summary>
        public static long EstimateBytes(int seconds, string quality)
        {
            if (seconds <= 0) return 0;
            double mbps = quality == "Audio only" ? 0.16 : quality == "480p" ? 1.4 : quality == "720p60" ? 3.5 : 6.5;   // Best and 1080p60
            return (long)(seconds * mbps * 1000000 / 8);
        }

        public static string Size(long bytes)
        {
            if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
            return Math.Max(1, bytes >> 20).ToString(CultureInfo.InvariantCulture) + " MB";
        }
    }
}
