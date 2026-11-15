// Picture presets (P3): the Sharpness of High and Ultra (menu > Playback > Sharpness; one sharpener at most per preset,
// src/core/PicturePreset.cs), B (or b on the video) to see the original picture and back, and the Playback menu.
// The stats overlay (I) shows the GPU time the chosen preset costs per frame, measured by mpv (vo-passes).
using System;
using System.Collections.Generic;
using System.Windows.Forms;
using LiteViewCore;

partial class LiteView
{
    Sharpness sharpness = Sharpness.Normal;   // setting "sharpness"
    bool comparing;                           // B: the original picture (no shaders, no RTX), until B again or the next stream
    double gpuMs = -1; int gpuGen = -1;

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
        var stats = new ToolStripMenuItem("Stats on the video  (I)") { Checked = statsOn, Enabled = watching };
        stats.Click += (o, e) => ToggleStats();
        var cmp = new ToolStripMenuItem("Compare with the original picture  (B)") { Checked = comparing, Enabled = watching };
        cmp.Click += (o, e) => ToggleCompare();
        var prev = Toggle("Previews on hover (Following page)", browse.Previews, v => { browse.Previews = v; if (!v) StopPreview(); });
        prev.ToolTipText = "Rest the mouse on a live channel's card: it plays there, muted, at the lowest quality.";
        m.DropDownItems.AddRange(new ToolStripItem[] { smooth, prev, sharp, new ToolStripSeparator(), stats, cmp });
        DarkMenuColors.Apply(m.DropDown);
        return m;
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
