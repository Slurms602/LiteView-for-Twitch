// Twitch's official Helix API for LiteView: device-code login (public client, no secret),
// followed live channels, avatars and chat badges. The token stays next to the exe,
// encrypted for this Windows user (DPAPI); it is never sent anywhere but Twitch.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

static class Tw
{
    // "LiteView for Twitch" on dev.twitch.tv (client type Public: no secret exists for it)
    public const string ClientId = "i82nikwku19tn6wk8ms6z7hr9l57wo";
    const string Scopes = "user:read:follows user:read:emotes chat:read chat:edit";
    public static readonly HttpClient Http = new HttpClient();
    static readonly JavaScriptSerializer js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    static readonly SemaphoreSlim refreshGate = new SemaphoreSlim(1);
    public static string Token, Login, UserId;
    public static string[] Granted = new string[0];   // scopes of the current token
    public static bool Has(string scope) { return Array.IndexOf(Granted, scope) >= 0; }
    static string refresh, path;

    public static void Init(string tokenFile)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        Http.Timeout = TimeSpan.FromSeconds(20);
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("LiteView/1.1");
        path = tokenFile;
        try
        {
            var s = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser)).Split('\n');
            if (s[0].Length > 0) { Token = s[0]; refresh = s.Length > 1 && s[1].Length > 0 ? s[1] : null; }
        }
        catch { }
    }

    static void Save()
    {
        try
        {
            if (Token == null) { File.Delete(path); return; }
            File.WriteAllBytes(path, ProtectedData.Protect(Encoding.UTF8.GetBytes(Token + "\n" + refresh), null, DataProtectionScope.CurrentUser));
        }
        catch { }
    }

    public static void LogOut()
    {
        string t = Token;
        Token = refresh = Login = UserId = null; Save();
        if (t != null) Post("https://id.twitch.tv/oauth2/revoke", "client_id=" + ClientId + "&token=" + Uri.EscapeDataString(t)).ContinueWith(x => { });
    }

    // ---- JSON helpers (JavaScriptSerializer: objects = Dictionary, arrays = object[]) ----
    public static object Parse(string s) { try { return s == null ? null : js.DeserializeObject(s); } catch { return null; } }
    public static object V(object o, string k) { var d = o as Dictionary<string, object>; object v; return d != null && d.TryGetValue(k, out v) ? v : null; }
    public static string S(object o, string k) { var v = V(o, k); return v == null ? null : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture); }
    public static long N(object o, string k) { var v = V(o, k); try { return v == null ? 0 : Convert.ToInt64(v); } catch { return 0; } }
    public static bool B(object o, string k) { var v = V(o, k); return v is bool && (bool)v; }
    public static object[] A(object o, string k) { return V(o, k) as object[] ?? new object[0]; }
    public static IEnumerable<KeyValuePair<string, object>> Props(object o) { return o as Dictionary<string, object> ?? new Dictionary<string, object>(); }

    public static async Task<string> Get(string url)
    {
        try { using (var r = await Http.GetAsync(url)) return r.IsSuccessStatusCode ? await r.Content.ReadAsStringAsync() : null; }
        catch { return null; }
    }

    static async Task<KeyValuePair<int, object>> Post(string url, string form)
    {
        try
        {
            using (var r = await Http.PostAsync(url, new StringContent(form, Encoding.UTF8, "application/x-www-form-urlencoded")))
                return new KeyValuePair<int, object>((int)r.StatusCode, Parse(await r.Content.ReadAsStringAsync()));
        }
        catch { return new KeyValuePair<int, object>(0, null); }
    }

    static void Take(object d)
    {
        Token = S(d, "access_token"); refresh = S(d, "refresh_token"); Save();
    }

    // Signed in and the token is good (refreshed when it expired). Twitch asks apps to check hourly.
    public static async Task<bool> Validate()
    {
        for (int i = 0; i < 2 && Token != null; i++)
        {
            string t = Token;
            var rq = new HttpRequestMessage(HttpMethod.Get, "https://id.twitch.tv/oauth2/validate");
            rq.Headers.Authorization = new AuthenticationHeaderValue("OAuth", t);
            int code = 0; object d = null;
            try { using (var r = await Http.SendAsync(rq)) { code = (int)r.StatusCode; d = Parse(await r.Content.ReadAsStringAsync()); } }
            catch { }
            if (code == 200) { Login = S(d, "login"); UserId = S(d, "user_id"); Granted = A(d, "scopes").Select(x => Convert.ToString(x)).ToArray(); return true; }
            if (code != 401) return Login != null;          // offline / Twitch trouble: keep the session
            if (!await RefreshToken(t)) break;
        }
        if (Token != null) { Token = refresh = null; Save(); }
        Login = UserId = null;
        return false;
    }

    // Public clients get one-time refresh tokens: one refresh at a time, and a caller that waited
    // just uses the token the other one got.
    static async Task<bool> RefreshToken(string failed)
    {
        await refreshGate.WaitAsync();
        try
        {
            if (Token != failed) return Token != null;
            if (refresh == null) return false;
            var r = await Post("https://id.twitch.tv/oauth2/token", "client_id=" + ClientId + "&grant_type=refresh_token&refresh_token=" + Uri.EscapeDataString(refresh));
            if (r.Key == 200 && S(r.Value, "access_token") != null) { Take(r.Value); return true; }
            if (r.Key == 400 || r.Key == 401) { Token = refresh = null; Save(); }
            return false;
        }
        finally { refreshGate.Release(); }
    }

    public static async Task<object> Helix(string query)
    {
        for (int i = 0; i < 2 && Token != null; i++)
        {
            string t = Token;
            var rq = new HttpRequestMessage(HttpMethod.Get, "https://api.twitch.tv/helix/" + query);
            rq.Headers.Add("Client-Id", ClientId);
            rq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", t);
            try
            {
                using (var r = await Http.SendAsync(rq))
                {
                    if ((int)r.StatusCode == 401) { if (await RefreshToken(t)) continue; return null; }
                    return r.IsSuccessStatusCode ? Parse(await r.Content.ReadAsStringAsync()) : null;
                }
            }
            catch { return null; }
        }
        return null;
    }

    // ---- Device Code Grant Flow: show the code page, then poll until the user allows it ----
    public static async Task<object> StartDevice()
    {
        var r = await Post("https://id.twitch.tv/oauth2/device", "client_id=" + ClientId + "&scopes=" + Uri.EscapeDataString(Scopes));
        return r.Key == 200 ? r.Value : null;
    }

    // "ok" when signed in; "authorization_pending" / "slow_down" = keep polling; anything else = failed
    public static async Task<string> PollDevice(string deviceCode)
    {
        var r = await Post("https://id.twitch.tv/oauth2/token", "client_id=" + ClientId + "&scopes=" + Uri.EscapeDataString(Scopes) +
            "&device_code=" + Uri.EscapeDataString(deviceCode) + "&grant_type=urn:ietf:params:oauth:grant-type:device_code");
        if (r.Key == 200 && S(r.Value, "access_token") != null) { Take(r.Value); return await Validate() ? "ok" : "login failed"; }
        if (r.Key == 0) return "authorization_pending";   // network hiccup: try again
        return S(r.Value, "message") ?? ("error " + r.Key);
    }
}
