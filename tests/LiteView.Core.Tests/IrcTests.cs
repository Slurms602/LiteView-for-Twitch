using LiteViewCore;
using Xunit;

public class IrcMsgTests
{
    [Fact]
    public void ParsesTagsPrefixCommandChannelText()
    {
        var m = IrcMsg.Parse("@badge-info=;color=#FF0000;display-name=Foo;emotes=25:0-4 :foo!foo@foo.tmi.twitch.tv PRIVMSG #bar :Kappa hello there")!;
        Assert.Equal("PRIVMSG", m.Cmd);
        Assert.Equal("bar", m.Chan);
        Assert.Equal("Kappa hello there", m.Text);
        Assert.Equal("foo", m.Nick);
        Assert.Equal("#FF0000", m.Tag("color"));
        Assert.Equal("", m.Tag("badge-info"));
        Assert.Equal("25:0-4", m.Tag("emotes"));
        Assert.Null(m.Tag("missing"));
    }

    [Fact]
    public void UnescapesTagValues()
    {
        var m = IrcMsg.Parse(@"@system-msg=5\sraiders\sfrom\sX\:\shi\\there\ :tmi.twitch.tv USERNOTICE #c")!;
        Assert.Equal(@"5 raiders from X; hi\there", m.Tag("system-msg"));
        Assert.Null(m.Text);
        Assert.Equal("USERNOTICE", m.Cmd);
    }

    [Fact]
    public void TagWithoutValueIsEmpty()
    {
        var m = IrcMsg.Parse("@flag;x=1 :tmi.twitch.tv ROOMSTATE #c")!;
        Assert.Equal("", m.Tag("flag"));
        Assert.Equal("1", m.Tag("x"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("@only-tags-no-space")]
    [InlineData(":prefix-without-command")]
    [InlineData("@a=b ")]
    [InlineData("   ")]
    public void MalformedLinesAreRejected(string? line)
    {
        Assert.Null(IrcMsg.Parse(line));
    }

    [Fact]
    public void OverlongLineIsRejected()
    {
        Assert.Null(IrcMsg.Parse("PRIVMSG #c :" + new string('a', IrcProtocol.MaxLineChars)));
    }

    [Fact]
    public void ServerLinesWithoutPrefix()
    {
        var m = IrcMsg.Parse("PING :tmi.twitch.tv")!;
        Assert.Equal("PING", m.Cmd);
        Assert.Equal("tmi.twitch.tv", m.Text);
        Assert.Null(m.Nick);
    }

    [Fact]
    public void ExtraSpacesBetweenPartsAreTolerated()
    {
        var m = IrcMsg.Parse("@a=1  :n!n@n  PRIVMSG  #c :x")!;
        Assert.Equal("PRIVMSG", m.Cmd);
        Assert.Equal("c", m.Chan);
    }
}

public class IrcProtocolTests
{
    [Fact]
    public void ReconnectOnlyForTheCommand()
    {
        Assert.True(IrcProtocol.IsReconnect(IrcMsg.Parse(":tmi.twitch.tv RECONNECT")));
        // upstream treated any line ending in " RECONNECT" as the command: a chatter could make every viewer reconnect
        Assert.False(IrcProtocol.IsReconnect(IrcMsg.Parse("@id=1 :a!a@a PRIVMSG #c :please RECONNECT")));
        Assert.False(IrcProtocol.IsReconnect(null));
    }

    [Fact]
    public void PongMirrorsPing()
    {
        Assert.Equal("PONG :tmi.twitch.tv", IrcProtocol.PongFor("PING :tmi.twitch.tv"));
        Assert.Equal("PONG", IrcProtocol.PongFor("PING"));
        Assert.Null(IrcProtocol.PongFor("PINGPONG"));
        Assert.Null(IrcProtocol.PongFor("@x PRIVMSG #c :PING"));
    }

    [Theory]
    [InlineData("hello\r\nJOIN #other", "hello  JOIN #other")]
    [InlineData("a\nb", "a b")]
    [InlineData("nul\0here", "nul here")]
    [InlineData("  padded  ", "padded")]
    [InlineData("tab\there", "tab here")]
    [InlineData("stray\u0001ctcp", "strayctcp")]
    [InlineData("\u0001ACTION waves\u0001", "\u0001ACTION waves\u0001")]
    [InlineData("line\u2028sep", "line sep")]
    public void OutgoingTextCannotInjectCommands(string input, string expected)
    {
        Assert.Equal(expected, IrcProtocol.CleanOutgoing(input));
        Assert.DoesNotContain('\r', IrcProtocol.CleanOutgoing(input));
        Assert.DoesNotContain('\n', IrcProtocol.CleanOutgoing(input));
    }

    [Fact]
    public void OutgoingTextIsCappedWithoutSplittingSurrogates()
    {
        string s = new string('a', 499) + "\U0001F600" + "tail";
        string r = IrcProtocol.CleanOutgoing(s);
        Assert.Equal(499, r.Length);
        Assert.Equal(500, IrcProtocol.CleanOutgoing(new string('b', 600)).Length);
    }

    [Fact]
    public void DisplayTextIsCapped()
    {
        Assert.Equal("short", IrcProtocol.ForDisplay("short"));
        var r = IrcProtocol.ForDisplay(new string('x', 5000));
        Assert.Equal(IrcProtocol.MaxShownChars + 1, r.Length);
        Assert.EndsWith("…", r);
        Assert.Equal("", IrcProtocol.ForDisplay(null));
    }

    [Fact]
    public void LinesSplitsBatches()
    {
        Assert.Equal(new[] { "a", "b", "c" }, IrcProtocol.Lines("a\r\nb\r\n\r\nc\r\n"));
    }

    [Fact]
    public void FrameBufferHasAHardLimit()
    {
        var fb = new FrameBuffer(10);
        Assert.True(fb.Append(new byte[] { 65, 66, 67 }, 3));
        Assert.True(fb.Append(new byte[] { 68 }, 1));
        Assert.Equal("ABCD", fb.Take());
        Assert.Equal(0, fb.Length);
        Assert.True(fb.Append(new byte[8], 8));
        Assert.False(fb.Append(new byte[8], 8));   // 16 > 10: refused and cleared
        Assert.Equal(0, fb.Length);
    }

    [Fact]
    public void FrameBufferDecodesUtf8SplitAcrossFragments()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("é😀");
        var fb = new FrameBuffer();
        fb.Append(bytes[..1], 1);
        fb.Append(bytes[1..], bytes.Length - 1);
        Assert.Equal("é😀", fb.Take());
    }
}

public class ChatStressTests
{
    [Fact]
    public void LinesAreValidTwitchShapedIrc()
    {
        var g = new ChatStress("chan", 3);
        var emotes = new[] { "KEKW", "Pog", "catJAM", "OMEGALUL" };
        var cmds = new Dictionary<string, int>();
        int withEmote = 0, long_ = 0;
        for (int i = 0; i < 5000; i++)
        {
            string line = g.Next(emotes);
            var m = IrcMsg.Parse(line);
            Assert.NotNull(m);
            Assert.DoesNotContain('\r', line); Assert.DoesNotContain('\n', line);
            cmds[m!.Cmd!] = cmds.TryGetValue(m.Cmd!, out var c) ? c + 1 : 1;
            Assert.Equal("chan", m.Chan);
            if (m.Cmd == "PRIVMSG")
            {
                Assert.True(m.Text!.Length <= IrcProtocol.MaxMessageChars);
                Assert.NotNull(m.Tag("id")); Assert.NotNull(m.Tag("display-name"));
                if (emotes.Any(e => m.Text.Split(' ').Contains(e))) withEmote++;
                if (m.Text.Length > 200) long_++;
            }
        }
        Assert.True(cmds["PRIVMSG"] > 4500);
        Assert.True(cmds.ContainsKey("USERNOTICE") && cmds.ContainsKey("CLEARMSG") && cmds.ContainsKey("CLEARCHAT"));
        Assert.True(withEmote > 2000, "emotes: " + withEmote);
        Assert.True(long_ > 50, "long messages: " + long_);
        Assert.Equal(5000, g.Count);
    }

    [Fact]
    public void SameSeedSameStream()
    {
        var a = new ChatStress("c", 9); var b = new ChatStress("c", 9);
        // ids are GUIDs, so compare the parts that come from the seed
        for (int i = 0; i < 200; i++) Assert.Equal(IrcMsg.Parse(a.Next(new[] { "X" }))!.Cmd, IrcMsg.Parse(b.Next(new[] { "X" }))!.Cmd);
    }
}
