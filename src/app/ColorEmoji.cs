// Colour emoji for the native chat. GDI+ (which draws the chat's text) has no colour fonts, so emoji came out in one
// colour. This draws an emoji sequence (src/core/Emoji.cs finds them) with Direct2D + DirectWrite and its colour-font
// option, as Twitch's site does in the browser, into a small transparent bitmap that the chat then draws like an
// emote. Direct2D's DC render target is driven through its COM vtable (no extra library). At start it draws one
// emoji and checks that colour came out; if not (no colour font, an old Windows, any error), Available is false and
// emoji are drawn as text, as before. Bitmaps are cached (at most MaxCached; the oldest go).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using LiteViewCore;

static class ColorEmoji
{
    const int MaxCached = 600;
    static readonly Dictionary<string, KeyValuePair<Bitmap, LinkedListNode<string>>> cache = new Dictionary<string, KeyValuePair<Bitmap, LinkedListNode<string>>>();
    static readonly LinkedList<string> order = new LinkedList<string>();   // most recently used first
    static IntPtr d2d, dwrite, target, brush, format; static float formatPx;
    static bool tried, ok;

    [StructLayout(LayoutKind.Sequential)] struct ColorF { public float r, g, b, a; }
    [StructLayout(LayoutKind.Sequential)] struct RectF { public float left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct TargetProps { public int type; public int format; public int alpha; public float dpiX, dpiY; public int usage; public int minLevel; }
    [StructLayout(LayoutKind.Sequential)]
    struct BitmapInfoHeader { public int size, width, height; public short planes, bits; public int compression, sizeImage, xppm, yppm, clrUsed, clrImportant; }

    [DllImport("d2d1.dll")] static extern int D2D1CreateFactory(int type, ref Guid riid, IntPtr options, out IntPtr factory);
    [DllImport("dwrite.dll")] static extern int DWriteCreateFactory(int type, ref Guid riid, out IntPtr factory);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfoHeader bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint ReleaseFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateDcTargetFn(IntPtr self, ref TargetProps props, out IntPtr target);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateBrushFn(IntPtr self, ref ColorF color, IntPtr props, out IntPtr brush);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)] delegate void DrawTextFn(IntPtr self, string text, uint length, IntPtr format, ref RectF rect, IntPtr brush, int options, int measuring);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void SetModeFn(IntPtr self, int mode);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void ClearFn(IntPtr self, ref ColorF color);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void BeginDrawFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EndDrawFn(IntPtr self, IntPtr tag1, IntPtr tag2);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int BindDcFn(IntPtr self, IntPtr hdc, ref Rect rect);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)] delegate int CreateFormatFn(IntPtr self, string family, IntPtr collection, int weight, int style, int stretch, float size, string locale, out IntPtr format);

    // vtable slots (d2d1.h, dwrite.h)
    const int Release = 2, FactoryCreateDcTarget = 16, RtCreateSolidBrush = 8, RtDrawText = 27, RtSetTextAntialias = 34, RtClear = 47, RtBeginDraw = 48, RtEndDraw = 49, DcBindDc = 57, DwCreateTextFormat = 15;

    static T Fn<T>(IntPtr obj, int slot) where T : class
    {
        var fp = Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size);
        return (T)(object)Marshal.GetDelegateForFunctionPointer(fp, typeof(T));
    }

    static void Free(ref IntPtr p) { if (p != IntPtr.Zero) { Fn<ReleaseFn>(p, Release)(p); p = IntPtr.Zero; } }

    /// <summary>Colour emoji work on this PC (checked once, on first use).</summary>
    public static bool Available { get { if (!tried) Init(); return ok; } }

    static void Init()
    {
        tried = true;
        try
        {
            var iid = new Guid("06152247-6f50-465a-9245-118bfd3b6007");
            if (D2D1CreateFactory(0, ref iid, IntPtr.Zero, out d2d) < 0) { Log.Info("emoji: Direct2D not available, drawn as text"); return; }
            var iidw = new Guid("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");
            if (DWriteCreateFactory(0, ref iidw, out dwrite) < 0) { Log.Info("emoji: DirectWrite not available, drawn as text"); return; }
            // software rendering: a few small bitmaps, no graphics device needed (the video has the GPU)
            var props = new TargetProps { type = 1, format = 87, alpha = 1, dpiX = 96, dpiY = 96 };
            if (Fn<CreateDcTargetFn>(d2d, FactoryCreateDcTarget)(d2d, ref props, out target) < 0) { Log.Info("emoji: no Direct2D target, drawn as text"); return; }
            Fn<SetModeFn>(target, RtSetTextAntialias)(target, 2);   // greyscale: ClearType needs an opaque background
            var white = new ColorF { r = 1, g = 1, b = 1, a = 1 };
            if (Fn<CreateBrushFn>(target, RtCreateSolidBrush)(target, ref white, IntPtr.Zero, out brush) < 0) return;
            using (var test = Draw("\U0001F600", 24))
                ok = test != null && Colourful(test);
            Log.Info(ok ? "emoji: colour (Segoe UI Emoji)" : "emoji: no colour came out, drawn as text");
        }
        catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is COMException || ex is SEHException || ex is AccessViolationException || ex is InvalidOperationException)
        {
            ok = false; Log.Warn("emoji: colour drawing failed (" + ex.GetType().Name + "), drawn as text");
        }
    }

    // some pixel with real colour (a yellow face has it; a monochrome glyph does not)
    static bool Colourful(Bitmap b)
    {
        for (int y = 0; y < b.Height; y += 2)
            for (int x = 0; x < b.Width; x += 2)
            {
                var c = b.GetPixel(x, y);
                if (c.A > 128 && Math.Abs(c.R - c.B) > 60) return true;
            }
        return false;
    }

    /// <summary>The emoji sequence as a colour picture about px pixels high (shared, never dispose it), or null.</summary>
    public static Bitmap Get(string emoji, int px)
    {
        if (!Available || string.IsNullOrEmpty(emoji) || emoji.Length > EmojiText.MaxSequence + 16) return null;
        px = Math.Max(8, Math.Min(64, px));
        string key = px + ":" + emoji;
        KeyValuePair<Bitmap, LinkedListNode<string>> hit;
        if (cache.TryGetValue(key, out hit)) { order.Remove(hit.Value); order.AddFirst(hit.Value); return hit.Key; }
        Bitmap b;
        try { b = Draw(emoji, px); }
        catch (Exception ex) when (ex is COMException || ex is SEHException || ex is AccessViolationException || ex is ExternalException) { b = null; }
        if (b == null) return null;
        cache[key] = new KeyValuePair<Bitmap, LinkedListNode<string>>(b, order.AddFirst(key));
        while (cache.Count > MaxCached) { string old = order.Last.Value; order.RemoveLast(); cache[old].Key.Dispose(); cache.Remove(old); }
        return b;
    }

    // DirectWrite text in Segoe UI Emoji with D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT, on a transparent DIB, trimmed
    static Bitmap Draw(string text, int px)
    {
        if (format == IntPtr.Zero || Math.Abs(formatPx - px) > 0.01f)
        {
            Free(ref format);
            if (Fn<CreateFormatFn>(dwrite, DwCreateTextFormat)(dwrite, "Segoe UI Emoji", IntPtr.Zero, 400, 0, 5, px, "en-us", out format) < 0) return null;
            formatPx = px;
        }
        int w = px * 3, h = (int)Math.Ceiling(px * 1.45);
        var bih = new BitmapInfoHeader { size = Marshal.SizeOf(typeof(BitmapInfoHeader)), width = w, height = -h, planes = 1, bits = 32 };
        IntPtr bits, dib = CreateDIBSection(IntPtr.Zero, ref bih, 0, out bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero) return null;
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        IntPtr old = SelectObject(dc, dib);
        try
        {
            var rc = new Rect { right = w, bottom = h };
            if (Fn<BindDcFn>(target, DcBindDc)(target, dc, ref rc) < 0) return null;
            Fn<BeginDrawFn>(target, RtBeginDraw)(target);
            var clear = new ColorF();
            Fn<ClearFn>(target, RtClear)(target, ref clear);
            var lr = new RectF { left = 0, top = 0, right = w, bottom = h };
            Fn<DrawTextFn>(target, RtDrawText)(target, text, (uint)text.Length, format, ref lr, brush, 4 /* ENABLE_COLOR_FONT */, 0);
            if (Fn<EndDrawFn>(target, RtEndDraw)(target, IntPtr.Zero, IntPtr.Zero) < 0) return null;
            // the DIB is premultiplied BGRA, top-down: copy it, then trim the empty columns on the right
            var full = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            var data = full.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try
            {
                var row = new byte[w * 4];
                for (int y = 0; y < h; y++) { Marshal.Copy(bits + y * w * 4, row, 0, row.Length); Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length); }
            }
            finally { full.UnlockBits(data); }
            // the drawn part: empty columns on the right and empty rows above and below go (the line keeps its height)
            int right = -1, top = h, bottom = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (full.GetPixel(x, y).A > 0) { if (x > right) right = x; if (y < top) top = y; if (y > bottom) bottom = y; }
            if (right < 0) { full.Dispose(); return null; }   // nothing drawn (no glyph for it)
            var trimmed = full.Clone(new Rectangle(0, top, right + 1, bottom - top + 1), PixelFormat.Format32bppPArgb);
            full.Dispose();
            return trimmed;
        }
        finally { SelectObject(dc, old); DeleteDC(dc); DeleteObject(dib); }
    }
}

// the chat's side: words with emoji are split around them (IRC thread: text only), the pictures are made and drawn on
// the UI thread (layout and painting)
partial class ChatPanel
{
    static void AddWord(Msg m, string w, byte kind, Color tc)
    {
        var parts = EmojiText.Split(w);
        if (parts.Count == 1 && !parts[0].Value) { m.P.Add(new Piece { Kind = kind, Text = w, C = tc }); return; }
        for (int i = 0; i < parts.Count; i++)
            m.P.Add(new Piece { Kind = parts[i].Value ? (byte)8 : kind, Text = parts[i].Key, C = tc, Glue = i < parts.Count - 1 });
    }
}

partial class ChatView
{
    /// <summary>Every message laid out and drawn again (the text smoothing changed: widths and pictures depend on it).</summary>
    public void ReLayout()
    {
        widths.Clear();
        spaceW = Txt.Width("x x", F) - Txt.Width("xx", F);
        textH = Txt.Height(F);
        Redo(m => true);
    }

    int EmojiPx { get { return (int)Math.Round(F.SizeInPoints * 96f / 72f * 1.25f); } }

    bool EmojiSize(string text, out int w, out int h)
    {
        var b = ColorEmoji.Get(text, EmojiPx);
        if (b == null) { w = h = 0; return false; }
        w = b.Width; h = b.Height;
        return true;
    }

    bool DrawEmoji(Graphics g, string text, Rectangle r)
    {
        var b = ColorEmoji.Get(text, EmojiPx);
        if (b == null) return false;
        g.DrawImage(b, r.X, r.Y, b.Width, b.Height);
        return true;
    }
}
