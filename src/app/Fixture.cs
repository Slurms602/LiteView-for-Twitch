// LITEVIEW_UI_FIXTURE=1 (testing only): made-up Twitch answers and generated pictures, with no network and no real
// login, so CI can take screenshots of the logged-in pages (tools/smoke.ps1). The real token file is not read or
// written, the window title says "UI fixture", and LITEVIEW_UI_FIXTURE_SCENE picks what is shown after start:
// live (default), followed, recent, history, search:<text>, problem (the details of src/app/ProblemPopup.cs), or chat
// (the watch view with synthetic chat; no video: Streamlink is not started in fixture mode).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LiteViewCore;

static class Fixture
{
    public static readonly bool On = Environment.GetEnvironmentVariable("LITEVIEW_UI_FIXTURE") == "1";
    public static string SceneName { get { return Environment.GetEnvironmentVariable("LITEVIEW_UI_FIXTURE_SCENE") ?? "live"; } }

    static readonly string[] Live =
    {
        "nightowl_plays", "pixelforge", "zed_speedruns", "lofi_studio", "chessmaster_k", "retro_rewind", "cosmic_kitchen",
        "trackmania_tom", "dev_streams", "wildlife_cam", "artbyjuno", "valorant_vee", "minecraft_mira", "jazz_nights",
    };
    static readonly string[] Offline =
    {
        "apex_andy", "bakingwithbea", "cyber_cat", "dungeon_dan", "echo_music", "factorio_fred", "golf_greg", "hollow_hana",
        "indie_ivy", "jrpg_jake", "karaoke_kim", "league_leo", "mods_mike", "nature_nina", "osu_oli", "piano_pete",
    };
    static readonly string[] Games = { "Just Chatting", "Elden Ring", "Speedrunning", "Music", "Chess", "Retro", "Food & Drink", "Trackmania", "Software and Game Development", "Animals", "Art", "VALORANT", "Minecraft", "Music" };
    static readonly string[] Titles =
    {
        "chill late night stream, come hang out", "building a castle in one sitting - day 41", "any% world record attempts all day",
        "beats to study and relax to", "rated games with viewers, !challenge to play", "playing every SNES game in order (part 212)",
        "cooking a three course dinner live", "new track every hour, map pack review", "writing a Twitch client from scratch in C#",
        "the eagles are back on the nest", "drawing your commissions, chatting", "ranked grind to Radiant", "hardcore survival, no deaths yet",
        "live from the jazz club, request songs in chat",
    };

    public static TwitchClient Client(string clientId) { return new TwitchClient(clientId, new HttpClient(new Handler(clientId)), new Store()); }

    public static void Scene(Form form, BrowsePage page, TextBox search)
    {
        form.Text += "  -  UI fixture (made-up data)";
        Log.Warn("UI fixture mode: made-up Twitch data, no network");
        string scene = SceneName;
        if (scene == "followed") page.SetTab(BrowsePage.Tab.Followed);
        else if (scene == "recent") { foreach (var l in new[] { "jazz_nights", "apex_andy", "pixelforge" }) page.Watched(l); page.SetTab(BrowsePage.Tab.Recent); }
        else if (scene == "history") { foreach (var l in new[] { "chessmaster_k", "osu_oli" }) page.Searched(l); page.ShowHistory(); }
        else if (scene.StartsWith("search:", StringComparison.Ordinal)) search.Text = scene.Substring(7);
        if (scene == "live" || scene == "followed") page.Favourites.Add("lofi_studio");
    }

    sealed class Store : ITokenStore
    {
        public bool TryLoad(out string token, out string refresh) { token = "fixture-token"; refresh = null; return true; }
        public void Save(string token, string refresh) { }
        public void Delete() { }
    }

    sealed class Handler : HttpMessageHandler
    {
        readonly string clientId;
        public Handler(string clientId) { this.clientId = clientId; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage rq, CancellationToken ct)
        {
            var u = rq.RequestUri;
            string path = u.AbsolutePath, q = u.Query;
            if (u.Host == "id.twitch.tv" && path.EndsWith("/validate", StringComparison.Ordinal))
                return Json("{\"client_id\":\"" + clientId + "\",\"login\":\"fixture_viewer\",\"user_id\":\"1000\",\"scopes\":[" +
                    string.Join(",", TwitchClient.Scopes.Split(' ').Select(s => "\"" + s + "\"")) + "],\"expires_in\":14400}");
            if (u.Host == "api.twitch.tv")
            {
                if (path.EndsWith("/streams/followed", StringComparison.Ordinal)) return Json(Streams(Live));
                if (path.EndsWith("/streams", StringComparison.Ordinal)) return Json(Streams(Values(q, "user_login").Where(l => Live.Contains(l))));
                if (path.EndsWith("/channels/followed", StringComparison.Ordinal))
                    return Json("{\"data\":[" + string.Join(",", Live.Concat(Offline).Select((l, i) => "{\"broadcaster_id\":\"" + (2000 + i) + "\",\"broadcaster_login\":\"" + l + "\",\"broadcaster_name\":\"" + Display(l) + "\",\"followed_at\":\"" + DateTime.UtcNow.AddDays(-40 * i - 3).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) + "\"}")) + "],\"pagination\":{}}");
                if (path.EndsWith("/users", StringComparison.Ordinal))
                    return Json("{\"data\":[" + string.Join(",", Values(q, "login").Select(l => "{\"login\":\"" + l + "\",\"display_name\":\"" + Display(l) + "\",\"profile_image_url\":\"https://static-cdn.jtvnw.net/jtv_user_pictures/fixture-" + l + "-profile_image-300x300.png\"}")) + "]}");
                if (path.EndsWith("/search/channels", StringComparison.Ordinal))
                {
                    string text = (Values(q, "query").FirstOrDefault() ?? "").ToLowerInvariant();
                    return Json("{\"data\":[" + string.Join(",", Live.Concat(Offline).Where(l => text.Length > 0 && l.Replace("_", "").Contains(text.Replace("_", "").Replace(" ", ""))).Select(l =>
                        "{\"id\":\"" + (3000 + Math.Abs(l.GetHashCode() % 1000)) + "\",\"broadcaster_login\":\"" + l + "\",\"display_name\":\"" + Display(l) + "\",\"is_live\":" + (Live.Contains(l) ? "true" : "false") +
                        ",\"game_name\":\"" + (Live.Contains(l) ? Games[Array.IndexOf(Live, l)] : "") + "\",\"title\":\"" + (Live.Contains(l) ? Titles[Array.IndexOf(Live, l)] : "") +
                        "\",\"thumbnail_url\":\"https://static-cdn.jtvnw.net/jtv_user_pictures/fixture-" + l + "-profile_image-300x300.png\"}")) + "]}");
                }
            }
            if (u.Host == "static-cdn.jtvnw.net")
            {
                string file = path.Substring(path.LastIndexOf('/') + 1);
                if (file.StartsWith("live_user_", StringComparison.Ordinal)) return Image(Preview(file.Substring(10, file.LastIndexOf('-') - 10)), "image/jpeg");
                if (file.StartsWith("fixture-", StringComparison.Ordinal)) return Image(Avatar(file.Substring(8, file.IndexOf("-profile_image", StringComparison.Ordinal) - 8)), "image/png");
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") });
        }

        static IEnumerable<string> Values(string query, string key)
        {
            foreach (var part in query.TrimStart('?').Split('&'))
            {
                int eq = part.IndexOf('=');
                if (eq > 0 && part.Substring(0, eq) == key) yield return Uri.UnescapeDataString(part.Substring(eq + 1));
            }
        }

        static string Streams(IEnumerable<string> logins)
        {
            var now = DateTime.UtcNow;
            return "{\"data\":[" + string.Join(",", logins.Select(l =>
            {
                int i = Array.IndexOf(Live, l);
                long viewers = new long[] { 182, 41250, 2210, 9800, 655, 74, 3120, 1460, 37, 12800, 940, 28300, 5100, 410 }[i];
                return "{\"user_id\":\"" + (2000 + i) + "\",\"user_login\":\"" + l + "\",\"user_name\":\"" + Display(l) + "\",\"game_name\":\"" + Games[i] + "\",\"title\":\"" + Titles[i] +
                    "\",\"viewer_count\":" + viewers + ",\"started_at\":\"" + now.AddMinutes(-(17 + i * 41)).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) +
                    "\",\"language\":\"en\",\"thumbnail_url\":\"https://static-cdn.jtvnw.net/previews-ttv/live_user_" + l + "-{width}x{height}.jpg\"}";
            })) + "],\"pagination\":{}}";
        }

        static string Display(string login)
        {
            return string.Concat(login.Split('_').Select(w => w.Length > 0 ? char.ToUpperInvariant(w[0]) + w.Substring(1) : w));
        }

        static Task<HttpResponseMessage> Json(string body)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }

        static Task<HttpResponseMessage> Image(byte[] bytes, string type)
        {
            var c = new ByteArrayContent(bytes); c.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = c });
        }

        static Color Hue(string s, float light)
        {
            int h = 0; foreach (char ch in s) h = h * 31 + ch;
            double hue = (h & 0x7fffffff) % 360 / 60.0; int sector = (int)hue; double f = hue - sector;
            double v = light, p = v * 0.35, qv = v * (1 - 0.65 * f), t = v * (1 - 0.65 * (1 - f));
            double r, g, b;
            switch (sector) { case 0: r = v; g = t; b = p; break; case 1: r = qv; g = v; b = p; break; case 2: r = p; g = v; b = t; break; case 3: r = p; g = qv; b = v; break; case 4: r = t; g = p; b = v; break; default: r = v; g = p; b = qv; break; }
            return Color.FromArgb((int)(r * 255), (int)(g * 255), (int)(b * 255));
        }

        static byte[] Preview(string login)
        {
            using (var bmp = new Bitmap(440, 248))
            using (var g = Graphics.FromImage(bmp))
            using (var bg = new LinearGradientBrush(new Rectangle(0, 0, 440, 248), Hue(login, 0.55f), Hue(login + "x", 0.18f), 35f))
            using (var font = new Font("Segoe UI Semibold", 22f))
            using (var small = new Font("Segoe UI", 11f))
            using (var ms = new System.IO.MemoryStream())
            {
                g.FillRectangle(bg, 0, 0, 440, 248);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                int i = Array.IndexOf(Live, login);
                g.DrawString(i >= 0 ? Games[i] : login, font, Brushes.White, 24, 150);
                g.DrawString("fixture preview - " + login, small, new SolidBrush(Color.FromArgb(200, 255, 255, 255)), 26, 196);
                bmp.Save(ms, ImageFormat.Jpeg);
                return ms.ToArray();
            }
        }

        static byte[] Avatar(string login)
        {
            using (var bmp = new Bitmap(70, 70))
            using (var g = Graphics.FromImage(bmp))
            using (var b = new SolidBrush(Hue(login, 0.75f)))
            using (var font = new Font("Segoe UI Semibold", 26f))
            using (var ms = new System.IO.MemoryStream())
            {
                g.FillRectangle(b, 0, 0, 70, 70);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(login.Substring(0, 1).ToUpperInvariant(), font, Brushes.White, new RectangleF(0, 0, 70, 70), sf);
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }
    }
}
