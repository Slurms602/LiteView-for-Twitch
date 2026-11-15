// Picture presets (P3): the Sharpness of High and Ultra (menu > Playback > Sharpness; one sharpener at most per preset,
// src/core/PicturePreset.cs), B (or b on the video) to see the original picture and back, and the Playback menu.
// The stats overlay (I) shows the GPU time the chosen preset costs per frame, measured by mpv (vo-passes).
using System;
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
        m.DropDownItems.AddRange(new ToolStripItem[] { smooth, sharp, new ToolStripSeparator(), stats, cmp });
        DarkMenuColors.Apply(m.DropDown);
        return m;
    }

    // PipeLoop thread: mpv's GPU time answer (request PlaybackStats.PassesId)
    bool PictureLine(string line, int g)
    {
        if (line.IndexOf("\"request_id\":" + PlaybackStats.PassesId, StringComparison.Ordinal) < 0) return false;
        double ms = PlaybackStats.GpuMs(line);
        Ui(g, () => { gpuMs = ms; gpuGen = gen; });
        return true;
    }
}
