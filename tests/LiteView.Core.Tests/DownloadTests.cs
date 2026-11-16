using System;
using System.Collections.Generic;
using LiteViewCore;
using Xunit;

public class DownloadTests
{
    static List<string> Argv(string cmd)
    {
        // the C runtime's rules, as StreamTests does it
        var list = new List<string>(); var cur = new System.Text.StringBuilder(); bool q = false, any = false;
        for (int i = 0; i < cmd.Length; i++)
        {
            char c = cmd[i];
            if (c == '\\') { int n = 0; while (i < cmd.Length && cmd[i] == '\\') { n++; i++; } if (i < cmd.Length && cmd[i] == '"') { cur.Append('\\', n / 2); if (n % 2 == 1) { cur.Append('"'); any = true; continue; } i--; continue; } cur.Append('\\', n); i--; any = true; continue; }
            if (c == '"') { q = !q; any = true; continue; }
            if (c == ' ' && !q) { if (any) { list.Add(cur.ToString()); cur.Clear(); any = false; } continue; }
            cur.Append(c); any = true;
        }
        if (any) list.Add(cur.ToString());
        return list;
    }

    [Fact]
    public void AVideoGoesToAFile()
    {
        var a = Argv(DownloadCommand.Build(new DownloadRequest { Streamlink = "C:\\LiteView\\streamlinkw.exe", LogFile = "C:\\LiteView\\dl.log", Output = "C:\\Users\\me\\Videos\\LiteView Downloads\\gdq 2026-01-05 run.ts.part", Channel = "gamesdonequick", Video = "123456", Quality = "720p60" }));
        Assert.Equal(new[] { "C:\\LiteView\\streamlinkw.exe", "--loglevel", "debug", "--logfile", "C:\\LiteView\\dl.log", "--output", "C:\\Users\\me\\Videos\\LiteView Downloads\\gdq 2026-01-05 run.ts.part",
            "--stream-segment-threads", "2", "--twitch-supported-codecs", "h264", "twitch.tv/videos/123456", "720p60,720p,best" }, a);
        var c = Argv(DownloadCommand.Build(new DownloadRequest { Streamlink = "s", LogFile = "l", Output = "C:\\x\\clip.mp4.part", Channel = "gdq", Clip = "Funny_Clip-1", Quality = "Best" }));
        Assert.Contains("clips.twitch.tv/Funny_Clip-1", c);
    }

    [Theory]
    [InlineData("C:\\x\\a.ts")]              // not .part
    [InlineData("a.ts.part")]                // not absolute
    [InlineData("C:\\x\\{author}.ts.part")]  // a Streamlink placeholder
    [InlineData("C:\\x\\a\".ts.part")]
    [InlineData("C:\\x\\a\n.ts.part")]
    [InlineData("\\\\server\\share\\a.ts.part")]
    public void BadOutputsAreRefused(string output)
    {
        Assert.False(DownloadCommand.SafeOutput(output));
        Assert.Throws<ArgumentException>(() => DownloadCommand.Build(new DownloadRequest { Output = output, Channel = "gdq", Video = "1" }));
    }

    [Fact]
    public void BadRequestsAreRefused()
    {
        string o = "C:\\x\\a.ts.part";
        Assert.Throws<ArgumentException>(() => DownloadCommand.Build(new DownloadRequest { Output = o, Channel = "gdq" }));                          // neither
        Assert.Throws<ArgumentException>(() => DownloadCommand.Build(new DownloadRequest { Output = o, Channel = "gdq", Video = "1", Clip = "x" }));   // both
        Assert.Throws<ArgumentException>(() => DownloadCommand.Build(new DownloadRequest { Output = o, Channel = "gdq", Video = "1 --player=x" }));
        Assert.Throws<ArgumentException>(() => DownloadCommand.Build(new DownloadRequest { Output = o, Channel = "g d q", Video = "1" }));
        Assert.Throws<ArgumentException>(() => DownloadCommand.Build(new DownloadRequest { Output = o, Channel = "gdq", Video = "1", Quality = "best --x" }));
    }

    [Theory]
    [InlineData("gamesdonequick", "Awesome Games Done Quick 2026 - Day 1: {stuff} / \"quoted\" <tags>", "gamesdonequick 2026-01-05 Awesome Games Done Quick 2026 - Day 1 stuff quoted tags.ts")]
    [InlineData("gdq", "", "gdq 2026-01-05.ts")]
    [InlineData("gdq", "...", "gdq 2026-01-05.ts")]
    [InlineData("gdq", "Ünïcödé 日本語 ok", "gdq 2026-01-05 Ünïcödé 日本語 ok.ts")]
    public void FileNamesAreSafe(string channel, string title, string want)
    {
        Assert.Equal(want, DownloadCommand.FileName(channel, title, new DateTimeOffset(2026, 1, 5, 12, 0, 0, TimeSpan.Zero), false));
        Assert.EndsWith(".mp4", DownloadCommand.FileName(channel, title, null, true));
        Assert.True(DownloadCommand.FileName("gdq", new string('x', 500), null, false).Length <= 120);
    }

    [Fact]
    public void ProgressFromStreamlinksLog()
    {
        var p = new DownloadProgress();
        Assert.Equal(-1, p.Fraction);
        DownloadCommand.Read(p, new[] {
            "[cli][info] Opening stream: 720p60 (hls)",
            "[stream.hls][debug] First Sequence: 0; Last Sequence: 99",
            "[stream.hls][debug] Start offset: 0; Duration: None; Start Sequence: 0; End Sequence: 99",
            "[stream.hls][debug] Writing segment 0 to output", "[stream.hls][debug] Writing segment 1 to output" });
        Assert.Equal(0.02, p.Fraction, 6);
        DownloadCommand.Read(p, new[] { "[stream.hls][debug] Writing segment 49 to output", "[stream.hls][debug] Writing segment 3 to output" });
        Assert.Equal(0.5, p.Fraction, 6);
        DownloadCommand.Read(p, new[] { "[stream.hls][debug] Writing segment 99 to output" });
        Assert.Equal(1.0, p.Fraction, 6);
    }

    [Fact]
    public void SizeEstimates()
    {
        Assert.Equal(0, DownloadCommand.EstimateBytes(0, "Best"));
        Assert.Equal("2.7 GB", DownloadCommand.Size(DownloadCommand.EstimateBytes(3600, "Best")));
        Assert.Equal("68 MB", DownloadCommand.Size(DownloadCommand.EstimateBytes(3600, "Audio only")));
    }
}
