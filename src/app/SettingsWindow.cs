// The Settings window (menu > Settings...): every setting of the menus in one place, grouped on the left. It is built
// from the very menu items the menus use (LiteView.SettingsPages), so a setting works and is saved the same way
// whichever is used, and nothing can drift apart: a click performs the item's own click, then the page is built again
// from fresh items (which show the new state). Kinds of rows: a switch (items made as toggles, Tag "toggle"), a choice
// (an item among checkable siblings, drawn as a radio button), a button (any other item), a heading (a submenu) and a
// note (a disabled item tagged "label"). A menu item's tooltip shows under its row.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using LiteViewCore;

sealed class SettingsWindow : Form
{
    readonly Func<List<KeyValuePair<string, List<ToolStripItem>>>> pages;
    readonly CategoryList list = new CategoryList();
    readonly Panel scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
    readonly SettingsPage page = new SettingsPage();
    List<KeyValuePair<string, List<ToolStripItem>>> current = new List<KeyValuePair<string, List<ToolStripItem>>>();
    string shown;

    public SettingsWindow(Func<List<KeyValuePair<string, List<ToolStripItem>>>> pages, string first)
    {
        this.pages = pages;
        Text = "Settings  -  LiteView"; StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(780, 560); MinimumSize = new Size(560, 380);
        BackColor = Theme.Panel; ForeColor = Theme.Text; KeyPreview = true; ShowInTaskbar = false;
        Font = UiFont.ForControls(FontKind.Text, 9.5f);
        list.Dock = DockStyle.Left; list.Width = 200;
        list.Picked += name => Show(name, true);
        scroller.BackColor = Theme.Panel;
        page.Width = 560; page.Clicked += Clicked;
        scroller.Controls.Add(page);
        scroller.Resize += (s, e) => { page.Width = Math.Max(300, scroller.ClientSize.Width); page.Relayout(); };
        Controls.Add(scroller); Controls.Add(list);
        KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        Reload();
        Show(first ?? (current.Count > 0 ? current[0].Key : null), true);
        HandleCreated += (s, e) => Theme.Frame(Handle);
        Activated += (s, e) => { if (shown != null) { Reload(); Show(shown, false); } };   // a setting changed in a menu meanwhile: the page shows it
    }

    void Reload()
    {
        current = pages();   // fresh items: they show the settings as they are now
        list.Set(current.Select(p => p.Key).ToList());
    }

    void Show(string name, bool top)
    {
        var p = current.FirstOrDefault(x => x.Key == name);
        if (p.Key == null) { if (current.Count == 0) return; p = current[0]; }
        shown = p.Key; list.Selected = p.Key;
        int y = scroller.VerticalScroll.Value;
        page.Set(p.Key, p.Value);
        page.Width = Math.Max(300, scroller.ClientSize.Width); page.Relayout();
        if (top) scroller.AutoScrollPosition = Point.Empty;
        else scroller.AutoScrollPosition = new Point(0, y);
    }

    void Clicked(ToolStripMenuItem item)
    {
        if (!item.Enabled) return;
        item.PerformClick();   // the menu item's own action (it saves the setting)
        if (IsDisposed) return;
        Reload();
        Show(shown, false);
        Log.Info("settings: " + shown + " > " + item.Text.Trim());
    }

    /// <summary>UI fixture (CI): which page is shown.</summary>
    public void FixtureShow(string name) { Show(name, true); }

    // ---------- the categories on the left ----------

    sealed class CategoryList : Control
    {
        List<string> names = new List<string>();
        string selected; int hover = -1;
        public event Action<string> Picked;
        const int RowH = 36;

        public CategoryList()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Side; Cursor = Cursors.Hand;
        }

        public void Set(List<string> n) { names = n; Invalidate(); }
        public string Selected { get { return selected; } set { selected = value; Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            for (int i = 0; i < names.Count; i++)
            {
                var r = new Rectangle(8, 12 + i * RowH, Width - 16, RowH - 4);
                bool sel = names[i] == selected;
                if (sel || i == hover) using (var b = new SolidBrush(sel ? Theme.Line : Theme.Hover)) g.FillRectangle(b, r);
                if (sel) using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, r.X, r.Y + 6, 3, r.Height - 12);
                Txt.In(g, names[i], UiFont.Get(sel ? FontKind.Semi : FontKind.Text, 10f), sel ? Theme.Text : Theme.Dim, new Rectangle(r.X + 14, r.Y, r.Width - 18, r.Height), false);
            }
        }

        int At(Point p) { int i = (p.Y - 12) / RowH; return p.Y >= 12 && i >= 0 && i < names.Count ? i : -1; }
        protected override void OnMouseMove(MouseEventArgs e) { int h = At(e.Location); if (h != hover) { hover = h; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); }
        protected override void OnMouseClick(MouseEventArgs e) { int i = At(e.Location); if (i >= 0 && Picked != null) Picked(names[i]); }
    }

    // ---------- one page of settings ----------

    sealed class SettingsPage : Control
    {
        enum Kind { Heading, Switch, Choice, Button, Note, Gap }
        sealed class Row { public Kind K; public string Text, Tip; public bool On, Enabled; public ToolStripMenuItem Item; public int Depth; public Rectangle R; }
        readonly List<Row> rows = new List<Row>();
        string title = "";
        int hover = -1;
        public event Action<ToolStripMenuItem> Clicked;

        public SettingsPage()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Panel;
        }

        static Font FTitle { get { return UiFont.Get(FontKind.Bold, 15f); } }
        static Font FHead { get { return UiFont.Get(FontKind.Semi, 10.5f); } }
        static Font FText { get { return UiFont.Get(FontKind.Text, 10f); } }
        static Font FTip { get { return UiFont.Get(FontKind.Text, 8.75f); } }

        public void Set(string name, List<ToolStripItem> items)
        {
            title = name; rows.Clear(); hover = -1;
            Add(items, 0);
            while (rows.Count > 0 && rows[rows.Count - 1].K == Kind.Gap) rows.RemoveAt(rows.Count - 1);
        }

        void Add(IEnumerable<ToolStripItem> items, int depth)
        {
            var all = items.ToList();
            // a group = items between separators: an item is a choice when it or a sibling in its group is checked
            int start = 0;
            for (int i = 0; i <= all.Count; i++)
            {
                if (i < all.Count && !(all[i] is ToolStripSeparator)) continue;
                var group = all.GetRange(start, i - start).OfType<ToolStripMenuItem>().ToList();
                bool choices = group.Any(x => x.DropDownItems.Count == 0 && x.Checked && !IsToggle(x));
                foreach (var it in group)
                {
                    string tip = string.IsNullOrEmpty(it.ToolTipText) ? null : it.ToolTipText;
                    string text = it.Text.Trim().Replace("&&", "&");
                    if (it.DropDownItems.Count > 0)
                    {
                        rows.Add(new Row { K = Kind.Heading, Text = text, Tip = tip, Depth = depth, Enabled = true });
                        Add(it.DropDownItems.Cast<ToolStripItem>(), depth + 1);
                        rows.Add(new Row { K = Kind.Gap });
                    }
                    else if ("label".Equals(it.Tag)) rows.Add(new Row { K = Kind.Note, Text = text, Tip = tip, Depth = depth });
                    else if (IsToggle(it)) rows.Add(new Row { K = Kind.Switch, Text = text, Tip = tip, On = it.Checked, Enabled = it.Enabled, Item = it, Depth = depth });
                    else if (choices) rows.Add(new Row { K = Kind.Choice, Text = text, Tip = tip, On = it.Checked, Enabled = it.Enabled, Item = it, Depth = depth });
                    else rows.Add(new Row { K = Kind.Button, Text = text, Tip = tip, Enabled = it.Enabled, Item = it, Depth = depth });
                }
                if (i < all.Count) rows.Add(new Row { K = Kind.Gap });
                start = i + 1;
            }
        }

        static bool IsToggle(ToolStripMenuItem it) { return "toggle".Equals(it.Tag); }

        public void Relayout()
        {
            int y = 64, w = Width - 48;
            foreach (var r in rows)
            {
                int x = 24 + r.Depth * 18, rw = Math.Max(100, w - r.Depth * 18);
                int th = Txt.Height(FText), tipH = r.Tip != null ? TipHeight(r.Tip, rw - 52) : 0;
                switch (r.K)
                {
                    case Kind.Gap: r.R = new Rectangle(x, y, rw, 10); break;
                    case Kind.Heading: r.R = new Rectangle(x, y + 6, rw, Txt.Height(FHead) + 8 + tipH); break;
                    case Kind.Note: r.R = new Rectangle(x, y, rw, th + 6 + tipH); break;
                    default: r.R = new Rectangle(x, y, rw, Math.Max(34, th + 14) + tipH); break;
                }
                y = r.R.Bottom + 2;
            }
            Height = Math.Max(y + 24, Parent != null ? Parent.ClientSize.Height : 0);
            Invalidate();
        }

        int TipHeight(string tip, int width)
        {
            int lines = 0;
            foreach (var para in tip.Split('\n'))
            {
                int line = 0;
                foreach (var word in para.Split(' '))
                {
                    int ww = Txt.Width(word + " ", FTip);
                    if (line > 0 && line + ww > width) { lines++; line = 0; }
                    line += ww;
                }
                lines++;
            }
            return lines * (Txt.Height(FTip) + 1) + 2;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Txt.At(g, title, FTitle, Theme.Text, 24, 18);
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i]; var b = r.R;
                if (!b.IntersectsWith(e.ClipRectangle) || r.K == Kind.Gap) continue;
                Color fg = r.Enabled ? Theme.Text : Theme.Faint;
                int th = Txt.Height(FText);
                switch (r.K)
                {
                    case Kind.Heading:
                        Txt.At(g, r.Text, FHead, Theme.Text, b.X, b.Y);
                        if (r.Tip != null) Tip(g, r.Tip, b.X, b.Y + Txt.Height(FHead) + 4, b.Width);
                        break;
                    case Kind.Note:
                        Txt.At(g, r.Text, FText, Theme.Dim, b.X, b.Y + 2);
                        if (r.Tip != null) Tip(g, r.Tip, b.X, b.Y + th + 4, b.Width);
                        break;
                    default:
                    {
                        if (i == hover && r.Enabled) using (var br = new SolidBrush(Theme.Hover)) g.FillRectangle(br, b.X - 8, b.Y, b.Width + 16, b.Height);
                        int cy = b.Y + Math.Max(34, th + 14) / 2;
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        if (r.K == Kind.Switch)
                        {   // a switch: purple and to the right when on
                            var track = new Rectangle(b.X, cy - 9, 36, 18);
                            using (var path = Round(track, 9)) using (var br = new SolidBrush(!r.Enabled ? Theme.Line : r.On ? Theme.Accent : Theme.Line)) g.FillPath(br, path);
                            using (var br = new SolidBrush(r.On ? Color.White : Theme.Dim)) g.FillEllipse(br, r.On ? track.Right - 16 : track.X + 2, cy - 7, 14, 14);
                        }
                        else if (r.K == Kind.Choice)
                        {
                            using (var p = new Pen(r.On ? Theme.Accent : Theme.Dim, 2)) g.DrawEllipse(p, b.X + 10, cy - 8, 16, 16);
                            if (r.On) using (var br = new SolidBrush(Theme.Accent)) g.FillEllipse(br, b.X + 14, cy - 4, 8, 8);
                        }
                        else
                        {   // a button: an arrow, then its text
                            Txt.In(g, "›", FHead, r.Enabled ? Theme.AccentText : Theme.Faint, new Rectangle(b.X + 8, cy - 12, 20, 24), true);
                        }
                        g.SmoothingMode = SmoothingMode.Default;
                        Txt.In(g, r.Text, FText, r.K == Kind.Button && r.Enabled ? Theme.AccentText : fg, new Rectangle(b.X + 52, cy - th / 2 - 2, b.Width - 56, th + 4), false);
                        if (r.Tip != null) Tip(g, r.Tip, b.X + 52, cy + th / 2 + 4, b.Width - 52);
                        break;
                    }
                }
            }
        }

        void Tip(Graphics g, string tip, int x, int y, int width)
        {
            int lh = Txt.Height(FTip) + 1;
            foreach (var para in tip.Split('\n'))
            {
                var line = new System.Text.StringBuilder();
                int lw = 0;
                foreach (var word in para.Split(' '))
                {
                    int ww = Txt.Width(word + " ", FTip);
                    if (lw > 0 && lw + ww > width) { Txt.At(g, line.ToString(), FTip, Theme.Dim, x, y); y += lh; line.Clear(); lw = 0; }
                    line.Append(word).Append(' '); lw += ww;
                }
                Txt.At(g, line.ToString(), FTip, Theme.Dim, x, y); y += lh;
            }
        }

        static GraphicsPath Round(Rectangle r, int radius)
        {
            var p = new GraphicsPath(); int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        int At(Point p) { for (int i = 0; i < rows.Count; i++) if (rows[i].Item != null && rows[i].R.Contains(p)) return i; return -1; }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = At(e.Location);
            Cursor = h >= 0 && rows[h].Enabled ? Cursors.Hand : Cursors.Default;
            if (h != hover) { hover = h; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); }
        protected override void OnMouseWheel(MouseEventArgs e)
        {   // the page is taller than its scrolling panel: the wheel scrolls the panel
            var p = Parent as ScrollableControl;
            if (p != null) p.AutoScrollPosition = new Point(0, Math.Max(0, -p.AutoScrollPosition.Y - e.Delta / 120 * 48));
            base.OnMouseWheel(e);
        }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            int i = At(e.Location);
            if (i >= 0 && rows[i].Enabled && Clicked != null) Clicked(rows[i].Item);
        }
    }
}
