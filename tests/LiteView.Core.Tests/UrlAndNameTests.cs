using LiteViewCore;
using Xunit;

public class ChannelNameTests
{
    [Theory]
    [InlineData("xQc", "xqc")]
    [InlineData("  shroud ", "shroud")]
    [InlineData("@pokimane", "pokimane")]
    [InlineData("twitch.tv/xqc", "xqc")]
    [InlineData("https://www.twitch.tv/xqc", "xqc")]
    [InlineData("https://www.twitch.tv/xqc/videos", "xqc")]          // upstream turned this into "xqcvideos"
    [InlineData("https://m.twitch.tv/xqc?referrer=raid", "xqc")]     // upstream: "xqcreferrerraid"
    [InlineData("https://www.twitch.tv/popout/xqc/chat?popout=", "xqc")]
    [InlineData("www.twitch.tv/some_user_123#x", "some_user_123")]
    [InlineData("xqc and more", "xqc")]
    public void Normalizes(string raw, string expected)
    {
        Assert.Equal(expected, ChannelName.Normalize(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad-name")]
    [InlineData("toolong_toolong_toolong_xx")]
    [InlineData("twitch.tv/directory")]
    [InlineData("https://www.twitch.tv/")]
    [InlineData("ünïcode")]
    [InlineData("a\"b")]
    public void RejectsWhatIsNotAChannel(string? raw)
    {
        Assert.Null(ChannelName.Normalize(raw));
    }

    [Fact]
    public void Ids()
    {
        Assert.True(ChannelName.IsNumericId("71092938"));
        Assert.False(ChannelName.IsNumericId("1/../../users"));
        Assert.False(ChannelName.IsNumericId("12a"));
        Assert.False(ChannelName.IsNumericId(""));
        Assert.False(ChannelName.IsNumericId(null));
    }
}

public class SafeUrlTests
{
    [Theory]
    [InlineData("https://example.com/a?b=c", "https://example.com/a?b=c")]
    [InlineData("HTTP://Example.com", "http://example.com/")]
    [InlineData("www.example.com", "https://www.example.com/")]
    [InlineData("clips.twitch.tv/Abc", "https://clips.twitch.tv/Abc")]
    [InlineData("example.com:8080/x", "https://example.com:8080/x")]
    public void ChatLinksBecomeWebUrls(string word, string expected)
    {
        Assert.Equal(expected, SafeUrl.FromChatWord(word)!.AbsoluteUri);
    }

    [Theory]
    [InlineData("http.com")]                 // upstream passed this to ShellExecute as a file name (.com runs!)
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("ms-settings:privacy")]
    [InlineData("search-ms:query=x")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:pw@evil.example/")]
    [InlineData("https://localhost")]
    [InlineData("")]
    [InlineData(null)]
    public void ChatWordsThatMustNotOpen(string? word)
    {
        var u = SafeUrl.FromChatWord(word);
        if (word == "http.com") { Assert.Equal("https://http.com/", u!.AbsoluteUri); return; }   // opened as a web page, never as a file
        Assert.Null(u);
    }

    [Theory]
    [InlineData("https://www.twitch.tv/x", true)]
    [InlineData("http://example.com", true)]
    [InlineData("file:///c:/x", false)]
    [InlineData("ms-msdt:/id", false)]
    [InlineData("twitch://open", false)]
    [InlineData("C:\\Windows\\notepad.exe", false)]
    [InlineData("notepad", false)]
    public void BrowserLaunchOnlyForWebUrls(string url, bool ok)
    {
        Assert.Equal(ok, SafeUrl.ForBrowser(url) != null);
    }

    [Theory]
    [InlineData("https://static-cdn.jtvnw.net/emoticons/v2/25/default/dark/1.0", true)]
    [InlineData("https://cdn.7tv.app/emote/abc/1x.gif", true)]
    [InlineData("https://cdn.betterttv.net/emote/abc/1x.png", true)]
    [InlineData("https://cdn.frankerfacez.com/emote/1/1", true)]
    [InlineData("http://cdn.7tv.app/emote/abc/1x.gif", false)]
    [InlineData("https://evil7tv.app/x.gif", false)]
    [InlineData("https://cdn.7tv.app.evil.com/x.gif", false)]
    [InlineData("https://example.com/x.png", false)]
    [InlineData("file:///c:/x.png", false)]
    public void ImagesOnlyFromKnownCdns(string url, bool ok)
    {
        Assert.Equal(ok, SafeUrl.IsAllowedImage(url));
    }

    [Theory]
    [InlineData("https://api.twitch.tv/helix/users", true)]
    [InlineData("https://7tv.io/v3/emote-sets/global", true)]
    [InlineData("https://api.betterttv.net/3/cached/emotes/global", true)]
    [InlineData("https://api.frankerfacez.com/v1/set/global", true)]
    [InlineData("https://api.twitch.tv.evil.com/", false)]
    [InlineData("http://7tv.io/v3/x", false)]
    public void ApisOnlyFromKnownHosts(string url, bool ok)
    {
        Assert.Equal(ok, SafeUrl.IsAllowedApi(url));
    }

    [Theory]
    [InlineData("https://www.twitch.tv/directory/following/live", SafeUrl.Nav.Allow)]
    [InlineData("https://passport.twitch.tv/x", SafeUrl.Nav.Allow)]
    [InlineData("about:blank", SafeUrl.Nav.Allow)]
    [InlineData("http://www.twitch.tv/", SafeUrl.Nav.OpenInBrowser)]
    [InlineData("https://www.youtube.com/watch?v=x", SafeUrl.Nav.OpenInBrowser)]
    [InlineData("file:///C:/Users", SafeUrl.Nav.Block)]
    [InlineData("ms-settings:", SafeUrl.Nav.Block)]
    [InlineData("data:text/html,hi", SafeUrl.Nav.Block)]
    [InlineData("https://twitch.tv.evil.com/", SafeUrl.Nav.OpenInBrowser)]
    [InlineData("", SafeUrl.Nav.Block)]
    public void WebViewNavigationPolicy(string url, SafeUrl.Nav expected)
    {
        Assert.Equal(expected, SafeUrl.ForWebView(url));
    }

    [Fact]
    public void ActivationPageMustBeTwitch()
    {
        Assert.Equal("https://www.twitch.tv/activate?device-code=ABCD", SafeUrl.ActivationPage("https://www.twitch.tv/activate?device-code=ABCD").AbsoluteUri);
        Assert.Equal("https://www.twitch.tv/activate", SafeUrl.ActivationPage("https://evil.example/activate").AbsoluteUri);
        Assert.Equal("https://www.twitch.tv/activate", SafeUrl.ActivationPage("file:///c:/x").AbsoluteUri);
        Assert.Equal("https://www.twitch.tv/activate", SafeUrl.ActivationPage(null).AbsoluteUri);
    }
}

public class WebMessageTests
{
    const string Secret = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void AcceptsOurWatchCommandFromTwitch()
    {
        Assert.Equal("xqc", WebMessage.Watch("watch:" + Secret + ":xqc", "https://www.twitch.tv/directory/following/live", Secret));
    }

    [Theory]
    [InlineData("watch:" + Secret + ":xqc", "https://evil.example/")]          // wrong origin
    [InlineData("watch:" + Secret + ":xqc", "http://www.twitch.tv/")]          // not https
    [InlineData("watch:" + Secret + ":xqc", null)]
    [InlineData("watch:xqc", "https://www.twitch.tv/")]                        // no secret: another script on the page
    [InlineData("watch:ffffffffffffffffffffffffffffffff:xqc", "https://www.twitch.tv/")]
    [InlineData("xqc", "https://www.twitch.tv/")]                              // upstream's bare-name format
    [InlineData("run:" + Secret + ":calc", "https://www.twitch.tv/")]
    [InlineData("watch:" + Secret + ":xq c", "https://www.twitch.tv/")]
    [InlineData("watch:" + Secret + ":directory", "https://www.twitch.tv/")]
    [InlineData("watch:" + Secret + ":", "https://www.twitch.tv/")]
    [InlineData(null, "https://www.twitch.tv/")]
    public void RejectsEverythingElse(string? msg, string? source)
    {
        Assert.Null(WebMessage.Watch(msg, source, Secret));
    }

    [Fact]
    public void EmptySecretAcceptsNothing()
    {
        Assert.Null(WebMessage.Watch("watch::xqc", "https://www.twitch.tv/", ""));
    }

    [Fact]
    public void SecretsAreRandom()
    {
        var a = WebMessage.NewSecret(); var b = WebMessage.NewSecret();
        Assert.Equal(32, a.Length);
        Assert.NotEqual(a, b);
        Assert.Matches("^[0-9a-f]{32}$", a);
    }
}

public class ImageSniffTests
{
    static byte[] Png(int w, int h)
    {
        var b = new byte[33];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(b, 0);
        b[16] = (byte)(w >> 24); b[17] = (byte)(w >> 16); b[18] = (byte)(w >> 8); b[19] = (byte)w;
        b[20] = (byte)(h >> 24); b[21] = (byte)(h >> 16); b[22] = (byte)(h >> 8); b[23] = (byte)h;
        return b;
    }

    static byte[] Gif(int w, int h)
    {
        var b = new byte[16];
        System.Text.Encoding.ASCII.GetBytes("GIF89a").CopyTo(b, 0);
        b[6] = (byte)w; b[7] = (byte)(w >> 8); b[8] = (byte)h; b[9] = (byte)(h >> 8);
        return b;
    }

    static byte[] Jpeg(int w, int h)
    {
        // SOI, APP0 (length 16), SOF0 (length 17: precision, height, width, ...)
        var b = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
        b.AddRange(new byte[14]);
        b.AddRange(new byte[] { 0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w });
        b.AddRange(new byte[12]);
        return b.ToArray();
    }

    [Fact]
    public void ReadsSizes()
    {
        Assert.True(ImageSniff.TrySize(Png(112, 32), out int w, out int h)); Assert.Equal((112, 32), (w, h));
        Assert.True(ImageSniff.TrySize(Gif(28, 28), out w, out h)); Assert.Equal((28, 28), (w, h));
        Assert.True(ImageSniff.TrySize(Jpeg(300, 300), out w, out h)); Assert.Equal((300, 300), (w, h));
    }

    [Fact]
    public void RefusesOversizedAndUnknown()
    {
        Assert.True(ImageSniff.Acceptable(Png(112, 32)));
        Assert.False(ImageSniff.Acceptable(Png(30000, 30000)));     // a decompression bomb is refused before decoding
        Assert.False(ImageSniff.Acceptable(Gif(65535, 65535)));
        Assert.False(ImageSniff.Acceptable(Png(0, 10)));
        Assert.False(ImageSniff.Acceptable(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }));    // EMF-like header
        Assert.False(ImageSniff.Acceptable(System.Text.Encoding.ASCII.GetBytes("BM............")));  // BMP
        Assert.False(ImageSniff.Acceptable(System.Text.Encoding.ASCII.GetBytes("RIFF....WEBPVP8 ")));
        Assert.False(ImageSniff.Acceptable(new byte[3]));
        Assert.False(ImageSniff.Acceptable(null!));
        var truncated = Jpeg(10, 10)[..22];
        Assert.False(ImageSniff.TrySize(truncated, out _, out _));
    }
}
