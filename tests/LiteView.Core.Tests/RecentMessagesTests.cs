using System.Linq;
using LiteViewCore;
using Xunit;

public class RecentMessagesTests
{
    const string A = "@id=1;tmi-sent-ts=1794711492646;historical=1;display-name=Foo :foo!foo@foo.tmi.twitch.tv PRIVMSG #bar :hello there";
    const string OneWord = "@id=2;historical=1 :foo!foo@foo.tmi.twitch.tv PRIVMSG #bar >continue";
    const string Sub = "@id=3;login=x;system-msg=x\\ssubscribed :tmi.twitch.tv USERNOTICE #bar :hi";

    static string Answer(params string[] lines)
    {
        return "{\"messages\":[" + string.Join(",", lines.Select(l => "\"" + l.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"")) + "],\"error\":null,\"error_code\":null}";
    }

    [Fact]
    public void UrlOnlyForChannelNames()
    {
        Assert.Equal("https://recent-messages.robotty.de/api/v2/recent-messages/bar?limit=50&hide_moderation_messages=true&hide_moderated_messages=true", RecentMessages.Url("bar"));
        Assert.Null(RecentMessages.Url("bar/../x"));
        Assert.Null(RecentMessages.Url("bar?limit=1"));
        Assert.Null(RecentMessages.Url(null));
        Assert.True(SafeUrl.IsAllowedApi(RecentMessages.Url("bar")));
    }

    [Fact]
    public void KeepsChatLinesOfTheChannel()
    {
        var got = RecentMessages.Lines(Answer(A, OneWord, Sub), "bar");
        Assert.Equal(new[] { A, OneWord, Sub }, got);
    }

    [Fact]
    public void OneWordMessageWithoutColonHasItsText()
    {
        Assert.Equal(">continue", IrcMsg.Parse(OneWord)!.Text);
        Assert.Equal("bar", IrcMsg.Parse(OneWord)!.Chan);
    }

    [Fact]
    public void DropsOtherChannelsCommandsAndJunk()
    {
        var got = RecentMessages.Lines(Answer(
            A.Replace("#bar", "#other"),
            "@id=9 :tmi.twitch.tv CLEARCHAT #bar :foo",
            ":tmi.twitch.tv ROOMSTATE #bar",
            "PRIVMSG",
            "",
            A + "\r\nPRIVMSG #bar :injected",
            A), "bar");
        Assert.Equal(new[] { A }, got);
        Assert.Empty(RecentMessages.Lines("{\"messages\":[1,true,null,{\"a\":\"b\"}]}", "bar"));
    }

    [Fact]
    public void BadAnswersGiveNothing()
    {
        Assert.Empty(RecentMessages.Lines(null, "bar"));
        Assert.Empty(RecentMessages.Lines("", "bar"));
        Assert.Empty(RecentMessages.Lines("<html>502</html>", "bar"));
        Assert.Empty(RecentMessages.Lines("{\"messages\":[],\"error\":\"The channel has not been joined yet\",\"error_code\":\"channel_not_joined\"}", "bar"));
        Assert.Empty(RecentMessages.Lines("{\"messages\":\"x\"}", "bar"));
    }

    [Fact]
    public void KeepsOnlyTheNewestLimit()
    {
        var lines = Enumerable.Range(0, 80).Select(i => "@id=" + i + " :foo!foo@foo.tmi.twitch.tv PRIVMSG #bar :m" + i).ToArray();
        var got = RecentMessages.Lines(Answer(lines), "bar");
        Assert.Equal(RecentMessages.Limit, got.Count);
        Assert.EndsWith(":m30", got[0]);
        Assert.EndsWith(":m79", got[got.Count - 1]);
    }

    [Fact]
    public void ChannelCaseDoesNotMatter()
    {
        Assert.Single(RecentMessages.Lines(Answer(A.Replace("#bar", "#Bar")), "bar"));
    }
}
