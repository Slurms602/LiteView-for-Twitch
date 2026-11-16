// Pause (P4): the button left of the volume, Space, or the keyboard's play/pause key. A live stream cannot be held
// without downloading it, so Pause ends Streamlink and mpv: no download, no decoding, no GPU work for the video.
// The chat keeps running. Play starts the stream again at the live edge (a few seconds, like opening the channel); a
// video continues where it was.
// The picture stays (owner's request: pause to read something on screen): mpv saves the frame it shows (its
// screenshot-to-file, the video's own pixels, without the stats or other text over it) to a temporary file just before
// it ends; LiteView shows that frame, fitted to the video area, until Play, and deletes the file at once. If mpv does not
// answer within PauseShotWait, it pauses without the frame (black, as before).
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using LiteViewCore;

partial class LiteView
{
    static readonly string PauseGlyph = ((char)0xE769).ToString(), PlayGlyph = ((char)0xE768).ToString();
    const int PauseShotId = 94, PauseShotWait = 1500;
    readonly Button pauseBtn = new Button();
    readonly PausedView pausedNote = new PausedView();
    readonly ToolTip pauseTip = new ToolTip();
    readonly System.Windows.Forms.Timer pauseWait = new System.Windows.Forms.Timer { Interval = PauseShotWait };
    bool paused, pauseShooting;
    string pauseShot;

    void InitPause()
    {
        Flat(pauseBtn, PauseGlyph); pauseBtn.Font = new Font("Segoe MDL2 Assets", 11f); pauseBtn.Width = 34;
        pauseBtn.AccessibleName = "Pause";
        pauseBtn.Click += (s, e) => TogglePause();
        pauseTip.SetToolTip(pauseBtn, "Pause (Space): stops the video and its download (the picture stays), the chat stays.\nPlay continues at live.");
        bar.Controls.Add(pauseBtn);

        pausedNote.Dock = DockStyle.Fill; pausedNote.Visible = false;
        pausedNote.Font = new Font("Segoe UI", 12f); pausedNote.Cursor = Cursors.Hand;
        pausedNote.Text = "Paused  ·  click or Space: continue at live";
        pausedNote.Click += (s, e) => TogglePause();
        video.Controls.Add(pausedNote);
        pauseWait.Tick += (s, e) => { if (pauseShooting) StopForPause(false); };
    }

    void TogglePause()
    {
        if (!watching || channel == null || pauseShooting) return;
        if (paused) { Log.Info("play"); Replay(); }
        else Pause();
    }

    void Pause()
    {
        if (paused || pauseShooting) return;
        if (watching && mpvGen == gen && firstFrameGen == gen)
        {   // the picture as it is now, then the stream stops (StopForPause, when mpv answers)
            pauseShot = Path.Combine(Path.GetTempPath(), "liteview-paused-" + Process.GetCurrentProcess().Id + ".jpg");
            try { if (File.Exists(pauseShot)) File.Delete(pauseShot); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            pauseShooting = true;
            SendMpv("{\"command\":[\"screenshot-to-file\"," + Quote(pauseShot.Replace('\\', '/')) + ",\"video\"],\"request_id\":" + PauseShotId + "}");
            pauseWait.Start();
            return;
        }
        StopForPause(false);
    }

    // PipeLoop thread: mpv saved the frame (or could not)
    bool PauseLine(string line, int g)
    {
        if (line.IndexOf("\"request_id\":" + PauseShotId, StringComparison.Ordinal) < 0) return false;
        bool ok = line.IndexOf("\"error\":\"success\"", StringComparison.Ordinal) >= 0;
        Ui(g, () => { if (pauseShooting) StopForPause(ok); });
        return true;
    }

    void StopForPause(bool shot)
    {
        pauseShooting = false; pauseWait.Stop();
        Bitmap frame = shot ? LoadFrame(pauseShot) : null;
        CancelRetry(); StopStream();
        paused = true;
        pausedNote.Picture = frame;
        pausedNote.Text = "Paused  ·  click or Space: " + (currentVideo != null ? "continue" : "continue at live");
        pausedNote.Visible = true; pausedNote.BringToFront();
        pauseBtn.Text = PlayGlyph; pauseBtn.AccessibleName = "Play";
        pauseTip.SetToolTip(pauseBtn, currentVideo != null ? "Play (Space): continues where it was" : "Play (Space): continues at live");
        status.ForeColor = Dim; status.Text = "Paused";
        Log.Info("paused: stream stopped" + (frame != null ? " (its last picture stays)" : ""));
    }

    // the saved frame, read into memory (the file is deleted at once)
    static Bitmap LoadFrame(string path)
    {
        try
        {
            if (path == null || !File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            try { File.Delete(path); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            if (bytes.Length > 64 << 20 || !ImageSniff.Acceptable(bytes)) return null;
            using (var ms = new MemoryStream(bytes))
            using (var img = Image.FromStream(ms))
            {
                if (img.Width > 8192 || img.Height > 8192) return null;
                return new Bitmap(img);
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is OutOfMemoryException) { return null; }
    }

    // a stream starts (Play, another channel, a new quality) or the Following page shows
    void EndPause()
    {
        if (!paused) return;
        paused = false;
        pausedNote.Visible = false;
        pausedNote.Picture = null;
        pauseBtn.Text = PauseGlyph; pauseBtn.AccessibleName = "Pause";
        pauseTip.SetToolTip(pauseBtn, "Pause (Space): stops the video and its download (the picture stays), the chat stays.\nPlay continues at live.");
    }
}

// The video area while paused: the last frame fitted (black bars where it does not fill), and a small note at the bottom.
sealed class PausedView : Control
{
    Bitmap picture;

    public PausedView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Black; ForeColor = Theme.OnVideo;
    }

    public Bitmap Picture
    {
        get { return picture; }
        set { if (picture != null && picture != value) picture.Dispose(); picture = value; Invalidate(); }
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.Black);
        if (picture != null && Width > 0 && Height > 0)
        {
            float k = Math.Min(Width / (float)picture.Width, Height / (float)picture.Height);
            int w = (int)(picture.Width * k), h = (int)(picture.Height * k);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(picture, (Width - w) / 2, (Height - h) / 2, w, h);
        }
        var size = TextRenderer.MeasureText(Text, Font);
        if (picture == null) { TextRenderer.DrawText(g, Text, Font, ClientRectangle, ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); return; }
        // the note in a dark pill at the bottom, so the picture stays readable
        var r = new Rectangle((Width - size.Width - 28) / 2, Height - size.Height - 30, size.Width + 28, size.Height + 12);
        using (var b = new SolidBrush(Color.FromArgb(200, 14, 14, 16))) g.FillRectangle(b, r);
        TextRenderer.DrawText(g, Text, Font, r, Color.FromArgb(239, 239, 241), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    protected override void Dispose(bool disposing) { if (disposing && picture != null) { picture.Dispose(); picture = null; } base.Dispose(disposing); }
}
