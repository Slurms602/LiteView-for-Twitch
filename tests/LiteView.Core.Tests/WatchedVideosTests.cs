using LiteViewCore;
using Xunit;

public class WatchedVideosTests
{
    [Fact]
    public void RemembersWhereEachVideoStopped()
    {
        var w = new WatchedVideos();
        w.Set("100", 600, 3600);
        w.Set("200", 10, 3600);
        w.Set("300", 3590, 3600);
        w.Set("bad id", 5, 5);
        w.Set("400", -1, 10);
        Assert.Equal(3, w.Count);
        Assert.Equal(600, w.ResumeAt("100", 0));
        Assert.Equal(0, w.ResumeAt("200", 0));     // barely started: from the beginning
        Assert.Equal(0, w.ResumeAt("300", 0));     // nearly finished: from the beginning
        Assert.Equal(0, w.ResumeAt("999", 0));
        Assert.Equal(1.0 / 6, w.Fraction("100", 0), 6);
        w.Set("500", 120, 0);                       // length unknown: the card's length is used
        Assert.Equal(0.5, w.Fraction("500", 240), 6);
        Assert.Equal(0, w.Fraction("500", 0));
        w.Set("600", 9000, 100);                    // past the end: the end
        int pos, len; Assert.True(w.TryGet("600", out pos, out len)); Assert.Equal(100, pos);
        w.Forget("100"); Assert.False(w.TryGet("100", out pos, out len));
    }

    [Fact]
    public void SavesNewestFirstAndKeepsTheLastOnes()
    {
        var w = new WatchedVideos();
        for (int i = 1; i <= WatchedVideos.MaxEntries + 20; i++) w.Set(i.ToString(), i, 1000);
        Assert.Equal(WatchedVideos.MaxEntries, w.Count);
        int pos, len;
        Assert.False(w.TryGet("1", out pos, out len));   // the oldest went
        var back = WatchedVideos.Load(w.Save());
        Assert.Equal(WatchedVideos.MaxEntries, back.Count);
        Assert.StartsWith((WatchedVideos.MaxEntries + 20) + " ", back.Save());   // still newest first
        Assert.True(back.TryGet("100", out pos, out len)); Assert.Equal(100, pos); Assert.Equal(1000, len);
        var junk = WatchedVideos.Load("12 34 56\nx y z\n1 -2 3\n7 8\n\n99 1 2 3\n5 10 20");
        Assert.Equal(2, junk.Count);
        Assert.StartsWith("12 34 56", junk.Save());
    }
}
