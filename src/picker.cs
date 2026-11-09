// Emote picker for the native chat, like 7TV's: a searchable grid of this channel's 7TV, BetterTTV and FFZ
// emotes, your Twitch emotes and the global sets, recently used on top. A click puts the name in the input.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using Named = System.Collections.Generic.KeyValuePair<string, Emote>;

class EmotePicker : Control
{
    public readonly TextBox Search = new TextBox();
    public event Action<string> Picked;
    public event Action HintClicked, Closed;
    public string Hint;
    static readonly Color Bg = Color.FromArgb(31, 31, 35), HoverC = Color.FromArgb(53, 53, 59), Line = Color.FromArgb(53, 53, 59);
    static readonly Font FHead = new Font("Segoe UI Semibold", 8.75f), FHint = new Font("Segoe UI", 9f);
    const int Cw = 40, Ch = 38, Hh = 26, Top0 = 46;

    class Cell { public Rectangle R; public string Name, Head; public Emote E; }
    List<KeyValuePair<string, List<Named>>> groups = new List<KeyValuePair<string, List<Named>>>();
    readonly List<Cell> cells = new List<Cell>();
    readonly ToolTip tip = new ToolTip();
    int top, total, hover = -1; string tipFor; Rectangle hintR; bool hintHover;

    public EmotePicker()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Bg;
        Search.BorderStyle = BorderStyle.FixedSingle; Search.BackColor = Color.FromArgb(38, 38, 44); Search.ForeColor = ChatView.Fg; Search.Font = new Font("Segoe UI", 10f);
        Search.Location = new Point(10, 10); Search.Width = 200; Search.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        Search.HandleCreated += (s, e) => SendMessage(Search.Handle, 0x1501, (IntPtr)1, "Search emotes");
        Search.TextChanged += (s, e) => { top = 0; Build(); Invalidate(); };
        Search.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Close(); }
            else if (e.KeyCode == Keys.Enter)
            {   // Enter: the first match
                e.SuppressKeyPress = true;
                var c = cells.FirstOrDefault(x => x.E != null);
                if (c != null && Picked != null) Picked(c.Name);
            }
        };
        Controls.Add(Search);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);

    void Close() { Visible = false; if (Closed != null) Closed(); }

    public void SetGroups(List<KeyValuePair<string, List<Named>>> g) { groups = g; Build(); Invalidate(); }

    void Build()
    {
        cells.Clear();
        int W = ClientSize.Width, cols = Math.Max(1, (W - 16) / Cw), x0 = 8 + (W - 16 - cols * Cw) / 2, y = Top0;
        string q = Search.Text.Trim();
        IEnumerable<KeyValuePair<string, List<Named>>> src = groups;
        if (q.Length > 0)
        {
            var seen = new HashSet<string>(); var res = new List<Named>();
            foreach (var g in groups) foreach (var n in g.Value) if (n.Key.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 && seen.Add(n.Key)) res.Add(n);
            res = res.OrderBy(n => n.Key.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(n => n.Key.Length).ThenBy(n => n.Key).ToList();
            src = new[] { new KeyValuePair<string, List<Named>>(res.Count > 0 ? "Results" : "No emotes found", res) };
        }
        hintR = Rectangle.Empty;
        if (Hint != null && q.Length == 0) { hintR = new Rectangle(10, y, W - 20, 22); y += 26; }
        foreach (var g in src)
        {
            if (g.Value.Count == 0 && g.Key != "No emotes found") continue;
            cells.Add(new Cell { R = new Rectangle(10, y, W - 20, Hh), Head = g.Key }); y += Hh;
            int i = 0;
            foreach (var n in g.Value) { cells.Add(new Cell { R = new Rectangle(x0 + (i % cols) * Cw, y + (i / cols) * Ch, Cw, Ch), Name = n.Key, E = n.Value }); i++; }
            y += (i + cols - 1) / cols * Ch + 6;
        }
        if (cells.Count == 0) { cells.Add(new Cell { R = new Rectangle(10, y, W - 20, Hh), Head = "Loading emotes..." }); y += Hh; }
        total = y + 6;
        top = Math.Max(0, Math.Min(top, total - ClientSize.Height));
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); Search.Width = ClientSize.Width - 20; Build(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var p = new Pen(Line)) g.DrawLine(p, 0, 0, Width, 0);
        g.SetClip(new Rectangle(0, Top0 - 4, Width, Height - Top0 + 4));
        g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.Half;
        if (!hintR.IsEmpty)
        {
            var r = hintR; r.Y -= top;
            Txt.In(g, Hint, FHint, hintHover ? ChatView.Fg : ChatView.LinkC, r, false);
        }
        for (int i = 0; i < cells.Count; i++)
        {
            var c = cells[i]; var r = c.R; r.Y -= top;
            if (r.Bottom < Top0 - 4 || r.Top > Height) continue;
            if (c.E == null) { Txt.In(g, c.Head, FHead, ChatView.Dim, r, false); continue; }
            if (i == hover) using (var b = new SolidBrush(HoverC)) g.FillRectangle(b, r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2);
            Images.Need(c.E);
            if (c.E.State != 2) continue;
            var f = c.E.Frames[0];
            float k = Math.Min(1f, Math.Min((Cw - 6f) / f.Width, (Ch - 6f) / f.Height));
            int w = (int)(f.Width * k), h = (int)(f.Height * k);
            g.DrawImage(f, r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h);
        }
    }

    int HitCell(Point p)
    {
        if (p.Y < Top0 - 4) return -1;
        var q = new Point(p.X, p.Y + top);
        for (int i = 0; i < cells.Count; i++) if (cells[i].E != null && cells[i].R.Contains(q)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = HitCell(e.Location);
        var hr = hintR; hr.Y -= top;
        bool hh = !hintR.IsEmpty && hr.Contains(e.Location) && e.Y >= Top0 - 4;
        Cursor = h >= 0 || hh ? Cursors.Hand : Cursors.Default;
        if (h != hover || hh != hintHover) { hover = h; hintHover = hh; Invalidate(); }
        string t = h >= 0 ? cells[h].Name : null;
        if (t == tipFor) return;
        tipFor = t;
        if (t == null) tip.Hide(this); else tip.Show(t, this, e.X + 12, e.Y + 18);
    }

    protected override void OnMouseLeave(EventArgs e) { hover = -1; hintHover = false; tipFor = null; tip.Hide(this); Invalidate(); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        int h = HitCell(e.Location);
        if (h >= 0) { if (Picked != null) Picked(cells[h].Name); return; }
        var hr = hintR; hr.Y -= top;
        if (!hintR.IsEmpty && hr.Contains(e.Location) && HintClicked != null) HintClicked();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        top = Math.Max(0, Math.Min(Math.Max(0, total - ClientSize.Height), top - e.Delta / 120 * Ch * 2));
        Invalidate();
    }

    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (Visible) { top = 0; Build(); } }
}
