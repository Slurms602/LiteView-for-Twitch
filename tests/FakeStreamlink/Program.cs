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
        Log("[cli][info] Available streams: audio_only, 160p (worst), 360p, 480p, 720p60, 1080p60 (best)");
        Log("[cli][info] Opening stream: 1080p60 (hls)");
        Log("[cli][info] Starting player: " + player);
        string video = Video();
        var psi = new ProcessStartInfo(player, playerArgs + " --loop-file=inf --force-media-title=" + channel + " \"" + video + "\"") { UseShellExecute = false };
        using (var p = Process.Start(psi)) p.WaitForExit();
        Log("[cli][info] Player closed");
        return 0;
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
