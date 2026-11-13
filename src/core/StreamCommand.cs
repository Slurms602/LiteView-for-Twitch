// Building the Streamlink command line, and reading what its exit means.
using System;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LiteViewCore
{
    /// <summary>Windows command-line quoting (the rules CommandLineToArgvW and the C runtime use).</summary>
    public static class WinArgs
    {
        static readonly char[] NeedsQuotes = { ' ', '\t', '\n', '\v', '"' };
        public static string Quote(string arg)
        {
            if (arg == null) throw new ArgumentNullException(nameof(arg));
            if (arg.Length > 0 && arg.IndexOfAny(NeedsQuotes) < 0) return arg;
            var sb = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { sb.Append('\\', slashes * 2 + 1); sb.Append('"'); slashes = 0; continue; }
                sb.Append('\\', slashes); slashes = 0; sb.Append(c);
            }
            sb.Append('\\', slashes * 2);
            return sb.Append('"').ToString();
        }

        public static string Join(IEnumerable<string> args)
        {
            var sb = new StringBuilder();
            foreach (var a in args) { if (sb.Length > 0) sb.Append(' '); sb.Append(Quote(a)); }
            return sb.ToString();
        }
    }

    public static class Quality
    {
        /// <summary>The choices in the quality box, in order.</summary>
        public static readonly string[] Choices = { "Best", "1080p60", "720p60", "480p", "Audio only" };

        /// <summary>Streamlink's stream selector for a choice (falls back so a stream always plays).</summary>
        public static string Selector(string? choice)
        {
            switch (choice)
            {
                case "1080p60": return "1080p60,1080p,best";
                case "720p60": return "720p60,720p,best";
                case "480p": return "480p,best";
                case "Audio only": return "audio_only,audio,worst";
            }
            var m = choice == null ? null : Variant.Match(choice);   // a variant this stream offers, e.g. 1440p60 or 360p
            if (m != null && m.Success) return choice + (m.Groups[2].Success ? "," + m.Groups[1].Value + "p" : "") + ",best";
            return "best";
        }

        // only these names ever reach the command line
        static readonly System.Text.RegularExpressions.Regex Variant = new System.Text.RegularExpressions.Regex(@"^([1-9][0-9]{2,3})p([1-9][0-9]{1,2})?$");

        /// <summary>
        /// The stream's variants from Streamlink's log line "Available streams: audio_only, 160p (worst), 360p, 720p60,
        /// 1080p60 (best)" (the last one in the log); only well-formed names (NNNp, NNNpFF, audio_only).
        /// </summary>
        public static List<string> Available(IEnumerable<string> logLines)
        {
            string? last = null;
            foreach (var l in logLines) if (l.IndexOf("Available streams:", StringComparison.Ordinal) >= 0) last = l;
            var list = new List<string>();
            if (last == null) return list;
            foreach (var part in last.Substring(last.IndexOf("Available streams:", StringComparison.Ordinal) + 18).Split(','))
            {
                string n = part.Replace("(worst)", "").Replace("(best)", "").Trim();
                if ((n == "audio_only" || Variant.IsMatch(n)) && !list.Contains(n)) list.Add(n);
            }
            return list;
        }

        /// <summary>The quality box for a stream: Best, its video variants from high to low, Audio only when it has one.</summary>
        public static List<string> ChoicesFor(IList<string> available)
        {
            var video = available.Select(n => new { n, m = Variant.Match(n) }).Where(x => x.m.Success)
                .OrderByDescending(x => int.Parse(x.m.Groups[1].Value, CultureInfo.InvariantCulture))
                .ThenByDescending(x => x.m.Groups[2].Success ? int.Parse(x.m.Groups[2].Value, CultureInfo.InvariantCulture) : 30)
                .Select(x => x.n);
            var list = new List<string> { "Best" };
            list.AddRange(video);
            if (available.Contains("audio_only")) list.Add("Audio only");
            return list;
        }
    }

    public sealed class StreamRequest
    {
        public string Streamlink = "", Mpv = "", LogFile = "", Channel = "", Quality = "Best", PipeName = "";
        public long WindowHandle;
    }

    public static class StreamCommand
    {
        public const int RetryStreamsSeconds = 30;

        /// <summary>The full command line for CreateProcess (program path first).</summary>
        public static string Build(StreamRequest r)
        {
            if (!ChannelName.IsValidLogin(r.Channel)) throw new ArgumentException("not a channel name: " + r.Channel);
            if (!IsPipeName(r.PipeName)) throw new ArgumentException("bad pipe name");
            string playerArgs = "--wid=" + r.WindowHandle.ToString(CultureInfo.InvariantCulture) + " --input-ipc-server=" + r.PipeName;
            var a = new List<string>
            {
                r.Streamlink,
                "--player", r.Mpv,
                "--player-args", playerArgs,
                "--twitch-low-latency",
                // an offline channel: Streamlink itself checks again every 30 s and starts the player when it goes live
                "--retry-streams", RetryStreamsSeconds.ToString(CultureInfo.InvariantCulture),
                "--twitch-supported-codecs", "h264,h265,av1",
                "--loglevel", "info",
                "--logfile", r.LogFile,
                "twitch.tv/" + r.Channel,
                Quality.Selector(r.Quality),
            };
            return WinArgs.Join(a);
        }

        /// <summary>A new, unguessable pipe name for one stream (mpv's IPC server).</summary>
        public static string NewPipeName()
        {
            return "liteview-mpv-" + Guid.NewGuid().ToString("N");
        }

        public static bool IsPipeName(string? s)
        {
            if (string.IsNullOrEmpty(s) || s!.Length > 100) return false;
            foreach (char c in s) if (!(c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '-')) return false;
            return true;
        }
    }

    public enum StreamEnd { Offline, Failed, Ended, Fatal }

    public static class StreamOutcome
    {
        // Streamlink 8.6's --logfile lines look like "[cli][info] ..." or "[plugins.twitch][error] ..."
        // (format "[{name}][{levelname}] {message}"). Its final fatal message ("error: ...") goes to the console only,
        // which streamlinkw.exe does not have, so it never reaches the log file (checked in streamlink_cli/main.py 8.6.3).
        static readonly System.Text.RegularExpressions.Regex LevelLine =
            new System.Text.RegularExpressions.Regex(@"^\[[A-Za-z0-9_.]+\]\[(error|critical)\]\s*(.*)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>The message of the last error line of a Streamlink log ("" if none).</summary>
        public static string LastError(IEnumerable<string> logLines)
        {
            string last = "";
            foreach (var raw in logLines)
            {
                string l = raw.Trim();
                var m = LevelLine.Match(l);
                if (m.Success) { last = m.Groups[2].Value.Trim(); continue; }
                if (l.StartsWith("error:", StringComparison.Ordinal)) last = l.Substring(6).Trim();   // console form, if ever logged
            }
            return last;
        }

        /// <summary>True while Streamlink waits for an offline channel (--retry-streams) and has not opened a stream since.</summary>
        public static bool Waiting(IEnumerable<string> logLines)
        {
            bool waiting = false;
            foreach (var l in logLines)
            {
                if (l.IndexOf("] Waiting for streams, retrying every", StringComparison.Ordinal) >= 0) waiting = true;
                else if (l.IndexOf("] Opening stream:", StringComparison.Ordinal) >= 0 || l.IndexOf("] Starting player:", StringComparison.Ordinal) >= 0) waiting = false;
            }
            return waiting;
        }

        /// <summary>Why the stream stopped, from Streamlink's last error and how long it played.</summary>
        public static StreamEnd Classify(string lastError, TimeSpan ranFor, bool playerStarted)
        {
            string e = (lastError ?? "").ToLowerInvariant();
            if (e.Contains("no playable streams") || e.Contains("no streams found") || e.Contains("is offline")) return StreamEnd.Offline;
            if (e.Contains("player") && (e.Contains("not found") || e.Contains("failed to start"))) return StreamEnd.Fatal;
            if (e.Contains("unrecognized arguments") || e.Contains("invalid choice")) return StreamEnd.Fatal;
            if (e.Length == 0 && (playerStarted || ranFor > TimeSpan.FromSeconds(20))) return StreamEnd.Ended;
            return StreamEnd.Failed;
        }

        /// <summary>Short text for the status bar.</summary>
        public static string Describe(StreamEnd end, string lastError)
        {
            switch (end)
            {
                case StreamEnd.Offline: return "Offline";
                case StreamEnd.Ended: return "Stream ended";
                case StreamEnd.Fatal: return lastError.Length > 0 ? lastError : "Could not start the player";
                default: return lastError.Length > 0 ? lastError : "Connection lost";
            }
        }
    }
}
