// Data for the native Following page (src/app/BrowsePage.cs): Helix answers turned into plain records, sorting,
// display formats, the small local lists (favourites, recently watched, recent searches), a response cache and
// a guard against slow answers overwriting newer ones. Only official Helix endpoints are used:
// streams/followed, channels/followed, users, search/channels.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LiteViewCore
{
    /// <summary>A live stream (streams/followed).</summary>
    public sealed class LiveStream
    {
        public string UserId = "", Login = "", Name = "", Game = "", Title = "", Language = "", Thumbnail = "";
        public long Viewers;
        public DateTimeOffset? Started;
    }

    /// <summary>A followed channel (channels/followed) or a search result (search/channels).</summary>
    public sealed class ChannelInfo
    {
        public string Id = "", Login = "", Name = "", Game = "", Title = "", Avatar = "";
        public bool Live;
        public DateTimeOffset? Started, Followed;
    }

    public enum SortOrder { Viewers, Name, Newest, Longest }

    public static class HelixData
    {
        public const int MaxText = 300;   // Twitch titles are at most 140 characters; anything longer is cut

        public static List<LiveStream> Streams(object? json)
        {
            var list = new List<LiveStream>();
            foreach (var s in Json.A(json, "data"))
            {
                string? login = Json.S(s, "user_login");
                if (!ChannelName.IsValidLogin(login)) continue;   // nothing that could not be watched or opened
                list.Add(new LiveStream
                {
                    UserId = Id(Json.S(s, "user_id")), Login = login!, Name = Or(Text(Json.S(s, "user_name")), login!),
                    Game = Text(Json.S(s, "game_name")), Title = Text(Json.S(s, "title")), Language = Text(Json.S(s, "language")),
                    Thumbnail = Json.S(s, "thumbnail_url") ?? "", Viewers = Math.Max(0, Json.N(s, "viewer_count")), Started = Time(Json.S(s, "started_at")),
                });
            }
            return list;
        }

        public static List<ChannelInfo> Followed(object? json)
        {
            var list = new List<ChannelInfo>();
            foreach (var c in Json.A(json, "data"))
            {
                string? login = Json.S(c, "broadcaster_login");
                if (!ChannelName.IsValidLogin(login)) continue;
                list.Add(new ChannelInfo { Id = Id(Json.S(c, "broadcaster_id")), Login = login!, Name = Or(Text(Json.S(c, "broadcaster_name")), login!), Followed = Time(Json.S(c, "followed_at")) });
            }
            return list;
        }

        public static List<ChannelInfo> Search(object? json)
        {
            var list = new List<ChannelInfo>();
            foreach (var c in Json.A(json, "data"))
            {
                string? login = Json.S(c, "broadcaster_login");
                if (!ChannelName.IsValidLogin(login)) continue;
                list.Add(new ChannelInfo
                {
                    Id = Id(Json.S(c, "id")), Login = login!, Name = Or(Text(Json.S(c, "display_name")), login!),
                    Game = Text(Json.S(c, "game_name")), Title = Text(Json.S(c, "title")), Avatar = Json.S(c, "thumbnail_url") ?? "",
                    Live = Json.B(c, "is_live"), Started = Time(Json.S(c, "started_at")),
                });
            }
            return list;
        }

        public static string? Cursor(object? json) { return Json.S(Json.V(json, "pagination"), "cursor"); }

        /// <summary>users?login=...: channel login → profile picture URL (the 70x70 size), allowed image hosts only.</summary>
        public static Dictionary<string, string> Avatars(object? json)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var u in Json.A(json, "data"))
            {
                string? login = Json.S(u, "login");
                string url = Avatar(Json.S(u, "profile_image_url"));
                if (ChannelName.IsValidLogin(login) && url.Length > 0) d[login!] = url;
            }
            return d;
        }

        /// <summary>A profile picture URL in the small size, or "" when it is not an allowed image URL.</summary>
        public static string Avatar(string? url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            string u = url!.Replace("-300x300.", "-70x70.");
            return SafeUrl.IsAllowedImage(u) ? u : "";
        }

        /// <summary>A stream preview URL from Helix's "{width}x{height}" template, or "" when not an allowed image URL.</summary>
        public static string Thumb(string? template, int w, int h)
        {
            if (string.IsNullOrEmpty(template) || w <= 0 || h <= 0 || w > 1920 || h > 1080) return "";
            string u = template!.Replace("{width}", w.ToString(CultureInfo.InvariantCulture)).Replace("{height}", h.ToString(CultureInfo.InvariantCulture));
            return SafeUrl.IsAllowedImage(u) ? u : "";
        }

        static string Or(string name, string login) { return name.Length > 0 ? name : login; }

        static string Id(string? s) { return ChannelName.IsNumericId(s) ? s! : ""; }

        static DateTimeOffset? Time(string? s)
        {
            DateTimeOffset t;
            return !string.IsNullOrEmpty(s) && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out t) ? t : (DateTimeOffset?)null;
        }

        /// <summary>One line for drawing: control characters (newlines, bidi overrides kept as text) removed, length capped.</summary>
        public static string Text(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(Math.Min(s!.Length, MaxText));
            foreach (char c in s)
            {
                if (sb.Length >= MaxText) break;
                if (char.IsControl(c)) { if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' '); continue; }
                if (c >= '\u202A' && c <= '\u202E' || c >= '\u2066' && c <= '\u2069') continue;   // no direction overrides in names and titles
                sb.Append(c);
            }
            if (sb.Length > 0 && char.IsHighSurrogate(sb[sb.Length - 1])) sb.Length--;   // never half a character
            return sb.ToString().Trim();
        }
    }

    public static class Fmt
    {
        /// <summary>950, 1.2K, 12.3K, 123K, 1.2M (as Twitch shows viewer counts).</summary>
        public static string Viewers(long v)
        {
            if (v < 1000) return Math.Max(0, v).ToString(CultureInfo.InvariantCulture);
            if (v < 100000) return (v / 100 / 10.0).ToString("0.#", CultureInfo.InvariantCulture) + "K";
            if (v < 1000000) return (v / 1000).ToString(CultureInfo.InvariantCulture) + "K";
            return (v / 100000 / 10.0).ToString("0.#", CultureInfo.InvariantCulture) + "M";
        }

        /// <summary>How long a stream has been live: "45m", "2h 05m", "" when unknown or in the future.</summary>
        public static string Uptime(DateTimeOffset? started, DateTimeOffset now)
        {
            if (started == null) return "";
            var d = now - started.Value;
            if (d < TimeSpan.Zero) return "";
            if (d.TotalHours < 1) return ((int)d.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m";
            return ((int)d.TotalHours).ToString(CultureInfo.InvariantCulture) + "h " + d.Minutes.ToString("00", CultureInfo.InvariantCulture) + "m";
        }
    }

    public static class Sorting
    {
        /// <summary>Favourites first when asked, then the chosen order; ties by name so the order is stable between refreshes.</summary>
        public static List<LiveStream> Sort(IEnumerable<LiveStream> streams, SortOrder order, ICollection<string>? favourites, bool favouritesFirst)
        {
            var fav = favourites ?? (ICollection<string>)Array.Empty<string>();
            IOrderedEnumerable<LiveStream> q = favouritesFirst ? streams.OrderBy(s => fav.Contains(s.Login) ? 0 : 1) : streams.OrderBy(s => 0);
            switch (order)
            {
                case SortOrder.Name: q = q.ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase); break;
                case SortOrder.Newest: q = q.ThenByDescending(s => s.Started ?? DateTimeOffset.MinValue); break;
                case SortOrder.Longest: q = q.ThenBy(s => s.Started ?? DateTimeOffset.MaxValue); break;
                default: q = q.ThenByDescending(s => s.Viewers); break;
            }
            return q.ThenBy(s => s.Login, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>
    /// A short list of channel logins kept in the settings file: favourites (in the order added) or recently watched /
    /// recent searches (newest first, oldest dropped). Only valid channel names get in, so the stored form
    /// (comma-separated) needs no escaping.
    /// </summary>
    public sealed class ChannelList
    {
        readonly List<string> items = new List<string>();
        readonly int max; readonly bool newestFirst;

        public ChannelList(int max, bool newestFirst) { this.max = Math.Max(1, max); this.newestFirst = newestFirst; }

        public IReadOnlyList<string> Items { get { return items; } }
        public int Count { get { return items.Count; } }
        public bool Contains(string? login) { return login != null && items.Contains(login); }

        /// <summary>Adds (or, for a newest-first list, moves to the front). False when nothing changed or the name is not valid.</summary>
        public bool Add(string? raw)
        {
            string? login = Exact(raw);
            if (login == null) return false;
            int i = items.IndexOf(login);
            if (newestFirst)
            {
                if (i == 0) return false;
                if (i > 0) items.RemoveAt(i);
                items.Insert(0, login);
                if (items.Count > max) items.RemoveRange(max, items.Count - max);
                return true;
            }
            if (i >= 0 || items.Count >= max) return false;
            items.Add(login);
            return true;
        }

        public bool Remove(string? login) { return login != null && items.Remove(login); }
        public void Clear() { items.Clear(); }

        public string Save() { return string.Join(",", items); }

        public void Load(string? stored)
        {
            items.Clear();
            if (string.IsNullOrEmpty(stored)) return;
            foreach (var part in stored!.Split(','))
            {
                string? login = Exact(part);
                if (login != null && !items.Contains(login) && items.Count < max) items.Add(login);
            }
        }

        // exactly a channel name (any case), not the search box's forgiving reading ("bad name" is not "bad")
        static string? Exact(string? raw)
        {
            string? s = raw?.Trim().ToLowerInvariant();
            return ChannelName.IsValidLogin(s) ? s : null;
        }
    }

    /// <summary>Answers kept for a while (search results, avatars): at most <c>capacity</c> entries, each for <c>ttl</c>.</summary>
    public sealed class TimedCache<TKey, TValue> where TKey : notnull
    {
        readonly Dictionary<TKey, KeyValuePair<DateTimeOffset, TValue>> d = new Dictionary<TKey, KeyValuePair<DateTimeOffset, TValue>>();
        readonly LinkedList<TKey> order = new LinkedList<TKey>();
        readonly int capacity; readonly TimeSpan ttl; readonly Func<DateTimeOffset> now;
        readonly object gate = new object();

        public TimedCache(int capacity, TimeSpan ttl, Func<DateTimeOffset>? now = null)
        {
            this.capacity = Math.Max(1, capacity); this.ttl = ttl; this.now = now ?? (() => DateTimeOffset.UtcNow);
        }

        public int Count { get { lock (gate) return d.Count; } }

        public bool TryGet(TKey key, out TValue value)
        {
            lock (gate)
            {
                KeyValuePair<DateTimeOffset, TValue> e;
                if (d.TryGetValue(key, out e) && now() - e.Key < ttl) { value = e.Value; return true; }
                if (d.Remove(key)) order.Remove(key);
                value = default!;
                return false;
            }
        }

        public void Set(TKey key, TValue value)
        {
            lock (gate)
            {
                if (d.ContainsKey(key)) order.Remove(key);   // moves to the newest end
                d[key] = new KeyValuePair<DateTimeOffset, TValue>(now(), value);
                order.AddLast(key);
                while (d.Count > capacity) { var oldest = order.First!.Value; order.RemoveFirst(); d.Remove(oldest); }
            }
        }

        public void Clear() { lock (gate) { d.Clear(); order.Clear(); } }
    }

    /// <summary>Stale-answer guard: take a ticket before a request, use the answer only if no newer request started since.</summary>
    public sealed class Latest
    {
        int n;
        public int Next() { return Interlocked.Increment(ref n); }
        public bool IsCurrent(int ticket) { return Volatile.Read(ref n) == ticket; }
    }

    public static class SearchText
    {
        public const int MaxLength = 100;

        /// <summary>What is sent to search/channels: trimmed, one line, at most 100 characters; null when empty.</summary>
        public static string? Clean(string? s)
        {
            string t = HelixData.Text(s);
            if (t.Length > MaxLength) t = t.Substring(0, MaxLength).TrimEnd();
            return t.Length == 0 ? null : t;
        }
    }

    public enum FeedState { Ok, NotLoggedIn, Unreachable }

    /// <summary>The Helix calls behind the Following page, over any Helix function (TwitchClient.Helix, or a fake in tests).</summary>
    public sealed class Following
    {
        public const int MaxLivePages = 5, MaxFollowedPages = 20, SearchResults = 20;   // 500 live, 2000 followed
        readonly Func<string, Task<object?>> helix;

        public Following(Func<string, Task<object?>> helix) { this.helix = helix; }

        public async Task<KeyValuePair<FeedState, List<LiveStream>>> Live(string? userId)
        {
            var list = new List<LiveStream>();
            if (!ChannelName.IsNumericId(userId)) return new KeyValuePair<FeedState, List<LiveStream>>(FeedState.NotLoggedIn, list);
            string? cursor = null;
            for (int page = 0; page < MaxLivePages; page++)
            {
                var j = await helix("streams/followed?user_id=" + userId + "&first=100" + After(cursor)).ConfigureAwait(false);
                if (j == null)
                {
                    if (page == 0) return new KeyValuePair<FeedState, List<LiveStream>>(FeedState.Unreachable, list);
                    break;   // keep what arrived
                }
                list.AddRange(HelixData.Streams(j));
                cursor = HelixData.Cursor(j);
                if (cursor == null) break;
            }
            return new KeyValuePair<FeedState, List<LiveStream>>(FeedState.Ok, Distinct(list, s => s.Login));
        }

        public async Task<KeyValuePair<FeedState, List<ChannelInfo>>> Followed(string? userId)
        {
            var list = new List<ChannelInfo>();
            if (!ChannelName.IsNumericId(userId)) return new KeyValuePair<FeedState, List<ChannelInfo>>(FeedState.NotLoggedIn, list);
            string? cursor = null;
            for (int page = 0; page < MaxFollowedPages; page++)
            {
                var j = await helix("channels/followed?user_id=" + userId + "&first=100" + After(cursor)).ConfigureAwait(false);
                if (j == null)
                {
                    if (page == 0) return new KeyValuePair<FeedState, List<ChannelInfo>>(FeedState.Unreachable, list);
                    break;
                }
                list.AddRange(HelixData.Followed(j));
                cursor = HelixData.Cursor(j);
                if (cursor == null) break;
            }
            return new KeyValuePair<FeedState, List<ChannelInfo>>(FeedState.Ok, Distinct(list, c => c.Login));
        }

        /// <summary>Profile pictures by channel login, 100 per request.</summary>
        public async Task<Dictionary<string, string>> Avatars(IEnumerable<string> logins)
        {
            var all = new Dictionary<string, string>(StringComparer.Ordinal);
            var want = logins.Where(ChannelName.IsValidLogin).Distinct().ToList();
            for (int i = 0; i < want.Count; i += 100)
            {
                var j = await helix("users?login=" + string.Join("&login=", want.Skip(i).Take(100))).ConfigureAwait(false);
                foreach (var kv in HelixData.Avatars(j)) all[kv.Key] = kv.Value;
            }
            return all;
        }

        /// <summary>Which of these channels are live now (streams?user_login=, 100 per request); null when unreachable.</summary>
        public async Task<List<LiveStream>?> LiveNow(IEnumerable<string> logins)
        {
            var all = new List<LiveStream>();
            var want = logins.Where(ChannelName.IsValidLogin).Distinct().ToList();
            for (int i = 0; i < want.Count; i += 100)
            {
                var j = await helix("streams?first=100&user_login=" + string.Join("&user_login=", want.Skip(i).Take(100))).ConfigureAwait(false);
                if (j == null) return null;
                all.AddRange(HelixData.Streams(j));
            }
            return all;
        }

        /// <summary>search/channels; null when Twitch could not be reached (an empty list is "nothing found").</summary>
        public async Task<List<ChannelInfo>?> Search(string? text)
        {
            string? q = SearchText.Clean(text);
            if (q == null) return new List<ChannelInfo>();
            var j = await helix("search/channels?first=" + SearchResults + "&query=" + Uri.EscapeDataString(q)).ConfigureAwait(false);
            if (j == null) return null;
            // live channels first, then Twitch's order (relevance)
            return HelixData.Search(j).Select((c, i) => new { c, i }).OrderBy(x => x.c.Live ? 0 : 1).ThenBy(x => x.i).Select(x => x.c).ToList();
        }

        static string After(string? cursor) { return cursor != null ? "&after=" + Uri.EscapeDataString(cursor) : ""; }

        static List<T> Distinct<T>(List<T> list, Func<T, string> key)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            return list.Where(x => seen.Add(key(x))).ToList();
        }
    }
}
