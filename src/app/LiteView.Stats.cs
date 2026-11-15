// The stats overlay (I, or i on the video): what the stream and the PC are doing, drawn by mpv on the video (its OSD,
// so it also shows in fullscreen). Once a second, while it is on: one expand-text request to mpv (PlaybackStats),
// the variant Streamlink opened (its log), the picture preset, how long the stream took to its first frame, and CPU
// and memory of LiteView and of the player processes (Streamlink and mpv, from the stream's job object). Not shown:
// GPU use (no dependable source without vendor tools) and the delay behind live (Twitch's player API only).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using LiteViewCore;

partial class LiteView
{
    bool statsOn;
    readonly System.Windows.Forms.Timer statsTimer = new System.Windows.Forms.Timer { Interval = 1000 };
    string openedVariant = ""; int openedVariantGen = -1;
    readonly Dictionary<int, TimeSpan> cpuPrev = new Dictionary<int, TimeSpan>();
    DateTime cpuAt;
    string cpuText = "", memText = "";

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool QueryInformationJobObject(IntPtr job, int infoClass, IntPtr info, int length, out int returned);

    void InitStats()
    {
        statsTimer.Tick += (s, e) => StatsTick();
        FormClosed += (s, e) => statsTimer.Stop();
    }

    // PipeLoop thread: a line from mpv that the fork handles (true: upstream's code need not look at it)
    bool ForkPipeLine(string line, int g)
    {
        if (IsFirstFrame(line)) { Ui(g, FirstFrame); return true; }
        if (line.IndexOf("\"request_id\":" + PlaybackStats.RequestId, StringComparison.Ordinal) >= 0)
        {
            var st = PlaybackStats.ParseAnswer(line);
            if (st != null) Ui(g, () => OnStats(st));
            return true;
        }
        if (PictureLine(line, g)) return true;
        if (VideoLine(line, g)) return true;
        if (VideoEndLine(line, g)) return true;
        if (line.IndexOf("client-message", StringComparison.Ordinal) >= 0)
        {
            if (line.IndexOf("\"liteview-stats\"", StringComparison.Ordinal) >= 0) { Ui(g, ToggleStats); return true; }
            if (line.IndexOf("\"liteview-compare\"", StringComparison.Ordinal) >= 0) { Ui(g, ToggleCompare); return true; }
        }
        return false;
    }

    void ToggleStats()
    {
        if (!watching) return;
        statsOn = !statsOn;
        Log.Info("stats overlay " + (statsOn ? "on" : "off"));
        if (statsOn) { cpuPrev.Clear(); statsTimer.Start(); StatsTick(); }
        else { statsTimer.Stop(); SendMpv(PlaybackStats.OverlayCommand(null)); }
    }

    void StatsTick()
    {
        if (!statsOn) { statsTimer.Stop(); return; }
        if (!watching || mpvGen != gen) return;   // nothing plays (paused, opening): the answer comes with the next stream
        SampleCpu();
        SendMpv(PlaybackStats.Request());
        SendMpv(PlaybackStats.PassesRequest());
    }

    // PipeLoop, UI thread: mpv's answer
    void OnStats(MpvStats s)
    {
        if (!statsOn || !watching) return;
        if (openedVariantGen != gen)
        {
            try { if (System.IO.File.Exists(curLog)) { openedVariant = Quality.Opened(ReadShared(curLog)); if (openedVariant.Length > 0) openedVariantGen = gen; } }
            catch (System.IO.IOException) { } catch (UnauthorizedAccessException) { }
        }
        var rows = new List<KeyValuePair<string, string>>();
        Action<string, string> row = (k, v) => { if (!string.IsNullOrEmpty(v)) rows.Add(new KeyValuePair<string, string>(k, v)); };
        string chosen = (string)quality.SelectedItem ?? "Best";
        row("Channel", channel);
        row("Stream", (openedVariantGen == gen ? openedVariant : "?") + "  (chosen: " + chosen + ")");
        var video = new List<string>();
        if (PlaybackStats.CodecName(s.Codec).Length > 0) video.Add(PlaybackStats.CodecName(s.Codec));
        if (s.Width > 0 && s.Height > 0) video.Add(s.Width + "x" + s.Height);
        if (s.Fps > 0) video.Add(s.Fps.ToString("0", CultureInfo.InvariantCulture) + " fps");
        if (s.VideoBitrate > 0) video.Add(PlaybackStats.Mbps(s.VideoBitrate));
        row("Video", string.Join(", ", video));
        row("Decoding", PlaybackStats.Decoding(s.Hwdec));
        if (s.Dropped >= 0) row("Dropped", s.Dropped + " frames" + (s.DecoderDropped > 0 ? " (+" + s.DecoderDropped + " by the decoder)" : ""));
        row("Buffer", PlaybackStats.Seconds(s.BufferSeconds));
        string pic = ((string)picture.SelectedItem ?? "").Replace("Picture: ", "");
        if (rtx.Checked && rtx.Visible) pic += " + RTX";
        if (gamingActive) pic += "  (Gaming mode: lowered)";
        if (pic.StartsWith("High", StringComparison.Ordinal) || pic.StartsWith("Ultra", StringComparison.Ordinal)) pic += ", sharpness " + sharpness.ToString().ToLowerInvariant();
        if (comparing) pic = "original (B returns to " + pic + ")";
        row("Picture", pic);
        if (gpuGen == gen && gpuMs > 0)
            row("GPU per frame", gpuMs.ToString(gpuMs < 10 ? "0.00" : "0.0", CultureInfo.InvariantCulture) + " ms" + (s.DisplayFps > 0 ? "  (a frame lasts " + (1000 / s.DisplayFps).ToString("0.0", CultureInfo.InvariantCulture) + " ms at " + s.DisplayFps.ToString("0", CultureInfo.InvariantCulture) + " Hz)" : ""));
        if (lastOpenSeconds >= 0 && firstFrameGen == gen) row("Opened in", PlaybackStats.Seconds(lastOpenSeconds));
        row("CPU", cpuText);
        row("Memory", memText);
        SendMpv(PlaybackStats.OverlayCommand(PlaybackStats.Ass(rows, "LiteView stats  (I hides this)"), this.video.Height));
    }

    // CPU (share of the whole processor, like Task Manager) and private memory of LiteView and of the stream's processes
    void SampleCpu()
    {
        var now = DateTime.UtcNow;
        double wall = (now - cpuAt).TotalMilliseconds * Environment.ProcessorCount;
        bool first = cpuPrev.Count == 0 || wall <= 0;
        cpuAt = now;
        double meCpu = 0, playerCpu = 0; long meMem = 0, playerMem = 0;
        var seen = new HashSet<int>();
        Action<Process, bool> add = (p, mine) =>
        {
            try
            {
                var t = p.TotalProcessorTime; long mem = p.PrivateMemorySize64;
                TimeSpan before; double pct = 0;
                if (cpuPrev.TryGetValue(p.Id, out before) && wall > 0) pct = Math.Max(0, (t - before).TotalMilliseconds / wall * 100);
                cpuPrev[p.Id] = t; seen.Add(p.Id);
                if (mine) { meCpu += pct; meMem += mem; } else { playerCpu += pct; playerMem += mem; }
            }
            catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        };
        using (var me = Process.GetCurrentProcess()) add(me, true);
        foreach (int pid in JobProcessIds(job))
        {
            try { using (var p = Process.GetProcessById(pid)) add(p, false); }
            catch (ArgumentException) { } catch (InvalidOperationException) { }
        }
        foreach (var k in new List<int>(cpuPrev.Keys)) if (!seen.Contains(k)) cpuPrev.Remove(k);
        Func<double, string> pc = v => v.ToString(v < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " %";
        cpuText = first ? "measuring..." : "LiteView " + pc(meCpu) + ", player " + pc(playerCpu);
        memText = "LiteView " + meMem / (1024 * 1024) + " MB, player " + playerMem / (1024 * 1024) + " MB";
    }

    // the processes in a job object (Streamlink and the mpv it started)
    static List<int> JobProcessIds(IntPtr j)
    {
        var list = new List<int>();
        if (j == IntPtr.Zero) return list;
        const int Max = 32; int size = 8 + Max * IntPtr.Size;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            int got;
            if (!QueryInformationJobObject(j, 3, buf, size, out got)) return list;   // JobObjectBasicProcessIdList
            int n = Math.Min(Max, Marshal.ReadInt32(buf, 4));
            for (int i = 0; i < n; i++) list.Add((int)Marshal.ReadIntPtr(buf, 8 + i * IntPtr.Size).ToInt64());
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }
}
