// Twitch's official APIs for LiteView: device-code login (public client, no secret), token validation and
// refresh, Helix requests, and bounded downloads for the emote services. Moved from src/helix.cs so the
// login state machine can be tested against a simulated Twitch; src/helix.cs keeps the static `Tw` facade
// that the rest of the app (and upstream's code) calls.
//
// Credential rules (each was a bug found in review):
//   - every change of the credentials happens under `cred` and bumps `gen`;
//   - each request takes one snapshot (token, refresh token, gen) under the lock and applies its answer
//     only if gen is unchanged, so an answer that arrives after a log out or a new login is thrown away;
//   - only Twitch saying "no" (400/401) to the refresh token logs out; no answer (network, 5xx, 429) keeps it;
//   - public clients get one-time refresh tokens: one refresh at a time, later callers use its result.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LiteViewCore
{
    /// <summary>Where the access and refresh token are kept (DPAPI file in the app, memory in tests).</summary>
    public interface ITokenStore
    {
        bool TryLoad(out string? token, out string? refresh);
        void Save(string token, string? refresh);
        void Delete();
    }

    public sealed class TwitchClient : IDisposable
    {
        public const string Scopes = "user:read:follows user:read:emotes chat:read chat:edit";
        public string ClientId { get; }
        public string IdBase = "https://id.twitch.tv/oauth2/", HelixBase = "https://api.twitch.tv/helix/";
        public int MaxJsonBytes = Bounded.MaxJsonBytes;
        public TimeSpan DownloadTimeout = TimeSpan.FromSeconds(30);

        readonly HttpClient http;
        readonly ITokenStore store;
        readonly Func<TimeSpan, Task> delay;
        readonly Func<DateTimeOffset> now;
        readonly SemaphoreSlim refreshGate = new SemaphoreSlim(1);
        readonly object cred = new object();
        int gen;
        string? token, refresh, login, userId;
        string[] granted = Array.Empty<string>();

        public TwitchClient(string clientId, HttpClient http, ITokenStore store, Func<TimeSpan, Task>? delay = null, Func<DateTimeOffset>? now = null)
        {
            ClientId = clientId; this.http = http; this.store = store;
            this.delay = delay ?? (t => Task.Delay(t));
            this.now = now ?? (() => DateTimeOffset.UtcNow);
        }

        public void Dispose() { refreshGate.Dispose(); }

        public string? Token { get { lock (cred) return token; } }
        public string? Login { get { lock (cred) return login; } }
        public string? UserId { get { lock (cred) return userId; } }
        public string[] Granted { get { lock (cred) return granted; } }
        public bool Has(string scope) { return Array.IndexOf(Granted, scope) >= 0; }

        /// <summary>The account chat should use right now: login and token from the same moment (token null = anonymous).</summary>
        public void Session(out string? login, out string? token)
        {
            lock (cred) { login = this.login; token = this.login != null ? this.token : null; }
        }

        /// <summary>Loads the stored token (not yet validated).</summary>
        public void Load()
        {
            try
            {
                if (store.TryLoad(out var t, out var r) && !string.IsNullOrEmpty(t)) lock (cred) { token = t; refresh = string.IsNullOrEmpty(r) ? null : r; }
            }
            catch (Exception ex) { Log.Warn("token unreadable (" + ex.GetType().Name + "): log in again"); }
        }

        void SaveLocked()
        {
            try { if (token == null) store.Delete(); else store.Save(token, refresh); }
            catch (Exception ex) { Log.Error("saving the token", ex); }
        }

        void ClearLocked(string why)
        {
            gen++; token = refresh = login = userId = null; granted = Array.Empty<string>(); SaveLocked();
            Log.Info(why);
        }

        void TakeLocked(object? d)
        {
            gen++; token = Json.S(d, "access_token"); refresh = Json.S(d, "refresh_token"); SaveLocked();
        }

        public void LogOut()
        {
            string? t;
            lock (cred) { t = token; ClearLocked("logged out; token revoked and deleted"); }
            if (t != null) _ = Post(IdBase + "revoke", "client_id=" + ClientId + "&token=" + Uri.EscapeDataString(t));
        }

        async Task<KeyValuePair<int, object?>> Post(string url, string form)
        {
            try
            {
                using (var c = new StringContent(form, Encoding.UTF8, "application/x-www-form-urlencoded"))
                using (var r = await http.PostAsync(url, c).ConfigureAwait(false))
                    return new KeyValuePair<int, object?>((int)r.StatusCode, await ReadJson(r).ConfigureAwait(false));
            }
            catch (Exception) { return new KeyValuePair<int, object?>(0, null); }
        }

        async Task<object?> ReadJson(HttpResponseMessage r)
        {
            using (var st = await r.Content.ReadAsStreamAsync().ConfigureAwait(false))
                return Json.Parse(Encoding.UTF8.GetString(await Bounded.ReadAllAsync(st, MaxJsonBytes).ConfigureAwait(false)));
        }

        /// <summary>Signed in and the token is good (refreshed when it expired). Twitch asks apps to check hourly.</summary>
        public async Task<bool> Validate()
        {
            for (int i = 0; i < 2; i++)
            {
                string? t; int g;
                lock (cred)
                {
                    t = token; g = gen;
                    if (t == null) { login = userId = null; granted = Array.Empty<string>(); return false; }
                }
                int code = 0; object? d = null;
                try
                {
                    using (var rq = new HttpRequestMessage(HttpMethod.Get, IdBase + "validate"))
                    {
                        rq.Headers.Authorization = new AuthenticationHeaderValue("OAuth", t);
                        using (var r = await http.SendAsync(rq).ConfigureAwait(false)) { code = (int)r.StatusCode; d = await ReadJson(r).ConfigureAwait(false); }
                    }
                }
                catch (Exception) { }
                if (code == 200)
                {
                    string? l = Json.S(d, "login"), uid = Json.S(d, "user_id"), cid = Json.S(d, "client_id");
                    // the token must belong to a real account and have been issued to this app
                    bool sane = ChannelName.IsValidLogin(l) && ChannelName.IsNumericId(uid) && (cid == null || cid == ClientId);
                    lock (cred)
                    {
                        if (g != gen) return login != null;   // logged out / in again meanwhile: this answer is stale
                        if (!sane) { ClearLocked("token validation: unexpected answer, logged out"); return false; }
                        login = l; userId = uid; granted = Json.A(d, "scopes").Select(x => Convert.ToString(x, System.Globalization.CultureInfo.InvariantCulture) ?? "").ToArray();
                    }
                    return true;
                }
                if (code != 401) { lock (cred) return login != null; }   // offline / Twitch trouble: keep the session
                var rr = await RefreshToken(t, g).ConfigureAwait(false);
                if (rr == RefreshResult.Ok) continue;
                if (rr == RefreshResult.Unreachable)
                {   // Twitch did not reject the refresh token, it just did not answer: keep it and try again later.
                    // Until then the session is not validated (chat anonymous, sending off).
                    lock (cred) { if (g == gen) { login = userId = null; granted = Array.Empty<string>(); } }
                    Log.Warn("token expired and Twitch is unreachable; keeping the login to retry");
                    return false;
                }
                return false;   // rejected: RefreshToken logged out (if nothing newer happened meanwhile)
            }
            lock (cred) return login != null && token != null;
        }

        public enum RefreshResult { Ok, Rejected, Unreachable }

        async Task<RefreshResult> RefreshToken(string failed, int g)
        {
            await refreshGate.WaitAsync().ConfigureAwait(false);
            try
            {
                string? rt;
                lock (cred)
                {
                    if (g != gen || token != failed) return token != null ? RefreshResult.Ok : RefreshResult.Rejected;   // refreshed or logged out meanwhile
                    rt = refresh;
                    if (rt == null) { ClearLocked("token expired and there is no refresh token: logged out"); return RefreshResult.Rejected; }
                }
                var r = await Post(IdBase + "token", "client_id=" + ClientId + "&grant_type=refresh_token&refresh_token=" + Uri.EscapeDataString(rt)).ConfigureAwait(false);
                lock (cred)
                {
                    if (g != gen) return token != null ? RefreshResult.Ok : RefreshResult.Rejected;   // logged out meanwhile: do not bring the token back
                    if (r.Key == 200 && Json.S(r.Value, "access_token") != null) { TakeLocked(r.Value); Log.Info("token refreshed"); return RefreshResult.Ok; }
                    if (r.Key == 400 || r.Key == 401) { ClearLocked("refresh token rejected: logged out"); return RefreshResult.Rejected; }
                }
                return RefreshResult.Unreachable;   // network error, 5xx, 429: the refresh token may still be good
            }
            finally { refreshGate.Release(); }
        }

        /// <summary>GET helix/{query} with the user's token; refreshes on 401, waits once on 429. Null on failure.</summary>
        public async Task<object?> Helix(string query)
        {
            bool waited = false;
            for (int i = 0; i < 3; i++)
            {
                string? t; int g;
                lock (cred) { t = token; g = gen; }
                if (t == null) return null;
                try
                {
                    using (var rq = new HttpRequestMessage(HttpMethod.Get, HelixBase + query))
                    {
                        rq.Headers.Add("Client-Id", ClientId);
                        rq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", t);
                        using (var r = await http.SendAsync(rq).ConfigureAwait(false))
                        {
                            if ((int)r.StatusCode == 401) { if (await RefreshToken(t, g).ConfigureAwait(false) == RefreshResult.Ok) continue; return null; }
                            if ((int)r.StatusCode == 429 && !waited)
                            {   // rate limited: wait until Twitch's bucket refills (1-10 s), once
                                waited = true;
                                double s = r.Headers.TryGetValues("Ratelimit-Reset", out var v) && long.TryParse(v.FirstOrDefault(), out long reset)
                                    ? reset - now().ToUnixTimeSeconds() : 2;
                                s = Math.Max(1, Math.Min(10, s));
                                Log.Warn("Twitch API rate limit; waiting " + s + " s");
                                await delay(TimeSpan.FromSeconds(s)).ConfigureAwait(false);
                                continue;
                            }
                            if (!r.IsSuccessStatusCode) return null;
                            return await ReadJson(r).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception) { return null; }
            }
            return null;
        }

        /// <summary>JSON from the emote services: only their API hosts over https, at most MaxJsonBytes.</summary>
        public async Task<string?> Get(string url)
        {
            if (!SafeUrl.IsAllowedApi(url)) { Log.Warn("refused API request to an unknown host"); return null; }
            var b = await GetBytes(url, MaxJsonBytes).ConfigureAwait(false);
            return b == null ? null : Encoding.UTF8.GetString(b);
        }

        /// <summary>
        /// A download with a size limit and one deadline for headers AND body (HttpClient.Timeout alone does not
        /// cover a body that stalls). Null on any failure; images are checked by the caller.
        /// </summary>
        public async Task<byte[]?> GetBytes(string url, int max)
        {
            using (var cts = new CancellationTokenSource(DownloadTimeout))
            {
                try
                {
                    using (var r = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                    using (cts.Token.Register(() => { try { r.Dispose(); } catch (Exception) { } }))   // ends a read stuck in the socket
                    {
                        if (!r.IsSuccessStatusCode) return null;
                        long? len = r.Content.Headers.ContentLength;
                        if (len.HasValue && len.Value > max) return null;
                        using (var st = await r.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            return await Bounded.ReadAllAsync(st, max, cts.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception) { return null; }
            }
        }

        // ---- Device Code Grant Flow: show the code page, then poll until the user allows it ----
        public async Task<object?> StartDevice()
        {
            var r = await Post(IdBase + "device", "client_id=" + ClientId + "&scopes=" + Uri.EscapeDataString(Scopes)).ConfigureAwait(false);
            return r.Key == 200 ? r.Value : null;
        }

        /// <summary>"ok" when signed in; "authorization_pending" / "slow_down" = keep polling; anything else = failed.</summary>
        public async Task<string> PollDevice(string deviceCode)
        {
            var r = await Post(IdBase + "token", "client_id=" + ClientId + "&scopes=" + Uri.EscapeDataString(Scopes) +
                "&device_code=" + Uri.EscapeDataString(deviceCode) + "&grant_type=urn:ietf:params:oauth:grant-type:device_code").ConfigureAwait(false);
            if (r.Key == 200 && Json.S(r.Value, "access_token") != null)
            {
                lock (cred) TakeLocked(r.Value);
                return await Validate().ConfigureAwait(false) ? "ok" : "login failed";
            }
            if (r.Key == 0) return "authorization_pending";   // network hiccup: try again
            return Json.S(r.Value, "message") ?? ("error " + r.Key);
        }
    }
}
