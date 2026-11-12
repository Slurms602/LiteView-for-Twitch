// The one place LiteView opens links: only http(s) URLs, handed to the default browser.
// Upstream passed chat words and web-page URIs straight to Process.Start, which also opens files,
// programs and any registered protocol (file:, ms-settings:, search-ms:...).
using System;
using System.Diagnostics;
using LiteViewCore;

static class Launcher
{
    public static bool Open(string url)
    {
        var u = SafeUrl.ForBrowser(url);
        if (u == null) { Log.Warn("refused to open a link (" + SchemeOf(url) + ")"); return false; }
        return Open(u);
    }

    public static bool Open(Uri u)
    {
        if (!SafeUrl.IsWebUrl(u)) { Log.Warn("refused to open a link (" + (u == null ? "null" : u.Scheme) + ")"); return false; }
        try
        {
            using (Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true })) { }
            return true;
        }
        catch (Exception ex) { Log.Error("opening a link", ex); return false; }
    }

    // for log lines: the scheme only, never the whole URL
    public static string SchemeOf(string url)
    {
        if (string.IsNullOrEmpty(url)) return "empty";
        int c = url.IndexOf(':');
        return c > 0 && c < 32 ? url.Substring(0, c).ToLowerInvariant() + ":" : "no scheme";
    }
}
