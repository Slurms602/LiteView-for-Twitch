// Twitch's official Helix API for LiteView: device-code login (public client, no secret),
// followed live channels, avatars and chat badges. The token stays next to the exe,
// encrypted for this Windows user (DPAPI); it is never sent anywhere but Twitch.
// The logic lives in src/core/TwitchClient.cs (tested); `Tw` keeps upstream's static API for the rest of the app.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using LiteViewCore;

static class Tw
{
    // "LiteView for Twitch" on dev.twitch.tv (client type Public: no secret exists for it).
    // This is UPSTREAM's registered application. Before this fork is released publicly it needs its own
    // registration (dev.twitch.tv > Applications, client type Public, device code flow) - see docs/KNOWN_ISSUES.md.
    public const string ClientId = "i82nikwku19tn6wk8ms6z7hr9l57wo";
    public static readonly HttpClient Http = new HttpClient();
    // Set by Init. Before that nobody is logged in: the chat panel and the sidebar are built (and ask) before the
    // window's constructor calls Init, as in upstream, where these were plain static fields.
    static volatile TwitchClient api;
    static TwitchClient Api { get { var a = api; if (a == null) throw new InvalidOperationException("Tw.Init has not run"); return a; } }

    public static void Init(string tokenFile)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        Http.Timeout = TimeSpan.FromSeconds(20);
        Http.MaxResponseContentBufferSize = Bounded.MaxJsonBytes;   // upstream: the 2 GB default
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("LiteView-Enhanced/" + typeof(Tw).Assembly.GetName().Version);
        api = new TwitchClient(ClientId, Http, new DpapiTokenFile(tokenFile));
        api.Load();
    }

    public static string Token { get { var a = api; return a != null ? a.Token : null; } }
    public static string Login { get { var a = api; return a != null ? a.Login : null; } }
    public static string UserId { get { var a = api; return a != null ? a.UserId : null; } }
    public static string[] Granted { get { var a = api; return a != null ? a.Granted : new string[0]; } }   // scopes of the current token
    public static bool Has(string scope) { var a = api; return a != null && a.Has(scope); }
    public static void Session(out string login, out string token) { var a = api; if (a != null) a.Session(out login, out token); else login = token = null; }

    public static void LogOut() { var a = api; if (a != null) a.LogOut(); }
    public static Task<bool> Validate() { return Api.Validate(); }
    public static Task<object> Helix(string query) { return Api.Helix(query); }
    public static Task<string> Get(string url) { return Api.Get(url); }
    public static Task<byte[]> GetBytes(string url, int max) { return Api.GetBytes(url, max); }
    public static Task<object> StartDevice() { return Api.StartDevice(); }
    public static Task<string> PollDevice(string deviceCode) { return Api.PollDevice(deviceCode); }

    // ---- JSON helpers (objects = Dictionary, arrays = object[], as with upstream's JavaScriptSerializer) ----
    public static object Parse(string s) { return Json.Parse(s); }
    public static object V(object o, string k) { return Json.V(o, k); }
    public static string S(object o, string k) { return Json.S(o, k); }
    public static long N(object o, string k) { return Json.N(o, k); }
    public static bool B(object o, string k) { return Json.B(o, k); }
    public static object[] A(object o, string k) { return Json.A(o, k); }
    public static IEnumerable<KeyValuePair<string, object>> Props(object o) { return Json.Props(o); }
}

// liteview.token: "token\nrefresh" encrypted with DPAPI for the current Windows user, written atomically.
sealed class DpapiTokenFile : ITokenStore
{
    readonly string path;
    public DpapiTokenFile(string path) { this.path = path; }

    public bool TryLoad(out string token, out string refresh)
    {
        token = refresh = null;
        if (!File.Exists(path)) return false;
        var s = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser)).Split('\n');
        token = s[0].Length > 0 ? s[0] : null;
        refresh = s.Length > 1 && s[1].Length > 0 ? s[1] : null;
        return token != null;
    }

    public void Save(string token, string refresh)
    {
        AtomicFile.Write(path, ProtectedData.Protect(Encoding.UTF8.GetBytes(token + "\n" + refresh), null, DataProtectionScope.CurrentUser), false);
    }

    public void Delete() { AtomicFile.Delete(path); }
}
