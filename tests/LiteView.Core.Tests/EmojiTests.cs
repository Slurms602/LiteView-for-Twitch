using System;
using System.Linq;
using LiteViewCore;
using Xunit;

public class EmojiTests
{
    static string[] Parts(string s) { return EmojiText.Split(s).Select(p => (p.Value ? "E:" : "T:") + p.Key).ToArray(); }

    [Fact]
    public void PlainTextStaysText()
    {
        Assert.Equal(new[] { "T:gg wp 123 #1" }, Parts("gg wp 123 #1"));
        Assert.Empty(EmojiText.Split(""));
        Assert.False(EmojiText.Has("你好 Привет café © ® ™ ☺ ↔"));   // text symbols without U+FE0F
    }

    [Theory]
    [InlineData("😀")]
    [InlineData("👍🏽")]                 // skin tone
    [InlineData("👨‍👩‍👧‍👦")]           // family (joined)
    [InlineData("🏳️‍🌈")]               // flag joined with FE0F
    [InlineData("🇩🇪")]                 // regional indicator pair
    [InlineData("1️⃣")]                 // keycap
    [InlineData("#⃣")]                  // keycap without FE0F
    [InlineData("☺️")]                  // text symbol made emoji by FE0F
    [InlineData("❤️‍🔥")]
    [InlineData("🏴󠁧󠁢󠁳󠁣󠁴󠁿")]          // Scotland (tag sequence)
    [InlineData("✋🏿")]                // BMP pictograph with a skin tone
    public void OneSequenceIsOneEmoji(string e)
    {
        Assert.Equal(new[] { "E:" + e }, Parts(e));
    }

    [Fact]
    public void MixedTextSplitsAtTheEmoji()
    {
        Assert.Equal(new[] { "T:gg", "E:😀", "E:👍🏽", "T:wp" }, Parts("gg😀👍🏽wp"));
        Assert.Equal(new[] { "T:a ", "E:🇩🇪", "E:🇫🇷", "T: b" }, Parts("a 🇩🇪🇫🇷 b"));
    }

    [Theory]
    [InlineData("\uD83D")]            // a lone surrogate
    [InlineData("‍‍")]
    [InlineData("🇩")]                // half a flag
    [InlineData("☺︎")]                // text presentation asked for
    [InlineData("1⃣x")]
    public void BrokenOrTextFormsNeverThrow(string s)
    {
        var parts = EmojiText.Split(s);
        Assert.Equal(s, string.Concat(parts.Select(p => p.Key)));
    }

    [Fact]
    public void RandomTextIsAlwaysCoveredExactly()
    {
        var r = new Random(5);
        string alphabet = "a 1#*‍️︎⃣😀👍🏽🇩🇪☺©🏴\U000E0067\U000E007F😀\uDC00";
        for (int n = 0; n < 3000; n++)
        {
            var sb = new System.Text.StringBuilder();
            int len = r.Next(30);
            for (int i = 0; i < len; i++) sb.Append(alphabet[r.Next(alphabet.Length)]);
            string s = sb.ToString();
            var parts = EmojiText.Split(s);
            Assert.Equal(s, string.Concat(parts.Select(p => p.Key)));
            Assert.All(parts.Where(p => p.Value), p => Assert.True(p.Key.Length <= EmojiText.MaxSequence + 16));
        }
    }

    [Fact]
    public void LongJoinedRunsAreCapped()
    {
        string spam = string.Concat(Enumerable.Repeat("😀‍", 100)) + "😀";
        Assert.All(EmojiText.Split(spam).Where(p => p.Value), p => Assert.True(p.Key.Length <= EmojiText.MaxSequence + 16));
    }
}
