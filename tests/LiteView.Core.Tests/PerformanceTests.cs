using LiteViewCore;
using Xunit;

public class PerformanceTests
{
    const ulong All24 = 0xFFFFFF, CcdB = 0xFFF000;   // a 12-core / 24-thread CPU, its second die

    [Fact]
    public void AutomaticAvoidsTheVCacheDieOnlyWhenThereIsOne()
    {
        var x3d = Scheduling.Plan("auto", null, CcdB, All24);
        Assert.Equal(CcdB, x3d.Mask);
        Assert.Equal(CpuPriority.BelowNormal, x3d.Priority);
        var plain = Scheduling.Plan(null, null, 0, All24);           // 9900X, Intel, one die: CpuTopology says 0
        Assert.Equal(0ul, plain.Mask);
        Assert.Equal(0ul, Scheduling.Plan("auto", null, All24, All24).Mask);   // "every core" is no pinning
    }

    [Fact]
    public void WindowsModeChangesNothing()
    {
        var p = Scheduling.Plan("Windows", null, CcdB, All24);
        Assert.Equal(0ul, p.Mask);
        Assert.Equal(CpuPriority.Normal, p.Priority);
    }

    [Fact]
    public void CustomMaskIsLimitedToExistingCores()
    {
        Assert.Equal(0xF0ul, Scheduling.Plan("custom", "0xF0", 0, All24).Mask);
        Assert.Equal(0xFFul, Scheduling.Plan("custom", "FFFF00000000FF", 0, All24).Mask);   // cores that do not exist are dropped
        Assert.Equal(CcdB, Scheduling.Plan("custom", "F000000000", CcdB, All24).Mask);      // nothing left: automatic
        Assert.Contains("no usable core", Scheduling.Plan("custom", "zz", CcdB, All24).Why);
        Assert.Equal(0ul, Scheduling.Plan("custom", "FFFFFF", CcdB, All24).Mask);            // all cores: no pinning
    }

    [Theory]
    [InlineData("FFF000", 0xFFF000)] [InlineData("0xff", 0xFF)] [InlineData(" 1 ", 1)] [InlineData("", 0)] [InlineData("12345678901234567", 0)] [InlineData("-1", 0)]
    public void Masks(string s, ulong v) { Assert.Equal(v, Scheduling.ParseMask(s)); }

    static readonly Box Monitor = new Box(0, 0, 2560, 1440);

    [Fact]
    public void GameInFront()
    {
        Assert.True(GameDetect.InFront(GameDetect.QunsD3dFullScreen, new Box(0, 0, 10, 10), Monitor, false, false));
        Assert.True(GameDetect.InFront(5, new Box(0, 0, 2560, 1440), Monitor, false, false));        // borderless fullscreen
        Assert.True(GameDetect.InFront(5, new Box(-8, -8, 2576, 1456), Monitor, false, false));       // maximised beyond the edges
        Assert.False(GameDetect.InFront(5, new Box(0, 0, 2560, 1400), Monitor, false, false));       // a normal maximised window above the taskbar
        Assert.False(GameDetect.InFront(GameDetect.QunsBusy, Monitor, Monitor, true, false));         // LiteView's own fullscreen video
        Assert.False(GameDetect.InFront(5, Monitor, Monitor, false, true));                           // the desktop
    }

    [Fact]
    public void GamingModeWaitsBeforeSwitching()
    {
        var g = new GamingSwitch();
        Assert.False(g.Update(true));
        Assert.True(g.Update(true)); Assert.True(g.Active);
        Assert.False(g.Update(false)); Assert.False(g.Update(true));   // a quick alt-tab does not end it
        Assert.False(g.Update(false)); Assert.False(g.Update(false));
        Assert.True(g.Update(false)); Assert.False(g.Active);
        g.Update(true); g.Reset(); Assert.False(g.Active);
    }
}
