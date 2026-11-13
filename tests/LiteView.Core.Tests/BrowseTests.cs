using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LiteViewCore;
using Xunit;

public class HelixDataTests
{
    const string Streams = "{\"data\":[" +
        "{\"user_id\":\"1\",\"user_login\":\"alpha\",\"user_name\":\"Alpha\",\"game_name\":\"Chess\",\"title\":\"line1\\nline2\",\"viewer_count\":1500,\"started_at\":\"2026-11-13T10:00:00Z\",\"language\":\"en\",\"thumbnail_url\":\"https://static-cdn.jtvnw.net/previews-ttv/live_user_alpha-{width}x{height}.jpg\"}," +
        "{\"user_id\":\"x\",\"user_login\":\"Bad Name\",\"user_name\":\"nope\"}," +
        "{\"user_id\":\"2\",\"user_login\":\"beta\",\"user_name\":\"\",\"viewer_count\":-5,\"title\":\"\\u202Eevil\"}" +
        "],\"pagination\":{\"cursor\":\"c1\"}}";

    [Fact]
    public void StreamsKeepOnlyWatchableChannelsWithCleanText()
    {
        var s = HelixData.Streams(Json.Parse(Streams));
        Assert.Equal(new[] { "alpha", "beta" }, s.Select(x => x.Login));
        Assert.Equal("Alpha", s[0].Name);
        Assert.Equal("line1 line2", s[0].Title);
        Assert.Equal(new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero), s[0].Started);
        Assert.Equal("1", s[0].UserId);
        Assert.Equal("beta", s[1].Name);          // empty display name: the login
        Assert.Equal(0, s[1].Viewers);            // never negative
        Assert.Equal("evil", s[1].Title);         // no right-to-left override
        Assert.Equal("c1", HelixData.Cursor(Json.Parse(Streams)));
        Assert.Empty(HelixData.Streams(Json.Parse("{}")));
        Assert.Empty(HelixData.Streams(null));
    }

    [Fact]
    public void TextIsOneLineAndCapped()
    {
        Assert.Equal("a b", HelixData.Text("a\r\n\tb"));
        Assert.Equal(HelixData.MaxText, HelixData.Text(new string('x', 5000)).Length);
        string emoji = new string('x', HelixData.MaxText - 1) + "😀";
        Assert.False(char.IsHighSurrogate(HelixData.Text(emoji).Last()));
        Assert.Equal("", HelixData.Text(null));
    }

    [Fact]
    public void FollowedAndSearchResults()
    {
        var f = HelixData.Followed(Json.Parse("{\"data\":[{\"broadcaster_id\":\"7\",\"broadcaster_login\":\"gamma\",\"broadcaster_name\":\"Gamma\",\"followed_at\":\"2024-01-02T03:04:05Z\"},{\"broadcaster_login\":\"directory\"}]}"));
        Assert.Single(f);   // "directory" is a Twitch page, not a channel
        Assert.Equal("Gamma", f[0].Name);
        Assert.Equal(2024, f[0].Followed!.Value.Year);

        var r = HelixData.Search(Json.Parse("{\"data\":[{\"id\":\"9\",\"broadcaster_login\":\"delta\",\"display_name\":\"Delta\",\"is_live\":true,\"game_name\":\"Art\",\"thumbnail_url\":\"https://static-cdn.jtvnw.net/jtv_user_pictures/d-profile_image-300x300.png\"}]}"));
        Assert.True(r[0].Live);
        Assert.Equal("Art", r[0].Game);
    }

    [Fact]
    public void ImageUrlsAreFilledInAndOnlyFromAllowedHosts()
    {
        Assert.Equal("https://static-cdn.jtvnw.net/previews-ttv/live_user_a-320x180.jpg", HelixData.Thumb("https://static-cdn.jtvnw.net/previews-ttv/live_user_a-{width}x{height}.jpg", 320, 180));
        Assert.Equal("", HelixData.Thumb("https://evil.example/x-{width}x{height}.jpg", 320, 180));
        Assert.Equal("", HelixData.Thumb("http://static-cdn.jtvnw.net/x-{width}x{height}.jpg", 320, 180));
        Assert.Equal("", HelixData.Thumb("https://static-cdn.jtvnw.net/x-{width}x{height}.jpg", 99999, 180));
        Assert.Equal("", HelixData.Thumb(null, 320, 180));
        Assert.Equal("https://static-cdn.jtvnw.net/jtv_user_pictures/a-profile_image-70x70.png", HelixData.Avatar("https://static-cdn.jtvnw.net/jtv_user_pictures/a-profile_image-300x300.png"));
        Assert.Equal("", HelixData.Avatar("https://evil.example/a-300x300.png"));
        var av = HelixData.Avatars(Json.Parse("{\"data\":[{\"login\":\"aa1\",\"profile_image_url\":\"https://static-cdn.jtvnw.net/a-300x300.png\"},{\"login\":\"bb1\",\"profile_image_url\":\"javascript:alert(1)\"},{\"login\":\"Not Valid\",\"profile_image_url\":\"https://static-cdn.jtvnw.net/b.png\"}]}"));
        Assert.Equal(new[] { "aa1" }, av.Keys);
    }
}

public class FmtAndSortTests
{
    [Theory]
    [InlineData(0, "0")] [InlineData(999, "999")] [InlineData(1000, "1K")] [InlineData(1250, "1.2K")] [InlineData(99999, "99.9K")]
    [InlineData(123456, "123K")] [InlineData(1250000, "1.2M")] [InlineData(-3, "0")]
    public void Viewers(long v, string s) { Assert.Equal(s, Fmt.Viewers(v)); }

    [Fact]
    public void Uptime()
    {
        var now = new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("45m", Fmt.Uptime(now.AddMinutes(-45), now));
        Assert.Equal("2h 05m", Fmt.Uptime(now.AddMinutes(-125), now));
        Assert.Equal("26h 00m", Fmt.Uptime(now.AddHours(-26), now));
        Assert.Equal("", Fmt.Uptime(now.AddMinutes(5), now));
        Assert.Equal("", Fmt.Uptime(null, now));
    }

    static LiveStream S(string login, long viewers, int startedMinutesAgo)
    {
        return new LiveStream { Login = login, Name = login.ToUpperInvariant(), Viewers = viewers, Started = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero).AddMinutes(-startedMinutesAgo) };
    }

    [Fact]
    public void SortOrdersAndFavouritesFirst()
    {
        var list = new[] { S("b", 10, 30), S("a", 500, 300), S("c", 100, 5), S("d", 100, 60) };
        Assert.Equal("acdb", string.Concat(Sorting.Sort(list, SortOrder.Viewers, null, false).Select(x => x.Login)));   // ties by login
        Assert.Equal("abcd", string.Concat(Sorting.Sort(list, SortOrder.Name, null, false).Select(x => x.Login)));
        Assert.Equal("cbda", string.Concat(Sorting.Sort(list, SortOrder.Newest, null, false).Select(x => x.Login)));
        Assert.Equal("adbc", string.Concat(Sorting.Sort(list, SortOrder.Longest, null, false).Select(x => x.Login)));
        Assert.Equal("dbac", string.Concat(Sorting.Sort(list, SortOrder.Viewers, new[] { "d", "b" }, true).Select(x => x.Login)));
        Assert.Equal("acdb", string.Concat(Sorting.Sort(list, SortOrder.Viewers, new[] { "d", "b" }, false).Select(x => x.Login)));
    }
}

public class ChannelListTests
{
    [Fact]
    public void RecentListIsNewestFirstAndCapped()
    {
        var l = new ChannelList(3, newestFirst: true);
        Assert.True(l.Add("a1")); Assert.True(l.Add("b1")); Assert.True(l.Add("c1"));
        Assert.True(l.Add("a1"));                      // moved to the front
        Assert.Equal(new[] { "a1", "c1", "b1" }, l.Items);
        Assert.False(l.Add("a1"));                     // already first
        l.Add("d1");
        Assert.Equal(new[] { "d1", "a1", "c1" }, l.Items);
        Assert.True(l.Add(" E1 "));
        Assert.Equal("e1", l.Items[0]);
        Assert.False(l.Add("twitch.tv/f1"));            // a name, not something that contains one
        Assert.False(l.Add("not a name!"));
        Assert.False(l.Add("directory"));
    }

    [Fact]
    public void FavouritesKeepTheirOrderWithoutDuplicates()
    {
        var l = new ChannelList(2, newestFirst: false);
        Assert.True(l.Add("x1")); Assert.True(l.Add("y1"));
        Assert.False(l.Add("x1")); Assert.False(l.Add("z1"));   // full
        Assert.Equal(new[] { "x1", "y1" }, l.Items);
        Assert.True(l.Remove("x1")); Assert.False(l.Remove("x1"));
        Assert.True(l.Contains("y1"));
    }

    [Fact]
    public void SavedFormRoundTripsAndIgnoresJunk()
    {
        var l = new ChannelList(5, newestFirst: true);
        l.Load("aa1,BB2,,bad name,aa1,directory,cc3");
        Assert.Equal(new[] { "aa1", "bb2", "cc3" }, l.Items);
        Assert.Equal("aa1,bb2,cc3", l.Save());
        var m = new ChannelList(5, newestFirst: true); m.Load(l.Save());
        Assert.Equal(l.Items, m.Items);
        l.Load(null); Assert.Equal(0, l.Count);
    }
}

public class CacheAndGuardTests
{
    [Fact]
    public void TimedCacheExpiresAndDropsTheOldest()
    {
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var c = new TimedCache<string, int>(2, TimeSpan.FromSeconds(60), () => t);
        c.Set("a", 1); c.Set("b", 2);
        int v;
        Assert.True(c.TryGet("a", out v)); Assert.Equal(1, v);
        c.Set("c", 3);                                  // "a" is the oldest entry
        Assert.False(c.TryGet("a", out v));
        Assert.True(c.TryGet("b", out v));
        t = t.AddSeconds(61);
        Assert.False(c.TryGet("b", out v));             // expired
        Assert.Equal(1, c.Count);
        c.Set("c", 4); Assert.True(c.TryGet("c", out v)); Assert.Equal(4, v);
    }

    [Fact]
    public void OnlyTheNewestTicketIsCurrent()
    {
        var g = new Latest();
        int a = g.Next(), b = g.Next();
        Assert.False(g.IsCurrent(a));
        Assert.True(g.IsCurrent(b));
    }

    [Fact]
    public void SearchTextIsCleaned()
    {
        Assert.Null(SearchText.Clean("   "));
        Assert.Null(SearchText.Clean(null));
        Assert.Equal("a b", SearchText.Clean(" a\nb "));
        Assert.Equal(SearchText.MaxLength, SearchText.Clean(new string('q', 500))!.Length);
    }
}

public class FollowingTests
{
    readonly List<string> asked = new List<string>();
    readonly Dictionary<string, string?> answers = new Dictionary<string, string?>();

    Following Make()
    {
        return new Following(q =>
        {
            asked.Add(q);
            string? key = answers.Keys.FirstOrDefault(k => q.StartsWith(k, StringComparison.Ordinal));
            return Task.FromResult(key == null || answers[key] == null ? null : Json.Parse(answers[key]));
        });
    }

    static string Page(string login, string? cursor)
    {
        return "{\"data\":[{\"user_id\":\"1\",\"user_login\":\"" + login + "\",\"viewer_count\":1}],\"pagination\":{" + (cursor != null ? "\"cursor\":\"" + cursor + "\"" : "") + "}}";
    }

    [Fact]
    public async Task LiveFollowsTheCursorAndDropsDuplicates()
    {
        answers["streams/followed?user_id=42&first=100&after=p2"] = Page("bb1", null);
        answers["streams/followed?user_id=42&first=100"] = Page("aa1", "p2");
        // the second page repeats nothing here; a channel that moved between pages would be listed once
        var r = await Make().Live("42");
        Assert.Equal(FeedState.Ok, r.Key);
        Assert.Equal(new[] { "aa1", "bb1" }, r.Value.Select(s => s.Login));
        Assert.Equal(2, asked.Count);
    }

    [Fact]
    public async Task LiveStatesWhenLoggedOutOrUnreachable()
    {
        var f = Make();
        Assert.Equal(FeedState.NotLoggedIn, (await f.Live(null)).Key);
        Assert.Equal(FeedState.NotLoggedIn, (await f.Live("abc")).Key);
        Assert.Empty(asked);                             // no request without a user id
        Assert.Equal(FeedState.Unreachable, (await f.Live("42")).Key);
    }

    [Fact]
    public async Task ALaterPageFailingKeepsWhatArrived()
    {
        answers["streams/followed?user_id=42&first=100&after=p2"] = null;
        answers["streams/followed?user_id=42&first=100"] = Page("aa1", "p2");
        var r = await Make().Live("42");
        Assert.Equal(FeedState.Ok, r.Key);
        Assert.Single(r.Value);
    }

    [Fact]
    public async Task LivePagesAreCapped()
    {
        answers["streams/followed"] = Page("aa1", "again");   // a cursor that never ends
        var r = await Make().Live("42");
        Assert.Equal(Following.MaxLivePages, asked.Count);
        Assert.Single(r.Value);
    }

    [Fact]
    public async Task AvatarsAreFetchedAHundredAtATime()
    {
        answers["users?login="] = "{\"data\":[]}";
        await Make().Avatars(Enumerable.Range(1, 250).Select(i => "user" + i).Concat(new[] { "user1", "bad name" }));
        Assert.Equal(3, asked.Count);
        Assert.Equal(100, asked[0].Split(new[] { "login=" }, StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("bad", asked[2]);
    }

    [Fact]
    public async Task LiveNowAsksByLoginAndReportsUnreachable()
    {
        answers["streams?first=100&user_login="] = Page("aa1", null);
        var r = await Make().LiveNow(new[] { "aa1", "bb1", "x y" });
        Assert.Equal("streams?first=100&user_login=aa1&user_login=bb1", asked.Single());
        Assert.Equal("aa1", r!.Single().Login);
        answers.Clear();
        Assert.Null(await Make().LiveNow(new[] { "aa1" }));
        Assert.Empty((await Make().LiveNow(Array.Empty<string>()))!);
    }

    [Fact]
    public async Task SearchEncodesTheQueryAndPutsLiveChannelsFirst()
    {
        answers["search/channels"] = "{\"data\":[{\"id\":\"1\",\"broadcaster_login\":\"off1\",\"is_live\":false},{\"id\":\"2\",\"broadcaster_login\":\"on1\",\"is_live\":true},{\"id\":\"3\",\"broadcaster_login\":\"off2\",\"is_live\":false}]}";
        var r = await Make().Search(" a&b c ");
        Assert.Equal("search/channels?first=20&query=a%26b%20c", asked.Single());
        Assert.Equal(new[] { "on1", "off1", "off2" }, r!.Select(c => c.Login));
        Assert.Empty((await Make().Search("  "))!);
        Assert.Single(asked);                            // an empty search asks nothing
        answers.Clear();
        Assert.Null(await Make().Search("x"));           // unreachable is not "nothing found"
    }

    [Fact]
    public async Task FollowedChannelsPaginate()
    {
        answers["channels/followed?user_id=42&first=100&after=n"] = "{\"data\":[{\"broadcaster_id\":\"2\",\"broadcaster_login\":\"bb1\"}],\"pagination\":{}}";
        answers["channels/followed?user_id=42&first=100"] = "{\"data\":[{\"broadcaster_id\":\"1\",\"broadcaster_login\":\"aa1\"}],\"pagination\":{\"cursor\":\"n\"}}";
        var r = await Make().Followed("42");
        Assert.Equal(new[] { "aa1", "bb1" }, r.Value.Select(c => c.Login));
    }
}
