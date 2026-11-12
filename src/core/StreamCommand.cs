// Building the Streamlink command line, and reading what its exit means.
using System;
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
                default: return "best";
            }
        }
    }

    public sealed class StreamRequest
    {
        public string Streamlink = "", Mpv = "", LogFile = "", Channel = "", Quality = "Best", PipeName = "";
        public long WindowHandle;
    }

    public static class StreamCommand
    {
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
        /// <summary>The last "error:" line of a Streamlink log, without the prefix ("" if none).</summary>
        public static string LastError(IEnumerable<string> logLines)
        {
            string last = "";
            foreach (var l in logLines)
            {
                int i = l.IndexOf("error:", StringComparison.Ordinal);
                if (i >= 0) last = l.Substring(i + 6).Trim();
            }
            return last;
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
