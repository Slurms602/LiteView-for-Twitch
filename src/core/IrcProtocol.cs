// Twitch IRC protocol rules that the chat connection relies on, kept apart from the socket code
// so they can be tested: control lines, outgoing message clean-up and bounded frame assembly.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LiteViewCore
{
    public static class IrcProtocol
    {
        /// <summary>Twitch caps a chat message at 500 characters.</summary>
        public const int MaxMessageChars = 500;
        /// <summary>Longest line we parse. Twitch lines are at most ~8 KB of tags plus 512 bytes.</summary>
        public const int MaxLineChars = 16 * 1024;
        /// <summary>Most text from one chat message that is laid out and drawn.</summary>
        public const int MaxShownChars = 2000;
        static readonly string[] LineBreaks = { "\r\n", "\n" };

        /// <summary>The PONG reply for a server PING line, or null for any other line.</summary>
        public static string? PongFor(string line)
        {
            if (line == null || !line.StartsWith("PING", StringComparison.Ordinal)) return null;
            if (line.Length > 4 && line[4] != ' ') return null;
            return "PONG" + line.Substring(4);
        }

        /// <summary>True only for Twitch's RECONNECT command (not a chat message that ends with the word).</summary>
        public static bool IsReconnect(IrcMsg? m) { return m != null && m.Cmd == "RECONNECT"; }

        /// <summary>
        /// What we may send as the text of a PRIVMSG: no CR, LF, NUL or other control characters (each one
        /// could end the IRC line and start a second, injected command), no more than Twitch accepts.
        /// \u0001 is kept only as the CTCP ACTION wrapper.
        /// </summary>
        public static string CleanOutgoing(string? text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(text!.Length);
            foreach (char c in text)
            {
                if (c == '\u0001') { sb.Append(c); continue; }
                sb.Append(char.IsControl(c) || c == '\u2028' || c == '\u2029' ? ' ' : c);
            }
            string s = sb.ToString().Trim();
            bool action = s.StartsWith("\u0001ACTION ", StringComparison.Ordinal) && s.EndsWith("\u0001", StringComparison.Ordinal) && s.Length > 9;
            if (!action) s = s.Replace("\u0001", "").Trim();
            if (s.Length > MaxMessageChars)
            {
                int n = MaxMessageChars;
                if (char.IsHighSurrogate(s[n - 1])) n--;   // never cut a surrogate pair in half
                s = s.Substring(0, n);
            }
            return s;
        }

        /// <summary>Text for display, cut to MaxShownChars without splitting a surrogate pair.</summary>
        public static string ForDisplay(string? text)
        {
            if (text == null) return "";
            if (text.Length <= MaxShownChars) return text;
            int n = MaxShownChars;
            if (char.IsHighSurrogate(text[n - 1])) n--;
            return text.Substring(0, n) + "…";
        }

        /// <summary>Lines of one WebSocket text message (Twitch may batch several, CR LF separated).</summary>
        public static IEnumerable<string> Lines(string text)
        {
            foreach (var l in text.Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries))
                if (l.Length > 0) yield return l;
        }

        /// <summary>A channel name that is safe to put in JOIN / PRIVMSG.</summary>
        public static bool IsSafeChannel(string? channel) { return ChannelName.IsValidLogin(channel); }
    }

    /// <summary>
    /// Collects the fragments of one WebSocket message with a hard size limit, so a broken or hostile
    /// server cannot make the client buffer without end.
    /// </summary>
    public sealed class FrameBuffer : IDisposable
    {
        readonly MemoryStream acc = new MemoryStream();
        public int Limit { get; }
        public FrameBuffer(int limit = 1024 * 1024) { Limit = limit; }

        /// <summary>False when the message would exceed the limit (the buffer is then cleared).</summary>
        public bool Append(byte[] data, int count)
        {
            if (count < 0 || acc.Length + count > Limit) { acc.SetLength(0); return false; }
            acc.Write(data, 0, count);
            return true;
        }

        /// <summary>The whole message as UTF-8 text; the buffer is emptied.</summary>
        public string Take()
        {
            string s = Encoding.UTF8.GetString(acc.GetBuffer(), 0, (int)acc.Length);
            acc.SetLength(0);
            return s;
        }

        public long Length { get { return acc.Length; } }

        public void Dispose() { acc.Dispose(); }
    }
}
