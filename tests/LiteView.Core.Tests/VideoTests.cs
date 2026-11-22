using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LiteViewCore;
using Xunit;

public class VideoTests
{
    static readonly DateTimeOffset Now = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("3h8m33s", 11313)]
    [InlineData("45m2s", 2702)]
    [InlineData("59s", 59)]
    [InlineData("1h", 3600)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    [InlineData("3h8m33s; rm", 0)]
    [InlineData("99999h", 0)]
    [InlineData("12:34", 0)]
    public void HelixDurations(string? s, int seconds) { Assert.Equal(seconds, VideoData.Duration(s)); }

    [Theory]
    [InlineData("AwkwardHelplessSalamanderSwiftRage-abc_123", true)]
    [InlineData("a", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("x y", false)]
    [InlineData("x\"--player=calc", false)]
    [InlineData("../../x", false)]
    [InlineData("x\n", false)]
    public void ClipSlugs(string? s, bool ok) { Assert.Equal(ok, VideoData.IsClipSlug(s)); }

    [Fact]
    public void VideosNeedANumericIdAndAValidChannel()
    {
        var j = Json.Parse("{\"data\":[" +
            "{\"id\":\"2001\",\"user_login\":\"nightowl\",\"user_name\":\"NightOwl\",\"title\":\"long\\nstream\",\"type\":\"archive\",\"duration\":\"3h8m33s\",\"view_count\":12345,\"created_at\":\"2026-11-12T10:00:00Z\",\"thumbnail_url\":\"https://static-cdn.jtvnw.net/cf_vods/x/thumb/thumb0-%{width}x%{height}.jpg\"}," +
            "{\"id\":\"2002\",\"user_login\":\"nightowl\",\"type\":\"highlight\",\"duration\":\"10m\"}," +
            "{\"id\":\"20x3\",\"user_login\":\"nightowl\"},{\"id\":\"2004\",\"user_login\":\"bad login\"},{\"id\":\"2001\",\"user_login\":\"nightowl\"}]}");
        var v = VideoData.Videos(j);
        Assert.Equal(new[] { "2001", "2002" }, v.Select(x => x.Id));
        Assert.Equal("NightOwl", v[0].Name); Assert.Equal("long stream", v[0].Title);
        Assert.Equal(VideoKind.Archive, v[0].Kind); Assert.Equal(VideoKind.Highlight, v[1].Kind);
        Assert.Equal(11313, v[0].Seconds); Assert.Equal(12345, v[0].Views);
        Assert.Equal("https://static-cdn.jtvnw.net/cf_vods/x/thumb/thumb0-320x180.jpg", VideoData.Thumb(v[0].Thumb, 320, 180));
        Assert.Equal("", VideoData.Thumb("https://evil.example/thumb-%{width}x%{height}.jpg", 320, 180));
    }

    [Fact]
    public void ClipsNeedASafeIdAndAreMostViewedFirst()
    {
        var j = Json.Parse("{\"data\":[" +
            "{\"id\":\"SmallClip-1\",\"title\":\"a\",\"creator_name\":\"Fan\",\"view_count\":5,\"duration\":12.6,\"thumbnail_url\":\"https://static-cdn.jtvnw.net/twitch-clips-thumbnails-prod/x/preview-480x272.jpg\"}," +
            "{\"id\":\"BigClip_2\",\"title\":\"b\",\"view_count\":900,\"duration\":30}," +
            "{\"id\":\"bad clip\",\"view_count\":99999},{\"id\":\"x\\\"y\",\"view_count\":1}]}");
        var c = VideoData.Clips(j, "nightowl", "NightOwl");
        Assert.Equal(new[] { "BigClip_2", "SmallClip-1" }, c.Select(x => x.Id));
        Assert.Equal(13, c[1].Seconds); Assert.Equal("Fan", c[1].Creator); Assert.True(c[0].IsClip);
        Assert.Equal("nightowl", c[0].Login); Assert.Equal("NightOwl", c[0].Name);
        Assert.Empty(VideoData.Clips(j, "bad login", ""));
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(5, "0:05")]
    [InlineData(754, "12:34")]
    [InlineData(3723, "1:02:03")]
    [InlineData(400000, "111:06:40")]
    public void Lengths(int s, string shown) { Assert.Equal(shown, VideoData.Length(s)); }

    [Fact]
    public void Ages()
    {
        Assert.Equal("just now", VideoData.Ago(Now.AddMinutes(-5), Now));
        Assert.Equal("1 hour ago", VideoData.Ago(Now.AddMinutes(-90), Now));
        Assert.Equal("5 hours ago", VideoData.Ago(Now.AddHours(-5), Now));
        Assert.Equal("yesterday", VideoData.Ago(Now.AddHours(-30), Now));
        Assert.Equal("3 days ago", VideoData.Ago(Now.AddDays(-3), Now));
        Assert.Equal("3 weeks ago", VideoData.Ago(Now.AddDays(-21), Now));
        Assert.Equal("5 months ago", VideoData.Ago(Now.AddDays(-150), Now));
        Assert.Equal("2 years ago", VideoData.Ago(Now.AddDays(-800), Now));
        Assert.Equal("just now", VideoData.Ago(Now.AddDays(1), Now));
        Assert.Equal("", VideoData.Ago(null, Now));
    }

    [Fact]
    public void TheStatusLineOfAVideo()
    {
        var v = new VideoInfo { Kind = VideoKind.Archive, Seconds = 3723, Views = 12345, Created = Now.AddDays(-3), Title = "Speedrun" };
        Assert.Equal("Past broadcast  ·  1:02:03  ·  12.3K views  ·  3 days ago  ·  Speedrun", VideoData.Line(v, Now));
        var c = new VideoInfo { Kind = VideoKind.Clip, Creator = "Fan", Seconds = 30, Views = 1 };
        Assert.Equal("Clip by Fan  ·  0:30  ·  1 view", VideoData.Line(c, Now));
    }

    [Fact]
    public async Task VideosAskForTheChannelsIdFirstAndKeepIt()
    {
        var asked = new List<string>();
        var cv = new ChannelVideos(q =>
        {
            asked.Add(q);
            if (q.StartsWith("users?", StringComparison.Ordinal)) return Task.FromResult(Json.Parse("{\"data\":[{\"id\":\"777\",\"login\":\"nightowl\"}]}"));
            return Task.FromResult(Json.Parse("{\"data\":[]}"));
        });
        Assert.Empty((await cv.Videos("nightowl"))!.Items);
        Assert.Empty((await cv.Clips("nightowl", "NightOwl", Now))!.Items);
        Assert.Equal("users?login=nightowl", asked[0]);
        Assert.Equal("videos?user_id=777&first=40&type=all&sort=time", asked[1]);
        Assert.Equal("clips?broadcaster_id=777&first=40&started_at=2026-10-16T12%3A00%3A00Z&ended_at=2026-11-15T12%3A00%3A00Z", asked[2]);
        Assert.Equal(3, asked.Count);   // the id was kept
        Assert.Empty((await cv.Videos("bad login"))!.Items);
        Assert.Equal(3, asked.Count);
    }

    [Fact]
    public async Task MoreVideosByKindAndPageAndClipsOfAllTime()
    {
        var asked = new List<string>();
        string answer = "{\"data\":[],\"pagination\":{\"cursor\":\"eyJiIjpudWxsfQ==\"}}";
        var cv = new ChannelVideos(q =>
        {
            asked.Add(q);
            if (q.StartsWith("users?", StringComparison.Ordinal)) return Task.FromResult(Json.Parse("{\"data\":[{\"id\":\"777\",\"login\":\"nightowl\"}]}"));
            return Task.FromResult(Json.Parse(answer));
        });
        var p = (await cv.Videos("nightowl", "highlight"))!;
        Assert.Equal("eyJiIjpudWxsfQ==", p.Next);
        await cv.Videos("nightowl", "upload", p.Next);
        await cv.Videos("nightowl", "calc.exe");          // an unknown kind is "all"
        await cv.Clips("nightowl", "NightOwl", Now, 0);    // all time: no dates
        await cv.Clips("nightowl", "NightOwl", Now, 7, "a&b=c");   // a cursor with other characters is not sent
        Assert.Equal("videos?user_id=777&first=40&type=highlight&sort=time", asked[1]);
        Assert.Equal("videos?user_id=777&first=40&type=upload&sort=time&after=eyJiIjpudWxsfQ%3D%3D", asked[2]);
        Assert.Equal("videos?user_id=777&first=40&type=all&sort=time", asked[3]);
        Assert.Equal("clips?broadcaster_id=777&first=40", asked[4]);
        Assert.StartsWith("clips?broadcaster_id=777&first=40&started_at=2026-11-08", asked[5]);
        Assert.DoesNotContain("after", asked[5]);
        answer = "{\"data\":[],\"pagination\":{\"cursor\":\"x y\"}}";
        Assert.Null((await cv.Videos("nightowl"))!.Next);   // an unsafe cursor ends the paging
        answer = "{\"data\":[],\"pagination\":{}}";
        Assert.Null((await cv.Videos("nightowl"))!.Next);
    }

    [Fact]
    public async Task UnreachableIsNullAndUnknownChannelsAreEmpty()
    {
        Assert.Null(await new ChannelVideos(q => Task.FromResult<object?>(null)).Videos("nightowl"));
        Assert.Empty((await new ChannelVideos(q => Task.FromResult(Json.Parse("{\"data\":[]}"))).Videos("nobody"))!.Items);
    }
}
