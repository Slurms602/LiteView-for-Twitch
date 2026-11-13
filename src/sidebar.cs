// "Followed channels" for the watch view, like Twitch's left column: the channels you follow
// that are live (avatar, name, game, viewers), refreshed every minute; one click switches.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using LiteViewCore;

class FollowBar : Control
{
    public const int OpenW = 240, ClosedW = 50;
    const int HeadH = 42, RowH = 42;
    static readonly Color Bg = Color.FromArgb(31, 31, 35), Fg = Color.FromArgb(239, 239, 241), Dim = Color.FromArgb(173, 173, 184),
        HoverC = Color.FromArgb(38, 38, 44), Sel = Color.FromArgb(53, 53, 59), Red = Color.FromArgb(235, 4, 0), Accent = Color.FromArgb(145, 71, 255);
    static readonly Font FName = new Font("Segoe UI Semibold", 9.75f), FGame = new Font("Segoe UI", 9f), FHead = new Font("Segoe UI Semibold", 9f), FIcon = new Font("Segoe MDL2 Assets", 10f);
    const TextFormatFlags TF = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;

    class Live { public string Id, Login, Name, Game, Title; public long Viewers; }
    List<Live> live = new List<Live>();
    readonly Dictionary<string, Bitmap> avatars = new Dictionary<string, Bitmap>();
    readonly Timer refresh = new Timer { Interval = 60000 };
    readonly ToolTip tip = new ToolTip();
    int hover = -2, top; string tipFor; bool collapsed, loading; DateTime loaded = DateTime.MinValue;
    string message;
    public string Current;
    public event Action<string> Pick;
    public event Action LoginRequested, Toggled;

    public FollowBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Bg; Width = OpenW;
        refresh.Tick += (s, e) => { var f = FindForm(); if (f == null || f.WindowState != FormWindowState.Minimized) Reload(); };   // no polling while minimized
    }

    public bool Collapsed { get { return collapsed; } set { collapsed = value; Width = value ? ClosedW : OpenW; Invalidate(); } }

    // start/stop the minute refresh with the watch view
    public void Run(bool on)
    {
        refresh.Enabled = on;
        if (on && (DateTime.Now - loaded).TotalSeconds > 55) Reload();
    }

    public async void Reload()
    {
        if (loading) return;
        if (Tw.Token == null) { live.Clear(); message = "Log in with Twitch to see the channels you follow."; Invalidate(); return; }
        loading = true;
        try
        {
            if (Tw.UserId == null && !await Tw.Validate()) { live.Clear(); message = "Log in with Twitch to see the channels you follow."; return; }
            var list = new List<Live>(); string cursor = null;
            for (int page = 0; page < 5; page++)
            {
                var j = await Tw.Helix("streams/followed?user_id=" + Tw.UserId + "&first=100" + (cursor != null ? "&after=" + cursor : ""));
                if (j == null) { if (page == 0) { message = "Could not reach Twitch."; return; } break; }
                foreach (var s in Tw.A(j, "data"))
                    list.Add(new Live { Id = Tw.S(s, "user_id"), Login = Tw.S(s, "user_login"), Name = Tw.S(s, "user_name"), Game = Tw.S(s, "game_name"), Title = Tw.S(s, "title"), Viewers = Tw.N(s, "viewer_count") });
                cursor = Tw.S(Tw.V(j, "pagination"), "cursor");
                if (cursor == null) break;
            }
            live = list.OrderByDescending(x => x.Viewers).ToList();
            message = live.Count == 0 ? "None of the channels you follow are live." : null;
            loaded = DateTime.Now;
            Invalidate();
            await LoadAvatars();
        }
        finally { loading = false; Invalidate(); }
    }

    async Task LoadAvatars()
    {
        var need = live.Where(x => ChannelName.IsNumericId(x.Id) && !avatars.ContainsKey(x.Id)).Select(x => x.Id).ToList();
        for (int i = 0; i < need.Count; i += 100)
        {
            var j = await Tw.Helix("users?id=" + string.Join("&id=", need.Skip(i).Take(100)));
            var urls = Tw.A(j, "data").Select(u => new KeyValuePair<string, string>(Tw.S(u, "id"), Tw.S(u, "profile_image_url"))).Where(kv => kv.Value != null).ToList();
            await Task.WhenAll(urls.Select(async kv =>
            {
                try
                {
                    string url = kv.Value.Replace("300x300", "70x70");
                    if (!SafeUrl.IsAllowedImage(url)) return;
                    var bytes = await Tw.GetBytes(url, 1024 * 1024);
                    if (bytes == null || !ImageSniff.Acceptable(bytes)) return;   // PNG/GIF/JPEG of a sane size only
                    var bmp = await Task.Run(() => Round(bytes));
                    if (IsDisposed) return;
                    Bitmap old; if (avatars.TryGetValue(kv.Key, out old)) old.Dispose();
                    avatars[kv.Key] = bmp; Invalidate();
                }
                catch { }
            }));
        }
    }

    // a 30 px round avatar, drawn once
    static Bitmap Round(byte[] bytes)
    {
        using (var ms = new System.IO.MemoryStream(bytes))
        using (var img = Image.FromStream(ms))
        {
            var b = new Bitmap(30, 30, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(b))
            using (var path = new GraphicsPath())
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                path.AddEllipse(0, 0, 30, 30);
                using (var tb = new TextureBrush(img, WrapMode.Clamp))
                {
                    tb.ScaleTransform(30f / img.Width, 30f / img.Height);
                    g.FillPath(tb, path);
                }
            }
            return b;
        }
    }

    static string Count(long v)
    {
        if (v < 1000) return v.ToString();
        if (v < 100000) return (v / 100 / 10.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "K";
        if (v < 1000000) return (v / 1000) + "K";
        return (v / 100000 / 10.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "M";
    }

    Rectangle ToggleRect { get { return collapsed ? new Rectangle((Width - 30) / 2, 6, 30, 30) : new Rectangle(Width - 38, 6, 30, 30); } }
    Rectangle LoginRect { get { return new Rectangle(10, HeadH + 64, Width - 20, 32); } }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        if (!collapsed) Txt.At(g, "FOLLOWED CHANNELS", FHead, Fg, 12, 13);
        var tr = ToggleRect;
        if (hover == -1) using (var b = new SolidBrush(HoverC)) g.FillRectangle(b, tr);
        TextRenderer.DrawText(g, collapsed ? "" : "", FIcon, tr, Fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        g.SetClip(new Rectangle(0, HeadH, Width, Height - HeadH));
        if (live.Count == 0)
        {
            if (!collapsed && message != null)
            {
                Txt.Wrap(g, message, FGame, Dim, new Rectangle(12, HeadH + 6, Width - 24, 54));
                if (Tw.Token == null)
                {
                    var lr = LoginRect;
                    using (var b = new SolidBrush(hover == -3 ? Color.FromArgb(119, 44, 232) : Accent)) g.FillRectangle(b, lr);
                    Txt.In(g, "Log in with Twitch", FName, Color.White, lr, true);
                }
            }
            return;
        }
        g.SmoothingMode = SmoothingMode.AntiAlias;
        for (int i = 0; i < live.Count; i++)
        {
            int y = HeadH + i * RowH - top;
            if (y + RowH < HeadH) continue; if (y > Height) break;
            var s = live[i];
            if (s.Login == Current || i == hover) using (var b = new SolidBrush(s.Login == Current ? Sel : HoverC)) g.FillRectangle(b, 0, y, Width, RowH);
            Bitmap av; int ax = collapsed ? (Width - 30) / 2 : 10;
            if (s.Id != null && avatars.TryGetValue(s.Id, out av)) g.DrawImageUnscaled(av, ax, y + 6);
            else using (var b = new SolidBrush(Sel)) g.FillEllipse(b, ax, y + 6, 30, 30);
            if (collapsed) continue;
            string count = Count(s.Viewers);
            int cw = Txt.Width(count, FGame);
            Txt.In(g, s.Name ?? s.Login, FName, Fg, new Rectangle(50, y + 3, Width - 50 - cw - 26, 19), false);
            Txt.In(g, s.Game ?? "", FGame, Dim, new Rectangle(50, y + 21, Width - 60, 17), false);
            using (var b = new SolidBrush(Red)) g.FillEllipse(b, Width - cw - 22, y + 10, 8, 8);
            Txt.At(g, count, FGame, Dim, Width - cw - 10, y + 6);
        }
    }

    int RowAt(Point p)
    {
        if (ToggleRect.Contains(p)) return -1;
        if (live.Count == 0) return Tw.Token == null && !collapsed && LoginRect.Contains(p) ? -3 : -2;
        if (p.Y < HeadH) return -2;
        int i = (p.Y - HeadH + top) / RowH;
        return i >= 0 && i < live.Count ? i : -2;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = RowAt(e.Location);
        Cursor = h != -2 ? Cursors.Hand : Cursors.Default;
        if (h != hover) { hover = h; Invalidate(); }
        string t = h >= 0 ? (collapsed ? (live[h].Name + "  •  " + Count(live[h].Viewers) + " viewers\n" + live[h].Game + "\n") : "") + live[h].Title : null;
        if (t == tipFor) return;
        tipFor = t;
        if (t == null) tip.Hide(this); else tip.Show(t, this, e.X + 14, e.Y + 18);
    }

    protected override void OnMouseLeave(EventArgs e) { hover = -2; tipFor = null; tip.Hide(this); Invalidate(); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        int h = RowAt(e.Location);
        if (h == -1) { Collapsed = !collapsed; if (Toggled != null) Toggled(); }
        else if (h == -3) { if (LoginRequested != null) LoginRequested(); }
        else if (h >= 0 && Pick != null) Pick(live[h].Login);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        top = Math.Max(0, Math.Min(Math.Max(0, live.Count * RowH - (Height - HeadH)), top - e.Delta / 120 * RowH));
        Invalidate();
    }
}
