// Start and tray (P5): start with Windows (the user's Run key, no administrator rights; the entry is only ever this
// LiteView.exe with --autostart), start minimised, and minimise to the tray (the window leaves the taskbar; the tray
// icon brings it back). All off by default. Whether LiteView starts with Windows is read from the registry, not from
// liteview.ini, so an imported settings file can never switch it on; moving the folder shows it as off.
using System;
using System.Windows.Forms;
using LiteViewCore;
using Microsoft.Win32;

partial class LiteView
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "LiteView Enhanced";
    bool startMin, toTray;
    FormWindowState shownState = FormWindowState.Normal;   // the state before minimising: what the tray icon restores

    static string RunCommand() { return "\"" + Application.ExecutablePath + "\" --autostart"; }

    static bool StartsWithWindows()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && string.Equals(k.GetValue(RunName) as string, RunCommand(), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is System.IO.IOException) { return false; }
    }

    static bool SetStartWithWindows(bool on)
    {
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(RunName, RunCommand(), RegistryValueKind.String);
                else if (k.GetValue(RunName) != null) k.DeleteValue(RunName, false);
            }
            Log.Info("start with Windows: " + (on ? "on" : "off"));
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is System.IO.IOException)
        {
            Log.Warn("start with Windows could not be changed: " + ex.GetType().Name);
            return false;
        }
    }

    void InitStartup()
    {
        Load += (s, e) =>
        {
            if (!startMin || Fixture.On) return;
            shownState = WindowState;
            WindowState = FormWindowState.Minimized;
            Log.Info("started minimised" + (toTray ? " to the tray" : "") + (autostarted ? " (by Windows)" : ""));
        };
        Shown += (s, e) => { if (WindowState == FormWindowState.Minimized && toTray) { Hide(); UpdateTray(); } };
        Resize += (s, e) =>
        {
            if (WindowState != FormWindowState.Minimized) shownState = WindowState;
            else if (toTray && Visible) { Hide(); UpdateTray(); }
        };
    }

    ToolStripMenuItem StartMenu()
    {
        var m = new ToolStripMenuItem("Start and tray");
        var win = new ToolStripMenuItem("Start with Windows") { Checked = StartsWithWindows() };
        win.ToolTipText = "Adds LiteView to your Windows sign-in (no administrator rights). Off removes it again.";
        win.Click += (s, e) => SetStartWithWindows(!StartsWithWindows());
        var min = Toggle("Start minimised", startMin, v => startMin = v);
        var tr = Toggle("Minimise to the tray", toTray, v => { toTray = v; UpdateTray(); });
        tr.ToolTipText = "A minimised LiteView leaves the taskbar; click its icon in the tray (by the clock) to bring it back.";
        var last = Toggle("Open the last channel at start", openLast, v => openLast = v);
        m.DropDownItems.AddRange(new ToolStripItem[] { win, min, tr, last });
        DarkMenuColors.Apply(m.DropDown);
        return m;
    }

    // the tray icon: there while live notifications or "minimise to the tray" need it
    void UpdateTray()
    {
        bool need = notify || toTray;
        if (need && tray == null)
        {
            tray = new NotifyIcon { Icon = Icon ?? System.Drawing.SystemIcons.Application, Visible = true };
            tray.BalloonTipClicked += (s, e) => { ShowWindow(); if (notifyLogin != null) Watch(notifyLogin); };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
            var menu = new ContextMenuStrip(); DarkMenuColors.Apply(menu); menu.ShowImageMargin = false;
            menu.Opening += (s, e) =>
            {
                menu.Items.Clear();
                menu.Items.Add("Open LiteView", null, (s2, e2) => ShowWindow());
                if (notify) menu.Items.Add("Turn off live notifications", null, (s2, e2) => { notify = false; ApplyNotify(); SaveSettings(); });
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("Close LiteView", null, (s2, e2) => Close());
                e.Cancel = false;
            };
            tray.ContextMenuStrip = menu;
        }
        else if (!need && tray != null)
        {
            tray.Visible = false; tray.Dispose(); tray = null;
            if (!Visible) ShowWindow();   // never leave the window hidden without an icon to bring it back
        }
        if (tray != null) tray.Text = notify ? "LiteView - notifies when favourites go live" : "LiteView";
    }

    void ShowWindow()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = shownState == FormWindowState.Maximized ? FormWindowState.Maximized : FormWindowState.Normal;
        Activate();
    }
}
