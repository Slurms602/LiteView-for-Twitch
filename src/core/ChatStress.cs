// Synthetic Twitch chat for the stress mode (LiteView.exe <channel> --chat-stress=<messages per second>):
// IRC lines shaped like Twitch's, with emotes, badges, colours, long messages, Unicode, links, replies,
// sub/raid notices, deletions and timeouts. Deterministic for a given seed.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LiteViewCore
{
    public sealed class ChatStress
    {
        static readonly string[] Words =
        {
            "lol", "W", "L", "gg", "nice", "clip", "that", "was", "insane", "bro", "what", "no", "way", "chat", "is", "this", "real",
            "pog", "first", "time", "here", "hello", "from", "Brazil", "😂", "🔥", "💀", "👀", "日本語", "テスト", "مرحبا", "שלום",
            "Ünïcödé", "é́", "👨‍👩‍👧", "🇧🇷", "ok", "?", "!!!", "@someone", "streamer", "aim", "diff", "1v5", "clutch",
        };
        static readonly string[] Names = { "viewer", "chatter", "lurker", "fan", "mod", "night_owl", "gamer", "Pixel", "Zed", "ÜberUser" };
        static readonly string[] Colors = { "#FF0000", "#1E90FF", "#00FF7F", "#9ACD32", "#FF69B4", "#000000", "", "#8A2BE2" };
        static readonly string[] Badges = { "", "subscriber/12", "subscriber/0,premium/1", "moderator/1,subscriber/24", "vip/1", "bits/1000" };

        readonly Random rnd;
        readonly string channel;
        readonly List<string> recentIds = new List<string>();
        readonly List<string> recentUsers = new List<string>();
        long n;

        public ChatStress(string channel, int seed = 1) { this.channel = channel; rnd = new Random(seed); }

        public long Count { get { return n; } }

        /// <summary>The next line. emotes = names that the chat shows as emotes (third-party or Twitch), may be empty.</summary>
        public string Next(IList<string> emotes)
        {
            n++;
            int roll = rnd.Next(1000);
            if (roll < 8 && recentIds.Count > 0) return Tags("target-msg-id=" + Pick(recentIds)) + ":tmi.twitch.tv CLEARMSG #" + channel + " :deleted";
            if (roll < 12 && recentUsers.Count > 0) return Tags("ban-duration=600") + ":tmi.twitch.tv CLEARCHAT #" + channel + " :" + Pick(recentUsers);
            if (roll < 20)
                return Tags("login=" + User() + ";msg-id=sub;system-msg=" + Esc("Someone subscribed at Tier 1. They've subscribed for 12 months!") + ";display-name=Someone;color=#FF69B4")
                    + ":tmi.twitch.tv USERNOTICE #" + channel + (rnd.Next(2) == 0 ? "" : " :" + Text(emotes, 6));
            string user = User(), id = Guid.NewGuid().ToString("D");
            Remember(recentIds, id); Remember(recentUsers, user);
            var tags = new StringBuilder("badge-info=;badges=" + Pick(Badges) + ";color=" + Pick(Colors) + ";display-name=" + user + ";emotes=;id=" + id);
            if (roll < 60) tags.Append(";reply-parent-display-name=" + User() + ";reply-parent-msg-body=" + Esc(Text(emotes, 8)));
            int len = roll < 100 ? 80 : roll < 300 ? 25 : 8;   // a few very long messages, some medium, most short
            return "@" + tags + " :" + user + "!" + user + "@" + user + ".tmi.twitch.tv PRIVMSG #" + channel + " :" + Text(emotes, len);
        }

        string Tags(string t) { return "@" + t + ";tmi-sent-ts=" + (1763000000000L + n).ToString(CultureInfo.InvariantCulture) + " "; }

        string User() { return Pick(Names).ToLowerInvariant() + rnd.Next(1, 5000).ToString(CultureInfo.InvariantCulture); }

        string Text(IList<string> emotes, int maxWords)
        {
            var sb = new StringBuilder();
            int words = 1 + rnd.Next(maxWords);
            for (int i = 0; i < words; i++)
            {
                if (sb.Length > 0) sb.Append(' ');
                int r = rnd.Next(100);
                if (r < 35 && emotes.Count > 0) sb.Append(emotes[rnd.Next(emotes.Count)]);
                else if (r < 37) sb.Append("https://clips.twitch.tv/Example").Append(rnd.Next(1000));
                else sb.Append(Pick(Words));
                if (sb.Length > IrcProtocol.MaxMessageChars - 40) break;
            }
            return IrcProtocol.CleanOutgoing(sb.ToString());   // what Twitch would accept: one line, at most 500 characters
        }

        static string Esc(string s) { return s.Replace("\\", "\\\\").Replace(" ", "\\s").Replace(";", "\\:"); }

        T Pick<T>(IList<T> a) { return a[rnd.Next(a.Count)]; }

        static void Remember(List<string> l, string v) { l.Add(v); if (l.Count > 200) l.RemoveAt(0); }
    }
}
