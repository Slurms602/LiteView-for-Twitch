// Settings of the extra windows (right-click a channel > Watch in a new window; src/app/ExtraPlayer.cs), shared by all
// of them and kept in liteview.ini: on top of other windows, with the chat, the window keeping the video's shape (no
// black bars), the quality, the volume. The owner's choices as defaults: on top (picture in picture), 720p60.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace LiteViewCore
{
    public sealed class PopOptions
    {
        /// <summary>The quality choices of an extra window ("main": the main window's quality box).</summary>
        public static readonly string[] Qualities = { "main", "Best", "1080p60", "720p60", "480p", "360p" };
        public const string DefaultQuality = "720p60";
        public const int DefaultVolume = 60;

        public bool OnTop = true, Chat, Fit;
        public string Quality = DefaultQuality;
        public int Volume = DefaultVolume;

        public static string QualityName(string q) { return q == "main" ? "Same as the main window" : q == "Best" ? "Best (source)" : q; }

        /// <summary>The Streamlink choice for a new window (main: the main window's, if it is one LiteView knows).</summary>
        public string QualityFor(string? mainQuality)
        {
            if (Quality != "main") return Quality;
            return mainQuality != null && (Array.IndexOf(LiteViewCore.Quality.Choices, mainQuality) >= 0 || LiteViewCore.Quality.IsVariant(mainQuality)) ? mainQuality : DefaultQuality;
        }

        public IEnumerable<KeyValuePair<string, string>> Save()
        {
            yield return new KeyValuePair<string, string>("popontop", OnTop ? "1" : "0");
            yield return new KeyValuePair<string, string>("popchat", Chat ? "1" : "0");
            yield return new KeyValuePair<string, string>("popfit", Fit ? "1" : "0");
            yield return new KeyValuePair<string, string>("popquality", Quality);
            yield return new KeyValuePair<string, string>("popvolume", Volume.ToString(CultureInfo.InvariantCulture));
        }

        public static PopOptions Load(Func<string, string, string> get)
        {
            var o = new PopOptions
            {
                OnTop = get("popontop", "1") != "0",
                Chat = get("popchat", "0") == "1",
                Fit = get("popfit", "0") == "1",
            };
            string q = get("popquality", DefaultQuality);
            o.Quality = Array.IndexOf(Qualities, q) >= 0 ? q : DefaultQuality;
            int v;
            o.Volume = int.TryParse(get("popvolume", DefaultVolume.ToString(CultureInfo.InvariantCulture)), NumberStyles.None, CultureInfo.InvariantCulture, out v) ? Math.Min(100, v) : DefaultVolume;
            return o;
        }
    }
}
