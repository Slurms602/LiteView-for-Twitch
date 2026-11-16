// Emote and badge pictures kept on disk between runs (owner's request: the emotes were downloaded again at every start).
// One file per picture URL (named by the URL's SHA-256, so nothing from the URL becomes a file name), at most MaxBytes
// in all and MaxAge old; the oldest go first. What comes back from disk is checked again like a download (size, image
// type) by the caller. Emote URLs of 7TV, BetterTTV, FFZ and Twitch name one picture forever, so a kept copy stays right.
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace LiteViewCore
{
    public sealed class DiskCache
    {
        readonly string dir;
        readonly long maxBytes, maxFileBytes;
        readonly TimeSpan maxAge;
        readonly object pruning = new object();
        long written;   // bytes written since the last tidy: past a quarter of MaxBytes it tidies again (in the background)

        public DiskCache(string dir, long maxBytes, long maxFileBytes, TimeSpan maxAge)
        {
            this.dir = dir; this.maxBytes = maxBytes; this.maxFileBytes = maxFileBytes; this.maxAge = maxAge;
        }

        public static string Key(string url)
        {
#if NET5_0_OR_GREATER
            var h = SHA256.HashData(Encoding.UTF8.GetBytes(url));
#else
            byte[] h; using (var sha = SHA256.Create()) h = sha.ComputeHash(Encoding.UTF8.GetBytes(url));
#endif
            var sb = new StringBuilder(64);
            foreach (var b in h) sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        string PathOf(string url) { return Path.Combine(dir, Key(url) + ".bin"); }

        /// <summary>The kept bytes, or null (none, too old, too big, unreadable).</summary>
        public byte[]? Read(string url)
        {
            try
            {
                var f = new FileInfo(PathOf(url));
                if (!f.Exists || f.Length == 0 || f.Length > maxFileBytes) return null;
                if (DateTime.UtcNow - f.LastWriteTimeUtc > maxAge) { f.Delete(); return null; }
                return File.ReadAllBytes(f.FullName);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return null; }
        }

        /// <summary>Keeps the bytes (written to a temporary file, then moved: a half-written file is never read).</summary>
        public void Write(string url, byte[] data)
        {
            if (data == null || data.Length == 0 || data.Length > maxFileBytes) return;
            string path = PathOf(url), tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(tmp, data);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                if (System.Threading.Interlocked.Add(ref written, data.Length) > Math.Max(maxBytes / 4, 1L << 20))
                {
                    System.Threading.Interlocked.Exchange(ref written, 0);
                    System.Threading.Tasks.Task.Run(() => Prune());
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception e2) when (e2 is IOException || e2 is UnauthorizedAccessException) { }
            }
        }

        /// <summary>Forgets one picture (it was kept but cannot be decoded).</summary>
        public void Remove(string url)
        {
            try { File.Delete(PathOf(url)); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        /// <summary>Removes old files and, beyond MaxBytes, the oldest; also left-over temporary files. Returns the bytes kept.</summary>
        public long Prune()
        {
            lock (pruning)
            try
            {
                if (!Directory.Exists(dir)) return 0;
                var files = new DirectoryInfo(dir).GetFiles().OrderByDescending(f => f.LastWriteTimeUtc).ToList();
                long total = 0;
                foreach (var f in files)
                {
                    if (!f.Name.EndsWith(".bin", StringComparison.Ordinal) && DateTime.UtcNow - f.LastWriteTimeUtc < TimeSpan.FromMinutes(1)) continue;   // a temporary file being written now
                    bool keep = f.Name.EndsWith(".bin", StringComparison.Ordinal) && DateTime.UtcNow - f.LastWriteTimeUtc <= maxAge && total + f.Length <= maxBytes;
                    if (keep) { total += f.Length; continue; }
                    try { f.Delete(); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                }
                return total;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return 0; }
        }
    }
}
