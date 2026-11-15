using System;
using System.IO;
using LiteViewCore;
using Xunit;

public class FontCoverageTests
{
    static string FontDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            string p = Path.Combine(d.FullName, "config", "fonts");
            if (Directory.Exists(p)) return p;
        }
        throw new DirectoryNotFoundException("config/fonts");
    }

    static FontCoverage Inter(string file = "Inter-Regular.ttf")
    {
        var c = FontCoverage.Read(File.ReadAllBytes(Path.Combine(FontDir(), file)));
        Assert.NotNull(c);
        return c!;
    }

    [Theory]
    [InlineData("Inter-Regular.ttf")]
    [InlineData("Inter-Bold.ttf")]
    [InlineData("Inter-SemiBold.ttf")]
    public void TheBundledFontsHaveLatinGreekAndCyrillic(string file)
    {
        var c = Inter(file);
        Assert.True(c.Covers("The quick brown fox jumps over the lazy dog 0123456789 !?@#:;.,'\"()[]{}<>=+-*/\\|_~^%&$"));
        Assert.True(c.Covers("Ärger über Öl, façade, naïve, Ågård, Łódź, Žluťoučký"));
        Assert.True(c.Covers("Привет мир"));
        Assert.True(c.Covers("Καλημέρα"));
        Assert.True(c.Covers("“quotes” – dashes — and… €"));
    }

    [Theory]
    [InlineData("你好")]
    [InlineData("こんにちは")]
    [InlineData("안녕")]
    [InlineData("مرحبا")]
    [InlineData("שלום")]
    [InlineData("😀")]
    [InlineData("gg😀")]
    [InlineData("ไทย")]
    public void ScriptsInterLacksAreNotCovered(string text)
    {
        Assert.False(Inter().Covers(text));
    }

    [Fact]
    public void SpacesJoinersAndVariationSelectorsNeedNoGlyph()
    {
        var c = Inter();
        Assert.True(c.Covers(""));
        Assert.True(c.Covers(null));
        Assert.True(c.Covers("a‍b️ c d"));
    }

    [Fact]
    public void ALoneSurrogateIsNotCovered()
    {
        Assert.False(Inter().Covers("a\uD83D"));
        Assert.False(Inter().Covers("\uDE00a"));
    }

    [Fact]
    public void BrokenFilesGiveNull()
    {
        Assert.Null(FontCoverage.Read(null));
        Assert.Null(FontCoverage.Read(new byte[0]));
        Assert.Null(FontCoverage.Read(new byte[11]));
        Assert.Null(FontCoverage.Read(new byte[4096]));
        var good = File.ReadAllBytes(Path.Combine(FontDir(), "Inter-Regular.ttf"));
        Assert.Null(FontCoverage.Read(good.AsSpan(0, 200).ToArray()));
    }

    [Fact]
    public void DamagedFontsNeverThrow()
    {
        var good = File.ReadAllBytes(Path.Combine(FontDir(), "Inter-Regular.ttf"));
        var r = new Random(41);
        for (int i = 0; i < 300; i++)
        {
            var f = (byte[])good.Clone();
            int n = 1 + r.Next(40);
            for (int k = 0; k < n; k++) f[r.Next(Math.Min(f.Length, 2000 + r.Next(f.Length)))] = (byte)r.Next(256);
            var c = FontCoverage.Read(f);
            if (c != null) { c.Covers("abc 你好 😀"); c.Has(-1); c.Has(int.MaxValue); }
        }
        for (int i = 0; i < 2000; i++)
        {
            var f = new byte[r.Next(400)];
            r.NextBytes(f);
            if (f.Length > 16) { f[4] = 0; f[5] = (byte)r.Next(4); f[12] = (byte)'c'; f[13] = (byte)'m'; f[14] = (byte)'a'; f[15] = (byte)'p'; }
            FontCoverage.Read(f);
        }
    }
}
