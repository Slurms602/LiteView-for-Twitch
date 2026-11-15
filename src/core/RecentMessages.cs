// Chat history when joining a channel (P4). Twitch's API has no chat history; the Twitch website shows the last
// messages from its own private service. LiteView uses recent-messages.robotty.de, the public service that Chatterino
// and other Twitch chat clients use (it can be turned off in the chat menu: docs/SECURITY_MODEL.md B16).
// Its answer is untrusted: only chat messages of the channel that was asked for are kept, at most Limit of them,
// and each line still goes through the same IRC parsing and display limits as a live line.
using System;
using System.Collections.Generic;

namespace LiteViewCore
{
    public static class RecentMessages
    {
        public const string Host = "recent-messages.robotty.de";
        public const int Limit = 50;
        static readonly char[] Bad = { '\r', '\n', '\0' };

        /// <summary>The request for a channel's last messages, without moderation lines and deleted messages (null: not a channel name).</summary>
        public static string? Url(string? channel)
        {
            if (!ChannelName.IsValidLogin(channel)) return null;
            return "https://" + Host + "/api/v2/recent-messages/" + channel + "?limit=" + Limit + "&hide_moderation_messages=true&hide_moderated_messages=true";
        }

        /// <summary>
        /// The IRC lines from the service's answer {"messages":["@tags :nick!nick@host PRIVMSG #channel :text", ...]}:
        /// chat messages (PRIVMSG) and sub/raid notices (USERNOTICE) of this channel only, the newest Limit, oldest first.
        /// </summary>
        public static List<string> Lines(string? json, string channel)
        {
            var keep = new List<string>();
            foreach (var v in Json.A(Json.Parse(json), "messages"))
            {
                if (!(v is string line) || line.Length == 0 || line.IndexOfAny(Bad) >= 0) continue;
                var m = IrcMsg.Parse(line);
                if (m == null || (m.Cmd != "PRIVMSG" && m.Cmd != "USERNOTICE")) continue;
                if (!string.Equals(m.Chan, channel, StringComparison.OrdinalIgnoreCase)) continue;
                keep.Add(line);
            }
            if (keep.Count > Limit) keep.RemoveRange(0, keep.Count - Limit);
            return keep;
        }
    }
}
