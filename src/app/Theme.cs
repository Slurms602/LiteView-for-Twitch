// Themes (menu > Appearance): Dark (Twitch's dark colours, as before), Black (black backgrounds, for OLED screens and
// dark rooms) and Light (Twitch's light colours). Every colour the windows use comes from here; the classes read them
// once when they start (static fields), so a new theme applies when LiteView starts again (the menu offers to). The
// video area stays black in every theme. The choice is read before the first window exists (Main).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LiteViewCore;

static class Theme
{
    public static readonly string[] Names = { "dark", "black", "light" };
    public static string Name { get; private set; }
    public static bool Light { get { return Name == "light"; } }

    /// <summary>The page behind everything; panels (top bar, chat, cards); the side column, menus; hover; lines and selection.</summary>
    public static Color Page, Panel, Side, Hover, Line;
    /// <summary>Text; secondary text; faint text.</summary>
    public static Color Text, Dim, Faint;
    /// <summary>Twitch purple; its pressed shade; purple text (links, a hovered title).</summary>
    public static Color Accent, AccentHot, AccentText;
    /// <summary>The live dot, a favourite's star, an error, a mention's background, a chat notice's background.</summary>
    public static Color Live, Gold, Error, Mention, Notice;
    /// <summary>Text on the video area, which is black in every theme ("Paused", "Opening...").</summary>
    public static readonly Color OnVideo = Color.FromArgb(173, 173, 184);

    static Theme() { Set("dark"); }

    public static string Title(string name) { return name == "black" ? "Black (OLED)" : name == "light" ? "Light" : "Dark (default)"; }

    public static void Set(string name)
    {
        Name = Array.IndexOf(Names, name) >= 0 ? name : "dark";
        Accent = Color.FromArgb(145, 71, 255); AccentHot = Color.FromArgb(119, 44, 232); Live = Color.FromArgb(235, 4, 0);
        switch (Name)
        {
            case "light":
                Page = Color.FromArgb(247, 247, 248); Panel = Color.White; Side = Color.FromArgb(239, 239, 241); Hover = Color.FromArgb(229, 229, 234); Line = Color.FromArgb(214, 214, 220);
                Text = Color.FromArgb(14, 14, 16); Dim = Color.FromArgb(83, 83, 95); Faint = Color.FromArgb(135, 135, 146);
                AccentText = Color.FromArgb(119, 44, 232); Gold = Color.FromArgb(196, 140, 0); Error = Color.FromArgb(200, 20, 20);
                Mention = Color.FromArgb(255, 226, 226); Notice = Color.FromArgb(239, 239, 241);
                break;
            case "black":
                Page = Color.Black; Panel = Color.FromArgb(8, 8, 9); Side = Color.FromArgb(14, 14, 16); Hover = Color.FromArgb(30, 30, 34); Line = Color.FromArgb(46, 46, 52);
                Text = Color.FromArgb(239, 239, 241); Dim = Color.FromArgb(173, 173, 184); Faint = Color.FromArgb(110, 110, 122);
                AccentText = Color.FromArgb(191, 148, 255); Gold = Color.FromArgb(255, 200, 61); Error = Color.FromArgb(255, 140, 140);
                Mention = Color.FromArgb(52, 16, 20); Notice = Color.FromArgb(14, 14, 16);
                break;
            default:
                Page = Color.FromArgb(14, 14, 16); Panel = Color.FromArgb(24, 24, 27); Side = Color.FromArgb(31, 31, 35); Hover = Color.FromArgb(38, 38, 44); Line = Color.FromArgb(53, 53, 59);
                Text = Color.FromArgb(239, 239, 241); Dim = Color.FromArgb(173, 173, 184); Faint = Color.FromArgb(110, 110, 122);
                AccentText = Color.FromArgb(191, 148, 255); Gold = Color.FromArgb(255, 200, 61); Error = Color.FromArgb(255, 140, 140);
                Mention = Color.FromArgb(62, 22, 26); Notice = Color.FromArgb(31, 31, 35);
                break;
        }
    }

    /// <summary>Main: the saved theme (setting "theme"), before any window is made. LITEVIEW_UI_FIXTURE_THEME overrides it in the UI fixture.</summary>
    public static void Load()
    {
        string name = "dark";
        try
        {
            string exe = typeof(Theme).Assembly.Location;
            bool unused;
            var d = IniStore.Read(Path.Combine(Path.GetDirectoryName(exe), Path.GetFileNameWithoutExtension(exe).ToLowerInvariant() + ".ini"), out unused);
            name = Native.Get(d, "theme", "dark");
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        string fx = Environment.GetEnvironmentVariable("LITEVIEW_UI_FIXTURE_THEME");
        if (Fixture.On && !string.IsNullOrEmpty(fx)) name = fx;
        Set(name);
        if (Name != "dark") Log.Info("theme: " + Name);
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr h, string app, string idList);

    /// <summary>A window's title bar in the theme (dark or light frame, the caption in the panel colour; Windows 11, ignored before).</summary>
    public static void Frame(IntPtr handle)
    {
        try
        {
            int dark = Light ? 0 : 1, col = Panel.R | (Panel.G << 8) | (Panel.B << 16);
            DwmSetWindowAttribute(handle, 20, ref dark, 4);
            DwmSetWindowAttribute(handle, 35, ref col, 4);
            DwmSetWindowAttribute(handle, 34, ref col, 4);
        }
        catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
    }

    /// <summary>A scrolling panel's scroll bars in the theme (dark ones in Dark and Black).</summary>
    public static void Scrollbars(Control c)
    {
        try { SetWindowTheme(c.Handle, Light ? "Explorer" : "DarkMode_Explorer", null); } catch (DllNotFoundException) { }
    }

    /// <summary>A combo box's drop-down in the theme.</summary>
    public static void Combo(ComboBox cb)
    {
        cb.HandleCreated += (s, e) => { try { SetWindowTheme(cb.Handle, Light ? "CFD" : "DarkMode_CFD", null); } catch (DllNotFoundException) { } };
    }

    /// <summary>A chat name's colour readable on the chat's background (dark names lighter on dark, light names darker on light).</summary>
    public static Color Readable(Color c)
    {
        double l = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;
        if (!Light)
        {
            if (l < 0.35) { double k = (0.35 - l) / 0.35 * 0.6 + 0.25; c = Color.FromArgb((int)(c.R + (255 - c.R) * k), (int)(c.G + (255 - c.G) * k), (int)(c.B + (255 - c.B) * k)); }
            return c;
        }
        if (l > 0.55) { double k = (l - 0.55) / 0.45 * 0.5 + 0.2; c = Color.FromArgb((int)(c.R * (1 - k)), (int)(c.G * (1 - k)), (int)(c.B * (1 - k))); }
        return c;
    }

    /// <summary>The Appearance menu (and the Settings window's page): a new theme applies at the next start.</summary>
    public static ToolStripMenuItem Menu(Func<string> saved, Action<string> choose)
    {
        var m = new ToolStripMenuItem("Appearance");
        var head = new ToolStripMenuItem("Theme (applies when LiteView starts again)") { Enabled = false, Tag = "label" };
        m.DropDownItems.Add(head);
        foreach (var n in Names)
        {
            string k = n;
            var it = new ToolStripMenuItem(Title(n)) { Checked = saved() == n };
            it.Click += (s, e) => choose(k);
            m.DropDownItems.Add(it);
        }
        DarkMenuColors.Apply(m.DropDown);
        return m;
    }
}
