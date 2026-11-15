// The font of the custom-drawn text (chat, Following page, followed column): Inter, bundled in fonts\ (the font
// Twitch uses, SIL Open Font License), or Segoe UI (menu > Text size > Font). Inter has Latin, Greek and Cyrillic;
// Windows fills in missing characters only for its own fonts, so text with a character Inter lacks (Chinese, emoji,
// ...) is drawn in Segoe UI: per word in the chat, per string elsewhere (Txt). Smoothing follows Windows: ClearType
// when it is on (like the browser draws Twitch), else grey.
// The window's own controls (the top bar's title and status, buttons, the chat's text box) draw with GDI, which does
// not see a GDI+ private font; the files are also added for GDI, privately to this process (AddFontResourceEx with
// FR_PRIVATE: nothing is installed), and those controls use Inter only when GDI then really picks it (GetTextFace).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using LiteViewCore;

enum FontKind { Text, Semi, Bold }

static class UiFont
{
    static readonly PrivateFontCollection pfc = new PrivateFontCollection();
    static readonly FontFamily inter, interSemi;
    static readonly FontCoverage cover;
    static readonly ConditionalWeakTable<Font, Font> fallback = new ConditionalWeakTable<Font, Font>();
    static readonly HashSet<string> interNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, Font> cache = new Dictionary<string, Font>();

    /// <summary>The user's choice (setting "font"); Inter only when its files loaded.</summary>
    public static bool UseInter = true;
    public static bool Available { get { return inter != null; } }
    public static bool On { get { return UseInter && inter != null; } }
    public static readonly TextRenderingHint Hint;
    static readonly bool gdi;   // GDI (the window's controls) draws Inter too

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern int AddFontResourceEx(string name, uint flags, IntPtr reserved);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern int GetTextFace(IntPtr dc, int count, StringBuilder name);
    const uint FrPrivate = 0x10;

    static UiFont()
    {
        try { Hint = SystemInformation.IsFontSmoothingEnabled && SystemInformation.FontSmoothingType == 2 ? TextRenderingHint.ClearTypeGridFit : TextRenderingHint.AntiAliasGridFit; }
        catch (Exception) { Hint = TextRenderingHint.AntiAliasGridFit; }
        try
        {
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fonts");
            string reg = Path.Combine(dir, "Inter-Regular.ttf"), bold = Path.Combine(dir, "Inter-Bold.ttf"), semi = Path.Combine(dir, "Inter-SemiBold.ttf");
            if (!File.Exists(reg) || !File.Exists(bold) || !File.Exists(semi)) { Log.Info("font: Inter not found in " + dir + ", using Segoe UI"); return; }
            cover = FontCoverage.Read(File.ReadAllBytes(reg));
            if (cover == null) { Log.Warn("font: Inter-Regular.ttf could not be read, using Segoe UI"); return; }
            pfc.AddFontFile(reg); pfc.AddFontFile(bold); pfc.AddFontFile(semi);
            foreach (var f in pfc.Families)
            {
                if (f.Name == "Inter" && f.IsStyleAvailable(FontStyle.Regular) && f.IsStyleAvailable(FontStyle.Bold)) inter = f;
                else if (f.Name == "Inter Semi Bold" && f.IsStyleAvailable(FontStyle.Regular)) interSemi = f;
            }
            if (inter == null || interSemi == null) { inter = interSemi = null; Log.Warn("font: Inter's families are incomplete, using Segoe UI"); return; }
            interNames.Add(inter.Name); interNames.Add(interSemi.Name);
            Log.Info("font: Inter loaded (" + cover.RangeCount + " character ranges), smoothing " + (Hint == TextRenderingHint.ClearTypeGridFit ? "ClearType" : "grey"));
            try
            {
                int added = 0;
                foreach (var file in new[] { reg, bold, semi }) if (AddFontResourceEx(file, FrPrivate, IntPtr.Zero) > 0) added++;
                gdi = added == 3 && GdiPicks(inter) && GdiPicks(interSemi);
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is ArgumentException || ex is ExternalException) { gdi = false; }
            Log.Info(gdi ? "font: the window's controls use Inter too" : "font: the window's controls stay in Segoe UI (GDI does not see Inter)");
        }
        catch (Exception ex) { inter = interSemi = null; Log.Warn("font: Inter could not be loaded (" + ex.GetType().Name + "), using Segoe UI"); }
    }

    /// <summary>A new font of the chosen family (the caller owns it).</summary>
    public static Font New(FontKind k, float pt)
    {
        if (On)
        {
            if (k == FontKind.Semi) return new Font(interSemi, pt, FontStyle.Regular);
            return new Font(inter, pt, k == FontKind.Bold ? FontStyle.Bold : FontStyle.Regular);
        }
        return new Font(k == FontKind.Text ? "Segoe UI" : "Segoe UI Semibold", pt);
    }

    // GDI's font for the family's name really is it (not a substitute)
    static bool GdiPicks(FontFamily family)
    {
        using (var f = new Font(family, 10f, FontStyle.Regular))
        {
            IntPtr hf = f.ToHfont(), dc = CreateCompatibleDC(IntPtr.Zero);
            try
            {
                IntPtr old = SelectObject(dc, hf);
                var sb = new StringBuilder(64);
                GetTextFace(dc, sb.Capacity, sb);
                SelectObject(dc, old);
                return string.Equals(sb.ToString(), family.Name, StringComparison.OrdinalIgnoreCase);
            }
            finally { DeleteDC(dc); DeleteObject(hf); }
        }
    }

    /// <summary>A shared font for a window control (GDI draws it): the chosen family when GDI sees it, else Segoe UI.</summary>
    public static Font ForControls(FontKind k, float pt)
    {
        if (On && !gdi)
        {
            string key = "g" + (int)k + ":" + pt.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            Font f;
            if (!cache.TryGetValue(key, out f)) { f = new Font(k == FontKind.Text ? "Segoe UI" : "Segoe UI Semibold", pt, k == FontKind.Bold ? FontStyle.Bold : FontStyle.Regular); cache[key] = f; }
            return f;
        }
        return Get(k, pt);
    }

    /// <summary>A shared font of the chosen family (never dispose it).</summary>
    public static Font Get(FontKind k, float pt)
    {
        string key = (On ? "i" : "s") + (int)k + ":" + pt.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        Font f;
        if (!cache.TryGetValue(key, out f)) { f = New(k, pt); cache[key] = f; }
        return f;
    }

    /// <summary>The font to draw this text with: Segoe UI of the same size and weight when Inter lacks one of its characters.</summary>
    public static Font For(Font f, string text)
    {
        if (inter == null || f == null || !interNames.Contains(f.Name) || cover.Covers(text)) return f;
        return fallback.GetValue(f, x => new Font(x.Bold || x.Name == interSemi.Name ? "Segoe UI Semibold" : "Segoe UI", x.Size));
    }

    /// <summary>Pixels to move text drawn in the fallback font down so its baseline meets the asked font's.</summary>
    public static int Shift(Font asked, Font used)
    {
        if (ReferenceEquals(asked, used)) return 0;
        return (int)Math.Round(Ascent(asked) - Ascent(used));
    }

    static float Ascent(Font f)
    {
        var st = f.Style & (FontStyle.Bold | FontStyle.Italic);
        return f.FontFamily.GetCellAscent(st) * f.SizeInPoints / 72f * 96f / f.FontFamily.GetEmHeight(st);
    }

    public static bool Covers(string text) { return cover == null || cover.Covers(text); }
}
