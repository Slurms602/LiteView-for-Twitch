// Monitors and graphics cards (src/core/Display.cs has the decisions):
// - fullscreen follows the monitor: moved with Win+Shift+Arrow, a resolution change or a monitor unplugged while
//   fullscreen, it fills the screen it is on (UI-2); leaving fullscreen never puts the window on a monitor that is gone;
// - the RTX box only shows with an NVIDIA RTX card (EXT-6; upstream: any NVIDIA driver).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LiteViewCore;

partial class LiteView
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string device, uint n, ref DISPLAY_DEVICE dd, uint flags);

    static List<string> GraphicsAdapters()
    {
        var names = new List<string>();
        try
        {
            for (uint i = 0; i < 32; i++)
            {
                var d = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
                if (!EnumDisplayDevices(null, i, ref d, 0)) break;
                if (!string.IsNullOrEmpty(d.DeviceString) && !names.Contains(d.DeviceString)) names.Add(d.DeviceString);
            }
        }
        catch (Exception) { }   // no answer: no RTX option, nothing else depends on it
        return names;
    }

    static bool HasRtxCard()
    {
        return File.Exists(Path.Combine(Environment.SystemDirectory, "nvapi64.dll")) && Display.HasRtx(GraphicsAdapters());
    }

    bool fitting;

    void InitScreens()
    {
        Move += (s, e) => KeepFullscreen();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        FormClosed += (s, e) => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
    }

    void OnDisplayChanged(object sender, EventArgs e)
    {
        if (IsDisposed) return;
        try { BeginInvoke((Action)(() => { Log.Info("display settings changed"); KeepFullscreen(); })); } catch (InvalidOperationException) { }
    }

    // fullscreen: fill exactly the screen the window is (mostly) on
    void KeepFullscreen()
    {
        if (!fullscreen || fitting || WindowState == FormWindowState.Minimized) return;
        var b = Screen.FromControl(this).Bounds;
        if (Bounds == b) return;
        fitting = true;
        try { Bounds = b; } finally { fitting = false; }
        Relayout();
    }

    // leaving fullscreen: the saved place, unless that monitor is gone
    Rectangle RestoreTarget(Rectangle saved)
    {
        var areas = Screen.AllScreens.Select(s => ToBox(s.WorkingArea)).ToList();
        var r = Display.Restore(ToBox(saved), areas, ToBox(Screen.FromControl(this).WorkingArea));
        return new Rectangle(r.X, r.Y, r.W, r.H);
    }

    static Box ToBox(Rectangle r) { return new Box(r.X, r.Y, r.Width, r.Height); }
}
