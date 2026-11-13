// Chat preferences that decide something (P4): users whose messages are hidden, words that highlight a message like
// a mention, and the time shown before a message. The chat (src/chat.cs) asks these while building a message.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace LiteViewCore
{
    public sealed class ChatFilter
    {
        public const int MaxWords = 50, MaxWordLength = 40, MaxIgnored = 500;
        static readonly char[] WordSeparators = { ',', '\n', '\r' }, NameSeparators = { ',', ' ', '\n', '\r', '\t', ';' };
        HashSet<string> ignored = new HashSet<string>(StringComparer.Ordinal);
        Regex? words;
        List<string> wordList = new List<string>();

        public IReadOnlyCollection<string> Ignored { get { return ignored; } }
        public IReadOnlyList<string> Words { get { return wordList; } }

        /// <summary>Logins, separated by commas, spaces or lines ("@Name" and any case accepted); invalid names are dropped.</summary>
        public void SetIgnored(string? text)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var part in Split(text))
            {
                string l = part.TrimStart('@').ToLowerInvariant();
                if (ChannelName.IsValidLogin(l) && set.Count < MaxIgnored) set.Add(l);
            }
            ignored = set;
        }

        /// <summary>Highlight words or phrases, one per line or comma-separated; matched as whole words, any case.</summary>
        public void SetWords(string? text)
        {
            var list = new List<string>();
            foreach (var part in (text ?? "").Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                string w = HelixData.Text(part);
                if (w.Length == 0 || w.Length > MaxWordLength || list.Contains(w, StringComparer.OrdinalIgnoreCase)) continue;
                if (list.Count >= MaxWords) break;
                list.Add(w);
            }
            wordList = list;
            words = list.Count == 0 ? null : new Regex(@"(?<![\w])(" + string.Join("|", list.Select(Regex.Escape)) + @")(?![\w])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        }

        public bool IsIgnored(string? login) { return login != null && ignored.Contains(login.ToLowerInvariant()); }

        public bool Highlights(string? text)
        {
            if (words == null || string.IsNullOrEmpty(text)) return false;
            try { return words.IsMatch(text); }
            catch (RegexMatchTimeoutException) { return false; }
        }

        public string SaveIgnored() { return string.Join(",", ignored.OrderBy(x => x, StringComparer.Ordinal)); }
        public string SaveWords() { return string.Join(",", wordList.Select(w => w.Replace(",", " "))); }

        /// <summary>"14:05" from Twitch's tmi-sent-ts (milliseconds since 1970), in local time; "" when missing.</summary>
        public static string Stamp(string? sentMs, TimeZoneInfo? zone = null)
        {
            long ms;
            if (!long.TryParse(sentMs, NumberStyles.None, CultureInfo.InvariantCulture, out ms) || ms <= 0 || ms > 253402300799999) return "";
            var t = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(ms), zone ?? TimeZoneInfo.Local);
            return t.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        static string[] Split(string? text) { return (text ?? "").Split(NameSeparators, StringSplitOptions.RemoveEmptyEntries); }
    }
}
