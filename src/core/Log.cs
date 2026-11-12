// LiteView's own log: one line per event, levels, size-rotated files, and every line passed through
// Redact first so tokens and codes never reach the disk. Chat messages are never logged.
using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace LiteViewCore
{
    public static class Redact
    {
        const string X = "[redacted]";
        static readonly Regex[] Secrets =
        {
            new Regex(@"(?i)\boauth:[a-z0-9]+", RegexOptions.CultureInvariant),
            new Regex(@"(?i)\b(bearer|oauth)\s+[a-z0-9._~+/=-]{6,}", RegexOptions.CultureInvariant),
            new Regex(@"(?i)\b(access_token|refresh_token|device_code|user_code|id_token|client_secret|token|code)=[^&\s""']+", RegexOptions.CultureInvariant),
            new Regex(@"(?i)""(access_token|refresh_token|device_code|user_code|id_token|client_secret)""\s*:\s*""[^""]*""", RegexOptions.CultureInvariant),
            new Regex(@"(?i)\b(authorization|client-id)\s*:\s*\S+(\s+\S+)?", RegexOptions.CultureInvariant),
        };
        static readonly Regex Channel = new Regex(@"(?i)twitch\.tv/[a-z0-9_]+", RegexOptions.CultureInvariant);

        /// <summary>The text with credentials replaced by [redacted].</summary>
        public static string Secretsless(string? s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            string r = s!;
            r = Secrets[0].Replace(r, "oauth:" + X);
            r = Secrets[1].Replace(r, m => m.Groups[1].Value + " " + X);
            r = Secrets[2].Replace(r, m => m.Groups[1].Value + "=" + X);
            r = Secrets[3].Replace(r, m => "\"" + m.Groups[1].Value + "\":\"" + X + "\"");
            r = Secrets[4].Replace(r, m => m.Groups[1].Value + ": " + X);
            return r;
        }

        /// <summary>Also hides which channel was watched (for diagnostics that may be shared).</summary>
        public static string ForSharing(string? s) { return Channel.Replace(Secretsless(s), "twitch.tv/<channel>"); }
    }

    public static class Log
    {
        static readonly object gate = new object();
        static readonly string[] LineBreaks = { "\r\n", "\n" };
        static string? file;
        static long maxBytes = 1024 * 1024;
        static int keep = 3;

        public static string? File { get { return file; } }

        /// <summary>Starts logging to dir/name.log, rotating at maxBytes into name.1.log ... name.keep.log.</summary>
        public static void Init(string dir, string name, long max = 1024 * 1024, int files = 3)
        {
            lock (gate) { file = Path.Combine(dir, name + ".log"); maxBytes = Math.Max(4096, max); keep = Math.Max(1, files); }
        }

        public static void Info(string msg) { Write("INFO ", msg); }
        public static void Warn(string msg) { Write("WARN ", msg); }
        public static void Error(string msg) { Write("ERROR", msg); }
        public static void Error(string msg, Exception ex) { Write("ERROR", msg + ": " + ex.GetType().Name + ": " + ex.Message); }

        static void Write(string level, string msg)
        {
            string? f = file;
            if (f == null) return;
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + level + " "
                + Redact.Secretsless(msg).Replace("\r", " ").Replace("\n", " ") + "\r\n";
            lock (gate)
            {
                try
                {
                    var fi = new FileInfo(f);
                    if (fi.Exists && fi.Length + line.Length > maxBytes) Rotate(f);
                    System.IO.File.AppendAllText(f, line, new UTF8Encoding(false));
                }
                catch { }   // logging must never break the app
            }
        }

        static string Numbered(string f, int i)
        {
            return Path.Combine(Path.GetDirectoryName(f) ?? ".", Path.GetFileNameWithoutExtension(f) + "." + i + Path.GetExtension(f));
        }

        static void Rotate(string f)
        {
            string last = Numbered(f, keep);
            if (System.IO.File.Exists(last)) System.IO.File.Delete(last);
            for (int i = keep - 1; i >= 1; i--)
            {
                string a = Numbered(f, i);
                if (System.IO.File.Exists(a)) System.IO.File.Move(a, Numbered(f, i + 1));
            }
            System.IO.File.Move(f, Numbered(f, 1));
        }

        /// <summary>The last lines of the current log (for diagnostics).</summary>
        public static string[] Tail(int lines)
        {
            string? f = file;
            if (f == null) return Array.Empty<string>();
            lock (gate)
            {
                try { return LastLines(f, lines); } catch { return Array.Empty<string>(); }
            }
        }

        public static string[] LastLines(string path, int lines)
        {
            if (!System.IO.File.Exists(path)) return Array.Empty<string>();
            string[] all;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long start = Math.Max(0, fs.Length - 256 * 1024);
                fs.Seek(start, SeekOrigin.Begin);
                using (var rd = new StreamReader(fs, Encoding.UTF8)) all = rd.ReadToEnd().Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries);
            }
            int n = Math.Min(lines, all.Length);
            var r = new string[n];
            Array.Copy(all, all.Length - n, r, 0, n);
            return r;
        }
    }
}
