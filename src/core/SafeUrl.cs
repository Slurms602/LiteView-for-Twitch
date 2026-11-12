// Which URLs LiteView may open, navigate to, or download from. Every URL here comes from outside
// (chat messages, web pages, emote services, Twitch's API) and is checked before use.
using System;
using System.Text.RegularExpressions;

namespace LiteViewCore
{
    public static class SafeUrl
    {
        /// <summary>Hosts that serve emote, badge and avatar images (and their subdomains).</summary>
        public static readonly string[] ImageHosts = { "jtvnw.net", "7tv.app", "betterttv.net", "frankerfacez.com" };
        /// <summary>Hosts that serve the emote lists (JSON APIs).</summary>
        public static readonly string[] ApiHosts = { "api.twitch.tv", "id.twitch.tv", "7tv.io", "api.betterttv.net", "api.frankerfacez.com" };

        /// <summary>host is domain itself or a subdomain of it ("cdn.7tv.app" matches "7tv.app", "evil7tv.app" does not).</summary>
        public static bool HostIs(string? host, string domain)
        {
            if (string.IsNullOrEmpty(host)) return false;
            host = host!.TrimEnd('.').ToLowerInvariant();
            return host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);
        }

        public static bool IsTwitchHost(string? host) { return HostIs(host, "twitch.tv"); }

        /// <summary>An absolute http(s) URL with a host and no user name/password part.</summary>
        public static bool IsWebUrl(Uri? u)
        {
            return u != null && u.IsAbsoluteUri && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)
                && !string.IsNullOrEmpty(u.Host) && string.IsNullOrEmpty(u.UserInfo);
        }

        /// <summary>The URL to hand to the system browser, or null when it must not be opened (any other scheme).</summary>
        public static Uri? ForBrowser(string? s)
        {
            if (string.IsNullOrWhiteSpace(s) || s!.Length > 4096) return null;
            return Uri.TryCreate(s.Trim(), UriKind.Absolute, out var u) && IsWebUrl(u) ? u : null;
        }

        static readonly Regex Scheme = new Regex("^[a-z][a-z0-9+.-]*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// A link written in chat ("https://x.com/a", "www.x.com", "x.tv/abc") as an http(s) URL, or null.
        /// Text with any other scheme ("file:", "ms-settings:", "javascript:") is never a link.
        /// </summary>
        public static Uri? FromChatWord(string? word)
        {
            if (string.IsNullOrEmpty(word) || word!.Length > 2048) return null;
            string w = word;
            bool hasScheme = w.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || w.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            if (!hasScheme)
            {
                // "host:port/x" would parse as scheme "host"; only bare host names get https:// added
                if (Scheme.IsMatch(w) && !Regex.IsMatch(w, "^[a-z0-9.-]+:[0-9]{1,5}(/|$)", RegexOptions.IgnoreCase)) return null;
                w = "https://" + w;
            }
            if (!Uri.TryCreate(w, UriKind.Absolute, out var u) || !IsWebUrl(u)) return null;
            if (u.Host.IndexOf('.') < 0) return null;
            return u;
        }

        /// <summary>An image we may download: https, from an emote/badge/avatar host.</summary>
        public static bool IsAllowedImage(string? url)
        {
            if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
            if (u.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(u.UserInfo)) return false;
            foreach (var h in ImageHosts) if (HostIs(u.Host, h)) return true;
            return false;
        }

        /// <summary>A JSON API URL we may fetch: https, from Twitch or an emote service.</summary>
        public static bool IsAllowedApi(string? url)
        {
            if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
            if (u.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(u.UserInfo)) return false;
            foreach (var h in ApiHosts) if (u.Host.Equals(h, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public enum Nav { Allow, OpenInBrowser, Block }

        /// <summary>
        /// What the embedded Twitch page may do when it navigates its main frame: stay on https Twitch pages,
        /// send ordinary web links to the system browser, block everything else (file:, custom protocols...).
        /// </summary>
        public static Nav ForWebView(string? url)
        {
            if (string.IsNullOrEmpty(url)) return Nav.Block;
            if (url == "about:blank") return Nav.Allow;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return Nav.Block;
            if (u.Scheme == Uri.UriSchemeHttps && IsTwitchHost(u.Host) && string.IsNullOrEmpty(u.UserInfo)) return Nav.Allow;
            return IsWebUrl(u) ? Nav.OpenInBrowser : Nav.Block;
        }

        /// <summary>Twitch's device-activation page from the device-code response, or the standard one.</summary>
        public static Uri ActivationPage(string? verificationUri)
        {
            var u = ForBrowser(verificationUri);
            if (u != null && u.Scheme == Uri.UriSchemeHttps && IsTwitchHost(u.Host)) return u;
            return new Uri("https://www.twitch.tv/activate");
        }
    }
}
