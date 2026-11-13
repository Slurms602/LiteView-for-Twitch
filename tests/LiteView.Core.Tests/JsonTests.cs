using LiteViewCore;
using Xunit;

public class JsonTests
{
    [Fact]
    public void ParsesTheShapesTheAppUses()
    {
        var j = Json.Parse("{\"data\":[{\"id\":\"71092938\",\"viewer_count\":37012,\"live\":true,\"x\":null,\"f\":1.5}],\"pagination\":{\"cursor\":\"abc\"}}");
        var first = Json.A(j, "data")[0];
        Assert.Equal("71092938", Json.S(first, "id"));
        Assert.Equal(37012, Json.N(first, "viewer_count"));
        Assert.True(Json.B(first, "live"));
        Assert.Null(Json.V(first, "x"));
        Assert.Equal("1.5", Json.S(first, "f"));
        Assert.Equal("abc", Json.S(Json.V(j, "pagination"), "cursor"));
        Assert.IsType<object?[]>(Json.Parse("[1,2]"));
        Assert.IsType<Dictionary<string, object?>>(Json.Parse("{}"));
    }

    [Fact]
    public void Numbers()
    {
        Assert.Equal(-12L, Json.Parse("-12"));
        Assert.Equal(0L, Json.Parse("0"));
        Assert.Equal(1e3, Json.Parse("1e3"));
        Assert.Equal(-0.25, Json.Parse("-0.25"));
        Assert.Equal(9.2233720368547758E+18, Json.Parse("9223372036854775808"));   // past long: double
        Assert.Equal(256, Json.N(Json.Parse("{\"flags\":256}"), "flags"));
        Assert.Equal(0, Json.N(Json.Parse("{\"flags\":\"x\"}"), "flags"));
    }

    [Fact]
    public void Strings()
    {
        Assert.Equal("a\"b\\c/d\n\t", Json.Parse("\"a\\\"b\\\\c\\/d\\n\\t\""));
        Assert.Equal("é😀", Json.Parse("\"\\u00e9\\ud83d\\ude00\""));
        Assert.Equal("plain ünïcode", Json.Parse("\"plain ünïcode\""));
    }

    [Fact]
    public void AccessorsAreSafeOnAnything()
    {
        Assert.Null(Json.S(null, "a"));
        Assert.Null(Json.S("text", "a"));
        Assert.Empty(Json.A(Json.Parse("{\"a\":1}"), "a"));
        Assert.Empty(Json.Props(null));
        Assert.Single(Json.Props(Json.Parse("{\"k\":1}")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{\"a\":}")]
    [InlineData("{\"a\":1,}")]
    [InlineData("[1,]")]
    [InlineData("[1 2]")]
    [InlineData("\"unterminated")]
    [InlineData("\"bad \\x escape\"")]
    [InlineData("\"control \u0001 char\"")]
    [InlineData("01")]
    [InlineData("1.")]
    [InlineData("-")]
    [InlineData("tru")]
    [InlineData("{} trailing")]
    [InlineData("{'single':1}")]
    [InlineData("NaN")]
    public void MalformedIsNull(string s)
    {
        Assert.Null(Json.Parse(s));
    }

    [Fact]
    public void DepthIsLimited()
    {
        Assert.NotNull(Json.Parse(new string('[', 60) + new string(']', 60)));
        Assert.Null(Json.Parse(new string('[', 100000) + new string(']', 100000)));   // no stack overflow
        Assert.Null(Json.Parse(null));
    }
}
