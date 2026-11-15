// Stand-in for streamlinkw.exe in CI (tools/smoke.ps1 -FakeStreamlink). It writes Streamlink-like log lines and,
// by channel name: "offline..." waits as Streamlink does for an offline channel; "fail..." fails like a broken
// stream; anything else plays a generated test video in the REAL mpv with the player arguments LiteView passed
// (its window and IPC pipe), so the whole watch path runs on a machine without Twitch. Never part of a release.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

static class FakeStreamlink
{
    static string logFile;

    static void Log(string line)
    {
        try { if (logFile != null) File.AppendAllText(logFile, line + "\n", new UTF8Encoding(false)); } catch (IOException) { }
    }

    static string Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    static int Main(string[] args)
    {
        logFile = Arg(args, "--logfile");
        string player = Arg(args, "--player"), playerArgs = Arg(args, "--player-args") ?? "";
        string url = args.FirstOrDefault(a => a.StartsWith("twitch.tv/", StringComparison.Ordinal)) ?? "twitch.tv/unknown";
        string channel = url.Substring(10);
        Log("[cli][info] Found matching plugin twitch for URL " + url);
        if (channel.StartsWith("offline", StringComparison.Ordinal))
        {
            Log("[cli][info] Waiting for streams, retrying every 30.0 second(s)");
            Thread.Sleep(Timeout.Infinite);
        }
        if (channel.StartsWith("fail", StringComparison.Ordinal))
        {
            Log("[cli][error] Unable to open URL: https://usher.ttvnw.net/api/channel/hls/" + channel + ".m3u8 (made-up failure from the CI stand-in)");
            return 1;
        }
        Thread.Sleep(1500);   // Streamlink needs a moment to open a stream (Python, Twitch's API, the first segments): seamless switching has something to hide
        Log("[cli][info] Available streams: audio_only, 160p (worst), 360p, 480p, 720p60, 1080p60 (best)");
        Log("[cli][info] Opening stream: 1080p60 (hls)");
        Log("[cli][info] Starting player: " + player);
        bool presets = channel.StartsWith("presets", StringComparison.Ordinal);
        string video = presets ? PresetVideo() : Video();
        var psi = new ProcessStartInfo(player, playerArgs + " --loop-file=inf --force-media-title=" + channel + (presets ? " --log-file=mpv-presets.log" : "") + " \"" + video + "\"") { UseShellExecute = false };
        using (var p = Process.Start(psi)) p.WaitForExit();
        Log("[cli][info] Player closed");
        return 0;
    }

    // The picture-preset comparison (fixture scene "presets", tools/preset-metrics.py): a test picture with text, a
    // Siemens star, fine lines, coloured edges and texture at 1024x576 is the truth (preset-truth.png in the working
    // folder); the stream is that picture at 512x288 after a JPEG round trip at quality 55 (blocks and ringing like a
    // Twitch stream), as a still YUV4MPEG2 video. Shown 2x in fullscreen on CI's 1024x768 screen, each preset's
    // upscale can be compared with the truth.
    static string PresetVideo()
    {
        const int W = 1024, H = 576;
        string path = Path.Combine(Path.GetTempPath(), "liteview-ci-presets.y4m");
        using (var big = new System.Drawing.Bitmap(W * 2, H * 2))
        {
            using (var g = System.Drawing.Graphics.FromImage(big)) DrawTestPicture(g, W * 2, H * 2);
            using (var truth = Resize(big, W, H))
            {
                truth.Save("preset-truth.png", System.Drawing.Imaging.ImageFormat.Png);
                using (var small = Resize(truth, W / 2, H / 2))
                using (var ms = new MemoryStream())
                {
                    var jpeg = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
                    var ps = new System.Drawing.Imaging.EncoderParameters(1);
                    ps.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 55L);
                    small.Save(ms, jpeg, ps);
                    ms.Position = 0;
                    using (var stream = new System.Drawing.Bitmap(ms)) WriteY4m(stream, path, 30);
                }
            }
        }
        return path;
    }

    static System.Drawing.Bitmap Resize(System.Drawing.Image src, int w, int h)
    {
        var b = new System.Drawing.Bitmap(w, h);
        using (var g = System.Drawing.Graphics.FromImage(b))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            using (var a = new System.Drawing.Imaging.ImageAttributes()) { a.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY); g.DrawImage(src, new System.Drawing.Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, System.Drawing.GraphicsUnit.Pixel, a); }
        }
        return b;
    }

    static void DrawTestPicture(System.Drawing.Graphics g, int w, int h)
    {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using (var bg = new System.Drawing.Drawing2D.LinearGradientBrush(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Color.FromArgb(40, 44, 60), System.Drawing.Color.FromArgb(150, 140, 120), 20f))
            g.FillRectangle(bg, 0, 0, w, h);
        // texture (bottom right): smooth value noise at three scales, green-brown like foliage or a game's terrain
        var rnd = new Random(7);
        for (int scale = 64, pass = 0; pass < 3; scale /= 3, pass++)
            for (int y = h / 2; y < h; y += scale)
                for (int x = w / 2; x < w; x += scale)
                    using (var b = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(90, 60 + rnd.Next(90), 90 + rnd.Next(100), 30 + rnd.Next(60))))
                        g.FillEllipse(b, x - scale / 2 + rnd.Next(scale), y - scale / 2 + rnd.Next(scale), scale * 1.4f, scale * 1.1f);
        // text (top left): chat-like lines, white on dark and dark on light, several sizes
        using (var panel = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(24, 24, 27))) g.FillRectangle(panel, 0, 0, w / 2, h / 2);
        using (var light = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(235, 235, 240))) g.FillRectangle(light, w / 4, h / 4, w / 4, h / 4);
        float ty = 16;
        foreach (float px in new[] { 18f, 22f, 26f, 32f, 40f, 52f })
        {
            using (var f = new System.Drawing.Font("Segoe UI", px, System.Drawing.GraphicsUnit.Pixel))
                g.DrawString("NightOwl: gg that was so clean 1234", f, System.Drawing.Brushes.White, 16, ty);
            ty += px * 1.35f;
        }
        using (var f = new System.Drawing.Font("Segoe UI Semibold", 30f, System.Drawing.GraphicsUnit.Pixel))
        {
            g.DrawString("Score 98,765", f, System.Drawing.Brushes.Black, w / 4 + 20, h / 4 + 30);
            g.DrawString("LIVE 2:31:07", f, System.Drawing.Brushes.DarkRed, w / 4 + 20, h / 4 + 90);
        }
        // Siemens star (top right): resolution and aliasing
        float cx = w * 3 / 4f, cy = h / 4f, r = h / 4f - 20;
        for (int i = 0; i < 72; i += 2)
        {
            double a0 = i * Math.PI / 36, a1 = (i + 1) * Math.PI / 36;
            var pts = new[] { new System.Drawing.PointF(cx, cy), new System.Drawing.PointF(cx + (float)(r * Math.Cos(a0)), cy + (float)(r * Math.Sin(a0))), new System.Drawing.PointF(cx + (float)(r * Math.Cos(a1)), cy + (float)(r * Math.Sin(a1))) };
            g.FillPolygon(System.Drawing.Brushes.White, pts);
        }
        // fine lines (bottom left): 2, 4, 6 px at the truth's double size = 1, 2, 3 px in the truth; diagonals; coloured edges
        int lx = 20, ly = h / 2 + 20;
        foreach (int lw in new[] { 2, 4, 6 })
        {
            for (int k = 0; k < 8; k++) g.FillRectangle(System.Drawing.Brushes.White, lx + k * lw * 3, ly, lw, h / 4 - 40);
            lx += lw * 3 * 8 + 30;
        }
        using (var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(250, 220, 60), 3f))
            for (int k = 0; k < 10; k++) g.DrawLine(pen, 20 + k * 22, h - 30, 220 + k * 22, h * 3 / 4 + 10);
        g.FillRectangle(System.Drawing.Brushes.Red, w / 4 + 40, h * 3 / 4, 120, 100);
        g.FillRectangle(System.Drawing.Brushes.Blue, w / 4 + 160, h * 3 / 4, 120, 100);
        g.FillEllipse(System.Drawing.Brushes.Magenta, w / 4 + 100, h * 3 / 4 + 30, 120, 120);
    }

    // a still picture as a YUV4MPEG2 video (BT.601 limited range, 4:2:0), frames copies of it
    static void WriteY4m(System.Drawing.Bitmap bmp, string path, int frames)
    {
        int W = bmp.Width, H = bmp.Height;
        var y = new byte[W * H]; var u = new byte[W * H / 4]; var v = new byte[W * H / 4];
        var cb = new double[W * H]; var cr = new double[W * H];
        for (int row = 0; row < H; row++)
            for (int col = 0; col < W; col++)
            {
                var c = bmp.GetPixel(col, row);
                double R = c.R / 255.0, G = c.G / 255.0, B = c.B / 255.0;
                y[row * W + col] = (byte)Math.Round(16 + 65.481 * R + 128.553 * G + 24.966 * B);
                cb[row * W + col] = 128 - 37.797 * R - 74.203 * G + 112.0 * B;
                cr[row * W + col] = 128 + 112.0 * R - 93.786 * G - 18.214 * B;
            }
        for (int row = 0; row < H / 2; row++)
            for (int col = 0; col < W / 2; col++)
            {
                int i = row * 2 * W + col * 2;
                u[row * W / 2 + col] = (byte)Math.Round((cb[i] + cb[i + 1] + cb[i + W] + cb[i + W + 1]) / 4);
                v[row * W / 2 + col] = (byte)Math.Round((cr[i] + cr[i + 1] + cr[i + W] + cr[i + W + 1]) / 4);
            }
        string tmp = path + "." + Process.GetCurrentProcess().Id;
        using (var f = new FileStream(tmp, FileMode.Create))
        {
            var head = Encoding.ASCII.GetBytes("YUV4MPEG2 W" + W + " H" + H + " F30:1 Ip A1:1 C420jpeg\n");
            f.Write(head, 0, head.Length);
            var frame = Encoding.ASCII.GetBytes("FRAME\n");
            for (int n = 0; n < frames; n++) { f.Write(frame, 0, frame.Length); f.Write(y, 0, y.Length); f.Write(u, 0, u.Length); f.Write(v, 0, v.Length); }
        }
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
    }

    // 2 seconds of moving colour bars, 640x360 at 30 fps, as YUV4MPEG2 (mpv reads it without any codec)
    static string Video()
    {
        string path = Path.Combine(Path.GetTempPath(), "liteview-ci-bars.y4m");
        if (File.Exists(path)) return path;
        const int W = 640, H = 360, Frames = 60;
        byte[][] bars = { new byte[] { 235, 128, 128 }, new byte[] { 210, 16, 146 }, new byte[] { 170, 166, 16 }, new byte[] { 145, 54, 34 },
                          new byte[] { 106, 202, 222 }, new byte[] { 81, 90, 240 }, new byte[] { 41, 240, 110 }, new byte[] { 16, 128, 128 } };
        string tmp = path + "." + Process.GetCurrentProcess().Id;
        using (var f = new FileStream(tmp, FileMode.Create))
        {
            var head = Encoding.ASCII.GetBytes("YUV4MPEG2 W" + W + " H" + H + " F30:1 Ip A1:1 C420jpeg\n");
            f.Write(head, 0, head.Length);
            var y = new byte[W * H]; var u = new byte[W * H / 4]; var v = new byte[W * H / 4];
            var frame = Encoding.ASCII.GetBytes("FRAME\n");
            for (int n = 0; n < Frames; n++)
            {
                int shift = n * W / Frames;
                for (int row = 0; row < H; row++)
                    for (int col = 0; col < W; col++)
                    {
                        var c = bars[((col + shift) % W) * bars.Length / W];
                        y[row * W + col] = row > H * 3 / 4 ? (byte)(16 + (col + shift) % W * 219 / W) : c[0];
                        if (row % 2 == 0 && col % 2 == 0) { u[row / 2 * W / 2 + col / 2] = c[1]; v[row / 2 * W / 2 + col / 2] = c[2]; }
                    }
                f.Write(frame, 0, frame.Length); f.Write(y, 0, y.Length); f.Write(u, 0, u.Length); f.Write(v, 0, v.Length);
            }
        }
        try { File.Move(tmp, path); } catch (IOException) { File.Delete(tmp); }   // another copy made it first
        return path;
    }
}
