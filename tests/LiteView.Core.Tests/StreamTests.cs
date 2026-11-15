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
            "--twitch-low-latency", "--retry-streams", "30", "--twitch-supported-codecs", "h264,h265,av1",
            "--loglevel", "info", "--logfile", @"C:\Apps\Lite View\streamlink.log",
            "twitch.tv/xqc", "720p60,720p,best",
        }, argv);
    }

    [Fact]
    public void ASeamlessSwitchStartsThePlayerMuted()
    {
        var argv = ArgvParse(StreamCommand.Build(new StreamRequest { Streamlink = "s", Mpv = "m", LogFile = "l", Channel = "xqc", PipeName = "liteview-mpv-1", WindowHandle = 7, StartMuted = true }));
        Assert.Equal("--wid=7 --input-ipc-server=liteview-mpv-1 --mute=yes", argv[argv.IndexOf("--player-args") + 1]);
        argv = ArgvParse(StreamCommand.Build(new StreamRequest { Streamlink = "s", Mpv = "m", LogFile = "l", Channel = "xqc", PipeName = "liteview-mpv-1", WindowHandle = 7 }));
        Assert.DoesNotContain("mute", argv[argv.IndexOf("--player-args") + 1]);
    }

    [Fact]
    public void VideosAndClipsPlayWithPassthroughAndNoLiveOptions()
    {
        var v = ArgvParse(StreamCommand.Build(new StreamRequest { Streamlink = "s", Mpv = "m", LogFile = "l", Channel = "xqc", PipeName = "liteview-mpv-1", WindowHandle = 7, Video = "2001", StartSeconds = 754, Quality = "720p60" }));
        Assert.Equal(new[] { "s", "--player", "m", "--player-args", "--wid=7 --input-ipc-server=liteview-mpv-1 --start=+754", "--player-passthrough", "hls,http",
            "--twitch-supported-codecs", "h264,h265,av1", "--loglevel", "info", "--logfile", "l", "twitch.tv/videos/2001", "720p60,720p,best" }, v);
        var c = ArgvParse(StreamCommand.Build(new StreamRequest { Streamlink = "s", Mpv = "m", LogFile = "l", Channel = "xqc", PipeName = "liteview-mpv-1", WindowHandle = 7, Clip = "Funny_Clip-1", StartSeconds = 9 }));
        Assert.Contains("clips.twitch.tv/Funny_Clip-1", c);
        Assert.DoesNotContain("--twitch-low-latency", c); Assert.DoesNotContain("--retry-streams", c);
        Assert.DoesNotContain("--start", c[c.IndexOf("--player-args") + 1]);   // clips are short: always from the start
    }

    [Fact]
    public void APreviewIsSmallMutedAndDeaf()
    {
        var p = ArgvParse(StreamCommand.Build(new StreamRequest { Streamlink = "s", Mpv = "m", LogFile = "l", Channel = "xqc", PipeName = "liteview-mpv-1", WindowHandle = 7, Preview = true, Quality = "1080p60" }));
        Assert.Equal("--wid=7 --input-ipc-server=liteview-mpv-1 --mute=yes --osc=no --osd-level=0 --input-cursor=no --input-vo-keyboard=no", p[p.IndexOf("--player-args") + 1]);
        Assert.Equal("160p,360p,worst", p[p.Count - 1]);
        Assert.DoesNotContain("--retry-streams", p);
        Assert.Throws<ArgumentException>(() => StreamCommand.Build(new StreamRequest { Channel = "xqc", PipeName = "p", Preview = true, Video = "1" }));
    }

    [Theory]
    [InlineData("12a", "")]
    [InlineData("1 --player=calc", "")]
    [InlineData("", "x y")]
    [InlineData("", "a\"b")]
    [InlineData("5", "clip")]
    public void BadVideoOrClipIdsAreRefused(string video, string clip)
    {
        Assert.Throws<ArgumentException>(() => StreamCommand.Build(new StreamRequest { Channel = "xqc", PipeName = "p", Video = video, Clip = clip }));
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
    // Lines in the format Streamlink 8.6.3 writes to --logfile ("[{name}][{levelname}] {message}")
    static readonly string[] Offline =
    {
        "[cli][info] Found matching plugin twitch for URL twitch.tv/somebody",
        "[cli][info] Waiting for streams, retrying every 30.0 second(s)",
    };

    [Fact]
    public void LastErrorFromRealLogLines()
    {
        var log = new[]
        {
            "[cli][info] Found matching plugin twitch for URL twitch.tv/x",
            "[plugins.twitch][error] Unable to open URL: https://gql.twitch.tv/gql (Read timed out.)",
            "[cli][info] Waiting for streams, retrying every 30.0 second(s)",
            "[cli][error] Try 1/1: Could not open stream <HLSStream ...> (Failed to read data from stream: ...)",
            "[cli][info] Closing currently open stream...",
        };
        Assert.Equal("Try 1/1: Could not open stream <HLSStream ...> (Failed to read data from stream: ...)", StreamOutcome.LastError(log));
        Assert.Equal("", StreamOutcome.LastError(Offline));
        Assert.Equal("", StreamOutcome.LastError(new[] { "[cli][info] error: in a message is not an error line" }));
        Assert.Equal("old console form", StreamOutcome.LastError(new[] { "error: old console form" }));
    }

    [Fact]
    public void WaitingForAnOfflineChannel()
    {
        Assert.True(StreamOutcome.Waiting(Offline));
        Assert.False(StreamOutcome.Waiting(Offline.Concat(new[] { "[cli][info] Available streams: audio_only, 160p, 360p, 480p, 720p60, 1080p60 (best)", "[cli][info] Opening stream: 1080p60 (hls)" })));
        Assert.False(StreamOutcome.Waiting(new[] { "[cli][info] Found matching plugin twitch for URL twitch.tv/x" }));
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

public class VariantTests
{
    const string Line = "[cli][info] Available streams: audio_only, 160p (worst), 360p, 480p, 720p60, 1080p60, 1440p60 (best)";

    [Fact]
    public void AvailableStreamsComeFromTheLastLogLine()
    {
        Assert.Equal(new[] { "audio_only", "160p", "360p", "480p", "720p60", "1080p60", "1440p60" },
            Quality.Available(new[] { "[cli][info] Available streams: 720p (best)", "x", Line }));
        Assert.Empty(Quality.Available(new[] { "[cli][info] Found matching plugin twitch" }));
        Assert.Equal(new[] { "720p60" }, Quality.Available(new[] { "Available streams: 720p60, 1080p60;calc.exe, best, 99999p, p60, audio" }));
    }

    [Fact]
    public void ChoicesAreBestThenHighToLowThenAudio()
    {
        Assert.Equal(new[] { "Best", "1440p60", "1080p60", "720p60", "480p", "360p", "160p", "Audio only" }, Quality.ChoicesFor(Quality.Available(new[] { Line })));
        Assert.Equal(new[] { "Best", "720p60", "720p" }, Quality.ChoicesFor(new[] { "720p", "720p60" }));
        Assert.Equal(new[] { "Best" }, Quality.ChoicesFor(new string[0]));
    }

    [Theory]
    [InlineData("1440p60", "1440p60,1440p,best")] [InlineData("360p", "360p,best")] [InlineData("1080p60", "1080p60,1080p,best")]
    [InlineData("Best", "best")] [InlineData("1080p60 --player=calc", "best")] [InlineData("0p", "best")] [InlineData(null, "best")]
    public void Selectors(string? choice, string selector) { Assert.Equal(selector, Quality.Selector(choice)); }
}
