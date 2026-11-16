// Picture presets (P3): the Sharpness of High and Ultra (menu > Playback > Sharpness; one sharpener at most per preset,
// src/core/PicturePreset.cs), B (or b on the video) to see the original picture and back, and the Playback menu.
// The stats overlay (I) shows the GPU time the chosen preset costs per frame, measured by mpv (vo-passes).
//
// RTX (NVIDIA RTX Video Super Resolution through mpv's d3d11vpp) only does something when the video is enlarged: it
// enlarges it to the size it is shown at, and the preset's own sharpening (and Ultra's clean-up) come after it.
// Upstream also ran it at 1.05x when the video was shown at about its own size (a 1080p stream in a window on a
// 1440p screen), and the owner's screenshots showed that only made the picture softer (edge energy 39.5 with
// Enhance + RTX against 42.8 with Off and 61.3 with Enhance; Ultra + RTX 53.7 against Ultra 57.0). So below
// RtxMinScale RTX stays off; the stats overlay says so.
//
// Memory: mpv keeps what a heavier preset needed (Ultra's compiled shaders: the player went from about 600 MB with
// High to 1150 MB with Ultra on the owner's PC) until it closes, so choosing a lighter preset did not give it back
// until a pause or the next channel. Now choosing a lighter preset after High or Ultra restarts the player behind the
// picture (seamless switching: the old one plays until the new one shows its first frame) a moment later.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Forms;
using LiteViewCore;

partial class LiteView
{
    Sharpness sharpness = Sharpness.Normal;   // setting "sharpness"
    bool comparing;                           // B: the original picture (no shaders, no RTX), until B again or the next stream
    double gpuMs = -1; int gpuGen = -1;

    string rtxNote = "";   // the stats overlay's word on RTX
    const string RtxTip = "NVIDIA RTX Video Super Resolution: NVIDIA's AI enlarges the video to the size it is shown at,\n"
        + "then the picture preset sharpens it. It works when the video is enlarged at least 1.15x with High or Ultra (their sharpening\n"
        + "comes after RTX), and 1.3x with Off or Enhance (which cannot sharpen after RTX: below that RTX alone looked softer).\n"
        + "Its GPU work is not in the stats' \"GPU per frame\". Needs RTX Video Enhancement > Super Resolution on in the NVIDIA app.";

    // ApplyEnhance: d3d11vpp's scale for RTX, or "off"
    string RtxScale(bool useRtx, double scale, int pic)
    {
        if (!useRtx) { rtxNote = ""; return "off"; }
        string vf = PicturePreset.RtxScale(scale, pic);
        if (vf == null)
        {
            rtxNote = "RTX waits (the video is shown at " + scale.ToString("0.00", CultureInfo.InvariantCulture) + "x, RTX needs " + PicturePreset.RtxMin(pic).ToString("0.00", CultureInfo.InvariantCulture) + "x"
                + (pic < 2 ? "; High and Ultra use it from " + PicturePreset.RtxMinScaleSharpened.ToString("0.00", CultureInfo.InvariantCulture) + "x" : "") + ")";
            return "off";
        }
        rtxNote = "RTX enlarges " + vf + "x (its GPU time is not counted below)";
        return vf;
    }

    // ---------- a heavier preset's memory back ----------
    int heaviestPic, heaviestGen = -1;
    System.Windows.Forms.Timer releaseTimer;

    void PictureApplied(int pic)
    {
        if (heaviestGen != gen) { heaviestGen = gen; heaviestPic = 0; }
        if (pic > heaviestPic) heaviestPic = pic;
    }

    // the user chose a preset (the picture box)
    void PictureChosen()
    {
        if (releaseTimer == null) { releaseTimer = new System.Windows.Forms.Timer { Interval = 2500 }; releaseTimer.Tick += (s, e) => ReleasePicture(); }
        releaseTimer.Stop();
        if (!LighterThanUsed()) return;
        if (!seamless) { Log.Info("picture: lighter preset; the heavier one's memory comes back with the next stream (seamless switching is off)"); return; }
        releaseTimer.Start();   // a moment later, in case another preset is chosen first
    }

    bool LighterThanUsed()
    {
        return watching && !paused && heaviestGen == gen && heaviestPic >= 2 && Math.Max(0, picture.SelectedIndex) < heaviestPic
            && (currentVideo == null || !currentVideo.IsClip);   // a clip would start again from its beginning
    }

    void ReleasePicture()
    {
        releaseTimer.Stop();
        if (!LighterThanUsed() || firstFrameGen != gen || !seamless) return;
        Log.Info("picture: " + PicturePreset.Name(Math.Max(0, picture.SelectedIndex)) + " after " + PicturePreset.Name(heaviestPic) + ": the player restarts behind the picture to give back the memory");
        if (currentVideo != null) videoPos += 2;   // the new player opens in about 2 s; the old one plays on meanwhile
        Replay();
    }

    void ToggleCompare()
    {
        if (!watching || mpvGen != gen) return;
        comparing = !comparing;
        ApplyEnhance(true);
        string pic = ((string)picture.SelectedItem ?? "").Replace("Picture: ", "");
        SendMpv("{\"command\":[\"show-text\"," + Quote(comparing ? "Original picture (B: back to " + pic + ")" : pic) + ",2500]}");
        Log.Info("compare: " + (comparing ? "original" : pic));
    }

    static string Quote(string s)
    {
        var sb = new System.Text.StringBuilder("\"");
        foreach (char c in s) { if (c == '"' || c == '\\') sb.Append('\\'); if (c >= ' ') sb.Append(c); }
        return sb.Append('"').ToString();
    }

    ToolStripMenuItem PlaybackMenu()
    {
        var m = new ToolStripMenuItem("Playback");
        var smooth = Toggle("Seamless channel switching", seamless, v => seamless = v);
        smooth.ToolTipText = "The playing stream keeps playing until the next channel shows its first picture\n(both download for those few seconds). Off: the video is black while the next one opens.";
        var sharp = new ToolStripMenuItem("Sharpness (High and Ultra)");
        for (int i = 0; i < PicturePreset.SharpnessNames.Length; i++)
        {
            var s = (Sharpness)i;
            var it = new ToolStripMenuItem(PicturePreset.SharpnessNames[i]) { Checked = sharpness == s };
            it.Click += (o, e) => { sharpness = s; SaveSettings(); ApplyEnhance(true); Log.Info("sharpness: " + s); };
            sharp.DropDownItems.Add(it);
        }
        DarkMenuColors.Apply(sharp.DropDown);
        var stats = new ToolStripMenuItem("Stats on the video  (I)") { Checked = statsOn, Enabled = watching, Tag = "toggle" };
        stats.Click += (o, e) => ToggleStats();
        var cmp = new ToolStripMenuItem("Compare with the original picture  (B)") { Checked = comparing, Enabled = watching, Tag = "toggle" };
        cmp.Click += (o, e) => ToggleCompare();
        var prev = Toggle("Previews on hover (Following page)", browse.Previews, v => { browse.Previews = v; if (!v) StopPreview(); });
        prev.ToolTipText = "Rest the mouse on a live channel's card: it plays there, muted, at the lowest quality.";
        m.DropDownItems.AddRange(new ToolStripItem[] { smooth, prev, sharp, new ToolStripSeparator(), stats, cmp, new ToolStripSeparator(), ClipsMenu() });
        DarkMenuColors.Apply(m.DropDown);
        return m;
    }

    // UI fixture (CI), scene "release": Ultra, then Enhance; the player restarts behind the picture
    void ReleaseScene()
    {
        Watch("play_demo");
        int step = 0, waited = 0;
        var t = new System.Windows.Forms.Timer { Interval = 500 };
        t.Tick += (s, e) =>
        {
            waited += 500;
            if (step == 0) { if (firstFrameGen != gen || waited < 2000) return; if (!statsOn) ToggleStats(); picture.SelectedIndex = 3; step = 1; waited = 0; return; }   // stats on: they must stay on the new player
            if (step == 1 && waited >= 3000) { picture.SelectedIndex = 1; t.Stop(); t.Dispose(); }
        };
        t.Start();
    }

    // CI's preset comparison (fixture scene "presets", tests/FakeStreamlink plays a known test picture at half size):
    // fullscreen, then each preset (and the old Ultra, for comparison) applied in turn and the picture mpv shows saved
    // as preset-<name>.png next to LiteView.exe; tools/preset-metrics.py compares them with preset-truth.png.
    void PresetShots()
    {
        picture.SelectedIndex = 0;   // ApplyEnhance's own state stays "Off": it never replaces the lists sent here
        Watch("presets_demo");
        var shots = new List<KeyValuePair<string, string[]>>
        {
            Shot("off"), Shot("enhance", "FSR.glsl", "CAS.glsl"),
            Shot("high-soft", "FSRCNNX_x2_8-0-4-1_any.glsl", "adaptive-sharpen_soft.glsl"),
            Shot("high", "FSRCNNX_x2_8-0-4-1_any.glsl", "adaptive-sharpen.glsl"),
            Shot("high-strong", "FSRCNNX_x2_8-0-4-1_any.glsl", "adaptive-sharpen_strong.glsl"),
            Shot("ultra-nosharpen", "KrigBilateral.glsl", "nlmeans.glsl", "FSRCNNX_x2_16-0-4-1_any.glsl", "SSimDownscaler.glsl"),
            Shot("ultra-soft", "KrigBilateral.glsl", "nlmeans.glsl", "FSRCNNX_x2_16-0-4-1_any.glsl", "SSimDownscaler.glsl", "adaptive-sharpen_soft.glsl"),
            Shot("ultra", "KrigBilateral.glsl", "nlmeans.glsl", "FSRCNNX_x2_16-0-4-1_any.glsl", "SSimDownscaler.glsl", "adaptive-sharpen.glsl"),
            Shot("ultra-old", "KrigBilateral.glsl", "nlmeans_sharpen_denoise.glsl", "FSRCNNX_x2_16-0-4-1_any.glsl", "SSimDownscaler.glsl", "adaptive-sharpen_strong.glsl"),
        };
        int step = -1, wait = 0;
        var t = new System.Windows.Forms.Timer { Interval = 500 };
        t.Tick += (s, e) =>
        {
            wait += 500;
            if (step < 0)
            {   // the first frame, then fullscreen: the 512x288 picture fills 1024x576 on CI's 1024x768 screen (2x)
                if (firstFrameGen != gen) { if (wait > 30000) { t.Stop(); Log.Warn("presets: no picture"); } return; }
                if (!fullscreen) ToggleFullscreen();
                step = 0; wait = 0;
                return;
            }
            if (step >= shots.Count) { t.Stop(); t.Dispose(); Log.Info("presets: done"); return; }
            var sh = shots[step];
            bool ultra = sh.Key.StartsWith("ultra", StringComparison.Ordinal);
            if (wait == 500)
            {
                SendMpv("{\"command\":[\"apply-profile\",\"" + (ultra ? "high-quality" : "fast") + "\"]}");
                SendMpv("{\"command\":[\"change-list\",\"glsl-shaders\",\"clr\",\"\"]}");
                foreach (var n in sh.Value) SendMpv("{\"command\":[\"change-list\",\"glsl-shaders\",\"append\"," + Quote(Shader(n)) + "]}");
                SendMpv("{\"command\":[\"set_property\",\"deband\"," + (sh.Key == "off" ? "false" : "true") + "]}");
            }
            else if (wait == 6000)
            {   // what mpv shows (scaled to the window, with the shaders), as a PNG
                string file = System.IO.Path.Combine(root, "preset-" + sh.Key + ".png").Replace('\\', '/');
                SendMpv("{\"command\":[\"screenshot-to-file\"," + Quote(file) + ",\"window\"]}");
                Log.Info("preset shot: " + sh.Key);
            }
            else if (wait >= 8000) { step++; wait = 0; }
        };
        t.Start();
    }

    static KeyValuePair<string, string[]> Shot(string name, params string[] shaders) { return new KeyValuePair<string, string[]>(name, shaders); }

    // PipeLoop thread: mpv's GPU time answer (request PlaybackStats.PassesId)
    bool PictureLine(string line, int g)
    {
        if (line.IndexOf("\"request_id\":" + PlaybackStats.PassesId, StringComparison.Ordinal) < 0) return false;
        double ms = PlaybackStats.GpuMs(line);
        Ui(g, () => { gpuMs = ms; gpuGen = gen; });
        return true;
    }
}
