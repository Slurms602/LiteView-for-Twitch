// Twitch channel names and ids as typed by the user, linked from web pages or sent by Twitch.
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace LiteViewCore
{
    public static class ChannelName
    {
        static readonly Regex Login = new Regex("^[a-z0-9_]{1,25}$", RegexOptions.CultureInvariant);
        static readonly char[] PathEnd = { '/', '?', '#' }, WordEnd = { ' ', '\t', '/', '?', '#' };
        static readonly Regex Digits = new Regex("^[0-9]{1,20}$", RegexOptions.CultureInvariant);

        /// <summary>twitch.tv paths that are pages, not channels (the same list the Following page script uses).</summary>
        public static readonly HashSet<string> Reserved = new HashSet<string>(StringComparer.Ordinal)
        {
            "directory", "videos", "settings", "subscriptions", "inventory", "wallet", "drops", "search", "downloads",
            "jobs", "p", "store", "turbo", "prime", "friends", "messages", "popout", "moderator", "u", "login", "signup",
            "logout", "team", "bits", "embed", "following", "activate",
        };

        /// <summary>A Twitch login: 1-25 of a-z, 0-9, underscore (lower case).</summary>
        public static bool IsValidLogin(string? s) { return s != null && Login.IsMatch(s) && !Reserved.Contains(s); }

        /// <summary>A Twitch numeric id (user id, room id).</summary>
        public static bool IsNumericId(string? s) { return s != null && Digits.IsMatch(s); }

        /// <summary>
        /// The channel login in what the user typed or pasted: "xqc", "@xqc", "twitch.tv/xqc",
        /// "https://www.twitch.tv/xqc/videos?x=1", "twitch.tv/popout/xqc/chat". Null if there is none.
        /// </summary>
        public static string? Normalize(string? raw)
        {
            if (raw == null) return null;
            string s = raw.Trim().ToLowerInvariant();
            int at = s.LastIndexOf("twitch.tv/", StringComparison.Ordinal);
            if (at >= 0)
            {
                var segs = s.Substring(at + "twitch.tv/".Length).Split(PathEnd, StringSplitOptions.None);
                s = segs.Length > 0 ? segs[0] : "";
                if ((s == "popout" || s == "embed") && segs.Length > 1) s = segs[1];
            }
            else
            {
                int ws = s.IndexOfAny(WordEnd);
                if (ws >= 0) s = s.Substring(0, ws);
            }
            if (s.StartsWith("@", StringComparison.Ordinal)) s = s.Substring(1);
            return IsValidLogin(s) ? s : null;
        }
    }
}
