// Random and mutated input for every parser that reads something from outside (Twitch chat, JSON answers, images,
// links, Streamlink's log, settings files). Each test checks that nothing throws and that the result keeps the
// promises the rest of the app relies on (no CR/LF in IRC text, only allowed hosts, bounded sizes...).
// Seeds are fixed, so a failure always reproduces; the seed and the input are in the failure message.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LiteViewCore;
using Xunit;

public class FuzzTests
{
    const int Runs = 4000;

    static string Rand(Random r, string alphabet, int maxLen)
    {
        int n = r.Next(maxLen + 1);
        var sb = new StringBuilder(n);
        for (int i = 0; i < n; i++)
        {
            int k = r.Next(20);
            if (k == 0) sb.Append((char)r.Next(0, 0x20));                       // control characters
            else if (k == 1) sb.Append((char)r.Next(0xD800, 0xE000));            // lone surrogates
            else if (k == 2) sb.Append((char)r.Next(0x80, 0xFFFF));
            else sb.Append(alphabet[r.Next(alphabet.Length)]);
        }
        return sb.ToString();
    }

    // small edits of a valid input: the way real-world breakage looks (truncated, doubled, a byte changed)
    static string Mutate(Random r, string s, string alphabet)
    {
        var sb = new StringBuilder(s);
        int edits = 1 + r.Next(4);
        for (int e = 0; e < edits; e++)
        {
            int at = sb.Length == 0 ? 0 : r.Next(sb.Length);
            switch (r.Next(6))
            {
                case 0: if (sb.Length > 0) sb.Remove(at, Math.Min(sb.Length - at, 1 + r.Next(8))); break;
                case 1: sb.Insert(at, alphabet[r.Next(alphabet.Length)]); break;
                case 2: if (sb.Length > 0) sb[at] = alphabet[r.Next(alphabet.Length)]; break;
                case 3: sb.Length = at; break;
                case 4: if (sb.Length > 0) { int len = Math.Min(sb.Length - at, 1 + r.Next(20)); sb.Insert(at, sb.ToString(at, len)); } break;
                default: sb.Insert(at, (char)r.Next(0, 0x10000)); break;
            }
        }
        return sb.ToString();
    }

    static void Each(int seed, Func<Random, string> make, Action<string> check)
    {
        var r = new Random(seed);
        for (int i = 0; i < Runs; i++)
        {
            string input = make(r);
            try { check(input); }
            catch (Exception ex) { throw new Exception("seed " + seed + " run " + i + " input " + Show(input) + ": " + ex.Message, ex); }
        }
    }

    static string Show(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s.Length > 300 ? s.Substring(0, 300) : s) sb.Append(c < ' ' || c > '~' ? "\\u" + ((int)c).ToString("x4") : c.ToString());
        return sb.Append('"').ToString();
    }

    // ---------- JSON (Twitch, 7TV, BTTV, FFZ, the chat history service, GitHub) ----------

    const string JsonChars = "{}[]\",:\\/ 0123456789.eE+-truefalsnul\tab\n";
    static readonly string[] JsonSamples =
    {
        "{\"data\":[{\"user_login\":\"a_b\",\"viewer_count\":12,\"started_at\":\"2026-11-13T10:00:00Z\",\"thumbnail_url\":\"https://static-cdn.jtvnw.net/x-{width}x{height}.jpg\"}],\"pagination\":{\"cursor\":\"eyJiIjp7\"}}",
        "{\"messages\":[\"@id=1;tmi-sent-ts=1 :a!a@a PRIVMSG #chan :hi \\\"there\\\" \\u00e9\\ud83d\\ude00\"],\"error\":null}",
        "[1,-2.5e3,true,false,null,\"\\n\\t\\\\\",{},[],{\"a\":{\"b\":[0.1]}}]",
        "{\"emote_set\":{\"emotes\":[{\"id\":\"60ae\",\"name\":\"Pog\",\"data\":{\"host\":{\"url\":\"//cdn.7tv.app/emote/60ae\",\"files\":[{\"name\":\"1x.webp\",\"width\":32,\"height\":32}]}}}]}}",
    };

    [Fact]
    public void JsonNeverThrows()
    {
        Each(11, r => r.Next(3) == 0 ? Rand(r, JsonChars, 200) : Mutate(r, JsonSamples[r.Next(JsonSamples.Length)], JsonChars), s =>
        {
            var v = Json.Parse(s);
            // the accessors take whatever came out
            Json.S(v, "data"); Json.N(v, "viewer_count"); Json.B(v, "x"); Json.A(v, "messages"); foreach (var _ in Json.Props(v)) { }
        });
    }

    [Theory]
    [InlineData('[', ']')]
    [InlineData('{', '}')]
    public void DeepJsonIsRefusedWithoutRunningOutOfStack(char open, char close)
    {
        // a stack overflow cannot be caught and would end the app: depth is limited instead
        string unit = open == '[' ? "[" : "{\"a\":";
        string deep = string.Concat(Enumerable.Repeat(unit, 200000)) + "1" + new string(close, 200000);
        Assert.Null(Json.Parse(deep));
        string ok = string.Concat(Enumerable.Repeat(unit, Json.MaxDepth)) + "1" + new string(close, Json.MaxDepth);
        Assert.NotNull(Json.Parse(ok));
    }

    // ---------- IRC (Twitch chat and the chat history) ----------

    const string IrcChars = "@;=\\: !#abcdefnrs0123456789-_/,.PRIVMSGUSERNOTICE\u0001";
    static readonly string[] IrcSamples =
    {
        "@badge-info=;badges=moderator/1;color=#FF0000;display-name=Some\\sOne;emotes=25:0-4,12-16/1902:6-10;id=abc-1;tmi-sent-ts=1700000000000 :someone!someone@someone.tmi.twitch.tv PRIVMSG #chan :Kappa Keepo Kappa",
        "@msg-id=sub;system-msg=a\\sb\\:c\\\\d\\r\\n :tmi.twitch.tv USERNOTICE #chan :yay",
        ":tmi.twitch.tv CLEARCHAT #chan :user",
        "@target-msg-id=xyz :tmi.twitch.tv CLEARMSG #chan :text",
        "PING :tmi.twitch.tv",
        "@id=1 :a!a@a PRIVMSG #chan word",
    };

    [Fact]
    public void IrcLinesNeverThrow()
    {
        Each(12, r => r.Next(3) == 0 ? Rand(r, IrcChars, 300) : Mutate(r, IrcSamples[r.Next(IrcSamples.Length)], IrcChars), s =>
        {
            var m = IrcMsg.Parse(s);
            if (m != null)
            {
                Assert.False(string.IsNullOrEmpty(m.Cmd));
                Assert.DoesNotContain(' ', m.Cmd);
                foreach (var kv in m.Tags) { Assert.DoesNotContain(';', kv.Key); Assert.DoesNotContain(' ', kv.Key); }
                _ = m.Nick; _ = m.Tag("id");
            }
            IrcMsg.Unescape(s);
            IrcProtocol.PongFor(s);
            foreach (var _ in IrcProtocol.Lines(s)) { }
        });
    }

    [Fact]
    public void OverlongIrcLinesAreNotParsed()
    {
        Assert.Null(IrcMsg.Parse("@a=" + new string('x', IrcProtocol.MaxLineChars) + " PRIVMSG #c :x"));
    }

    [Fact]
    public void PongNeverCarriesASecondLine()
    {
        Each(13, r => "PING" + Rand(r, " :abc\r\n", 40), s =>
        {
            string? pong = IrcProtocol.PongFor(s);
            if (pong != null) Assert.StartsWith("PONG", pong);
        });
    }

    [Fact]
    public void OutgoingChatCanNotInjectCommands()
    {
        Each(14, r => r.Next(4) == 0 ? "\u0001ACTION " + Rand(r, "ab \u0001\r\n", 600) + "\u0001" : Rand(r, "ab \u0001\r\n\u2028\u2029:", 700), s =>
        {
            string t = IrcProtocol.CleanOutgoing(s);
            Assert.True(t.Length <= IrcProtocol.MaxMessageChars, "too long: " + t.Length);
            foreach (char c in t) Assert.True(c == '\u0001' || !char.IsControl(c) && c != '\u2028' && c != '\u2029', "control character " + (int)c);
            if (t.Length > 0) Assert.False(char.IsHighSurrogate(t[t.Length - 1]), "half a character at the end");
            if (t.IndexOf('\u0001') >= 0)
            {   // \u0001 only as a complete ACTION wrapper
                Assert.StartsWith("\u0001ACTION ", t);
                Assert.EndsWith("\u0001", t);
                Assert.Equal(2, t.Count(c => c == '\u0001'));
            }
        });
    }

    [Fact]
    public void LongActionsKeepTheirClosingMark()
    {
        string t = IrcProtocol.CleanOutgoing("\u0001ACTION " + new string('a', 600) + "\u0001");
        Assert.Equal(IrcProtocol.MaxMessageChars, t.Length);
        Assert.StartsWith("\u0001ACTION a", t);
        Assert.EndsWith("a\u0001", t);
    }

    [Fact]
    public void DisplayTextIsBounded()
    {
        Each(15, r => Rand(r, "a\ud83d", 2200), s =>
        {
            string t = IrcProtocol.ForDisplay(s);
            Assert.True(t.Length <= IrcProtocol.MaxShownChars + 1);
        });
    }

    [Fact]
    public void ChatHistoryKeepsOnlyChatLinesOfTheChannel()
    {
        Each(16, r =>
        {
            var lines = new List<string>();
            int n = r.Next(80);
            for (int i = 0; i < n; i++) lines.Add(Mutate(r, IrcSamples[r.Next(IrcSamples.Length)], IrcChars));
            var json = new StringBuilder("{\"messages\":[");
            for (int i = 0; i < lines.Count; i++) json.Append(i > 0 ? "," : "").Append(JsonString(lines[i]));
            if (r.Next(5) == 0) json.Append(",1,null,{}");
            json.Append("]}");
            return r.Next(6) == 0 ? Mutate(r, json.ToString(), JsonChars) : json.ToString();
        }, json =>
        {
            var keep = RecentMessages.Lines(json, "chan");
            Assert.True(keep.Count <= RecentMessages.Limit);
            foreach (var l in keep)
            {
                Assert.True(l.IndexOfAny(new[] { '\r', '\n', '\0' }) < 0);
                var m = IrcMsg.Parse(l);
                Assert.NotNull(m);
                Assert.Contains(m!.Cmd, new[] { "PRIVMSG", "USERNOTICE" });
                Assert.Equal("chan", m.Chan, StringComparer.OrdinalIgnoreCase);
            }
        });
    }

    static string JsonString(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    // ---------- images (emotes, badges, avatars, thumbnails) ----------

    [Fact]
    public void ImageHeadersNeverThrow()
    {
        var heads = new[]
        {
            new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 0, 28, 0, 0, 0, 28 },
            new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 28, 0, 28, 0, 0, 0 },
            new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0, 0xFF, 0xC0, 0, 11, 8, 0, 28, 0, 28, 1, 1, 0x11, 0 },
        };
        var r = new Random(17);
        for (int i = 0; i < Runs * 2; i++)
        {
            byte[] b;
            if (r.Next(3) == 0) { b = new byte[r.Next(64)]; r.NextBytes(b); }
            else
            {
                var h = heads[r.Next(heads.Length)];
                b = new byte[Math.Max(0, h.Length + r.Next(-h.Length, 16))];
                Array.Copy(h, b, Math.Min(h.Length, b.Length));
                for (int k = r.Next(4); k > 0 && b.Length > 0; k--) b[r.Next(b.Length)] = (byte)r.Next(256);
            }
            ImageSniff.Kind(b);
            if (ImageSniff.TrySize(b, out int w, out int hh)) Assert.True(w > 0 && hh > 0);
            if (ImageSniff.Acceptable(b))
            {
                Assert.True(ImageSniff.TrySize(b, out w, out hh));
                Assert.True(EmoteLimits.SourceOk(w, hh));
            }
        }
        Assert.False(ImageSniff.Acceptable(null!));
    }

    [Fact]
    public void EmoteSizesStayInBounds()
    {
        var r = new Random(18);
        int[] edge = { int.MinValue, -1, 0, 1, 2, 27, 28, 32, 33, 4096, 65535, int.MaxValue };
        int Pick() => r.Next(3) == 0 ? edge[r.Next(edge.Length)] : r.Next(-5, 5000);
        for (int i = 0; i < Runs; i++)
        {
            int w = Pick(), h = Pick(), fixedH = r.Next(2) == 0 ? 0 : r.Next(1, 64);
            EmoteLimits.Fit(w, h, fixedH, out int ow, out int oh);
            Assert.True(ow >= 1 && oh >= 1, w + "x" + h);
            Assert.True(oh <= Math.Max(32, fixedH) || w <= 0 || h <= 0, "height " + oh);
            Assert.True(ow <= Math.Max(32, fixedH) * EmoteLimits.MaxAspect, "width " + ow);   // a wider one is scaled down whole

            int frames = Pick(), keep = EmoteLimits.FramesToKeep(frames, Math.Abs(w % 3000), Math.Abs(h % 3000));
            Assert.True(keep >= 1 && keep <= Math.Max(1, Math.Min(frames, EmoteLimits.MaxFrames)));
            if (frames > 0 && frames < 100000)
            {
                var plan = EmoteLimits.FramePlan(frames, keep);
                Assert.True(plan.Length >= 1 && plan.Length <= frames);
                for (int k = 1; k < plan.Length; k++) Assert.True(plan[k] > plan[k - 1]);
                var delays = new int[frames];
                for (int k = 0; k < delays.Length; k++) delays[k] = Pick();
                var merged = EmoteLimits.MergeDelays(delays, plan);
                Assert.Equal(plan.Length, merged.Length);
                Assert.All(merged, d => Assert.True(d >= 20));
            }
        }
    }

    // ---------- links (chat words, web pages, Twitch answers) ----------

    const string UrlChars = "abcxyz019.-_~:/?#[]@!$&'()*+,;=%\\ \t\u3002\uff0e\u00e9";
    static readonly string[] UrlSamples =
    {
        "https://www.twitch.tv/somechannel", "http://example.com/a?b=c#d", "www.example.com", "example.tv/abc", "file:///C:/Windows/notepad.exe",
        "ms-settings:privacy", "javascript:alert(1)", "https://user:pw@evil.com/", "https://static-cdn.jtvnw.net/emoticons/v2/25/default/dark/1.0",
        "https://cdn.7tv.app/emote/x/1x.webp", "https://evil7tv.app/x.png", "https://7tv.app.evil.com/x.png", "http://cdn.7tv.app/x.png",
        "localhost:8080/x", "https://api.twitch.tv/helix/streams", "https://recent-messages.robotty.de/api/v2/recent-messages/x", "\\\\server\\share",
        "https://twitch.tv.evil.com/", "https://www.twitch.tv./x", "HTTPS://WWW.TWITCH.TV/X",
    };

    [Fact]
    public void LinksAreOnlyEverWebUrls()
    {
        Each(19, r => r.Next(3) == 0 ? Rand(r, UrlChars, 80) : Mutate(r, UrlSamples[r.Next(UrlSamples.Length)], UrlChars), s =>
        {
            var b = SafeUrl.ForBrowser(s);
            if (b != null) { Assert.True(SafeUrl.IsWebUrl(b)); Assert.Empty(b.UserInfo); }
            var c = SafeUrl.FromChatWord(s);
            if (c != null) { Assert.Contains(c.Scheme, new[] { "http", "https" }); Assert.Empty(c.UserInfo); Assert.Contains('.', c.Host); }
            if (SafeUrl.IsAllowedImage(s))
            {
                var u = new Uri(s);
                Assert.Equal("https", u.Scheme);
                Assert.Contains(SafeUrl.ImageHosts, h => SafeUrl.HostIs(u.Host, h));
            }
            if (SafeUrl.IsAllowedApi(s))
            {
                var u = new Uri(s);
                Assert.Equal("https", u.Scheme);
                Assert.Contains(SafeUrl.ApiHosts, h => string.Equals(u.Host, h, StringComparison.OrdinalIgnoreCase));
            }
            var nav = SafeUrl.ForWebView(s);
            if (nav == SafeUrl.Nav.Allow && s != "about:blank") Assert.True(SafeUrl.IsTwitchHost(new Uri(s).Host));
            var act = SafeUrl.ActivationPage(s);
            Assert.Equal("https", act.Scheme);
            Assert.True(SafeUrl.IsTwitchHost(act.Host));
        });
    }

    [Fact]
    public void ChannelNamesAreAlwaysValidLogins()
    {
        Each(20, r => r.Next(2) == 0 ? Rand(r, "abcXYZ_09@/:.?# twitch.tvpopoutembed", 60) : Mutate(r, UrlSamples[r.Next(UrlSamples.Length)], UrlChars), s =>
        {
            string? n = ChannelName.Normalize(s);
            if (n != null) Assert.True(ChannelName.IsValidLogin(n), n);
            string? w = WebMessage.Watch(s, "https://www.twitch.tv/directory/following", "secret");
            if (w != null) Assert.True(ChannelName.IsValidLogin(w));
            Assert.Null(WebMessage.Watch("watch:secret:" + s, "https://evil.com/", "secret"));
        });
    }

    // ---------- Twitch API answers ----------

    [Fact]
    public void HelixAnswersGiveCleanRecords()
    {
        Each(21, r => r.Next(3) == 0 ? Rand(r, JsonChars, 200) : Mutate(r, JsonSamples[0], JsonChars), s =>
        {
            var j = Json.Parse(s);
            foreach (var st in HelixData.Streams(j))
            {
                Assert.True(ChannelName.IsValidLogin(st.Login), st.Login);
                Assert.True(st.Thumbnail.Length == 0 || SafeUrl.IsAllowedImage(HelixData.Thumb(st.Thumbnail, 320, 180)) || HelixData.Thumb(st.Thumbnail, 320, 180).Length == 0);
                Assert.DoesNotContain(st.Title, c => char.IsControl(c));
            }
            foreach (var c in HelixData.Followed(j).Concat(HelixData.Search(j))) Assert.True(ChannelName.IsValidLogin(c.Login), c.Login);
            foreach (var u in HelixData.Users(j).Values) { Assert.True(ChannelName.IsValidLogin(u.Login)); Assert.True(u.Avatar.Length == 0 || SafeUrl.IsAllowedImage(u.Avatar)); }
            HelixData.Cursor(j);
        });
    }

    [Fact]
    public void DisplayTextHasNoControlOrDirectionCharacters()
    {
        Each(22, r => Rand(r, "ab \u202a\u202b\u202c\u202d\u202e\u2066\u2067\u2068\u2069\ud83d\ude00", 600), s =>
        {
            string t = HelixData.Text(s);
            Assert.DoesNotContain(t, c => char.IsControl(c) || c >= '\u202A' && c <= '\u202E' || c >= '\u2066' && c <= '\u2069');
            if (t.Length > 0) Assert.False(char.IsHighSurrogate(t[t.Length - 1]));
        });
    }

    // ---------- Streamlink's log, settings files, preferences ----------

    [Fact]
    public void StreamlinkLogNeverGivesUnsafeQualities()
    {
        Each(23, r =>
        {
            var parts = new List<string>();
            for (int n = r.Next(12); n > 0; n--) parts.Add(r.Next(3) == 0 ? Rand(r, "0123456789p_audio_only (best)(worst) -;&|\"", 14) : new[] { "160p", "360p30", "720p60", "1080p60 (best)", "audio_only", "0p", "99999p", "720p60;calc" }[r.Next(8)]);
            return "[cli][info] Available streams: " + string.Join(", ", parts);
        }, line =>
        {
            var lines = new[] { "[cli][info] Found matching plugin twitch", line, "[plugins.twitch][error] " + line };
            foreach (var q in Quality.Available(lines))
                Assert.True(q == "audio_only" || Quality.IsVariant(q), q);
            foreach (var c in Quality.ChoicesFor(Quality.Available(lines)))
            {
                string sel = Quality.Selector(c);
                Assert.Matches("^[a-z0-9_,]+$", sel);
            }
            StreamOutcome.LastError(lines); StreamOutcome.Waiting(lines);
        });
    }

    [Fact]
    public void SettingsFilesOnlyGiveKnownSingleLineValues()
    {
        Each(24, r =>
        {
            var sb = new StringBuilder();
            for (int n = r.Next(30); n > 0; n--)
            {
                string key = r.Next(2) == 0 ? SettingsImport.Keys.ElementAt(r.Next(SettingsImport.Keys.Count)) : Rand(r, "abc=[] ;#", 10);
                sb.Append(key).Append(r.Next(5) == 0 ? "" : "=").Append(Rand(r, "abc=1,0 \u0000", 30)).Append(r.Next(2) == 0 ? "\n" : "\r\n");
            }
            return sb.ToString();
        }, text =>
        {
            var parsed = IniStore.Parse(text.Split('\n'));
            var d = SettingsImport.Filter(parsed);
            foreach (var kv in d)
            {
                Assert.Contains(kv.Key, SettingsImport.Keys);
                Assert.True(kv.Value.IndexOfAny(new[] { '\r', '\n', '\0' }) < 0);
            }
            IniStore.GetInt(parsed, "volume", 50, 0, 100);
        });
    }

    [Fact]
    public void ChatPreferencesTakeAnyText()
    {
        var f = new ChatFilter();
        Each(25, r => Rand(r, "ab@,\n \t;()[]*+?\\.|^$", 300), s =>
        {
            f.SetWords(s); f.SetIgnored(s);
            Assert.True(f.Words.Count <= ChatFilter.MaxWords);
            Assert.All(f.Words, w => Assert.True(w.Length > 0 && w.Length <= ChatFilter.MaxWordLength));
            Assert.All(f.Ignored, n => Assert.True(ChannelName.IsValidLogin(n)));
            f.Highlights(s); f.IsIgnored(s);
            ChatFilter.Stamp(s); QuietHours.IsQuiet(s, DateTime.Now); Versions.Parse(s); Versions.IsNewer(s, "1.2.0");
            SearchText.Clean(s);
            new ChannelList(10, true).Load(s);
        });
    }
}
