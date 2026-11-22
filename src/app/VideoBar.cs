// The seek bar under a past broadcast, highlight, upload or clip (owner's request: "a scrollbar for the video, to jump
// around"): where the video is and how long it is, a purple bar like Twitch's; click or drag to jump there (the time
// under the mouse shows on the left while hovering or dragging). The video's own keys still work: Left/Right 10 s.
// It sits below the picture (not over it: mpv's window belongs to another process and cannot be drawn on).
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using LiteViewCore;

sealed class VideoBar : Control
{
    public const int BarHeight = 34;
    static readonly Color Back = Theme.Panel, Track = Theme.Line, Played = Theme.Accent, Fg = Theme.Text, Dim = Theme.Dim, Knob = Theme.Accent;
    double pos, dur, dragAt = -1;
    int hoverX = -1;
    public event Action<double> Seek;

    public VideoBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        Height = BarHeight; BackColor = Back; Cursor = Cursors.Hand; TabStop = false;
        AccessibleRole = AccessibleRole.Slider; AccessibleName = "Video position";
    }

    public double Position { get { return pos; } }
    public double Duration { get { return dur; } }

    /// <summary>Where the video is (seconds) and how long it is (0: not known yet).</summary>
    public void Set(double position, double duration)
    {
        if (dragAt < 0) pos = Math.Max(0, position);
        if (duration > 0) dur = duration;
        AccessibleDescription = Clock(pos) + " of " + Clock(dur);
        Invalidate();
    }

    public void Clear() { pos = dur = 0; dragAt = -1; hoverX = -1; Invalidate(); }

    static Font F { get { return UiFont.Get(FontKind.Text, 9f); } }

    static string Clock(double s)
    {
        if (s <= 0 || double.IsNaN(s)) return "0:00";
        string t = VideoData.Length((int)s);
        return t.Length > 0 ? t : "0:00";
    }

    // the track's ends (they leave room for the two times)
    void TrackEnds(out int x0, out int x1)
    {
        int w = Txt.Width(dur >= 3600 ? "00:00:00" : "00:00", F) + 4;
        x0 = 12 + w + 10; x1 = Width - 12 - w - 10;
        if (x1 < x0 + 10) x1 = x0 + 10;
    }

    double TimeAt(int x)
    {
        int x0, x1; TrackEnds(out x0, out x1);
        double f = Math.Max(0, Math.Min(1, (x - x0) / (double)(x1 - x0)));
        return f * dur;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        int x0, x1; TrackEnds(out x0, out x1);
        int y = Height / 2;
        bool hot = hoverX >= 0 || dragAt >= 0;
        double shown = dragAt >= 0 ? dragAt : pos;
        double lookAt = dragAt >= 0 ? dragAt : hoverX >= 0 ? TimeAt(hoverX) : pos;
        int th = Txt.Height(F);
        Txt.At(g, Clock(lookAt), F, hot ? Played : Fg, 12, y - th / 2);
        string d = Clock(dur);
        Txt.At(g, d, F, Dim, Width - 12 - Txt.Width(d, F), y - th / 2);
        int h = hot ? 6 : 4;
        using (var b = new SolidBrush(Track)) g.FillRectangle(b, x0, y - h / 2, x1 - x0, h);
        if (dur > 0)
        {
            int xp = x0 + (int)Math.Round((x1 - x0) * Math.Max(0, Math.Min(1, shown / dur)));
            using (var b = new SolidBrush(Played)) g.FillRectangle(b, x0, y - h / 2, xp - x0, h);
            if (hot)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(Knob)) g.FillEllipse(b, xp - 6, y - 6, 12, 12);
                if (hoverX >= 0 && dragAt < 0) using (var p = new Pen(Color.FromArgb(140, Fg), 1)) g.DrawLine(p, hoverX, y - h, hoverX, y + h);
            }
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || dur <= 0) return;
        Capture = true; dragAt = TimeAt(e.X); Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (dragAt >= 0) { dragAt = TimeAt(e.X); Invalidate(); return; }
        hoverX = e.X; Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (dragAt < 0) return;
        double t = TimeAt(e.X);
        dragAt = -1; Capture = false; pos = t; Invalidate();
        if (Seek != null) Seek(t);
    }

    protected override void OnMouseLeave(EventArgs e) { hoverX = -1; Invalidate(); base.OnMouseLeave(e); }
}
