using System;
using System.Collections.Generic;
using LiteViewCore;
using Xunit;

public class ExtrasTests
{
    [Fact]
    public void LiveWatchReportsOnlyNewlyLiveChannelsAfterTheFirstLook()
    {
        var w = new LiveWatch();
        Assert.Empty(w.Update(new[] { "aa1", "bb1" }));               // first look: no burst
        Assert.Empty(w.Update(new[] { "aa1", "bb1" }));
        Assert.Equal(new[] { "cc1" }, w.Update(new[] { "aa1", "cc1" }));   // bb1 went offline, cc1 live
        Assert.Equal(new[] { "bb1" }, w.Update(new[] { "aa1", "bb1", "cc1" }));
        w.Reset();
        Assert.Empty(w.Update(new[] { "dd1" }));
    }

    [Theory]
    [InlineData("23-8", 23, true)] [InlineData("23-8", 3, true)] [InlineData("23-8", 8, false)] [InlineData("23-8", 12, false)]
    [InlineData("13-14", 13, true)] [InlineData("13-14", 14, false)] [InlineData("", 3, false)] [InlineData("off", 3, false)]
    [InlineData("5-5", 5, false)] [InlineData("25-3", 1, false)] [InlineData(" 22 - 7 ", 6, true)]
    public void QuietHoursWrapAroundMidnight(string setting, int hour, bool quiet)
    {
        Assert.Equal(quiet, QuietHours.IsQuiet(setting, new DateTime(2026, 11, 13, hour, 30, 0)));
    }

    [Fact]
    public void ImportKeepsOnlyKnownOneLineSettings()
    {
        var d = SettingsImport.Filter(new Dictionary<string, string>
        {
            { "Volume", "40" }, { "favorites", "aa1,bb1" }, { "token", "oauth:secret" }, { "searches", "a\nb" },
            { "recent", new string('x', SettingsImport.MaxValue + 1) }, { "streamlink", "C:\\evil.exe" },
        });
        Assert.Equal(new[] { "volume", "favorites" }, d.Keys);
        Assert.Equal("40", d["volume"]);
    }

    [Theory]
    [InlineData("v0.2.0", "0.1.0", true)] [InlineData("v0.1.0", "0.1.0", false)] [InlineData("0.1.1", "0.1.0-ci+abc", true)]
    [InlineData("v1.0", "0.9.9", true)] [InlineData("v0.0.9", "0.1.0", false)] [InlineData("v0.3.0-beta", "0.1.0", false)]
    [InlineData("nightly", "0.1.0", false)] [InlineData("v0.2.0", "dev", false)] [InlineData(null, "0.1.0", false)]
    public void ReleaseIsNewer(string? tag, string current, bool newer) { Assert.Equal(newer, Versions.IsNewer(tag, current)); }
}

public class ReviewRegressionTests
{
    [Fact]
    public void OtherScriptsDigitsAreNotNumbers()
    {
        Assert.False(QuietHours.IsQuiet("١-٢", new DateTime(2026, 1, 1, 1, 30, 0)));   // Arabic-Indic digits: not a crash, not quiet
        int f, t; Assert.False(QuietHours.TryParse("١-٢", out f, out t));
        Assert.Null(Versions.Parse("v١.٢.٣"));
        Assert.False(Versions.IsNewer("v٩.0.0", "0.1.0"));
    }

    [Fact]
    public void QualityVariantNames()
    {
        Assert.True(Quality.IsVariant("360p")); Assert.True(Quality.IsVariant("1440p60"));
        Assert.False(Quality.IsVariant("Best")); Assert.False(Quality.IsVariant("360p,best")); Assert.False(Quality.IsVariant(null));
    }
}
