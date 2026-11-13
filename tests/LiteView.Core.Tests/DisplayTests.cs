using LiteViewCore;
using Xunit;

public class DisplayTests
{
    [Theory]
    [InlineData("NVIDIA GeForce RTX 5080", true)]
    [InlineData("NVIDIA GeForce RTX 3060 Laptop GPU", true)]
    [InlineData("NVIDIA RTX A2000 12GB", true)]
    [InlineData("NVIDIA Quadro RTX 4000", true)]
    [InlineData("NVIDIA RTX 6000 Ada Generation", true)]
    [InlineData("NVIDIA GeForce GTX 1080 Ti", false)]
    [InlineData("NVIDIA GeForce GTX 1660 SUPER", false)]
    [InlineData("AMD Radeon RX 7900 XTX", false)]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", false)]
    [InlineData("Microsoft Basic Display Adapter", false)]
    public void RtxCards(string name, bool rtx) { Assert.Equal(rtx, Display.HasRtx(new[] { name })); }

    [Fact]
    public void AnyRtxAdapterCounts()
    {
        Assert.True(Display.HasRtx(new[] { "Intel(R) UHD Graphics 770", null, "NVIDIA GeForce RTX 4070" }));
        Assert.False(Display.HasRtx(new string?[] { null }));
    }

    static readonly Box Left = new Box(0, 0, 1920, 1040), RightScreen = new Box(1920, 0, 2560, 1400);

    [Fact]
    public void WindowGoesBackWhereItWasWhenThatIsStillOnAScreen()
    {
        var saved = new Box(100, 100, 1296, 760);
        Assert.Equal(saved, Display.Restore(saved, new[] { Left, RightScreen }, RightScreen));
        var straddling = new Box(1500, 100, 1296, 760);   // mostly on the right screen, partly on the left
        Assert.Equal(straddling, Display.Restore(straddling, new[] { Left, RightScreen }, Left));
    }

    [Fact]
    public void WindowOnAMonitorThatIsGoneIsCentredOnTheCurrentScreen()
    {
        var saved = new Box(2200, 200, 1296, 760);         // was on the right monitor, now unplugged
        var r = Display.Restore(saved, new[] { Left }, Left);
        Assert.Equal(new Box((1920 - 1296) / 2, (1040 - 760) / 2, 1296, 760), r);
        var huge = Display.Restore(new Box(5000, 0, 3000, 2000), new[] { Left }, Left);
        Assert.Equal(Left, huge);                           // never larger than the screen
        Assert.Equal(new Box((1920 - 640) / 2, (1040 - 400) / 2, 640, 400), Display.Restore(new Box(9000, 0, 0, 0), new[] { Left }, Left));
    }
}
