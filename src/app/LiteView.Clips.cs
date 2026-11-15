// Clips saved on this PC (owner's request: "make my own clips, and save them locally"): the scissors button in the top
// bar, Alt+X (Twitch's own clip key) or menu > Playback > Save a clip writes the last ClipSeconds of what is playing
// (live or a video) to Videos\LiteView Clips\<channel> <date> <time>.mkv. Nothing is sent to Twitch.
// How: mpv keeps the stream's last ClipSeconds in memory (its demuxer cache, --demuxer-max-back-bytes, sized for about
// 8 Mb/s, Twitch's highest bitrate), and its dump-cache command writes that part as it came (no re-encoding: the
// original quality, no CPU or GPU work) into a Matroska file, which plays in VLC, mpv, Windows' Media Player and
// browsers' players. The memory it costs (about 1 MB a second of clip) is why the length is a setting (menu > Playback >
// Clips: 30 s, 60 s, 2 minutes, or Off, which keeps upstream's 4 MB). A clip can be shorter right after a stream started.
using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using LiteViewCore;

partial class LiteView
{
    public static readonly int[] ClipChoices = { 30, 60, 120, 0 };
    int clipSeconds = 60;   // setting "clipsecs"; 0: off
    readonly Button clipBtn = new Button();
    const int ClipPosId = 92, ClipSaveId = 93;
    string clipFile; int clipGen = -1; DateTime clipAsked;

    static string ClipFolder { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "LiteView Clips"); } }

    /// <summary>The player's memory for clips (MiB), for StreamRequest.BackBufferMiB; 0: mpv.conf's 4 MB.</summary>
    int ClipBufferMiB { get { return clipSeconds <= 0 ? 0 : StreamCommand.ClipBufferMiB(clipSeconds); } }

    void InitClips()
    {
        Flat(clipBtn, ((char)0xE8C6).ToString()); clipBtn.Font = new Font("Segoe MDL2 Assets", 11f); clipBtn.Width = 34;
        clipBtn.AccessibleName = "Save a clip";
        clipBtn.Click += (s, e) => SaveClip();
        bar.Controls.Add(clipBtn);
        UpdateClipTip();
    }

    void UpdateClipTip()
    {
        new ToolTip().SetToolTip(clipBtn, clipSeconds > 0
            ? "Save a clip (Alt+X): the last " + ClipName(clipSeconds) + " to Videos\\LiteView Clips.\nThe length: menu > Playback > Clips."
            : "Clips are off: menu > Playback > Clips (then they work from the next stream).");
    }

    static string ClipName(int s) { return s >= 120 ? (s / 60) + " minutes" : s + " s"; }

    void SaveClip()
    {
        if (!watching || paused || mpvGen != gen || firstFrameGen != gen) { status.ForeColor = Dim; status.Text = "Nothing to clip yet"; return; }
        if (clipSeconds <= 0) { status.ForeColor = Dim; status.Text = "Clips are off: menu > Playback > Clips"; return; }
        if (clipGen == gen && DateTime.Now - clipAsked < TimeSpan.FromSeconds(3)) return;   // one at a time
        try { Directory.CreateDirectory(ClipFolder); }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { ShowProblem("Could not save the clip", "The folder " + ClipFolder + " could not be made: " + ex.Message, null); return; }
        string name = (channel ?? "clip") + " " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture) + ".mkv";
        clipFile = Path.Combine(ClipFolder, name); clipGen = gen; clipAsked = DateTime.Now;
        SendMpv("{\"command\":[\"get_property\",\"time-pos\"],\"request_id\":" + ClipPosId + "}");   // then dump-cache from there (ClipLine)
        Log.Info("clip: asked (" + ClipName(clipSeconds) + ")");
    }

    // PipeLoop thread: the position (then mpv writes the clip), and mpv's answer to that
    bool ClipLine(string line, int g)
    {
        bool pos = line.IndexOf("\"request_id\":" + ClipPosId, StringComparison.Ordinal) >= 0;
        if (!pos && line.IndexOf("\"request_id\":" + ClipSaveId, StringComparison.Ordinal) < 0) return false;
        var v = Json.Parse(line);
        string err = Json.S(v, "error") ?? "no answer";
        if (pos)
        {
            var d = Json.V(v, "data");
            double t = d is double dd ? dd : d is long dl ? dl : -1;
            Ui(g, () =>
            {
                if (clipGen != gen || clipFile == null) return;
                if (err != "success" || t < 0) { ClipFailed("mpv did not say where the stream is (" + err + ")"); return; }
                // from ClipSeconds before now to now: a fixed range ("no" as the end would keep writing as the stream goes on)
                string start = Math.Max(0, t - clipSeconds).ToString("0.000", CultureInfo.InvariantCulture), end = t.ToString("0.000", CultureInfo.InvariantCulture);
                SendMpv("{\"command\":[\"dump-cache\"," + start + "," + end + "," + Quote(clipFile.Replace('\\', '/')) + "],\"request_id\":" + ClipSaveId + "}");
            });
            return true;
        }
        Ui(g, () =>
        {
            if (clipGen != gen || clipFile == null) return;
            string file = clipFile; clipFile = null;
            long size = 0;
            try { size = new FileInfo(file).Length; } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            if (err != "success" || size == 0) { ClipFailed("mpv could not write it (" + err + ")"); return; }
            Log.Info("clip: saved (" + (size / (1024 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB)");
            status.ForeColor = Dim; status.Text = "Clip saved: " + Path.GetFileName(file) + "  (menu > Playback > Open the clips folder)";
            SendMpv("{\"command\":[\"show-text\"," + Quote("Clip saved: " + Path.GetFileName(file)) + ",2500]}");
        });
        return true;
    }

    void ClipFailed(string why)
    {
        Log.Warn("clip: not saved, " + why);
        if (clipFile != null) { try { var f = new FileInfo(clipFile); if (f.Exists && f.Length == 0) f.Delete(); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { } }
        clipFile = null;
        ShowProblem("Could not save the clip", "LiteView asked the player to save the last " + ClipName(clipSeconds) + ", and " + why + ".", null);
    }

    void OpenClipFolder()
    {
        try { Directory.CreateDirectory(ClipFolder); System.Diagnostics.Process.Start("explorer.exe", "\"" + ClipFolder + "\""); }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception) { Log.Warn("clip folder: " + ex.GetType().Name); }
    }

    ToolStripMenuItem ClipsMenu()
    {
        var m = new ToolStripMenuItem("Clips");
        var save = new ToolStripMenuItem("Save a clip  (Alt+X)") { Enabled = watching && clipSeconds > 0 };
        save.Click += (s, e) => SaveClip();
        m.DropDownItems.Add(save);
        m.DropDownItems.Add(new ToolStripSeparator());
        foreach (int c in ClipChoices)
        {
            int k = c;
            var it = new ToolStripMenuItem(k == 0 ? "Off (no extra memory)" : "Keep the last " + ClipName(k) + "  (about " + StreamCommand.ClipBufferMiB(k) + " MB)" + (k == 60 ? "  (default)" : "")) { Checked = clipSeconds == k };
            it.Click += (s, e) => { clipSeconds = k; SaveSettings(); UpdateClipTip(); Log.Info("clips: " + (k == 0 ? "off" : k + " s") + " (from the next stream)"); };
            m.DropDownItems.Add(it);
        }
        m.DropDownItems.Add(new ToolStripSeparator());
        var open = new ToolStripMenuItem("Open the clips folder");
        open.Click += (s, e) => OpenClipFolder();
        m.DropDownItems.Add(open);
        DarkMenuColors.Apply(m.DropDown);
        return m;
    }

    // UI fixture (CI), scene "clip": a stream, then a clip of it
    void ClipScene()
    {
        Watch("play_demo");
        int waited = 0;
        var t = new Timer { Interval = 500 };
        t.Tick += (s, e) => { waited += 500; if ((firstFrameGen != gen || waited < 7000) && waited < 30000) return; t.Stop(); t.Dispose(); SaveClip(); };
        t.Start();
    }
}
