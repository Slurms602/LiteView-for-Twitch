using System;
using System.IO;
using System.Linq;
using LiteViewCore;
using Xunit;

public class SideButtonsTests
{
    [Fact]
    public void BothButtonsShowOrHideTheChatByDefault()
    {
        Assert.Equal(SideAction.Chat, SideButtons.Default);
        Assert.Equal(SideAction.Chat, SideButtons.Parse(null));   // an older liteview.ini has no such line
        Assert.Equal(SideButtons.Default, SideButtons.Choices[0]);
    }

    [Fact]
    public void EveryChoiceSavesAndLoadsBack()
    {
        foreach (var a in SideButtons.Choices) Assert.Equal(a, SideButtons.Parse(SideButtons.Save(a)));
        Assert.Equal(Enum.GetValues(typeof(SideAction)).Length, SideButtons.Choices.Distinct().Count());   // every action can be chosen
        Assert.Equal(SideButtons.Choices.Length, SideButtons.Choices.Select(SideButtons.Name).Distinct().Count());
    }

    [Theory]
    [InlineData("nothing", SideAction.Nothing)] [InlineData(" Mute ", SideAction.Mute)] [InlineData("FULLSCREEN", SideAction.Fullscreen)]
    [InlineData("", SideAction.Chat)] [InlineData("volume", SideAction.Chat)] [InlineData("1", SideAction.Chat)] [InlineData("chat\n", SideAction.Chat)]
    public void ParseIgnoresCaseAndFallsBackToTheDefault(string saved, SideAction expected) { Assert.Equal(expected, SideButtons.Parse(saved)); }

    [Theory]
    [InlineData(0x00010000L, false)] [InlineData(0x00010020L, false)]   // XBUTTON1 (back), with or without MK_XBUTTON1 in the low word
    [InlineData(0x00020000L, true)] [InlineData(0x00020040L, true)]     // XBUTTON2 (forward)
    public void WindowMessageButtons(long wParam, bool forward) { Assert.Equal(forward, SideButtons.IsForward(wParam)); }

    [Theory]
    [InlineData(0L)] [InlineData(0x00030000L)] [InlineData(0x00000001L)] [InlineData(-1L)]
    public void OtherWindowMessagesAreNoSideButton(long wParam) { Assert.Null(SideButtons.IsForward(wParam)); }

    [Fact]
    public void MpvLines()
    {
        Assert.Equal(false, SideButtons.FromMpvLine("{\"event\":\"client-message\",\"args\":[\"liteview-mouse\",\"back\"]}"));
        Assert.Equal(true, SideButtons.FromMpvLine("{\"event\":\"client-message\",\"args\":[\"liteview-mouse\",\"forward\"]}"));
        Assert.Null(SideButtons.FromMpvLine("{\"event\":\"client-message\",\"args\":[\"liteview-mouse\",\"up\"]}"));
        Assert.Null(SideButtons.FromMpvLine("{\"event\":\"client-message\",\"args\":[\"liteview-mouse\"]}"));
        Assert.Null(SideButtons.FromMpvLine("{\"event\":\"client-message\",\"args\":[\"liteview-click\"]}"));
        Assert.Null(SideButtons.FromMpvLine("{\"event\":\"property-change\",\"name\":\"liteview-mouse\",\"data\":\"back\"}"));
        Assert.Null(SideButtons.FromMpvLine("{\"event\":\"client-message\",\"args\":[\"liteview-mouse\",\"back\""));   // cut off
        Assert.Null(SideButtons.FromMpvLine(null));
        Assert.True(SideButtons.IsChatKey("{\"event\":\"client-message\",\"args\":[\"liteview-chat\"]}"));
        Assert.False(SideButtons.IsChatKey("{\"event\":\"client-message\",\"args\":[\"liteview-chat\",\"x\"]}"));
        Assert.False(SideButtons.IsChatKey("{\"event\":\"client-message\",\"args\":[\"liteview-stats\"]}"));
        Assert.False(SideButtons.IsChatKey(null));
    }

    [Fact]
    public void MpvSendsTheButtonsAndC()
    {
        var binds = File.ReadAllLines(Path.Combine(ConfigDir(), "mpv", "input.conf"))
            .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#"))
            .Select(l => l.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries))
            .ToDictionary(p => p[0], p => p[1].Trim());
        Assert.Equal("script-message liteview-mouse back", binds["MBTN_BACK"]);
        Assert.Equal("script-message liteview-mouse forward", binds["MBTN_FORWARD"]);
        Assert.Equal("script-message liteview-mouse back", binds["GO_BACK"]);      // the keyboard's Browser Back (some mouse software)
        Assert.Equal("script-message liteview-mouse forward", binds["GO_FORWARD"]);
        Assert.Equal("script-message liteview-chat", binds["c"]);
    }

    [Fact]
    public void SettingsImportKeepsTheButtons()
    {
        var d = SettingsImport.Filter(new System.Collections.Generic.Dictionary<string, string> { { "mouseback", "mute" }, { "MouseForward", "nothing" } });
        Assert.Equal("mute", d["mouseback"]); Assert.Equal("nothing", d["mouseforward"]);
    }

    static string ConfigDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            string p = Path.Combine(d.FullName, "config", "mpv", "input.conf");
            if (File.Exists(p)) return Path.Combine(d.FullName, "config");
        }
        throw new FileNotFoundException("config/mpv/input.conf");
    }
}
