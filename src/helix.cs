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
using LiteViewCore;

static class Tw
{
    // "LiteView for Twitch" on dev.twitch.tv (client type Public: no secret exists for it).
    // This is UPSTREAM's registered application. Before this fork is released publicly it needs its own
    // registration (dev.twitch.tv > Applications, client type Public, device code flow) - see docs/KNOWN_ISSUES.md.
    public const string ClientId = "i82nikwku19tn6wk8ms6z7hr9l57wo";
    const string Scopes = "user:read:follows user:read:emotes chat:read chat:edit";
    public static readonly HttpClient Http = new HttpClient();
    static readonly JavaScriptSerializer js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    static readonly SemaphoreSlim refreshGate = new SemaphoreSlim(1);
    public static string Token, Login, UserId;
    public static string[] Granted = new string[0];   // scopes of the current token
    public static bool Has(string scope) { return Array.IndexOf(Granted, scope) >= 0; }
    static string refresh, path;
    // Credentials change only under credLock, and each change bumps authGen; an answer that arrives after a
    // log out or a new login (authGen moved on) is thrown away instead of bringing old credentials back.
    static readonly object credLock = new object();
    static int authGen;
    enum Refresh { Ok, Rejected, Unreachable }

    public static void Init(string tokenFile)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        Http.Timeout = TimeSpan.FromSeconds(20);
        Http.MaxResponseContentBufferSize = Bounded.MaxJsonBytes;   // upstream: the 2 GB default
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("LiteView-Enhanced/" + typeof(Tw).Assembly.GetName().Version);
        path = tokenFile;
        try
        {
            if (!File.Exists(path)) return;
            var s = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser)).Split('\n');
            if (s[0].Length > 0) { Token = s[0]; refresh = s.Length > 1 && s[1].Length > 0 ? s[1] : null; }
        }
        catch (Exception ex) { Log.Warn("token file unreadable (" + ex.GetType().Name + "): log in again"); }
    }

    static void Save()
    {
        try
        {
            if (Token == null) { AtomicFile.Delete(path); return; }
            // DPAPI (this Windows user only), written atomically (upstream: File.WriteAllBytes)
            AtomicFile.Write(path, ProtectedData.Protect(Encoding.UTF8.GetBytes(Token + "\n" + refresh), null, DataProtectionScope.CurrentUser), false);
        }
        catch (Exception ex) { Log.Error("saving the token", ex); }
    }

    public static void LogOut()
    {
        string t;
        lock (credLock) { t = Token; authGen++; Token = refresh = Login = UserId = null; Granted = new string[0]; Save(); }
        Log.Info("logged out; token revoked and deleted");
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

    // JSON from the emote services: only their API hosts over https, at most 16 MB
    public static async Task<string> Get(string url)
    {
        if (!SafeUrl.IsAllowedApi(url)) { Log.Warn("refused API request to an unknown host"); return null; }
        var b = await GetBytes(url, Bounded.MaxJsonBytes);
        return b == null ? null : Encoding.UTF8.GetString(b);
    }

    // a download with a size limit (null on any failure); images are checked by the caller
    // (one deadline for headers AND body: HttpClient.Timeout alone does not cover a body that stalls)
    public static async Task<byte[]> GetBytes(string url, int max)
    {
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            try
            {
                using (var r = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token))
                using (cts.Token.Register(() => { try { r.Dispose(); } catch { } }))   // ends a read that is stuck in the socket
                {
                    if (!r.IsSuccessStatusCode) return null;
                    long? len = r.Content.Headers.ContentLength;
                    if (len.HasValue && len.Value > max) return null;
                    using (var st = await r.Content.ReadAsStreamAsync()) return await Bounded.ReadAllAsync(st, max, cts.Token);
                }
            }
            catch { return null; }
        }
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
        lock (credLock) { authGen++; Token = S(d, "access_token"); refresh = S(d, "refresh_token"); Save(); }
    }

    // Signed in and the token is good (refreshed when it expired). Twitch asks apps to check hourly.
    public static async Task<bool> Validate()
    {
        for (int i = 0; i < 2 && Token != null; i++)
        {
            string t = Token; int g = authGen;
            var rq = new HttpRequestMessage(HttpMethod.Get, "https://id.twitch.tv/oauth2/validate");
            rq.Headers.Authorization = new AuthenticationHeaderValue("OAuth", t);
            int code = 0; object d = null;
            try { using (var r = await Http.SendAsync(rq)) { code = (int)r.StatusCode; d = Parse(await r.Content.ReadAsStringAsync()); } }
            catch { }
            if (g != authGen) return Login != null && Token != null;   // logged out / in again meanwhile: this answer is stale
            if (code == 200)
            {
                string login = S(d, "login"), uid = S(d, "user_id");
                // the token must belong to a real account and have been issued to this app
                if (!ChannelName.IsValidLogin(login) || !ChannelName.IsNumericId(uid) || (S(d, "client_id") != null && S(d, "client_id") != ClientId))
                { Log.Warn("token validation: unexpected answer, logging out"); break; }
                lock (credLock) { if (g != authGen) return Login != null; Login = login; UserId = uid; Granted = A(d, "scopes").Select(x => Convert.ToString(x)).ToArray(); }
                return true;
            }
            if (code != 401) return Login != null;          // offline / Twitch trouble: keep the session
            var rr = await RefreshToken(t);
            if (rr == Refresh.Ok) continue;
            if (rr == Refresh.Unreachable)
            {   // Twitch did not reject the refresh token, it just did not answer: keep it and try again later
                // (upstream deleted the login here). Until then chat runs anonymously.
                Log.Warn("token expired and Twitch is unreachable; keeping the login to retry");
                lock (credLock) { Login = UserId = null; Granted = new string[0]; }
                return false;
            }
            break;
        }
        lock (credLock)
        {
            if (Token != null) { authGen++; Token = refresh = null; Save(); Log.Info("token no longer valid: logged out"); }
            Login = UserId = null; Granted = new string[0];
        }
        return false;
    }

    // Public clients get one-time refresh tokens: one refresh at a time, and a caller that waited
    // just uses the token the other one got.
    static async Task<Refresh> RefreshToken(string failed)
    {
        await refreshGate.WaitAsync();
        try
        {
            if (Token != failed) return Token != null ? Refresh.Ok : Refresh.Rejected;
            if (refresh == null) return Refresh.Rejected;
            int g = authGen;
            var r = await Post("https://id.twitch.tv/oauth2/token", "client_id=" + ClientId + "&grant_type=refresh_token&refresh_token=" + Uri.EscapeDataString(refresh));
            lock (credLock)
            {
                if (g != authGen) return Token != null ? Refresh.Ok : Refresh.Rejected;   // logged out meanwhile: do not bring the token back
                if (r.Key == 200 && S(r.Value, "access_token") != null) { Take(r.Value); Log.Info("token refreshed"); return Refresh.Ok; }
                if (r.Key == 400 || r.Key == 401) { authGen++; Token = refresh = null; Save(); Log.Info("refresh token rejected: logged out"); return Refresh.Rejected; }
            }
            return Refresh.Unreachable;   // network error, 5xx, 429: the refresh token may still be good
        }
        finally { refreshGate.Release(); }
    }

    public static async Task<object> Helix(string query)
    {
        bool waited = false;
        for (int i = 0; i < 3 && Token != null; i++)
        {
            string t = Token;
            var rq = new HttpRequestMessage(HttpMethod.Get, "https://api.twitch.tv/helix/" + query);
            rq.Headers.Add("Client-Id", ClientId);
            rq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", t);
            try
            {
                using (var r = await Http.SendAsync(rq))
                {
                    if ((int)r.StatusCode == 401) { if (await RefreshToken(t) == Refresh.Ok) continue; return null; }
                    if ((int)r.StatusCode == 429 && !waited)
                    {   // rate limited: wait until Twitch's bucket refills (at most 10 s), once
                        waited = true;
                        IEnumerable<string> v; long reset;
                        double s = r.Headers.TryGetValues("Ratelimit-Reset", out v) && long.TryParse(v.FirstOrDefault(), out reset)
                            ? reset - DateTimeOffset.UtcNow.ToUnixTimeSeconds() : 2;
                        Log.Warn("Twitch API rate limit; waiting " + Math.Max(1, Math.Min(10, s)) + " s");
                        await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, Math.Min(10, s))));
                        continue;
                    }
                    if (!r.IsSuccessStatusCode) return null;
                    using (var st = await r.Content.ReadAsStreamAsync()) return Parse(Encoding.UTF8.GetString(await Bounded.ReadAllAsync(st, Bounded.MaxJsonBytes)));
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
