// A channel's videos (past broadcasts, highlights, uploads) and clips, from Helix's official "videos" and "clips"
// endpoints, for the Following page's Videos and clips view; Streamlink plays them (twitch.tv/videos/<id>,
// clips.twitch.tv/<slug>). Like every Helix answer they are untrusted: a video needs a numeric id, a clip an id of
// letters, digits, "-" and "_" (the only values that reach a command line), text is cleaned, pictures allow-listed.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LiteViewCore
{
    public enum VideoKind { Archive, Highlight, Upload, Clip }

    public sealed class VideoInfo
    {
        public string Id = "", Login = "", Name = "", Title = "", Thumb = "", Creator = "";
        public VideoKind Kind;
        public int Seconds;
        public long Views;
        public DateTimeOffset? Created;
        public bool IsClip { get { return Kind == VideoKind.Clip; } }
    }

    public static class VideoData
    {
        static readonly Regex Slug = new Regex(@"\A[A-Za-z0-9_-]{1,100}\z", RegexOptions.CultureInvariant);
        static readonly Regex Hms = new Regex(@"\A(?:(\d{1,4})h)?(?:(\d{1,4})m)?(?:(\d{1,4})s)?\z", RegexOptions.CultureInvariant);

        public static bool IsClipSlug(string? s) { return s != null && Slug.IsMatch(s); }

        /// <summary>Helix's video duration ("3h8m33s", "45m2s", "59s") in seconds; 0 when it is not that form.</summary>
        public static int Duration(string? s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            var m = Hms.Match(s);
            if (!m.Success) return 0;
            long t = 0;
            if (m.Groups[1].Success) t += long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 3600;
            if (m.Groups[2].Success) t += long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60;
            if (m.Groups[3].Success) t += long.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            return (int)Math.Min(int.MaxValue, t);
        }

        /// <summary>A video picture URL ("%{width}x%{height}" or "{width}x{height}" template, or a plain URL), "" when not allowed.</summary>
        public static string Thumb(string? template, int w, int h)
        {
            if (string.IsNullOrEmpty(template)) return "";
            return HelixData.Thumb(template!.Replace("%{width}", "{width}").Replace("%{height}", "{height}"), w, h);
        }

        /// <summary>videos?user_id=...: past broadcasts, highlights and uploads with a numeric id and a valid channel.</summary>
        public static List<VideoInfo> Videos(object? json)
        {
            var list = new List<VideoInfo>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var v in Json.A(json, "data"))
            {
                string? id = Json.S(v, "id"), login = Json.S(v, "user_login");
                if (!ChannelName.IsNumericId(id) || !ChannelName.IsValidLogin(login) || !seen.Add(id!)) continue;
                string type = Json.S(v, "type") ?? "";
                list.Add(new VideoInfo
                {
                    Id = id!, Login = login!, Name = Or(HelixData.Text(Json.S(v, "user_name")), login!), Title = HelixData.Text(Json.S(v, "title")),
                    Thumb = Json.S(v, "thumbnail_url") ?? "", Kind = type == "highlight" ? VideoKind.Highlight : type == "upload" ? VideoKind.Upload : VideoKind.Archive,
                    Seconds = Duration(Json.S(v, "duration")), Views = Math.Max(0, Json.N(v, "view_count")), Created = Time(Json.S(v, "created_at")),
                });
            }
            return list;
        }

        /// <summary>clips?broadcaster_id=...: the channel's clips (its login and name come from the request).</summary>
        public static List<VideoInfo> Clips(object? json, string login, string name)
        {
            var list = new List<VideoInfo>();
            if (!ChannelName.IsValidLogin(login)) return list;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in Json.A(json, "data"))
            {
                string? id = Json.S(c, "id");
                if (!IsClipSlug(id) || !seen.Add(id!)) continue;
                double secs = 0;
                var d = Json.V(c, "duration");
                if (d is double dd) secs = dd; else if (d is long dl) secs = dl;
                list.Add(new VideoInfo
                {
                    Id = id!, Login = login, Name = Or(HelixData.Text(Json.S(c, "broadcaster_name")), name.Length > 0 ? name : login), Title = HelixData.Text(Json.S(c, "title")),
                    Creator = HelixData.Text(Json.S(c, "creator_name")), Thumb = Json.S(c, "thumbnail_url") ?? "", Kind = VideoKind.Clip,
                    Seconds = (int)Math.Max(0, Math.Min(86400, Math.Round(secs))), Views = Math.Max(0, Json.N(c, "view_count")), Created = Time(Json.S(c, "created_at")),
                });
            }
            list.Sort((a, b) => b.Views.CompareTo(a.Views));
            return list;
        }

        public static string KindName(VideoKind k)
        {
            switch (k) { case VideoKind.Highlight: return "Highlight"; case VideoKind.Upload: return "Upload"; case VideoKind.Clip: return "Clip"; default: return "Past broadcast"; }
        }

        /// <summary>"1:02:03" or "12:34" (or "0:05").</summary>
        public static string Length(int seconds)
        {
            if (seconds <= 0) return "";
            var t = TimeSpan.FromSeconds(seconds);
            return t.TotalHours >= 1 ? ((int)t.TotalHours).ToString(CultureInfo.InvariantCulture) + ":" + t.Minutes.ToString("00", CultureInfo.InvariantCulture) + ":" + t.Seconds.ToString("00", CultureInfo.InvariantCulture)
                : t.Minutes.ToString(CultureInfo.InvariantCulture) + ":" + t.Seconds.ToString("00", CultureInfo.InvariantCulture);
        }

        /// <summary>"today", "yesterday", "3 days ago", "2 weeks ago", "5 months ago", "2 years ago"; "" when unknown.</summary>
        public static string Ago(DateTimeOffset? when, DateTimeOffset now)
        {
            if (when == null) return "";
            var d = now - when.Value;
            if (d < TimeSpan.Zero) d = TimeSpan.Zero;
            int days = (int)d.TotalDays;
            if (d.TotalHours < 24) return d.TotalHours < 1 ? "just now" : ((int)d.TotalHours) + (d.TotalHours < 2 ? " hour ago" : " hours ago");
            if (days < 2) return "yesterday";
            if (days < 14) return days + " days ago";
            if (days < 60) return days / 7 + " weeks ago";
            if (days < 730) return days / 30 + " months ago";
            return days / 365 + " years ago";
        }

        /// <summary>The status line while a video plays: "Past broadcast  ·  3:08:33  ·  12.3K views  ·  3 days ago  ·  Title".</summary>
        public static string Line(VideoInfo? v, DateTimeOffset now)
        {
            if (v == null) return "";
            var parts = new List<string> { v.IsClip && v.Creator.Length > 0 ? "Clip by " + v.Creator : KindName(v.Kind) };
            string len = Length(v.Seconds); if (len.Length > 0) parts.Add(len);
            parts.Add(Fmt.Viewers(v.Views) + (v.Views == 1 ? " view" : " views"));
            string ago = Ago(v.Created, now); if (ago.Length > 0) parts.Add(ago);
            if (v.Title.Length > 0) parts.Add(v.Title);
            return string.Join("  ·  ", parts);
        }

        static string Or(string a, string b) { return a.Length > 0 ? a : b; }

        static DateTimeOffset? Time(string? s)
        {
            DateTimeOffset t;
            return !string.IsNullOrEmpty(s) && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out t) ? t : (DateTimeOffset?)null;
        }
    }

    public sealed class ChannelVideos
    {
        public const int Max = 40, ClipDays = 30;
        readonly Func<string, Task<object?>> helix;
        readonly Dictionary<string, string> ids = new Dictionary<string, string>(StringComparer.Ordinal);

        public ChannelVideos(Func<string, Task<object?>> helix) { this.helix = helix; }

        async Task<string?> IdOf(string login)
        {
            string? id;
            if (ids.TryGetValue(login, out id)) return id;
            var j = await helix("users?login=" + login).ConfigureAwait(false);
            if (j == null) return null;
            foreach (var u in Json.A(j, "data"))
                if (Json.S(u, "login") == login && ChannelName.IsNumericId(Json.S(u, "id"))) { id = Json.S(u, "id"); break; }
            if (id != null) { if (ids.Count > 500) ids.Clear(); ids[login] = id; }
            return id ?? "";
        }

        /// <summary>The newest videos of a channel; null when Twitch could not be reached (empty: none, or no such channel).</summary>
        public async Task<List<VideoInfo>?> Videos(string login)
        {
            if (!ChannelName.IsValidLogin(login)) return new List<VideoInfo>();
            var id = await IdOf(login).ConfigureAwait(false);
            if (id == null) return null;
            if (id.Length == 0) return new List<VideoInfo>();
            var j = await helix("videos?user_id=" + id + "&first=" + Max + "&type=all&sort=time").ConfigureAwait(false);
            return j == null ? null : VideoData.Videos(j);
        }

        /// <summary>The most viewed clips of the last ClipDays days; null when Twitch could not be reached.</summary>
        public async Task<List<VideoInfo>?> Clips(string login, string name, DateTimeOffset now)
        {
            if (!ChannelName.IsValidLogin(login)) return new List<VideoInfo>();
            var id = await IdOf(login).ConfigureAwait(false);
            if (id == null) return null;
            if (id.Length == 0) return new List<VideoInfo>();
            string from = now.UtcDateTime.AddDays(-ClipDays).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            string to = now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            var j = await helix("clips?broadcaster_id=" + id + "&first=" + Max + "&started_at=" + Uri.EscapeDataString(from) + "&ended_at=" + Uri.EscapeDataString(to)).ConfigureAwait(false);
            return j == null ? null : VideoData.Clips(j, login, name);
        }
    }
}
