// One Twitch IRC line (IRCv3 tags + prefix + command + params). Moved here unchanged in behaviour from
// upstream src/chat.cs so it can be unit tested; the chat code uses it exactly as before.
// Everything in a line comes from the network and is treated as untrusted text.
using System;
using System.Collections.Generic;
using System.Text;

namespace LiteViewCore
{
    public sealed class IrcMsg
    {
        static readonly char[] Space = { ' ' };
        public Dictionary<string, string> Tags = new Dictionary<string, string>(StringComparer.Ordinal);
        public string? Prefix, Cmd, Chan, Text;

        public string? Tag(string k) { return Tags.TryGetValue(k, out var v) ? v : null; }

        public string? Nick
        {
            get
            {
                if (Prefix == null) return null;
                int i = Prefix.IndexOf('!');
                return i < 0 ? Prefix : Prefix.Substring(0, i);
            }
        }

        /// <summary>Parses one line (without CR LF). Returns null for lines that are not IRC messages.</summary>
        public static IrcMsg? Parse(string? line)
        {
            if (string.IsNullOrEmpty(line) || line!.Length > IrcProtocol.MaxLineChars) return null;
            var m = new IrcMsg(); int i = 0;
            if (line[0] == '@')
            {
                int sp = line.IndexOf(' '); if (sp < 0) return null;
                foreach (var kv in line.Substring(1, sp - 1).Split(';'))
                {
                    if (kv.Length == 0) continue;
                    int eq = kv.IndexOf('=');
                    if (eq > 0) m.Tags[kv.Substring(0, eq)] = Unescape(kv.Substring(eq + 1));
                    else if (eq < 0) m.Tags[kv] = "";   // IRCv3: a key without "=" has an empty value
                }
                i = sp + 1;
                while (i < line.Length && line[i] == ' ') i++;
            }
            if (i < line.Length && line[i] == ':')
            {
                int sp = line.IndexOf(' ', i); if (sp < 0) return null;
                m.Prefix = line.Substring(i + 1, sp - i - 1); i = sp + 1;
                while (i < line.Length && line[i] == ' ') i++;
            }
            if (i >= line.Length) return null;
            int t = line.IndexOf(" :", i, StringComparison.Ordinal);
            string mid = t < 0 ? line.Substring(i) : line.Substring(i, t - i);
            m.Text = t < 0 ? null : line.Substring(t + 2);
            var parts = mid.Split(Space, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;
            m.Cmd = parts[0]; m.Chan = parts.Length > 1 ? parts[1].TrimStart('#') : null;
            return m;
        }

        // IRCv3 tag value escapes: \s space, \: semicolon, \r, \n, \\ backslash; a lone trailing \ is dropped.
        public static string Unescape(string v)
        {
            if (v.IndexOf('\\') < 0) return v;
            var sb = new StringBuilder(v.Length);
            for (int i = 0; i < v.Length; i++)
            {
                if (v[i] != '\\') { sb.Append(v[i]); continue; }
                if (i + 1 >= v.Length) break;
                char n = v[++i];
                sb.Append(n == 's' ? ' ' : n == ':' ? ';' : n == 'r' ? '\r' : n == 'n' ? '\n' : n);
            }
            return sb.ToString();
        }
    }
}
