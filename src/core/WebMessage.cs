// Messages from the embedded Twitch page to LiteView. Only one command exists:
//   "watch:<session secret>:<login>"
// sent by our own click script and accepted only from an https twitch.tv page. The secret is created
// at start-up and lives only inside our injected script's closure, so other scripts running on the
// page (ads, trackers) cannot make LiteView switch streams by posting messages of their own.
using System;
using System.Security.Cryptography;

namespace LiteViewCore
{
    public static class WebMessage
    {
        public const string WatchPrefix = "watch:";

        /// <summary>A new random secret (32 hex characters) for this run of the app.</summary>
        public static string NewSecret()
        {
            var b = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>The channel to watch, or null when the message, its secret or its sender is not acceptable.</summary>
        public static string? Watch(string? message, string? sourceUrl, string secret)
        {
            if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(message) || message!.Length > 128) return null;
            if (sourceUrl == null || !Uri.TryCreate(sourceUrl, UriKind.Absolute, out var src)) return null;
            if (src.Scheme != Uri.UriSchemeHttps || !SafeUrl.IsTwitchHost(src.Host)) return null;
            string head = WatchPrefix + secret + ":";
            if (!message.StartsWith(head, StringComparison.Ordinal)) return null;
            string name = message.Substring(head.Length);
            return ChannelName.IsValidLogin(name) ? name : null;
        }
    }
}
