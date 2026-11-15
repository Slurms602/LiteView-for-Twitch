// Chat replay for past broadcasts (owner's request: a video showed the channel's live chat). Twitch keeps a past
// broadcast's chat; its website reads it from Twitch's GraphQL API (gql.twitch.tv), which has no official, documented
// form (Helix has no chat replay). LiteView asks it the way Streamlink already asks it for stream access: with the
// website's public client id and NO user token (the request is anonymous; nothing of the user's is sent). The comments
// come by video position ("contentOffsetSeconds"): about a minute and a half of a busy chat per answer, starting a
// little before the asked position. Twitch refuses its cursor paging to apps ("failed integrity check", checked
// 2026-11-15), so the next page is asked by the last comment's position and repeats are dropped by id.
// Everything in the answer is untrusted: names must be channel names, colours #RRGGBB, emote ids and badges short
// letters/digits (they go into image URLs and dictionary keys), text loses control characters; each comment becomes an
// IrcMsg like a live chat line, so the chat shows it with the same code (emotes, badges, filters, display limits).
// Twitch can change or close this API at any time: then the chat says replay is not available.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LiteViewCore
{
    public sealed class ReplayComment
    {
        public string Id = "";
        public double Offset;
        public IrcMsg Msg = new IrcMsg();
    }

    public sealed class ReplayPage
    {
        public List<ReplayComment> Comments = new List<ReplayComment>();
        public bool HasNext;
        /// <summary>Twitch's error ("failed integrity check", ...), or null.</summary>
        public string? Error;
    }

    public static class ChatReplay
    {
        public const string GqlUrl = "https://gql.twitch.tv/gql";
        /// <summary>The twitch.tv website's public client id (the one Streamlink sends to the same API). Never sent with a token.</summary>
        public const string WebClientId = "kimne78kx3ncx6brgo4mv6wki5h1ko";
        public const int MaxPerPage = 500, MaxTextChars = 1000;

        static readonly Regex ColorRx = new Regex(@"\A#[0-9A-Fa-f]{6}\z", RegexOptions.CultureInvariant);
        static readonly Regex EmoteIdRx = new Regex(@"\A[A-Za-z0-9_]{1,64}\z", RegexOptions.CultureInvariant);
        static readonly Regex BadgeSetRx = new Regex(@"\A[a-z0-9_-]{1,48}\z", RegexOptions.CultureInvariant);
        static readonly Regex BadgeVersionRx = new Regex(@"\A[A-Za-z0-9_-]{1,24}\z", RegexOptions.CultureInvariant);
        static readonly Regex CommentIdRx = new Regex(@"\A[A-Za-z0-9-]{1,64}\z", RegexOptions.CultureInvariant);

        /// <summary>The request body for the comments from offsetSeconds on (videoId must be numeric).</summary>
        public static string Query(string videoId, int offsetSeconds)
        {
            if (!ChannelName.IsNumericId(videoId)) throw new ArgumentException("not a video id");
            int off = Math.Max(0, Math.Min(offsetSeconds, 10000000));
            string q = "query{video(id:\\\"" + videoId + "\\\"){comments(contentOffsetSeconds:" + off.ToString(CultureInfo.InvariantCulture)
                + "){edges{node{id contentOffsetSeconds createdAt commenter{login displayName} message{userColor fragments{text emote{emoteID}} userBadges{setID version}}}} pageInfo{hasNextPage}}}}";
            return "{\"query\":\"" + q + "\"}";
        }

        /// <summary>An answer as comments (in position order), or null when it is not one.</summary>
        public static ReplayPage? Parse(object? json, string channel)
        {
            if (json == null || !ChannelName.IsValidLogin(channel)) return null;
            var page = new ReplayPage();
            var errors = Json.A(json, "errors");
            if (errors.Length > 0) { page.Error = Clean(Json.S(errors[0], "message") ?? "error", 120); }
            var comments = Json.V(Json.V(Json.V(json, "data"), "video"), "comments");
            if (comments == null) { if (page.Error == null) page.Error = Json.V(json, "data") != null ? "no chat replay for this video" : null; return page.Error == null ? null : page; }
            page.HasNext = Json.B(Json.V(comments, "pageInfo"), "hasNextPage");
            foreach (var edge in Json.A(comments, "edges").Take(MaxPerPage))
            {
                var c = Comment(Json.V(edge, "node"), channel);
                if (c != null) page.Comments.Add(c);
            }
            page.Comments.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            return page;
        }

        static ReplayComment? Comment(object? node, string channel)
        {
            string? id = Json.S(node, "id");
            if (id == null || !CommentIdRx.IsMatch(id)) return null;
            var off = Json.V(node, "contentOffsetSeconds");
            double offset = off is double d ? d : off is long l ? l : -1;
            if (offset < 0 || offset > 1e7) return null;
            var who = Json.V(node, "commenter");
            string? login = Json.S(who, "login")?.ToLowerInvariant();
            if (login == null || !ChannelName.IsValidLogin(login)) return null;   // deleted users have none: such a comment is left out
            string display = Clean(Json.S(who, "displayName") ?? "", 40);
            if (display.Length == 0) display = login;
            var msg = Json.V(node, "message");
            var text = new StringBuilder();
            var emotes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var order = new List<string>();
            int cp = 0;
            foreach (var f in Json.A(msg, "fragments"))
            {
                string t = Clean(Json.S(f, "text") ?? "", MaxTextChars);
                if (t.Length == 0 || text.Length + t.Length > MaxTextChars) continue;
                int n = CodePoints(t);
                string? eid = Json.S(Json.V(f, "emote"), "emoteID");
                if (eid != null && EmoteIdRx.IsMatch(eid) && t.Trim().Length == t.Length && n > 0)
                {
                    List<string>? at;
                    if (!emotes.TryGetValue(eid, out at)) { at = new List<string>(); emotes[eid] = at; order.Add(eid); }
                    at.Add(cp.ToString(CultureInfo.InvariantCulture) + "-" + (cp + n - 1).ToString(CultureInfo.InvariantCulture));
                }
                text.Append(t); cp += n;
            }
            string body = text.ToString().TrimEnd();
            if (body.Length > 0 && char.IsWhiteSpace(body[0])) { body = body.TrimStart(); emotes.Clear(); }   // the positions moved: plain text then (rare)
            if (body.Length == 0) return null;
            var im = new IrcMsg { Cmd = "PRIVMSG", Chan = "#" + channel, Prefix = login + "!" + login + "@" + login + ".tmi.twitch.tv", Text = body };
            im.Tags["id"] = id;
            im.Tags["display-name"] = display;
            string? color = Json.S(msg, "userColor");
            im.Tags["color"] = color != null && ColorRx.IsMatch(color) ? color : "";
            var badges = new List<string>();
            foreach (var b in Json.A(msg, "userBadges").Take(8))
            {
                string? set = Json.S(b, "setID"), ver = Json.S(b, "version");
                if (set != null && ver != null && BadgeSetRx.IsMatch(set) && BadgeVersionRx.IsMatch(ver)) badges.Add(set + "/" + ver);
            }
            im.Tags["badges"] = string.Join(",", badges);
            if (emotes.Count > 0) im.Tags["emotes"] = string.Join("/", order.Where(emotes.ContainsKey).Select(e => e + ":" + string.Join(",", emotes[e])));
            DateTimeOffset at2;
            if (DateTimeOffset.TryParse(Json.S(node, "createdAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out at2))
                im.Tags["tmi-sent-ts"] = at2.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            return new ReplayComment { Id = id, Offset = offset, Msg = im };
        }

        static int CodePoints(string s) { int n = 0; for (int i = 0; i < s.Length; i++) { n++; if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++; } return n; }

        // no control characters (no line breaks, NUL or the IRC ACTION marker), no lone surrogates, at most max chars
        static string Clean(string s, int max)
        {
            var sb = new StringBuilder(Math.Min(s.Length, max));
            for (int i = 0; i < s.Length && sb.Length < max; i++)
            {
                char c = s[i];
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { if (sb.Length + 2 > max) break; sb.Append(c).Append(s[i + 1]); i++; }
                    continue;
                }
                if (char.IsLowSurrogate(c) || char.IsControl(c)) continue;
                sb.Append(c);
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// The comments fetched but not shown yet, and where to ask next. Due(position) hands out what the video has reached;
    /// a seek starts over (Reset), with the last ContextSeconds before the new position shown at once.
    /// </summary>
    public sealed class ReplayBuffer
    {
        public const double AheadSeconds = 30, ContextSeconds = 10;
        public const int MaxPending = 5000, MaxSeen = 20000;
        readonly List<ReplayComment> pending = new List<ReplayComment>();
        readonly HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        double skipBefore;
        /// <summary>Comments up to here have been fetched.</summary>
        public double FetchedTo { get; private set; }
        /// <summary>Twitch has nothing after FetchedTo.</summary>
        public bool End { get; private set; }
        public bool Started { get; private set; }
        public int Pending { get { return pending.Count; } }

        public void Reset(double position)
        {
            pending.Clear(); seen.Clear(); End = false; Started = true;
            skipBefore = Math.Max(0, position - ContextSeconds);
            FetchedTo = skipBefore;
        }

        /// <summary>The position to ask Twitch for next.</summary>
        public int NextOffset() { return (int)Math.Floor(FetchedTo); }

        public bool NeedsFetch(double position) { return Started && !End && FetchedTo < position + AheadSeconds && pending.Count < MaxPending; }

        public void Add(ReplayPage page)
        {
            double before = FetchedTo, last = FetchedTo;
            if (seen.Count > MaxSeen) seen.Clear();
            foreach (var c in page.Comments)
            {
                if (c.Offset > last) last = c.Offset;
                if (c.Offset < skipBefore || !seen.Add(c.Id)) continue;
                pending.Add(c);
            }
            pending.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            if (pending.Count > MaxPending) pending.RemoveRange(MaxPending, pending.Count - MaxPending);
            // no step forward (a page of repeats, or an empty one): ask a second further next time, or stop at the end
            FetchedTo = last > before ? last : before + 1;
            if (!page.HasNext) End = true;
        }

        /// <summary>The comments the video has reached (in order), taken out of the buffer.</summary>
        public List<ReplayComment> Due(double position)
        {
            int n = 0;
            while (n < pending.Count && pending[n].Offset <= position) n++;
            var due = pending.GetRange(0, n);
            pending.RemoveRange(0, n);
            return due;
        }
    }
}
