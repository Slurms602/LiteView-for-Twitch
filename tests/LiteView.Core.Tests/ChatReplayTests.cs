using System;
using System.Linq;
using LiteViewCore;
using Xunit;

public class ChatReplayTests
{
    // the shape Twitch answered on 2026-11-15 (names and text changed)
    const string Sample = @"{""data"":{""video"":{""comments"":{""edges"":[
 {""node"":{""id"":""149d3d25-514b-4ce0-b79e-395fd468448f"",""contentOffsetSeconds"":3593,""createdAt"":""2026-11-14T05:23:35.471Z"",
   ""commenter"":{""login"":""someviewer"",""displayName"":""SomeViewer""},
   ""message"":{""userColor"":""#008000"",""fragments"":[{""text"":""hello "",""emote"":null},{""text"":""PopNemo"",""emote"":{""emoteID"":""emotesv2_5d523adb8bbb4786821cd7091e47da21""}},{""text"":"" 😂 and "",""emote"":null},{""text"":""Kappa"",""emote"":{""emoteID"":""25""}}],
   ""userBadges"":[{""setID"":""subscriber"",""version"":""12""},{""setID"":""premium"",""version"":""1""}]}}},
 {""node"":{""id"":""70997 5c6"",""contentOffsetSeconds"":3594,""commenter"":{""login"":""x""},""message"":{""fragments"":[{""text"":""bad id""}]}}},
 {""node"":{""id"":""a1"",""contentOffsetSeconds"":3590,""commenter"":null,""message"":{""fragments"":[{""text"":""deleted user""}]}}},
 {""node"":{""id"":""a2"",""contentOffsetSeconds"":3591.5,""createdAt"":""nonsense"",""commenter"":{""login"":""Mod_Person"",""displayName"":""Mod\u0000Person\r\n""},
   ""message"":{""userColor"":""red; color=#000000"",""fragments"":[{""text"":""line\r\nbreak \u0001ACTION x\u0001""}],""userBadges"":[{""setID"":""../../x"",""version"":""1""},{""setID"":""moderator"",""version"":""1""}]}}}
 ],""pageInfo"":{""hasNextPage"":true}}}}}";

    [Fact]
    public void CommentsBecomeChatLines()
    {
        var page = ChatReplay.Parse(Json.Parse(Sample), "streamer")!;
        Assert.True(page.HasNext); Assert.Null(page.Error);
        Assert.Equal(new[] { "a2", "149d3d25-514b-4ce0-b79e-395fd468448f" }, page.Comments.Select(c => c.Id).ToArray());   // in position order; bad id and no user left out
        var c = page.Comments[1].Msg;
        Assert.Equal("PRIVMSG", c.Cmd); Assert.Equal("#streamer", c.Chan); Assert.Equal("someviewer", c.Nick);
        Assert.Equal("hello PopNemo 😂 and Kappa", c.Text);
        Assert.Equal("SomeViewer", c.Tag("display-name")); Assert.Equal("#008000", c.Tag("color"));
        Assert.Equal("subscriber/12,premium/1", c.Tag("badges"));
        // code points: "hello " is 6, PopNemo 6-12, " 😂 and " is 7 code points (13-19), Kappa 20-24
        Assert.Equal("emotesv2_5d523adb8bbb4786821cd7091e47da21:6-12/25:20-24", c.Tag("emotes"));
        Assert.Equal(DateTimeOffset.Parse("2026-11-14T05:23:35.471Z").ToUnixTimeMilliseconds().ToString(), c.Tag("tmi-sent-ts"));
        var m = page.Comments[0].Msg;
        Assert.Equal("mod_person", m.Nick); Assert.Equal("ModPerson", m.Tag("display-name"));
        Assert.Equal("", m.Tag("color"));                 // not #RRGGBB
        Assert.Equal("moderator/1", m.Tag("badges"));    // the path-like badge is left out
        Assert.Equal("linebreak ACTION x", m.Text);       // no control characters
        Assert.Null(m.Tag("tmi-sent-ts"));
        Assert.Equal(3591.5, page.Comments[0].Offset);
    }

    [Fact]
    public void ErrorsAndJunk()
    {
        var refused = ChatReplay.Parse(Json.Parse(@"{""errors"":[{""message"":""failed integrity check""}],""data"":{""video"":{""comments"":null}}}"), "streamer")!;
        Assert.Equal("failed integrity check", refused.Error); Assert.Empty(refused.Comments);
        var none = ChatReplay.Parse(Json.Parse(@"{""data"":{""video"":null}}"), "streamer")!;
        Assert.NotNull(none.Error);
        Assert.Null(ChatReplay.Parse(null, "streamer"));
        Assert.Null(ChatReplay.Parse(Json.Parse("[1,2]"), "streamer"));
        Assert.Null(ChatReplay.Parse(Json.Parse(Sample), "bad channel"));
        var many = "{\"data\":{\"video\":{\"comments\":{\"edges\":[" + string.Join(",", Enumerable.Range(0, 2000).Select(i => "{\"node\":{\"id\":\"i" + i + "\",\"contentOffsetSeconds\":" + i + ",\"commenter\":{\"login\":\"a\"},\"message\":{\"fragments\":[{\"text\":\"x\"}]}}}")) + "]}}}}";
        Assert.Equal(ChatReplay.MaxPerPage, ChatReplay.Parse(Json.Parse(many), "streamer")!.Comments.Count);
        var longText = "{\"data\":{\"video\":{\"comments\":{\"edges\":[{\"node\":{\"id\":\"l\",\"contentOffsetSeconds\":1,\"commenter\":{\"login\":\"a\"},\"message\":{\"fragments\":[{\"text\":\"" + new string('y', 5000) + "\"}]}}}]}}}}";
        Assert.Equal(ChatReplay.MaxTextChars, ChatReplay.Parse(Json.Parse(longText), "streamer")!.Comments[0].Msg.Text!.Length);
    }

    [Theory]
    [InlineData("123", 3600, "contentOffsetSeconds:3600")]
    [InlineData("123", -5, "contentOffsetSeconds:0")]
    public void QueriesAskByPosition(string video, int offset, string part)
    {
        string q = ChatReplay.Query(video, offset);
        Assert.Contains(part, q);
        Assert.Contains("video(id:\\\"123\\\")", q);
        Assert.NotNull(Json.Parse(q));   // valid JSON
    }

    [Theory]
    [InlineData("12a")]
    [InlineData("1\\\"){x}")]
    [InlineData("")]
    public void QueriesNeedANumericVideoId(string video)
    {
        Assert.Throws<ArgumentException>(() => ChatReplay.Query(video, 0));
    }

    static ReplayPage Page(bool next, params (string id, double at)[] cs)
    {
        var p = new ReplayPage { HasNext = next };
        foreach (var c in cs) p.Comments.Add(new ReplayComment { Id = c.id, Offset = c.at });
        return p;
    }

    [Fact]
    public void TheBufferHandsOutWhatTheVideoReached()
    {
        var b = new ReplayBuffer();
        Assert.False(b.NeedsFetch(0));   // not started
        b.Reset(100);
        Assert.Equal(90, b.NextOffset());   // a little before, for context
        Assert.True(b.NeedsFetch(100));
        b.Add(Page(true, ("a", 85), ("b", 92), ("c", 101), ("d", 150)));
        Assert.Equal(150, b.FetchedTo);
        Assert.Equal(new[] { "b" }, b.Due(100).Select(c => c.Id).ToArray());   // "a" is before the context
        Assert.Empty(b.Due(100.5));
        Assert.Equal(new[] { "c" }, b.Due(120).Select(c => c.Id).ToArray());
        Assert.False(b.NeedsFetch(119));   // 150 is more than 30 s ahead of 119
        Assert.True(b.NeedsFetch(121));
        b.Add(Page(true, ("d", 150), ("e", 151)));   // the next page repeats the last comment
        Assert.Equal(new[] { "d", "e" }, b.Due(200).Select(c => c.Id).ToArray());
        b.Add(Page(true, ("e", 151)));   // no step forward: one second further next time
        Assert.Equal(152, b.FetchedTo);
        b.Add(Page(false));
        Assert.True(b.End); Assert.False(b.NeedsFetch(1000));
        b.Reset(10);   // a seek starts over
        Assert.False(b.End); Assert.Equal(0, b.Pending); Assert.Equal(0, b.NextOffset());
    }
}
