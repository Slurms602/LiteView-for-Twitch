using System;
using System.Linq;
using System.Collections.Generic;
using LiteViewCore;
using Xunit;

public class PlaybackStatsTests
{
    [Fact]
    public void ARealAnswerIsRead()
    {
        var s = PlaybackStats.ParseAnswer("{\"data\":\"h264|d3d11va|1920|1080|59.940060|3|1|1.733000|6123456.000000\",\"request_id\":81,\"error\":\"success\"}");
        Assert.NotNull(s);
        Assert.Equal("h264", s!.Codec); Assert.Equal("d3d11va", s.Hwdec);
        Assert.Equal(1920, s.Width); Assert.Equal(1080, s.Height);
        Assert.Equal(59.94006, s.Fps, 3); Assert.Equal(3, s.Dropped); Assert.Equal(1, s.DecoderDropped);
        Assert.Equal(1.733, s.BufferSeconds, 3); Assert.Equal(6123456, s.VideoBitrate, 0);
    }

    [Theory]
    [InlineData("{\"data\":\"x\",\"request_id\":71,\"error\":\"success\"}")]
    [InlineData("{\"data\":\"x\",\"request_id\":81,\"error\":\"property unavailable\"}")]
    [InlineData("{\"data\":null,\"request_id\":81,\"error\":\"success\"}")]
    [InlineData("{\"event\":\"playback-restart\"}")]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherLinesAreNotStats(string? line)
    {
        Assert.Null(PlaybackStats.ParseAnswer(line));
    }

    [Fact]
    public void UnknownValuesStayUnknown()
    {
        var s = PlaybackStats.Parse("-|-|-|-|-|-|-|-|-");
        Assert.Equal("", s.Codec); Assert.Equal(0, s.Width); Assert.Equal(-1, s.Fps); Assert.Equal(-1, s.Dropped); Assert.Equal(-1, s.BufferSeconds);
        s = PlaybackStats.Parse("");
        Assert.Equal(-1, s.VideoBitrate);
    }

    [Fact]
    public void HostileValuesAreClampedAndCleaned()
    {
        var s = PlaybackStats.Parse("{\\an1}evil\u0001codec|‮no|99999999|-5|NaN|1e400|-1|inf|1e99");
        Assert.Equal("{\\an1}evilcodec", s.Codec); Assert.Equal("no", s.Hwdec);   // control and direction characters dropped
        Assert.Equal(16384, s.Width); Assert.Equal(0, s.Height);
        Assert.Equal(-1, s.Fps); Assert.Equal(-1, s.Dropped); Assert.Equal(0, s.DecoderDropped); Assert.Equal(-1, s.BufferSeconds); Assert.Equal(1e10, s.VideoBitrate);
        var ass = PlaybackStats.Ass(new[] { new KeyValuePair<string, string>("Video", s.Codec) }, "t");
        Assert.DoesNotContain("{\\an1}", ass);
    }

    [Theory]
    [InlineData("h264 (H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10)", "H.264")]
    [InlineData("h264", "H.264")]
    [InlineData("hevc", "HEVC (H.265)")]
    [InlineData("av1", "AV1")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("vp9 (Google VP9)", "vp9")]
    public void CodecNames(string? raw, string shown)
    {
        Assert.Equal(shown, PlaybackStats.CodecName(raw));
    }

    [Theory]
    [InlineData("d3d11va", "graphics card (d3d11va)")]
    [InlineData("no", "processor (software)")]
    [InlineData("", "")]
    public void DecodingIsSaidPlainly(string hw, string shown)
    {
        Assert.Equal(shown, PlaybackStats.Decoding(hw));
    }

    [Fact]
    public void TheOverlayCannotBreakOutOfItsText()
    {
        Assert.Equal("(\\an1)x /N y", PlaybackStats.Escape("{\\an1}x \\N y").Replace("/", "/").Replace("(/an1)", "(\\an1)"));
        var ass = PlaybackStats.Ass(new[] { new KeyValuePair<string, string>("Channel", "a{b}c\\Nd\ne") }, "LiteView");
        Assert.Contains("Channel  a(b)c/Nd e", ass);
        var cmd = PlaybackStats.OverlayCommand(ass);
        Assert.StartsWith("{\"command\":{\"name\":\"osd-overlay\"", cmd);
        Assert.NotNull(Json.Parse(cmd));   // valid JSON whatever the text
        Assert.Equal(ass, Json.S(Json.V(Json.Parse(cmd), "command"), "data"));
        Assert.Contains("\"format\":\"none\"", PlaybackStats.OverlayCommand(null));
    }

    [Fact]
    public void TheRequestIsOneLineOfJson()
    {
        var r = PlaybackStats.Request();
        Assert.DoesNotContain("\n", r);
        Assert.Equal(PlaybackStats.RequestId, Json.N(Json.Parse(r), "request_id"));
    }

    [Theory]
    [InlineData("[cli][info] Opening stream: 1080p60 (hls)", "1080p60")]
    [InlineData("[cli][info] Opening stream: audio_only (hls)", "audio_only")]
    [InlineData("[cli][info] Opening stream: evil$(x) (hls)", "")]
    [InlineData("[cli][info] Available streams: 160p (worst), 1080p60 (best)", "")]
    public void TheOpenedVariantIsRead(string line, string variant)
    {
        Assert.Equal(variant, Quality.Opened(new[] { "[cli][info] Found matching plugin twitch", line }));
    }
}

public class GpuTimeTests
{
    [Fact]
    public void FreshPassesAreAddedUp()
    {
        string line = "{\"data\":{\"fresh\":[{\"desc\":\"upload frame\",\"last\":120000,\"avg\":100000,\"peak\":300000,\"count\":10,\"samples\":[1,2]}," +
            "{\"desc\":\"user shader: FSRCNNX\",\"last\":2100000,\"avg\":2000000,\"peak\":2500000,\"count\":10,\"samples\":[]}],\"redraw\":[{\"desc\":\"x\",\"avg\":999999999}]},\"request_id\":82,\"error\":\"success\"}";
        Assert.Equal(2.1, PlaybackStats.GpuMs(line), 6);
    }

    [Theory]
    [InlineData("{\"data\":{\"fresh\":[]},\"request_id\":82,\"error\":\"success\"}")]
    [InlineData("{\"data\":null,\"request_id\":82,\"error\":\"property unavailable\"}")]
    [InlineData("{\"data\":{\"fresh\":[{\"avg\":-5}]},\"request_id\":82,\"error\":\"success\"}")]
    [InlineData("{\"data\":{\"fresh\":[{\"avg\":100}]},\"request_id\":81,\"error\":\"success\"}")]
    [InlineData("garbage")]
    [InlineData(null)]
    public void NoMeasurementGivesMinusOne(string? line)
    {
        Assert.Equal(-1, PlaybackStats.GpuMs(line));
    }
}

public class PicturePresetTests
{
    [Theory]
    [InlineData(1.0, null)]
    [InlineData(1.05, null)]   // a 1080p stream in a window on a 1440p screen: RTX only softened it (owner's screenshots)
    [InlineData(1.24, null)]
    [InlineData(1.25, "1.25")]
    [InlineData(1.3333, "1.33")]   // 1080p in fullscreen at 1440p
    [InlineData(2.0, "2.00")]      // 720p at 1440p
    [InlineData(9.0, "4.00")]
    [InlineData(double.NaN, null)]
    public void RtxOnlyWhenTheVideoIsEnlargedEnough(double scale, string? want)
    {
        Assert.Equal(want, PicturePreset.RtxScale(scale));
    }

    [Fact]
    public void PresetNames()
    {
        Assert.Equal(new[] { "Off", "Enhance", "High", "Ultra" }, new[] { 0, 1, 2, 3 }.Select(PicturePreset.Name).ToArray());
        Assert.Equal("Off", PicturePreset.Name(-1));
    }

    static string ShaderDir()
    {
        for (var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            string p = System.IO.Path.Combine(d.FullName, "config", "mpv", "shaders");
            if (System.IO.Directory.Exists(p)) return p;
        }
        throw new System.IO.DirectoryNotFoundException("config/mpv/shaders");
    }

    [Fact]
    public void EveryPresetSharpensOnceAtMost()
    {
        foreach (Sharpness s in Enum.GetValues(typeof(Sharpness)))
            for (int pic = 0; pic <= 3; pic++)
                Assert.True(PicturePreset.Sharpeners(PicturePreset.Shaders(pic, s)) <= 1, "picture " + pic + ", sharpness " + s);
    }

    [Fact]
    public void UltraSharpensLikeHighAndCleansUp()
    {
        var high = PicturePreset.Shaders(2, Sharpness.Normal); var ultra = PicturePreset.Shaders(3, Sharpness.Normal);
        Assert.Equal(high[high.Count - 1], ultra[ultra.Count - 1]);
        Assert.Contains("nlmeans.glsl", ultra);
        Assert.DoesNotContain("nlmeans_sharpen_denoise.glsl", ultra);
        Assert.DoesNotContain("adaptive-sharpen_strong.glsl", ultra);
        Assert.Equal(new[] { "KrigBilateral.glsl", "nlmeans.glsl", "FSRCNNX_x2_16-0-4-1_any.glsl", "SSimDownscaler.glsl" }, PicturePreset.Shaders(3, Sharpness.Off));
    }

    [Fact]
    public void SharpnessOnlyChangesHighAndUltra()
    {
        Assert.Empty(PicturePreset.Shaders(0, Sharpness.Strong));
        Assert.Equal(PicturePreset.Shaders(1, Sharpness.Off), PicturePreset.Shaders(1, Sharpness.Strong));
        Assert.Equal("adaptive-sharpen_soft.glsl", PicturePreset.Shaders(2, Sharpness.Soft)[1]);
        Assert.Equal("adaptive-sharpen_strong.glsl", PicturePreset.Shaders(3, Sharpness.Strong)[4]);
    }

    [Fact]
    public void EveryShaderFileExists()
    {
        string dir = ShaderDir();
        foreach (Sharpness s in Enum.GetValues(typeof(Sharpness)))
            for (int pic = 0; pic <= 3; pic++)
                foreach (var f in PicturePreset.Shaders(pic, s))
                    Assert.True(System.IO.File.Exists(System.IO.Path.Combine(dir, f)), f);
    }

    [Theory]
    [InlineData("off", Sharpness.Off)]
    [InlineData("soft", Sharpness.Soft)]
    [InlineData("normal", Sharpness.Normal)]
    [InlineData("strong", Sharpness.Strong)]
    [InlineData("STRONG", Sharpness.Normal)]
    [InlineData(null, Sharpness.Normal)]
    [InlineData("x", Sharpness.Normal)]
    public void SharpnessSettingRoundTrips(string? saved, Sharpness s)
    {
        Assert.Equal(s, PicturePreset.ParseSharpness(saved));
        Assert.Equal(PicturePreset.Save(s), PicturePreset.Save(PicturePreset.ParseSharpness(PicturePreset.Save(s))));
    }
}
