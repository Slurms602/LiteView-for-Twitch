using System.Text;
using LiteViewCore;
using Xunit;

public class EmoteLimitTests
{
    [Theory]
    [InlineData(28, 28, 0, 28, 28)]          // small: unchanged
    [InlineData(112, 32, 0, 112, 32)]        // 7TV wide emote at 1x: unchanged
    [InlineData(64, 64, 0, 32, 32)]          // tall: scaled to the 32 px line
    [InlineData(72, 72, 18, 18, 18)]         // badge: fixed 18 px
    [InlineData(10000, 32, 0, 160, 1)]       // absurdly wide: capped (upstream kept 10000 px per frame)
    [InlineData(100000, 100, 0, 160, 1)]
    [InlineData(0, 0, 0, 28, 28)]
    public void Fit(int w, int h, int fixedH, int ew, int eh)
    {
        EmoteLimits.Fit(w, h, fixedH, out int ow, out int oh);
        Assert.Equal(ew, ow); Assert.Equal(eh, oh);
        Assert.True(ow <= Math.Max(1, oh) * EmoteLimits.MaxAspect || ow <= 160);
    }

    [Fact]
    public void SourceDimensionLimits()
    {
        Assert.True(EmoteLimits.SourceOk(112, 32));
        Assert.True(EmoteLimits.SourceOk(2048, 1024));
        Assert.False(EmoteLimits.SourceOk(2048, 2048));
        Assert.False(EmoteLimits.SourceOk(30000, 30));
        Assert.False(EmoteLimits.SourceOk(0, 10));
    }

    [Fact]
    public void FrameBudget()
    {
        Assert.Equal(1, EmoteLimits.FramesToKeep(1, 32, 32));
        Assert.Equal(50, EmoteLimits.FramesToKeep(50, 32, 32));
        Assert.Equal(EmoteLimits.MaxFrames, EmoteLimits.FramesToKeep(5000, 16, 16));
        int keep = EmoteLimits.FramesToKeep(400, 160, 32);
        Assert.True((long)keep * 160 * 32 * 4 <= EmoteLimits.MaxDecodedBytes);
        Assert.True(keep >= 1);
    }

    [Fact]
    public void FramePlanSpreadsFramesAndKeepsTheSpeed()
    {
        var plan = EmoteLimits.FramePlan(10, 5);
        Assert.Equal(new[] { 0, 2, 4, 6, 8 }, plan);
        var delays = EmoteLimits.MergeDelays(Enumerable.Repeat(40, 10).ToArray(), plan);
        Assert.Equal(new[] { 80, 80, 80, 80, 80 }, delays);
        Assert.Equal(400, delays.Sum());
        Assert.Equal(new[] { 0, 1, 2 }, EmoteLimits.FramePlan(3, 10));
        Assert.Empty(EmoteLimits.FramePlan(0, 3));
        // GIF delays under 20 ms play at 100 ms, like browsers
        Assert.Equal(new[] { 100, 100 }, EmoteLimits.MergeDelays(new[] { 0, 10 }, new[] { 0, 1 }));
    }
}

public class ByteBudgetTests
{
    [Fact]
    public void EvictsLeastRecentlyUsedUntilUnderBudget()
    {
        var b = new ByteBudget<string>(100);
        b.Touch("a", 40); b.Touch("b", 40); b.Touch("c", 40);
        Assert.Equal(120, b.Total);
        b.Touch("a", 40);                       // a is now the most recent
        Assert.Equal(new[] { "b" }, b.Evict());
        Assert.Equal(80, b.Total);
        Assert.False(b.Contains("b"));
        Assert.Empty(b.Evict());
    }

    [Fact]
    public void KeepsWhatIsInUse()
    {
        var b = new ByteBudget<string>(50);
        b.Touch("visible", 40); b.Touch("old", 40); b.Touch("new", 40);
        var gone = b.Evict(k => k == "visible");
        Assert.Equal(new[] { "old", "new" }, gone);
        Assert.True(b.Contains("visible"));
        // when everything is in use nothing is freed and the loop ends
        var c = new ByteBudget<int>(1); c.Touch(1, 10); c.Touch(2, 10);
        Assert.Empty(c.Evict(_ => true));
    }

    [Fact]
    public void SizeChangesAndRemoval()
    {
        var b = new ByteBudget<string>(1000);
        b.Touch("x", 10); b.Touch("x", 30);
        Assert.Equal(30, b.Total);
        b.Remove("x"); b.Remove("missing");
        Assert.Equal(0, b.Total); Assert.Equal(0, b.Count);
    }
}

public class StorageTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "lvtest-" + Guid.NewGuid().ToString("N"));
    public StorageTests() { Directory.CreateDirectory(dir); }
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    [Fact]
    public void IniRoundTripKeepsLastKnownGood()
    {
        string p = Path.Combine(dir, "liteview.ini");
        IniStore.Write(p, "LiteView", new Dictionary<string, string> { ["volume"] = "40", ["quality"] = "720p60" });
        IniStore.Write(p, "LiteView", new Dictionary<string, string> { ["volume"] = "55" });
        var d = IniStore.Read(p, out bool bak);
        Assert.False(bak);
        Assert.Equal("55", d["VOLUME"]);
        Assert.True(File.Exists(p + ".bak"));
        Assert.Equal("40", IniStore.Read(p + ".bak")["volume"]);
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }

    [Fact]
    public void TornFileFallsBackToBackup()
    {
        string p = Path.Combine(dir, "liteview.ini");
        IniStore.Write(p, "", new Dictionary<string, string> { ["volume"] = "33" });
        IniStore.Write(p, "", new Dictionary<string, string> { ["volume"] = "44" });
        File.WriteAllBytes(p, new byte[] { 0, 0, 0, 0 });            // what a crash mid-write used to leave
        var d = IniStore.Read(p, out bool bak);
        Assert.True(bak);
        Assert.Equal("33", d["volume"]);
    }

    [Fact]
    public void MissingFileIsEmpty()
    {
        Assert.Empty(IniStore.Read(Path.Combine(dir, "none.ini")));
    }

    [Fact]
    public void ValuesCannotAddLines()
    {
        string p = Path.Combine(dir, "x.ini");
        IniStore.Write(p, "", new Dictionary<string, string> { ["a"] = "1\r\nb=2" });
        var d = IniStore.Read(p);
        Assert.False(d.ContainsKey("b"));
    }

    [Fact]
    public void ParseAndGetters()
    {
        var d = IniStore.Parse(new[] { "# c", "; c", "a = 1", "B=two", "bad", "=x", "n=999" });
        Assert.Equal("1", IniStore.Get(d, "A", "z"));
        Assert.Equal("z", IniStore.Get(d, "missing", "z"));
        Assert.Equal(100, IniStore.GetInt(d, "n", 5, 0, 100));
        Assert.Equal(5, IniStore.GetInt(d, "B", 5, 0, 100));
    }

    [Fact]
    public void AtomicWriteAndDelete()
    {
        string p = Path.Combine(dir, "liteview.token");
        AtomicFile.Write(p, new byte[] { 1, 2, 3 }, false);
        AtomicFile.Write(p, new byte[] { 4 }, false);
        Assert.Equal(new byte[] { 4 }, File.ReadAllBytes(p));
        Assert.False(File.Exists(p + ".bak"));
        File.WriteAllText(p + ".bak", "old");
        AtomicFile.Delete(p);
        Assert.False(File.Exists(p)); Assert.False(File.Exists(p + ".bak"));
    }
}

public class RedactTests
{
    [Theory]
    [InlineData("PASS oauth:abc123def456", "PASS oauth:[redacted]")]
    [InlineData("Authorization: Bearer abcdefghijkl", "Authorization: [redacted]")]
    [InlineData("hdr Bearer abcdefghijkl x", "hdr Bearer [redacted] x")]
    [InlineData("client_id=x&refresh_token=r1r2r3&grant_type=refresh_token", "client_id=x&refresh_token=[redacted]&grant_type=refresh_token")]
    [InlineData("{\"access_token\": \"tok\", \"expires_in\": 1}", "{\"access_token\":\"[redacted]\", \"expires_in\": 1}")]
    [InlineData("device_code=ABCD&x=1", "device_code=[redacted]&x=1")]
    [InlineData("nothing secret here", "nothing secret here")]
    public void RemovesCredentials(string input, string expected)
    {
        Assert.Equal(expected, Redact.Secretsless(input));
    }

    [Fact]
    public void SharingAlsoHidesChannels()
    {
        Assert.Equal("error: No playable streams found on this URL: twitch.tv/<channel>", Redact.ForSharing("error: No playable streams found on this URL: twitch.tv/somebody"));
    }
}

// Log is process-wide: run this alone, not in parallel with tests whose code writes log lines.
[CollectionDefinition("log", DisableParallelization = true)] public class LogCollection { }

[Collection("log")]
public class LogTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "lvlog-" + Guid.NewGuid().ToString("N"));
    public LogTests() { Directory.CreateDirectory(dir); }
    public void Dispose() { Log.Close(); try { Directory.Delete(dir, true); } catch { } }

    [Fact]
    public void RotatesRedactsAndTails()
    {
        Log.Init(dir, "test", 4096, 2);
        Log.Info("PASS oauth:secretvalue123");
        for (int i = 0; i < 400; i++) Log.Info("line " + i + " " + new string('x', 40));
        var files = Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(x => x).ToArray();
        Assert.Contains("test.log", files); Assert.Contains("test.1.log", files); Assert.Contains("test.2.log", files);
        Assert.DoesNotContain("test.3.log", files);
        foreach (var f in Directory.GetFiles(dir)) Assert.True(new FileInfo(f).Length <= 4096 + 200);
        foreach (var f in Directory.GetFiles(dir)) Assert.DoesNotContain("secretvalue123", File.ReadAllText(f));
        var tail = Log.Tail(3);
        Assert.Equal(3, tail.Length);
        Assert.Contains("line 399", tail[2]);
    }
}

public class TopologyTests
{
    const ulong Ccd0 = 0x000FFF, Ccd1 = 0xFFF000;

    [Fact]
    public void X3dDualDiePinsToTheDieWithoutVCache()
    {
        // 9900X3D: CCD0 96 MB (V-Cache), CCD1 32 MB
        var l3 = new List<L3Cache> { new L3Cache(96u << 20, Ccd0, 0), new L3Cache(32u << 20, Ccd1, 0) };
        Assert.Equal(Ccd1, CpuTopology.SmallestL3Mask(l3));
    }

    [Fact]
    public void EqualDiesAreNotPinned()
    {
        // 9900X: two 32 MB dies (the owner's CPU) - Windows decides
        var l3 = new List<L3Cache> { new L3Cache(32u << 20, Ccd0, 0), new L3Cache(32u << 20, Ccd1, 0) };
        Assert.Equal(0UL, CpuTopology.SmallestL3Mask(l3));
    }

    [Fact]
    public void SingleDieMultiGroupAndTinyResultsAreNotPinned()
    {
        Assert.Equal(0UL, CpuTopology.SmallestL3Mask(new List<L3Cache> { new L3Cache(36u << 20, ulong.MaxValue, 0) }));
        Assert.Equal(0UL, CpuTopology.SmallestL3Mask(new List<L3Cache> { new L3Cache(96u << 20, Ccd0, 0), new L3Cache(32u << 20, Ccd1, 1) }));
        Assert.Equal(0UL, CpuTopology.SmallestL3Mask(new List<L3Cache> { new L3Cache(96u << 20, Ccd0, 0), new L3Cache(32u << 20, 0x1, 0) }));
        Assert.Equal(0UL, CpuTopology.SmallestL3Mask(new List<L3Cache>()));
    }

    [Fact]
    public void ParsesTheWindowsBuffer()
    {
        var buf = new byte[56 * 3];
        void Rec(int off, int rel, byte level, uint size, ulong mask, ushort group)
        {
            BitConverter.GetBytes(rel).CopyTo(buf, off); BitConverter.GetBytes(56).CopyTo(buf, off + 4);
            buf[off + 8] = level; BitConverter.GetBytes(size).CopyTo(buf, off + 12);
            BitConverter.GetBytes(mask).CopyTo(buf, off + 40); BitConverter.GetBytes(group).CopyTo(buf, off + 48);
        }
        Rec(0, 2, 3, 96u << 20, Ccd0, 0);
        Rec(56, 2, 2, 1u << 20, 0x3, 0);     // an L2: ignored
        Rec(112, 2, 3, 32u << 20, Ccd1, 0);
        var l3 = CpuTopology.ParseL3(buf, buf.Length);
        Assert.Equal(2, l3.Count);
        Assert.Equal(Ccd1, CpuTopology.SmallestL3Mask(l3));
        Assert.Empty(CpuTopology.ParseL3(buf, 7));
        BitConverter.GetBytes(0).CopyTo(buf, 4);   // a zero-size record must not loop forever
        Assert.Empty(CpuTopology.ParseL3(buf, buf.Length));
    }
}

public class BoundedTests
{
    [Fact]
    public async Task ReadsUpToTheLimit()
    {
        var data = new byte[1000];
        Assert.Equal(1000, (await Bounded.ReadAllAsync(new MemoryStream(data), 1000)).Length);
        await Assert.ThrowsAsync<TooLargeException>(() => Bounded.ReadAllAsync(new MemoryStream(data), 999));
    }
}

public class DiagnosticsTests
{
    [Fact]
    public void ReportHidesSecretsAndChannels()
    {
        var r = Diagnostics.Report(
            new Dictionary<string, string> { ["App"] = "LiteView Enhanced 0.1.0", ["Last error"] = "twitch.tv/someone offline" },
            new Dictionary<string, string> { ["volume"] = "70", ["client_id"] = "abc", ["token_hint"] = "x", ["last"] = "someone" },
            new[] { "INFO PASS oauth:zzz", "INFO playing twitch.tv/someone" });
        Assert.Contains("volume=70", r);
        Assert.DoesNotContain("someone", r);
        Assert.DoesNotContain("zzz", r);
        Assert.DoesNotContain("abc", r);
        Assert.Contains("client_id=[redacted]", r);
    }
}
