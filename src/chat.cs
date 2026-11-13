// Native Twitch chat for LiteView: Twitch IRC over WebSocket, Twitch + 7TV + BetterTTV + FFZ
// emotes, badges, name colours, replies and sending - drawn by one light control (no browser).
// Messages are drawn once into small bitmaps; only animated emotes on screen keep moving.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Named = System.Collections.Generic.KeyValuePair<string, Emote>;
using LiteViewCore;

// Text drawn with GDI+ grayscale anti-aliasing (AntiAliasGridFit): smooth like the Twitch website whatever the
// Windows font-smoothing setting is (GDI's own "standard" smoothing looks heavy and jagged at chat sizes).
static class Txt
{
    static readonly Bitmap mb = new Bitmap(1, 1);
    static readonly Graphics mg = Prep(Graphics.FromImage(mb));
    public static readonly StringFormat Typo = (StringFormat)StringFormat.GenericTypographic.Clone();
    static readonly StringFormat clip = Fmt(StringAlignment.Near), center = Fmt(StringAlignment.Center), wrap = new StringFormat(StringFormat.GenericTypographic) { Trimming = StringTrimming.Word };
    static readonly Dictionary<int, SolidBrush> brushes = new Dictionary<int, SolidBrush>();
    static StringFormat Fmt(StringAlignment a)
    {
        var f = (StringFormat)StringFormat.GenericTypographic.Clone();
        f.FormatFlags |= StringFormatFlags.NoWrap; f.Trimming = StringTrimming.EllipsisCharacter; f.Alignment = a; f.LineAlignment = StringAlignment.Center;
        return f;
    }
    public static Graphics Prep(Graphics g) { g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit; return g; }
    public static SolidBrush Brush(Color c)
    {
        SolidBrush b; int k = c.ToArgb();
        if (!brushes.TryGetValue(k, out b)) { b = new SolidBrush(c); brushes[k] = b; }
        return b;
    }
    public static int Width(string s, Font f) { lock (mg) return (int)Math.Round(mg.MeasureString(s, f, PointF.Empty, Typo).Width); }
    public static int Height(Font f) { lock (mg) return (int)Math.Ceiling(f.GetHeight(mg)); }
    public static void At(Graphics g, string s, Font f, Color c, int x, int y) { Prep(g).DrawString(s, f, Brush(c), x, y, Typo); }
    // one line in a box, vertically centred, "..." when it does not fit
    public static void In(Graphics g, string s, Font f, Color c, Rectangle r, bool centred) { Prep(g).DrawString(s, f, Brush(c), r, centred ? center : clip); }
    public static void Wrap(Graphics g, string s, Font f, Color c, Rectangle r) { Prep(g).DrawString(s, f, Brush(c), r, wrap); }
}

// One emote or badge image
// (all frames decoded once for animated GIFs, drawn at 1x size)
class Emote
{
    public string Name, Url; public int W = 28, H = 28, FixedH; public bool Zero, SizeKnown;
    public Bitmap[] Frames; public int[] Ends; public int Total;
    public volatile int State;   // 0 new, 1 loading, 2 ready, 3 failed
    public long LastUse;          // Images.Clock ms when last drawn (the decoded frames' memory budget)
    public long Bytes;            // decoded size of Frames
    public bool Animated { get { return Frames != null && Frames.Length > 1; } }
    public int Index(long ms)
    {
        if (Frames == null || Frames.Length < 2) return 0;
        long t = ms % Total;
        for (int i = 0; i < Ends.Length; i++) if (t < Ends[i]) return i;
        return 0;
    }
}

static class Images
{
    static readonly Dictionary<string, Emote> byUrl = new Dictionary<string, Emote>();
    static readonly SemaphoreSlim gate = new SemaphoreSlim(EmoteLimits.MaxConcurrentDownloads);
    public static event Action<Emote> Loaded;   // raised on a worker thread
    // Decoded frames are kept within a memory budget (upstream kept every emote ever seen, forever).
    // The budget is only touched on the UI thread, which is also the only thread that draws frames.
    public static long BudgetBytes = 128L * 1024 * 1024;
    static readonly ByteBudget<Emote> budget = new ByteBudget<Emote>(BudgetBytes);
    static readonly Stopwatch clock = Stopwatch.StartNew();
    static int evicted, refused;
    public static long Clock { get { return clock.ElapsedMilliseconds; } }

    /// <summary>Marks an emote as drawn now (UI thread).</summary>
    public static void Used(Emote e) { e.LastUse = clock.ElapsedMilliseconds; if (e.State == 2) budget.Touch(e, e.Bytes); }

    /// <summary>Frees the least recently drawn emotes while over budget; nothing drawn in the last 10 s is freed (UI thread).</summary>
    public static void Trim()
    {
        budget.Budget = BudgetBytes;
        if (budget.Total <= budget.Budget) return;
        long now = clock.ElapsedMilliseconds;
        foreach (var e in budget.Evict(x => now - x.LastUse < 10000 || x.State != 2))
        {
            var frames = e.Frames;
            e.State = 0; e.Frames = null; e.Ends = null;   // loads again if it is needed later
            if (frames != null) foreach (var b in frames) b.Dispose();
            evicted++;
        }
    }

    public static string Stats()
    {
        int n; lock (byUrl) n = byUrl.Count;
        return n + " known, " + budget.Count + " decoded, " + (budget.Total / (1024 * 1024)) + " of " + (BudgetBytes / (1024 * 1024)) + " MB, " + evicted + " freed, " + refused + " refused";
    }

    public static Emote Get(string url, string name, int w, int h, int fixedH)
    {
        lock (byUrl)
        {
            Emote e;
            if (byUrl.TryGetValue(url, out e)) return e;
            if (byUrl.Count >= 50000)   // the records themselves are small, but not unlimited: forget idle ones
                foreach (var k in byUrl.Where(kv => kv.Value.State == 0 || kv.Value.State == 3).Select(kv => kv.Key).ToList()) byUrl.Remove(k);
            e = new Emote { Url = url, Name = name, FixedH = fixedH };
            if (fixedH > 0) { e.W = e.H = fixedH; e.SizeKnown = true; }
            else if (w > 0 && h > 0) { Fit(e, w, h); e.SizeKnown = true; }
            if (!SafeUrl.IsAllowedImage(url)) e.State = 3;   // not from Twitch / 7TV / BTTV / FFZ: never downloaded
            byUrl[url] = e;
            return e;
        }
    }

    static void Fit(Emote e, int w, int h)
    {
        int ow, oh; EmoteLimits.Fit(w, h, e.FixedH, out ow, out oh);   // upstream rule, plus a width cap
        e.W = ow; e.H = oh;
    }

    public static void Need(Emote e)
    {
        if (e.State != 0) return;
        e.State = 1;
        Task.Run(async () =>
        {
            await gate.WaitAsync();
            try
            {
                var bytes = await Tw.GetBytes(e.Url, EmoteLimits.MaxDownloadBytes);
                if (bytes == null || !ImageSniff.Acceptable(bytes)) { Interlocked.Increment(ref refused); e.State = 3; }
                else { Decode(e, bytes); e.State = 2; }
            }
            catch { e.State = 3; }
            finally { gate.Release(); }
            var h = Loaded; if (h != null) h(e);
        });
    }

    static void Decode(Emote e, byte[] b)
    {
        using (var ms = new MemoryStream(b))
        using (var img = Image.FromStream(ms))
        {
            if (!EmoteLimits.SourceOk(img.Width, img.Height)) throw new InvalidDataException("image too large");
            Fit(e, img.Width, img.Height);
            int n = 1; FrameDimension dim = null;
            try { dim = new FrameDimension(img.FrameDimensionsList[0]); n = Math.Max(1, img.GetFrameCount(dim)); } catch { }
            // keep at most EmoteLimits.FramesToKeep frames (by count and decoded bytes), spread evenly, same total duration
            int[] plan = EmoteLimits.FramePlan(n, EmoteLimits.FramesToKeep(n, e.W, e.H));
            var src = new int[n];
            if (n > 1) try { var p = img.GetPropertyItem(0x5100); for (int i = 0; i < n && i * 4 + 3 < p.Value.Length; i++) src[i] = BitConverter.ToInt32(p.Value, i * 4) * 10; } catch { }
            int[] delays = EmoteLimits.MergeDelays(src, plan);
            var frames = new Bitmap[plan.Length]; var ends = new int[plan.Length]; long t = 0;
            try
            {
                for (int k = 0; k < plan.Length; k++)
                {
                    if (n > 1) img.SelectActiveFrame(dim, plan[k]);
                    var f = new Bitmap(e.W, e.H, PixelFormat.Format32bppPArgb);
                    frames[k] = f;
                    using (var g = Graphics.FromImage(f))
                    {
                        g.InterpolationMode = img.Height == e.H ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        g.DrawImage(img, new Rectangle(0, 0, e.W, e.H));
                    }
                    t += delays[k]; ends[k] = (int)Math.Min(int.MaxValue, t);
                }
            }
            catch { foreach (var f in frames) if (f != null) f.Dispose(); throw; }
            e.Ends = ends; e.Total = (int)Math.Max(1, Math.Min(int.MaxValue, t)); e.Bytes = (long)frames.Length * e.W * e.H * 4; e.Frames = frames;
        }
    }
}

// Irc (the chat connection) moved to src/core/IrcClient.cs so it is tested against a fake server.
// IrcMsg (one parsed IRC line) moved to src/core/IrcMsg.cs, unchanged in use, so it is unit tested.

// A piece of a chat line: 0 word, 1 emote, 2 badge, 3 name, 4 link, 5 @mention, 6 dim word
class Piece { public byte Kind; public string Text; public Emote E; public Color C; }
class Place { public Rectangle R; public Piece P; public int Last = -1; }

class Msg
{
    public string Id, Login, Display, Reply; public Color NameColor;
    public List<Piece> P = new List<Piece>();
    public byte Style;   // 0 normal, 1 mentions me, 2 sub/raid notice, 3 dim system line
    public bool Deleted;
    public List<Piece> Shown;   // what is drawn (P, or "message deleted")
    public int LW = -1, H, Seen; public List<Place> Places; public Bitmap Cache; public bool Dynamic;
}

// The message list: lays out and draws chat lines like Twitch (Segoe UI 13 px, 20 px lines).
class ChatView : Control
{
    public static readonly Color Bg = Color.FromArgb(24, 24, 27), Fg = Color.FromArgb(239, 239, 241), Dim = Color.FromArgb(173, 173, 184),
        MentionBg = Color.FromArgb(62, 22, 26), NoticeBg = Color.FromArgb(31, 31, 35), Accent = Color.FromArgb(145, 71, 255), LinkC = Color.FromArgb(191, 148, 255);
    static Font F = new Font("Segoe UI", 9.75f), FB = new Font("Segoe UI Semibold", 9.75f), FS = new Font("Segoe UI", 8.75f);   // fork: SetTextSize
    public readonly List<Msg> Msgs = new List<Msg>();
    public event Action<string> NameClicked;
    int scroll, gen; readonly List<Place> anims = new List<Place>(); readonly List<KeyValuePair<Place, int>> animY = new List<KeyValuePair<Place, int>>();
    readonly System.Windows.Forms.Timer animTick = new System.Windows.Forms.Timer { Interval = 33 };
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly Dictionary<string, int> widths = new Dictionary<string, int>();
    readonly ToolTip tip = new ToolTip(); string tipText;
    Rectangle pausedBox;
    int spaceW, textH;

    public ChatView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Bg;
        animTick.Tick += (s, e) => Animate();
        spaceW = Txt.Width("x x", F) - Txt.Width("xx", F);
        textH = Txt.Height(F);
    }

    public void ToBottom() { if (scroll > 0) { scroll = 0; Invalidate(); } }

    // fork (chat text size, src/app/LiteView.Chat.cs): new fonts, then every message is laid out again
    public void SetTextSize(float pt)
    {
        if (Math.Abs(F.Size - pt) < 0.01f) return;
        F = new Font("Segoe UI", pt); FB = new Font("Segoe UI Semibold", pt); FS = new Font("Segoe UI", pt - 1f);
        widths.Clear();
        spaceW = Txt.Width("x x", F) - Txt.Width("xx", F);
        textH = Txt.Height(F);
        Redo(m => true);
    }

    public void Clear() { foreach (var m in Msgs) Drop(m); Msgs.Clear(); scroll = 0; Invalidate(); }

    public void Add(List<Msg> list)
    {
        if (list.Count == 0) return;
        if (scroll > 0) foreach (var m in list) { Lay(m); scroll += m.H; }
        Msgs.AddRange(list);
        int max = scroll > 0 ? 2000 : 300;
        if (Msgs.Count > max) { for (int i = 0; i < Msgs.Count - max; i++) Drop(Msgs[i]); Msgs.RemoveRange(0, Msgs.Count - max); }
        Invalidate();
    }

    public void Redo(Func<Msg, bool> which) { foreach (var m in Msgs) if (which(m)) { m.LW = -1; Drop(m); } Invalidate(); }

    // an image arrived: redraw the lines on screen that use it
    public void OnImage(Emote e)
    {
        bool hit = false;
        foreach (var m in Msgs)
            if (m.Places != null && m.Shown.Any(p => p.E == e)) { m.LW = -1; Drop(m); hit = true; }
        e.SizeKnown = true;
        if (hit) Invalidate();
    }

    static void Drop(Msg m) { if (m.Cache != null) { m.Cache.Dispose(); m.Cache = null; } }

    int Measure(string w, Font f)
    {
        string k = (f == FB ? "\u0001" : f == FS ? "\u0002" : "") + w;
        int v;
        if (widths.TryGetValue(k, out v)) return v;
        if (widths.Count > 20000) widths.Clear();
        v = Txt.Width(w, f);
        widths[k] = v; return v;
    }

    Font FontOf(Piece p) { return p.Kind == 3 || p.Kind == 5 ? FB : F; }

    void Lay(Msg m)
    {
        int W = Math.Max(120, ClientSize.Width);
        if (m.LW == W) return;
        m.LW = W; m.Places = new List<Place>(); m.Dynamic = false;
        m.Shown = m.Deleted ? m.P.Where(p => p.Kind == 2 || p.Kind == 3).Concat(new[] { new Piece { Kind = 6, Text = "<message deleted>", C = Dim } }).ToList() : m.P;
        int x0 = m.Style == 2 ? 14 : 10, right = W - 10, y = 5;
        if (m.Reply != null) { m.Places.Add(new Place { R = new Rectangle(x0, y, right - x0, 16), P = new Piece { Kind = 7, Text = m.Reply, C = Dim } }); y += 18; }
        var lines = new List<List<Place>>(); var line = new List<Place>(); int x = x0;
        Place prevEmote = null;
        foreach (var p0 in m.Shown)
        {
            // an emote whose image could not load is shown as its name
            var p = p0.Kind == 1 && p0.E.State == 3 ? new Piece { Kind = 0, Text = p0.E.Name ?? "?", C = Fg } : p0;
            int w, h;
            if (p.Kind == 1 || p.Kind == 2)
            {
                Images.Need(p.E); w = p.E.W; h = p.E.H;
                if (p.E.State == 2 && p.E.Animated) m.Dynamic = true;
                if (p.Kind == 1 && p.E.Zero && prevEmote != null)
                {   // 7TV zero-width emote: drawn over the emote before it
                    var r = prevEmote.R; m.Dynamic = true;
                    line.Add(new Place { R = new Rectangle(r.X + (r.Width - w) / 2, 0, w, h), P = p }); continue;
                }
            }
            else { w = Measure(p.Text, FontOf(p)); h = textH; }
            if (x + w > right && x > x0) { lines.Add(line); line = new List<Place>(); x = x0; }
            if (w > right - x0 && p.Kind != 1 && p.Kind != 2)
            {   // a very long word: break it over lines
                string rest = p.Text;
                while (rest.Length > 0)
                {
                    int n = rest.Length;
                    while (n > 1 && Measure(rest.Substring(0, n), F) > right - x) n = Math.Max(1, n * 3 / 4);
                    var q = new Piece { Kind = p.Kind, Text = rest.Substring(0, n), C = p.C };
                    line.Add(new Place { R = new Rectangle(x, 0, Measure(q.Text, F), h), P = q });
                    rest = rest.Substring(n);
                    if (rest.Length > 0) { lines.Add(line); line = new List<Place>(); x = x0; }
                    else x += Measure(q.Text, F) + spaceW;
                }
                prevEmote = null; continue;
            }
            var pl = new Place { R = new Rectangle(x, 0, w, h), P = p };
            line.Add(pl);
            prevEmote = p.Kind == 1 ? pl : null;
            x += w + (p.Kind == 2 ? 3 : p.Kind == 3 && p.Text.EndsWith(":") ? spaceW : p.Kind == 3 ? spaceW : spaceW);
        }
        if (line.Count > 0) lines.Add(line);
        foreach (var l in lines)
        {
            int lh = 20; foreach (var pl in l) lh = Math.Max(lh, pl.R.Height + 2);
            foreach (var pl in l) { var r = pl.R; r.Y = y + (lh - r.Height) / 2; pl.R = r; m.Places.Add(pl); }
            y += lh;
        }
        m.H = y + 5;
    }

    Color BgOf(Msg m) { return m.Style == 1 ? MentionBg : m.Style == 2 ? NoticeBg : Bg; }

    void Render(Msg m)
    {
        if (m.Cache != null) return;
        var b = new Bitmap(m.LW, m.H, PixelFormat.Format32bppRgb);
        var bg = BgOf(m);
        using (var g = Graphics.FromImage(b))
        {
            g.Clear(bg);
            if (m.Style == 2) using (var br = new SolidBrush(Accent)) g.FillRectangle(br, 0, 0, 4, m.H);
            if (m.Style == 1) using (var br = new SolidBrush(Color.FromArgb(235, 4, 0))) g.FillRectangle(br, 0, 0, 3, m.H);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            foreach (var pl in m.Places)
            {
                var p = pl.P;
                if (p.Kind == 1 || p.Kind == 2)
                {
                    if (p.E.State != 2 || p.E.Animated || p.E.Zero) continue;   // moving ones are drawn live
                    g.DrawImage(p.E.Frames[0], pl.R.X, pl.R.Y, pl.R.Width, pl.R.Height);
                }
                else if (p.Kind == 7)
                    Txt.In(g, "↳ " + p.Text, FS, Dim, pl.R, false);
                else
                {
                    Txt.At(g, p.Text, FontOf(p), p.Kind == 4 ? LinkC : p.C, pl.R.X, pl.R.Y);
                    if (p.Kind == 4) using (var pen = new Pen(LinkC)) g.DrawLine(pen, pl.R.Left, pl.R.Bottom, pl.R.Right - 1, pl.R.Bottom);
                }
            }
        }
        m.Cache = b;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.InterpolationMode = InterpolationMode.NearestNeighbor;
        gen++; anims.Clear(); animY.Clear();
        long now = clock.ElapsedMilliseconds;
        int y = ClientSize.Height + scroll, top = 0;
        for (int i = Msgs.Count - 1; i >= 0; i--)
        {
            var m = Msgs[i]; Lay(m);
            y -= m.H;
            if (y >= ClientSize.Height) continue;
            if (y + m.H <= 0) { top = 1; break; }
            // every visible emote/badge: load it again if its frames were freed, and mark it in use
            foreach (var pl in m.Places) { var pe = pl.P.E; if (pe == null) continue; if (pe.State == 0) Images.Need(pe); Images.Used(pe); }
            Render(m); m.Seen = gen;
            g.DrawImageUnscaled(m.Cache, 0, y);
            if (!m.Dynamic) continue;
            foreach (var pl in m.Places)
            {
                var p = pl.P;
                if ((p.Kind != 1 && p.Kind != 2) || p.E.State != 2 || (!p.E.Animated && !p.E.Zero)) continue;
                int f = p.E.Index(now); pl.Last = f;
                g.DrawImage(p.E.Frames[f], pl.R.X, pl.R.Y + y, pl.R.Width, pl.R.Height);
                if (p.E.Animated) { anims.Add(pl); animY.Add(new KeyValuePair<Place, int>(pl, y)); }
            }
        }
        if (top == 0 && scroll > 0 && y > 0) { scroll = Math.Max(0, scroll - y); Invalidate(); }   // scrolled past the oldest line
        // free the pictures of lines that left the screen
        foreach (var m in Msgs) if (m.Cache != null && m.Seen != gen) Drop(m);
        if (scroll > 0)
        {
            string t = "⏸  Chat paused due to scroll";
            int tw = Txt.Width(t, FB);
            pausedBox = new Rectangle((ClientSize.Width - tw - 28) / 2, ClientSize.Height - 44, tw + 28, 32);
            using (var br = new SolidBrush(Color.FromArgb(230, 14, 14, 16))) g.FillRectangle(br, pausedBox);
            Txt.In(g, t, FB, Fg, pausedBox, true);
        }
        else pausedBox = Rectangle.Empty;
        animTick.Enabled = anims.Count > 0;
    }

    void Animate()
    {
        var form = FindForm();
        if (form == null || form.WindowState == FormWindowState.Minimized || !Visible) return;
        long now = clock.ElapsedMilliseconds;
        foreach (var kv in animY)
        {
            var pl = kv.Key; int f = pl.P.E.Index(now);
            if (f == pl.Last) continue;
            pl.Last = f;
            var r = pl.R; r.Y += kv.Value;
            // a zero-width emote on top is redrawn with it
            r.Inflate(8, 0);
            Invalidate(r);
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        int total = 0; foreach (var m in Msgs) { Lay(m); total += m.H; }
        scroll = Math.Max(0, Math.Min(Math.Max(0, total - ClientSize.Height), scroll + e.Delta / 120 * 60));
        Invalidate();
    }

    Place Hit(Point pt, out Msg msg)
    {
        msg = null;
        int y = ClientSize.Height + scroll;
        for (int i = Msgs.Count - 1; i >= 0; i--)
        {
            var m = Msgs[i]; if (m.Places == null) break;
            y -= m.H;
            if (pt.Y < y) continue;
            if (pt.Y >= y + m.H) break;
            msg = m;
            foreach (var pl in m.Places) { var r = pl.R; r.Y += y; if (r.Contains(pt)) return pl; }
            return null;
        }
        return null;
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (pausedBox.Contains(e.Location)) { scroll = 0; Invalidate(); return; }
        Msg m; var pl = Hit(e.Location, out m);
        if (pl == null) return;
        if (pl.P.Kind == 4) { var u = SafeUrl.FromChatWord(pl.P.Text); if (u != null) Launcher.Open(u); }   // upstream: Process.Start(raw word)
        else if (pl.P.Kind == 3 && m.Login != null && NameClicked != null) NameClicked(m.Login);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        Msg m; var pl = Hit(e.Location, out m);
        string t = pl == null ? null : pl.P.Kind == 1 || pl.P.Kind == 2 ? pl.P.E.Name : pl.P.Kind == 4 ? LinkTip(pl.P.Text) : null;
        Cursor = pl != null && (pl.P.Kind == 4 || pl.P.Kind == 3) || pausedBox.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
        if (t == tipText) return;
        tipText = t;
        if (t == null) tip.Hide(this); else tip.Show(t, this, e.X + 12, e.Y + 18);
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); foreach (var m in Msgs) Drop(m); }

    // where a chat link really goes, shown before it is clicked
    static string LinkTip(string word) { var u = SafeUrl.FromChatWord(word); return u == null ? null : "Opens in your browser: " + u.Host + "\n" + (u.AbsoluteUri.Length > 120 ? u.AbsoluteUri.Substring(0, 120) + "..." : u.AbsoluteUri); }
}

// The chat column: header, messages, input. Joins one channel at a time.
class ChatPanel : Panel
{
    public readonly ChatView View = new ChatView();
    readonly Panel head = new Panel(), foot = new Panel();
    static readonly Font HeadFont = new Font("Segoe UI Semibold", 9f);
    readonly Button gear = new Button();
    readonly TextBox input = new TextBox();
    readonly LinkLabel loginLink = new LinkLabel();
    public readonly ContextMenuStrip Menu = new ContextMenuStrip();
    readonly Irc irc = new Irc(Tw.Session);   // login + token are read for every connection attempt
    readonly ConcurrentQueue<Msg> inbox = new ConcurrentQueue<Msg>();
    readonly System.Windows.Forms.Timer drain = new System.Windows.Forms.Timer { Interval = 100 };
    public event Action LoginRequested;
    string channel, roomId;
    volatile Dictionary<string, Emote> third = new Dictionary<string, Emote>();
    static Dictionary<string, Emote> globals;
    static readonly object globalLock = new object();
    volatile Dictionary<string, Emote> badges = new Dictionary<string, Emote>();
    static Dictionary<string, Emote> globalBadges;
    readonly ConcurrentDictionary<string, string> twitchEmotes = new ConcurrentDictionary<string, string>();   // name -> id, learned from chat
    readonly ConcurrentDictionary<string, byte> chatters = new ConcurrentDictionary<string, byte>();
    IrcMsg me;   // our USERSTATE (badges, colour) for echoing our own messages
    readonly ConcurrentQueue<string> pending = new ConcurrentQueue<string>();
    readonly List<string> sent = new List<string>(); int sentPos;
    string tabBase, tabPrefix; List<string> tabList; int tabIdx;
    // emote picker: sets by source (for its sections), recently used names
    public static string RecentPath;
    readonly EmotePicker picker = new EmotePicker();
    readonly Button emoteBtn = new Button();
    static List<KeyValuePair<string, List<Named>>> globalSets = new List<KeyValuePair<string, List<Named>>>();
    volatile List<KeyValuePair<string, List<Named>>> channelSets = new List<KeyValuePair<string, List<Named>>>();
    volatile List<Named> twitchSet; string twitchFor; bool twitchLoading;
    List<string> recent;

    static readonly string[] DefaultColors = { "#FF0000", "#0000FF", "#008000", "#B22222", "#FF7F50", "#9ACD32", "#FF4500", "#2E8B57", "#DAA520", "#D2691E", "#5F9EA0", "#1E90FF", "#FF69B4", "#8A2BE2", "#00FF7F" };
    static readonly Regex UrlRx = new Regex(@"^(https?://\S+|(www\.)?[a-z0-9-]+(\.[a-z0-9-]+)*\.(com|tv|gg|net|org|io|be|ly|me|co|app|link)(/\S*)?)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public ChatPanel()
    {
        BackColor = ChatView.Bg;
        head.Dock = DockStyle.Top; head.Height = 38; head.BackColor = ChatView.Bg;
        gear.Text = ""; gear.Font = new Font("Segoe MDL2 Assets", 10f); gear.FlatStyle = FlatStyle.Flat; gear.FlatAppearance.BorderSize = 0;
        gear.FlatAppearance.MouseOverBackColor = Color.FromArgb(38, 38, 44); gear.ForeColor = ChatView.Fg; gear.Width = 34; gear.Dock = DockStyle.Right; gear.Cursor = Cursors.Hand;
        gear.Click += (s, e) => Menu.Show(gear, new Point(gear.Width - Menu.Width, gear.Height));
        Menu.Renderer = new ToolStripProfessionalRenderer(new DarkColors()); Menu.ForeColor = ChatView.Fg; Menu.ShowImageMargin = false;
        head.Controls.Add(gear);
        head.Paint += (s, e) =>
        {
            using (var p = new Pen(Color.FromArgb(53, 53, 59))) e.Graphics.DrawLine(p, 0, head.Height - 1, head.Width, head.Height - 1);
            Txt.In(e.Graphics, "STREAM CHAT", HeadFont, ChatView.Fg, new Rectangle(gear.Width, 0, head.Width - 2 * gear.Width, head.Height - 1), true);
        };
        head.Resize += (s, e) => head.Invalidate();

        foot.Dock = DockStyle.Bottom; foot.Height = 50; foot.BackColor = ChatView.Bg; foot.Padding = new Padding(10, 8, 10, 10);
        input.BorderStyle = BorderStyle.FixedSingle; input.BackColor = Color.FromArgb(38, 38, 44); input.ForeColor = ChatView.Fg; input.Font = new Font("Segoe UI", 10.5f);
        input.Dock = DockStyle.Fill; input.MaxLength = 500;
        input.HandleCreated += (s, e) => SendMessage(input.Handle, 0x1501, (IntPtr)1, "Send a message");
        input.PreviewKeyDown += (s, e) => { if (e.KeyCode == Keys.Tab) e.IsInputKey = true; };
        input.KeyDown += OnKey;
        loginLink.Text = "Log in with Twitch to chat"; loginLink.LinkColor = ChatView.LinkC; loginLink.ActiveLinkColor = ChatView.Fg; loginLink.Dock = DockStyle.Fill;
        loginLink.TextAlign = ContentAlignment.MiddleCenter; loginLink.LinkBehavior = LinkBehavior.HoverUnderline;
        loginLink.LinkClicked += (s, e) => { if (LoginRequested != null) LoginRequested(); };
        emoteBtn.Text = ""; emoteBtn.Font = new Font("Segoe MDL2 Assets", 11f); emoteBtn.FlatStyle = FlatStyle.Flat; emoteBtn.FlatAppearance.BorderSize = 0;
        emoteBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(53, 53, 59); emoteBtn.BackColor = Color.FromArgb(38, 38, 44); emoteBtn.ForeColor = ChatView.Fg;
        emoteBtn.Width = 32; emoteBtn.Dock = DockStyle.Right; emoteBtn.Cursor = Cursors.Hand; emoteBtn.TabStop = false;
        emoteBtn.Click += (s, e) => TogglePicker();
        new ToolTip().SetToolTip(emoteBtn, "Emotes");
        foot.Controls.Add(input); foot.Controls.Add(emoteBtn); foot.Controls.Add(loginLink);
        picker.Dock = DockStyle.Bottom; picker.Height = 340; picker.Visible = false;
        picker.Picked += Insert;
        picker.HintClicked += () => { if (LoginRequested != null) LoginRequested(); };
        picker.Closed += () => input.Focus();
        View.Dock = DockStyle.Fill;
        View.NameClicked += n => { input.Text = (input.Text.Length > 0 && !input.Text.EndsWith(" ") ? input.Text + " " : input.Text) + "@" + n + " "; input.Focus(); input.SelectionStart = input.Text.Length; };
        Controls.Add(View); Controls.Add(picker); Controls.Add(foot); Controls.Add(head);

        irc.Line += OnLine;
        irc.Status += s => inbox.Enqueue(Sys(s));
        drain.Tick += (s, e) => Drain();
        // a decoded image is counted in the frame budget when it arrives, even if it is never drawn
        Images.Loaded += e => { try { if (IsHandleCreated) BeginInvoke((Action)(() => { Images.Used(e); Images.Trim(); View.OnImage(e); if (picker.Visible) picker.Invalidate(); })); } catch { } };
        UpdateLogin();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);

    string joinedAs;   // the account the chat connection was opened with (null = anonymous)

    public void UpdateLogin()
    {
        // logged in, out or as someone else since the chat connected: reconnect as the current account
        if (channel != null && drain.Enabled && joinedAs != Tw.Login) { Reconnect(); return; }
        bool can = Tw.Token != null && Tw.Login != null;   // a validated session (not a token kept while Twitch was unreachable)
        input.Visible = emoteBtn.Visible = can; loginLink.Visible = !can;
        if (!can) picker.Visible = false;
        else if (picker.Visible) { LoadTwitch(); RefreshPicker(); }
    }

    public string Channel { get { return channel; } }

    public void Join(string ch)
    {
        if (ch == channel && drain.Enabled) return;
        channel = ch; roomId = null; badges = new Dictionary<string, Emote>(); third = globals ?? new Dictionary<string, Emote>();
        channelSets = new List<KeyValuePair<string, List<Named>>>(); RefreshPicker();
        View.Clear(); Msg dummy; while (inbox.TryDequeue(out dummy)) { }
        string dummyS; while (pending.TryDequeue(out dummyS)) { }
        chatters.Clear(); me = null; joinedAs = Tw.Login;
        UpdateLogin();
        inbox.Enqueue(Sys("Welcome to the chat room!"));
        irc.Start(ch);
        drain.Start();
        LoadGlobals();
    }

    public void Part() { irc.Stop(); drain.Stop(); channel = null; View.Clear(); }

    public void Reconnect() { if (channel != null) { var c = channel; channel = null; Join(c); } }

    // ---------- emotes and badges ----------
    void LoadGlobals()
    {
        lock (globalLock) { if (globals != null) return; globals = new Dictionary<string, Emote>(); }
        Task.Run(async () =>
        {
            var d = new Dictionary<string, Emote>();
            var lf = await Ffz(d, "https://api.frankerfacez.com/v1/set/global", true);
            var lb = await Bttv(d, "https://api.betterttv.net/3/cached/emotes/global", false);
            var l7 = await SevenTv(d, "https://7tv.io/v3/emote-sets/global", false);
            globals = d;
            globalSets = new List<KeyValuePair<string, List<Named>>> { Set("7TV global", l7), Set("BetterTTV global", lb), Set("FFZ global", lf) };
            Post(RefreshPicker);
            if (roomId == null) third = d; else LoadChannel(roomId);
            if (Tw.Token != null && globalBadges == null) globalBadges = await Badges("chat/badges/global");
        });
    }

    void LoadChannel(string id)
    {
        string ch = channel;
        Task.Run(async () =>
        {
            var d = new Dictionary<string, Emote>(globals ?? new Dictionary<string, Emote>());
            var lf = await Ffz(d, "https://api.frankerfacez.com/v1/room/id/" + id, false);
            var lb = await Bttv(d, "https://api.betterttv.net/3/cached/users/twitch/" + id, true);
            var l7 = await SevenTv(d, "https://7tv.io/v3/users/twitch/" + id, true);
            if (ch != channel) return;
            third = d;
            channelSets = new List<KeyValuePair<string, List<Named>>> { Set("7TV channel", l7), Set("BetterTTV channel", lb), Set("FFZ channel", lf) };
            Post(RefreshPicker);
            if (Tw.Token != null)
            {
                if (globalBadges == null) globalBadges = await Badges("chat/badges/global");
                var b = await Badges("chat/badges?broadcaster_id=" + id);
                if (ch == channel) badges = b;
            }
        });
    }

    static async Task<Dictionary<string, Emote>> Badges(string q)
    {
        var d = new Dictionary<string, Emote>();
        var j = await Tw.Helix(q);
        foreach (var set in Tw.A(j, "data"))
            foreach (var v in Tw.A(set, "versions"))
            {
                string url = Tw.S(v, "image_url_1x"); if (url == null) continue;
                d[Tw.S(set, "set_id") + "/" + Tw.S(v, "id")] = Images.Get(url, Tw.S(v, "title"), 18, 18, 18);
            }
        return d;
    }

    static KeyValuePair<string, List<Named>> Set(string name, List<Named> list) { return new KeyValuePair<string, List<Named>>(name, list); }

    static async Task<List<Named>> SevenTv(Dictionary<string, Emote> d, string url, bool user)
    {
        var added = new List<Named>();
        var j = Tw.Parse(await Tw.Get(url));
        if (user) j = Tw.V(j, "emote_set");
        foreach (var e in Tw.A(j, "emotes"))
        {
            var data = Tw.V(e, "data"); var host = Tw.V(data, "host");
            string name = Tw.S(e, "name"), hu = Tw.S(host, "url"); if (name == null || hu == null) continue;
            bool anim = Tw.B(data, "animated");
            int w = 0, h = 0;
            foreach (var f in Tw.A(host, "files")) if (Tw.S(f, "name") == "1x.webp") { w = (int)Tw.N(f, "width"); h = (int)Tw.N(f, "height"); }
            var em = Images.Get("https:" + hu + "/1x." + (anim ? "gif" : "png"), name, w, h, 0);
            em.Zero = (Tw.N(e, "flags") & 1) != 0 || (Tw.N(data, "flags") & 256) != 0;
            d[name] = em; added.Add(new Named(name, em));
        }
        return added;
    }

    static readonly HashSet<string> BttvZero = new HashSet<string> { "SoSnowy", "IceCold", "SantaHat", "TopHat", "ReinDeer", "CandyCane", "cvMask", "cvHazmat" };
    static async Task<List<Named>> Bttv(Dictionary<string, Emote> d, string url, bool user)
    {
        var added = new List<Named>();
        var j = Tw.Parse(await Tw.Get(url));
        var list = user ? Tw.A(j, "channelEmotes").Concat(Tw.A(j, "sharedEmotes")) : (j as object[] ?? new object[0]);
        foreach (var e in list)
        {
            string code = Tw.S(e, "code"), id = Tw.S(e, "id"); if (code == null || id == null) continue;
            bool anim = Tw.B(e, "animated") || Tw.S(e, "imageType") == "gif";
            var em = Images.Get("https://cdn.betterttv.net/emote/" + id + "/1x." + (anim ? "gif" : "png"), code, 0, 0, 0);
            em.Zero = BttvZero.Contains(code);
            d[code] = em; added.Add(new Named(code, em));
        }
        return added;
    }

    static async Task<List<Named>> Ffz(Dictionary<string, Emote> d, string url, bool global)
    {
        var added = new List<Named>();
        var j = Tw.Parse(await Tw.Get(url));
        var sets = Tw.V(j, "sets");
        var only = global ? new HashSet<string>(Tw.A(j, "default_sets").Select(x => Convert.ToString(x))) : null;
        foreach (var kv in Tw.Props(sets))
        {
            if (only != null && !only.Contains(kv.Key)) continue;
            foreach (var e in Tw.A(kv.Value, "emoticons"))
            {
                string name = Tw.S(e, "name"), u = Tw.S(Tw.V(e, "urls"), "1"); if (name == null || u == null) continue;
                if (u.StartsWith("//")) u = "https:" + u;
                var em = Images.Get(u, name, (int)Tw.N(e, "width"), (int)Tw.N(e, "height"), 0);
                d[name] = em; added.Add(new Named(name, em));
            }
        }
        return added;
    }

    // ---------- emote picker ----------
    void TogglePicker()
    {
        if (picker.Visible) { picker.Visible = false; input.Focus(); return; }
        LoadRecent(); LoadTwitch();
        picker.Height = Math.Max(200, Math.Min(360, (Height - head.Height - foot.Height) * 3 / 5));
        RefreshPicker(true);
        picker.Visible = true; picker.Search.Text = ""; picker.Search.Focus();
    }

    void RefreshPicker() { RefreshPicker(false); }
    void RefreshPicker(bool force)
    {
        if (!picker.Visible && !force) return;
        var g = new List<KeyValuePair<string, List<Named>>>();
        var rec = new List<Named>();
        if (recent != null) foreach (var n in recent) { var e = Lookup(n); if (e != null) rec.Add(new Named(n, e)); }
        g.Add(Set("Recently used", rec));
        g.AddRange(channelSets);
        var tw = twitchSet; if (tw != null) g.Add(Set(Tw.Has("user:read:emotes") ? "Twitch" : "Twitch global", tw));
        g.AddRange(globalSets);
        picker.Hint = Tw.Token != null && !Tw.Has("user:read:emotes") ? "Log in again to add your own Twitch emotes here" : null;
        picker.SetGroups(g);
    }

    Emote Lookup(string name)
    {
        Emote e; if (third.TryGetValue(name, out e)) return e;
        var tw = twitchSet; if (tw != null) foreach (var n in tw) if (n.Key == name) return n.Value;
        string id; if (twitchEmotes.TryGetValue(name, out id)) return TwitchEmote(id, name);
        return null;
    }

    static Emote TwitchEmote(string id, string name) { return Images.Get("https://static-cdn.jtvnw.net/emoticons/v2/" + id + "/default/dark/1.0", name, 28, 28, 0); }

    // your own Twitch emotes (needs the user:read:emotes scope), else Twitch's global ones
    async void LoadTwitch()
    {
        if (Tw.Token == null || twitchLoading || (twitchSet != null && twitchFor == Tw.Token)) return;
        twitchLoading = true;
        try
        {
            string token = Tw.Token;
            bool mine = Tw.Has("user:read:emotes") && Tw.UserId != null;
            var list = new List<Named>(); var seen = new HashSet<string>(); string cursor = null;
            for (int page = 0; page < 30; page++)
            {
                var j = await Tw.Helix(mine ? "chat/emotes/user?user_id=" + Tw.UserId + (cursor != null ? "&after=" + cursor : "") : "chat/emotes/global");
                if (j == null) break;
                foreach (var e in Tw.A(j, "data"))
                {
                    string id = Tw.S(e, "id"), name = Tw.S(e, "name");
                    if (id == null || name == null || !seen.Add(name)) continue;
                    twitchEmotes[name] = id; list.Add(new Named(name, TwitchEmote(id, name)));
                }
                cursor = Tw.S(Tw.V(j, "pagination"), "cursor");
                if (!mine || cursor == null) break;
            }
            twitchSet = list; twitchFor = token;
        }
        finally { twitchLoading = false; }
        RefreshPicker();
    }

    void LoadRecent()
    {
        if (recent != null) return;
        recent = new List<string>();
        try { if (RecentPath != null && File.Exists(RecentPath)) recent.AddRange(File.ReadAllLines(RecentPath).Where(x => x.Length > 0).Take(30)); } catch { }
    }

    void Used(IEnumerable<string> names)
    {
        LoadRecent();
        foreach (var n in names) { recent.Remove(n); recent.Insert(0, n); }
        if (recent.Count > 30) recent.RemoveRange(30, recent.Count - 30);
        try { if (RecentPath != null) File.WriteAllLines(RecentPath, recent); } catch { }
    }

    void Insert(string name)
    {
        string t = input.Text; int s = Math.Min(input.SelectionStart, t.Length), l = Math.Min(input.SelectionLength, t.Length - s);
        string before = t.Substring(0, s), after = t.Substring(s + l).TrimStart();
        string ins = (before.Length > 0 && !before.EndsWith(" ") ? " " : "") + name + " ";
        if (before.Length + ins.Length + after.Length > input.MaxLength) return;
        input.Text = before + ins + after;
        input.SelectionStart = before.Length + ins.Length; input.SelectionLength = 0;
        Used(new[] { name });
        RefreshPicker();
    }

    // ---------- incoming ----------
    static Msg Sys(string text)
    {
        var m = new Msg { Style = 3 };
        foreach (var w in text.Split(' ')) if (w.Length > 0) m.P.Add(new Piece { Kind = 6, Text = w, C = ChatView.Dim });
        return m;
    }

    // chat stress mode (src/app/LiteView.Fork.cs): synthetic lines take the same path as Twitch's
    public void Inject(string line) { OnLine(line); }
    public List<string> EmoteNames() { return third.Keys.Take(5000).ToList(); }

    void OnLine(string line)
    {
        IrcMsg im;
        try { im = IrcMsg.Parse(line); } catch { return; }
        if (im == null) return;
        switch (im.Cmd)
        {
            case "PRIVMSG": if (!ChatPrefs.Filter.IsIgnored(im.Nick)) inbox.Enqueue(Build(im, im.Text, im.Nick)); break;   // fork: ignored users
            case "USERNOTICE":
            {
                var m = Build(im, im.Text ?? "", im.Tag("login")); m.Style = 2;
                var sys = new List<Piece>();
                foreach (var w in (im.Tag("system-msg") ?? "").Split(' ')) if (w.Length > 0) sys.Add(new Piece { Kind = 0, Text = w, C = ChatView.Fg });
                if (im.Text == null) m.P = sys; else { sys.Add(new Piece { Kind = 0, Text = "•", C = ChatView.Dim }); m.P = sys.Concat(m.P).ToList(); }
                inbox.Enqueue(m); break;
            }
            case "ROOMSTATE":
                if (ChannelName.IsNumericId(im.Tag("room-id")) && roomId == null) { roomId = im.Tag("room-id"); LoadChannel(roomId); }   // digits only: it goes into API URLs
                break;
            case "USERSTATE": case "GLOBALUSERSTATE":
            {
                if (im.Cmd == "USERSTATE") me = im;
                string text;
                if (im.Cmd == "USERSTATE" && pending.TryDequeue(out text)) inbox.Enqueue(Build(im, text, Tw.Login));
                break;
            }
            case "NOTICE":
            {
                string t; if (im.Tag("msg-id") != null && im.Tag("msg-id").StartsWith("msg_")) pending.TryDequeue(out t);
                if (im.Text != null) inbox.Enqueue(Sys(im.Text));
                if (im.Text != null && im.Text.Contains("authentication failed"))
                    try { BeginInvoke((Action)(async () => { await Tw.Validate(); UpdateLogin(); Reconnect(); })); } catch { }
                break;
            }
            case "CLEARMSG":
            {
                string id = im.Tag("target-msg-id");
                if (id != null) Post(() => View.Redo(m => { if (m.Id != id) return false; m.Deleted = true; return true; }));
                break;
            }
            case "CLEARCHAT":
            {
                string who = im.Text;
                if (who == null) inbox.Enqueue(Sys("Chat was cleared by a moderator"));
                else Post(() => View.Redo(m => { if (m.Login != who || m.Deleted) return false; m.Deleted = true; return true; }));
                break;
            }
        }
    }

    void Post(Action a) { try { if (IsHandleCreated) BeginInvoke(a); } catch { } }

    static Color ParseColor(string hex, string login)
    {
        Color c;
        try { c = ColorTranslator.FromHtml(string.IsNullOrEmpty(hex) ? DefaultColors[(int)((uint)(login ?? "").GetHashCode() % DefaultColors.Length)] : hex); }
        catch { c = ChatView.Fg; }
        // readable on the dark background, like Twitch's own colour adjustment
        double l = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;
        if (l < 0.35) { double k = (0.35 - l) / 0.35 * 0.6 + 0.25; c = Color.FromArgb((int)(c.R + (255 - c.R) * k), (int)(c.G + (255 - c.G) * k), (int)(c.B + (255 - c.B) * k)); }
        return c;
    }

    Msg Build(IrcMsg im, string text, string login)
    {
        text = IrcProtocol.ForDisplay(text);   // never lay out more than 2000 characters of one message
        if (chatters.Count > 20000) chatters.Clear();
        if (twitchEmotes.Count > 20000) twitchEmotes.Clear();
        var m = new Msg { Id = im.Tag("id"), Login = login };
        m.Display = im.Tag("display-name"); if (string.IsNullOrEmpty(m.Display)) m.Display = login ?? "";
        m.NameColor = ParseColor(im.Tag("color"), login);
        if (login != null) chatters[m.Display] = 0;
        var bd = badges; var gb = globalBadges;
        if (ChatPrefs.Timestamps) { string ts = ChatFilter.Stamp(im.Tag("tmi-sent-ts")); m.P.Add(new Piece { Kind = 6, Text = ts.Length > 0 ? ts : DateTime.Now.ToString("HH:mm"), C = ChatView.Dim }); }   // fork
        foreach (var b in (im.Tag("badges") ?? "").Split(','))
        {
            if (b.Length == 0) continue;
            Emote e;
            if (bd.TryGetValue(b, out e) || (gb != null && gb.TryGetValue(b, out e))) m.P.Add(new Piece { Kind = 2, E = e });
        }
        bool action = text.StartsWith("\u0001ACTION ") && text.EndsWith("\u0001");
        if (action) text = text.Substring(8, text.Length - 9);
        else if (text.StartsWith("/me ")) { action = true; text = text.Substring(4); }
        if (m.Display.Length > 0)
        {
            string shown = m.Display;
            if (login != null && !m.Display.Equals(login, StringComparison.OrdinalIgnoreCase)) shown += " (" + login + ")";
            m.P.Add(new Piece { Kind = 3, Text = shown + (action ? "" : ":"), C = m.NameColor });
        }
        string rp = im.Tag("reply-parent-display-name");
        if (rp != null)
        {
            m.Reply = IrcProtocol.ForDisplay("Replying to @" + rp + ": " + (im.Tag("reply-parent-msg-body") ?? ""));
        }
        Color tc = action ? m.NameColor : ChatView.Fg;
        // Twitch emotes by position (positions count Unicode code points, not UTF-16 units)
        var ranges = new List<KeyValuePair<int, int>>(); var ids = new Dictionary<int, string>();
        string tags = im.Tag("emotes");
        if (!string.IsNullOrEmpty(tags))
        {
            var cp = new List<int>(); for (int i = 0; i < text.Length; i++) { cp.Add(i); if (char.IsHighSurrogate(text[i])) i++; }
            cp.Add(text.Length);
            foreach (var part in tags.Split('/'))
            {
                int c = part.IndexOf(':'); if (c < 0) continue;
                string id = part.Substring(0, c);
                foreach (var r in part.Substring(c + 1).Split(','))
                {
                    var se = r.Split('-'); int a, z;
                    if (se.Length != 2 || !int.TryParse(se[0], out a) || !int.TryParse(se[1], out z) || a < 0 || z + 1 >= cp.Count || a > z) continue;
                    int s0 = cp[a], s1 = cp[z + 1];
                    ranges.Add(new KeyValuePair<int, int>(s0, s1)); ids[s0] = id;
                    twitchEmotes[text.Substring(s0, s1 - s0)] = id;
                }
            }
            ranges.Sort((x, y) => x.Key.CompareTo(y.Key));
        }
        int pos = 0;
        foreach (var r in ranges)
        {
            if (r.Key < pos) continue;
            Words(m, text.Substring(pos, r.Key - pos), tc, false);
            string name = text.Substring(r.Key, r.Value - r.Key);
            m.P.Add(new Piece { Kind = 1, E = Images.Get("https://static-cdn.jtvnw.net/emoticons/v2/" + ids[r.Key] + "/default/dark/1.0", name, 28, 28, 0) });
            pos = r.Value;
        }
        Words(m, text.Substring(pos), tc, login == Tw.Login && login != null && im.Tag("emotes") == null);
        if (Tw.Login != null && login != Tw.Login && Regex.IsMatch(text, @"(^|\W)@?" + Regex.Escape(Tw.Login) + @"(\W|$)", RegexOptions.IgnoreCase)) m.Style = 1;
        if (m.Reply != null && Tw.Login != null && im.Tag("reply-parent-user-login") == Tw.Login && login != Tw.Login) m.Style = 1;
        if (m.Style == 0 && login != null && login != Tw.Login && ChatPrefs.Filter.Highlights(text)) m.Style = 1;   // fork: highlight words
        return m;
    }

    void Words(Msg m, string s, Color tc, bool own)
    {
        var d = third;
        foreach (var w in s.Split(' '))
        {
            if (w.Length == 0) continue;
            Emote e; string id;
            if (d.TryGetValue(w, out e)) m.P.Add(new Piece { Kind = 1, E = e });
            else if (own && twitchEmotes.TryGetValue(w, out id))
                m.P.Add(new Piece { Kind = 1, E = Images.Get("https://static-cdn.jtvnw.net/emoticons/v2/" + id + "/default/dark/1.0", w, 28, 28, 0) });
            else if (w.Length > 4 && w.IndexOf('.') > 0 && UrlRx.IsMatch(w)) m.P.Add(new Piece { Kind = 4, Text = w });
            else m.P.Add(new Piece { Kind = (byte)(w[0] == '@' && w.Length > 1 ? 5 : 0), Text = w, C = tc });
        }
    }

    void Drain()
    {
        var list = new List<Msg>(); Msg m;
        while (inbox.TryDequeue(out m)) list.Add(m);
        if (list.Count > 150) list = list.GetRange(list.Count - 150, 150);   // a flood: keep the newest
        View.Add(list);
        Images.Trim();
    }

    // ---------- sending ----------
    void OnKey(object s, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Tab) tabList = null;
        if (e.KeyCode == Keys.Enter)
        {
            e.SuppressKeyPress = true;
            string t = IrcProtocol.CleanOutgoing(input.Text);   // no CR/LF/control characters, at most 500
            if (t.Length == 0 || channel == null || Tw.Token == null || Tw.Login == null) return;
            if (irc.Send("PRIVMSG #" + channel + " :" + t))
            {
                pending.Enqueue(t);
                var d = third; var used = t.Split(' ').Where(w => w.Length > 0 && (d.ContainsKey(w) || twitchEmotes.ContainsKey(w))).Distinct().Reverse().ToList();
                if (used.Count > 0) Used(used);
                sent.Add(t); if (sent.Count > 50) sent.RemoveAt(0); sentPos = sent.Count;
                input.Text = "";
                View.ToBottom();
            }
        }
        else if (e.KeyCode == Keys.Up && sent.Count > 0) { e.SuppressKeyPress = true; sentPos = Math.Max(0, sentPos - 1); input.Text = sent[sentPos]; input.SelectionStart = input.Text.Length; }
        else if (e.KeyCode == Keys.Down && sent.Count > 0) { e.SuppressKeyPress = true; sentPos = Math.Min(sent.Count, sentPos + 1); input.Text = sentPos < sent.Count ? sent[sentPos] : ""; input.SelectionStart = input.Text.Length; }
        else if (e.KeyCode == Keys.Tab) { e.SuppressKeyPress = true; e.Handled = true; Complete(e.Shift); }
    }

    // Tab: complete emote names and @chatters, like 7TV
    void Complete(bool back)
    {
        if (tabList == null)
        {
            string t = input.Text.Substring(0, input.SelectionStart);
            int sp = t.LastIndexOf(' ') + 1;
            tabBase = input.Text.Substring(0, sp); tabPrefix = t.Substring(sp);
            string rest = input.Text.Substring(input.SelectionStart);
            if (tabPrefix.Length == 0) return;
            bool at = tabPrefix[0] == '@'; string p = at ? tabPrefix.Substring(1) : tabPrefix;
            IEnumerable<string> pool = at ? chatters.Keys.Select(x => "@" + x) : third.Keys.Concat(twitchEmotes.Keys).Concat(chatters.Keys);
            tabList = pool.Where(x => (at ? x.Substring(1) : x).StartsWith(p, StringComparison.OrdinalIgnoreCase)).Distinct().OrderBy(x => x.Length).ThenBy(x => x).Take(50).ToList();
            tabIdx = back ? tabList.Count : -1;
            tabBase = tabBase + "\u0000" + rest;
            if (tabList.Count == 0) { tabList = null; return; }
        }
        tabIdx = (tabIdx + (back ? -1 : 1) + tabList.Count) % tabList.Count;
        var parts = tabBase.Split('\u0000');
        input.Text = parts[0] + tabList[tabIdx] + " " + parts[1].TrimStart();
        input.SelectionStart = parts[0].Length + tabList[tabIdx].Length + 1;
    }

    class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Color.FromArgb(31, 31, 35); } }
        public override Color MenuItemSelected { get { return Color.FromArgb(53, 53, 59); } }
        public override Color MenuItemBorder { get { return Color.FromArgb(53, 53, 59); } }
        public override Color MenuBorder { get { return Color.FromArgb(53, 53, 59); } }
        public override Color ImageMarginGradientBegin { get { return Color.FromArgb(31, 31, 35); } }
        public override Color ImageMarginGradientMiddle { get { return Color.FromArgb(31, 31, 35); } }
        public override Color ImageMarginGradientEnd { get { return Color.FromArgb(31, 31, 35); } }
        public override Color SeparatorDark { get { return Color.FromArgb(53, 53, 59); } }
    }
}
