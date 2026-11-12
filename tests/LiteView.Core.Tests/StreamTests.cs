using LiteViewCore;
using Xunit;

public class WinArgsTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "\"\"")]
    [InlineData("has space", "\"has space\"")]
    [InlineData("C:\\Program Files\\x\\", "\"C:\\Program Files\\x\\\\\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("a\\\\\"b", "\"a\\\\\\\\\\\"b\"")]
    [InlineData("C:\\no\\spaces", "C:\\no\\spaces")]
    public void QuotesLikeCommandLineToArgvW(string arg, string expected)
    {
        Assert.Equal(expected, WinArgs.Quote(arg));
        Assert.Equal(new[] { arg }, ArgvParse(WinArgs.Quote(arg)));
    }

    [Fact]
    public void JoinRoundTrips()
    {
        var args = new[] { "C:\\Program Files\\LiteView\\streamlinkw.exe", "--player", "C:\\a b\\mpv.exe", "--wid=1 --x=2", "\"q\"", "end\\" };
        Assert.Equal(args, ArgvParse(WinArgs.Join(args)));
    }

    // CommandLineToArgvW's rules (2n backslashes + quote -> n backslashes, toggle; 2n+1 -> n + literal quote)
    static List<string> ArgvParse(string cmd)
    {
        var r = new List<string>(); var cur = new System.Text.StringBuilder(); bool inq = false, any = false; int i = 0;
        while (i < cmd.Length)
        {
            char c = cmd[i];
            if (c == '\\')
            {
                int n = 0; while (i < cmd.Length && cmd[i] == '\\') { n++; i++; }
                if (i < cmd.Length && cmd[i] == '"') { cur.Append('\\', n / 2); if (n % 2 == 1) { cur.Append('"'); i++; } }
                else cur.Append('\\', n);
                any = true; continue;
            }
            if (c == '"') { inq = !inq; any = true; i++; continue; }
            if ((c == ' ' || c == '\t') && !inq) { if (any) { r.Add(cur.ToString()); cur.Clear(); any = false; } i++; continue; }
            cur.Append(c); any = true; i++;
        }
        if (any) r.Add(cur.ToString());
        return r;
    }

    [Fact]
    public void StreamCommandHasEveryArgumentInPlace()
    {
        var cmd = StreamCommand.Build(new StreamRequest
        {
            Streamlink = @"C:\Apps\Lite View\twitch\streamlink\bin\streamlinkw.exe",
            Mpv = @"C:\Apps\Lite View\twitch\mpv\mpv.exe",
            LogFile = @"C:\Apps\Lite View\streamlink.log",
            Channel = "xqc", Quality = "720p60", PipeName = "liteview-mpv-abc123", WindowHandle = 132456,
        });
        var argv = ArgvParse(cmd);
        Assert.Equal(new[]
        {
            @"C:\Apps\Lite View\twitch\streamlink\bin\streamlinkw.exe",
            "--player", @"C:\Apps\Lite View\twitch\mpv\mpv.exe",
            "--player-args", "--wid=132456 --input-ipc-server=liteview-mpv-abc123",
            "--twitch-low-latency", "--twitch-supported-codecs", "h264,h265,av1",
            "--loglevel", "info", "--logfile", @"C:\Apps\Lite View\streamlink.log",
            "twitch.tv/xqc", "720p60,720p,best",
        }, argv);
    }

    [Theory]
    [InlineData("xqc --player-args=calc")]
    [InlineData("a b")]
    [InlineData("")]
    public void StreamCommandRefusesBadChannels(string ch)
    {
        Assert.Throws<ArgumentException>(() => StreamCommand.Build(new StreamRequest { Channel = ch, PipeName = "p" }));
    }

    [Fact]
    public void PipeNamesAreUniqueAndSafe()
    {
        var a = StreamCommand.NewPipeName(); var b = StreamCommand.NewPipeName();
        Assert.NotEqual(a, b);
        Assert.True(StreamCommand.IsPipeName(a));
        Assert.False(StreamCommand.IsPipeName("x --y"));
        Assert.False(StreamCommand.IsPipeName(@"..\pipe"));
        Assert.False(StreamCommand.IsPipeName(null));
    }
}

public class QualityTests
{
    [Theory]
    [InlineData("Best", "best")]
    [InlineData("1080p60", "1080p60,1080p,best")]
    [InlineData("720p60", "720p60,720p,best")]
    [InlineData("480p", "480p,best")]
    [InlineData("Audio only", "audio_only,audio,worst")]
    [InlineData("garbage", "best")]
    [InlineData(null, "best")]
    public void Selectors(string? choice, string expected) { Assert.Equal(expected, Quality.Selector(choice)); }

    [Fact]
    public void EveryChoiceHasASelector()
    {
        foreach (var c in Quality.Choices) Assert.False(string.IsNullOrEmpty(Quality.Selector(c)));
        Assert.Equal("Best", Quality.Choices[0]);
    }
}

public class StreamOutcomeTests
{
    [Fact]
    public void LastErrorLine()
    {
        var log = new[] { "[cli][info] Found matching plugin twitch", "[cli][error] error: first", "[cli][info] x", "error: No playable streams found on this URL: twitch.tv/x" };
        Assert.Equal("No playable streams found on this URL: twitch.tv/x", StreamOutcome.LastError(log));
        Assert.Equal("", StreamOutcome.LastError(new[] { "fine" }));
    }

    [Theory]
    [InlineData("No playable streams found on this URL: twitch.tv/x", 2, false, StreamEnd.Offline)]
    [InlineData("", 3600, true, StreamEnd.Ended)]
    [InlineData("", 2, false, StreamEnd.Failed)]
    [InlineData("Unable to open URL: https://usher.ttvnw.net (HTTPSConnectionPool...)", 1, false, StreamEnd.Failed)]
    [InlineData("Failed to start player: C:\\x\\mpv.exe (Player executable not found)", 1, false, StreamEnd.Fatal)]
    [InlineData("unrecognized arguments: --twitch-low-latency", 1, false, StreamEnd.Fatal)]
    public void Classifies(string err, int seconds, bool played, StreamEnd expected)
    {
        Assert.Equal(expected, StreamOutcome.Classify(err, TimeSpan.FromSeconds(seconds), played));
    }
}

public class BackoffTests
{
    [Fact]
    public void GrowsToTheCapWithJitter()
    {
        var b = new Backoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), new Random(42));
        var waits = Enumerable.Range(0, 10).Select(_ => b.Next().TotalMilliseconds).ToList();
        double[] nominal = { 1000, 2000, 4000, 8000, 16000, 30000, 30000, 30000, 30000, 30000 };
        for (int i = 0; i < waits.Count; i++)
        {
            Assert.InRange(waits[i], nominal[i] * 0.5, nominal[i]);
        }
    }

    [Fact]
    public void ResetsOnlyAfterAHealthyConnection()
    {
        var b = new Backoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), new Random(1));
        b.Next(); b.Next(); b.Next();
        b.Ended(TimeSpan.FromSeconds(5));     // accepted then dropped at once: keep slowing down
        Assert.Equal(3, b.Attempt);
        b.Ended(TimeSpan.FromMinutes(5));
        Assert.Equal(0, b.Attempt);
    }

    [Fact]
    public void RejectsNonsense()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Backoff(TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Backoff(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), TimeSpan.Zero));
    }
}
