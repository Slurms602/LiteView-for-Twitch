using System;
using LiteViewCore;
using Xunit;

public class ChatFilterTests
{
    [Fact]
    public void IgnoredUsersAreNormalisedLogins()
    {
        var f = new ChatFilter();
        f.SetIgnored("@Spammer1, botty_2\nnot-a-name!, directory  SPAMMER1");
        Assert.Equal("botty_2,spammer1", f.SaveIgnored());
        Assert.True(f.IsIgnored("Spammer1"));
        Assert.False(f.IsIgnored("someone"));
        Assert.False(f.IsIgnored(null));
        f.SetIgnored("");
        Assert.False(f.IsIgnored("spammer1"));
    }

    [Fact]
    public void HighlightWordsMatchWholeWordsAnyCase()
    {
        var f = new ChatFilter();
        f.SetWords("giveaway, c++, big news\nGIVEAWAY");
        Assert.Equal(new[] { "giveaway", "c++", "big news" }, f.Words);
        Assert.True(f.Highlights("GIVEAWAY time!"));
        Assert.True(f.Highlights("I love c++"));
        Assert.True(f.Highlights("there is Big News today"));
        Assert.False(f.Highlights("giveaways are over"));   // whole words only
        Assert.False(f.Highlights("bignews"));
        Assert.False(f.Highlights(null));
        f.SetWords(" , ,");
        Assert.False(f.Highlights("giveaway"));
    }

    [Fact]
    public void WordsAreCapped()
    {
        var f = new ChatFilter();
        f.SetWords(string.Join(",", new string('x', ChatFilter.MaxWordLength + 1), "ok"));
        Assert.Equal(new[] { "ok" }, f.Words);
        var many = new System.Text.StringBuilder();
        for (int i = 0; i < 200; i++) many.Append("w" + i + ",");
        f.SetWords(many.ToString());
        Assert.Equal(ChatFilter.MaxWords, f.Words.Count);
    }

    [Fact]
    public void StampsAreLocalHoursAndMinutes()
    {
        var utc = TimeZoneInfo.Utc;
        Assert.Equal("10:00", ChatFilter.Stamp("1763028000000", utc));   // 2025-11-13 10:00:00 UTC
        Assert.Equal("", ChatFilter.Stamp(null, utc));
        Assert.Equal("", ChatFilter.Stamp("-5", utc));
        Assert.Equal("", ChatFilter.Stamp("abc", utc));
        Assert.Equal("", ChatFilter.Stamp("99999999999999999", utc));
    }
}
