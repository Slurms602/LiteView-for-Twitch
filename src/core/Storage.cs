// Settings and token files: written atomically (a crash mid-write never leaves a half file), with the
// previous settings kept as the last-known-good copy that is used when the main file is unreadable.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LiteViewCore
{
    public static class AtomicFile
    {
        /// <summary>Writes path via a temp file in the same folder, then replaces it in one step.
        /// With keepBackup the old file becomes path + ".bak".</summary>
        public static void Write(string path, byte[] data, bool keepBackup)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
            string tmp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
            try
            {
                using (var f = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    f.Write(data, 0, data.Length);
                    f.Flush(true);
                }
                if (File.Exists(path))
                {
                    string? bak = keepBackup ? path + ".bak" : null;
                    if (bak != null && File.Exists(bak)) File.Delete(bak);
                    File.Replace(tmp, path, bak, true);
                }
                else File.Move(tmp, path);
            }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }

        public static void WriteText(string path, string text, bool keepBackup)
        {
            Write(path, new UTF8Encoding(false).GetBytes(text), keepBackup);
        }

        /// <summary>Deletes the file and its .bak (used for credentials on log out).</summary>
        public static void Delete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            try { if (File.Exists(path + ".bak")) File.Delete(path + ".bak"); } catch { }
        }
    }

    public static class IniStore
    {
        /// <summary>key=value lines ('#' and ';' comments), keys case-insensitive (same format as upstream).</summary>
        public static Dictionary<string, string> Parse(IEnumerable<string> lines)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                int eq = line.IndexOf('=');
                if (eq > 0) d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return d;
        }

        /// <summary>
        /// Reads path; when it is missing, unreadable or has no settings in it but a last-known-good .bak
        /// exists, the .bak is used instead (usedBackup tells which).
        /// </summary>
        public static Dictionary<string, string> Read(string path, out bool usedBackup)
        {
            usedBackup = false;
            var main = TryRead(path);
            if (main != null && main.Count > 0) return main;
            var bak = TryRead(path + ".bak");
            if (bak != null && bak.Count > 0) { usedBackup = true; return bak; }
            return main ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public static Dictionary<string, string> Read(string path) { return Read(path, out _); }

        static Dictionary<string, string>? TryRead(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length > 1024 * 1024) return null;            // not a settings file
                foreach (var b in bytes) if (b == 0) return null;         // torn / binary garbage
                return Parse(new UTF8Encoding(false).GetString(bytes).Split('\n'));
            }
            catch { return null; }
        }

        public static string Get(Dictionary<string, string> d, string k, string def)
        {
            return d.TryGetValue(k, out var v) && v.Length > 0 ? v : def;
        }

        public static int GetInt(Dictionary<string, string> d, string k, int def, int min, int max)
        {
            return int.TryParse(Get(d, k, ""), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v)
                ? Math.Max(min, Math.Min(max, v)) : def;
        }

        /// <summary>Writes key=value lines (values with line breaks are flattened) atomically, keeping a .bak.</summary>
        public static void Write(string path, string header, IEnumerable<KeyValuePair<string, string>> values)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(header)) sb.Append("# ").Append(header).Append("\r\n");
            foreach (var kv in values)
                sb.Append(kv.Key).Append('=').Append((kv.Value ?? "").Replace("\r", " ").Replace("\n", " ")).Append("\r\n");
            AtomicFile.WriteText(path, sb.ToString(), true);
        }
    }
}
