using System;
using System.IO;
using System.Linq;
using LiteViewCore;
using Xunit;

public class DiskCacheTests
{
    static string Dir() { var d = Path.Combine(Path.GetTempPath(), "lv-cache-" + Guid.NewGuid().ToString("N")); return d; }

    [Fact]
    public void KeepsAndGivesBackBytesByUrl()
    {
        string d = Dir();
        try
        {
            var c = new DiskCache(d, 1000, 100, TimeSpan.FromDays(30));
            Assert.Null(c.Read("https://cdn.7tv.app/emote/a/1x.webp"));
            c.Write("https://cdn.7tv.app/emote/a/1x.webp", new byte[] { 1, 2, 3 });
            Assert.Equal(new byte[] { 1, 2, 3 }, c.Read("https://cdn.7tv.app/emote/a/1x.webp"));
            Assert.Null(c.Read("https://cdn.7tv.app/emote/b/1x.webp"));
            c.Write("https://x/big", new byte[101]);   // over the per-file limit: not kept
            Assert.Null(c.Read("https://x/big"));
            Assert.All(Directory.GetFiles(d), f => Assert.Matches("^[0-9a-f]{64}\\.bin$", Path.GetFileName(f)));   // nothing of the URL in names
        }
        finally { if (Directory.Exists(d)) Directory.Delete(d, true); }
    }

    [Fact]
    public void OldAndExcessFilesGo()
    {
        string d = Dir();
        try
        {
            var c = new DiskCache(d, 250, 100, TimeSpan.FromDays(30));
            for (int i = 0; i < 5; i++)
            {
                c.Write("u" + i, new byte[100]);
                File.SetLastWriteTimeUtc(Path.Combine(d, DiskCache.Key("u" + i) + ".bin"), DateTime.UtcNow.AddMinutes(-10 + i));
            }
            File.WriteAllText(Path.Combine(d, "left.tmp"), "x");
            Assert.Equal(200, c.Prune());   // the two newest fit in 250 bytes
            Assert.NotNull(c.Read("u4")); Assert.NotNull(c.Read("u3")); Assert.Null(c.Read("u2")); Assert.Null(c.Read("u0"));
            Assert.False(File.Exists(Path.Combine(d, "left.tmp")));
            File.SetLastWriteTimeUtc(Path.Combine(d, DiskCache.Key("u4") + ".bin"), DateTime.UtcNow.AddDays(-31));
            Assert.Null(c.Read("u4"));   // too old: not used (and removed)
            Assert.Equal(100, c.Prune());
        }
        finally { if (Directory.Exists(d)) Directory.Delete(d, true); }
    }
}
