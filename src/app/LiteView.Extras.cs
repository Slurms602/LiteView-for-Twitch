// Smaller conveniences (P4/P5): keyboard shortcuts; a tray notification when a favourite goes live (opt-in, with
// quiet hours); settings export / import / reset; and a safe update check (it only tells you and opens the release
// page; nothing is downloaded or replaced). Decisions with tests are in src/core/Extras.cs.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using LiteViewCore;

partial class LiteView
{
    // ---------- keyboard ----------

    [DllImport("user32.dll")] static extern IntPtr GetFocus();
    static bool Typing { get { return Control.FromChildHandle(GetFocus()) is TextBoxBase; } }

    public const string Shortcuts =
        "Ctrl+L  Go to channel (search)\n" +
        "Ctrl+F  Search (on the Following page)\n" +
        "F5  Refresh the Following page / restart the stream\n" +
        "Alt+Left or Backspace  Back to the Following page\n" +
        "F11  Fullscreen (Esc leaves it)\n" +
        "M  Mute\n" +
        "Ctrl+Up / Ctrl+Down  Volume\n" +
        "C  Show or hide the chat\n\n" +
        "On the Following page: arrow keys choose, Enter watches, the menu key opens the channel menu.\n" +
        "With the video clicked, mpv's keys apply (double-click or F: fullscreen, M: mute, wheel: volume).";

    void InitKeys()
    {
        KeyDown += (s, e) =>   // the form previews keys
        {
            if (e.Handled || e.SuppressKeyPress) return;
            bool typing = Typing, plain = !e.Control && !e.Alt && !e.Shift;
            if (e.Control && e.KeyCode == Keys.L) { if (watching) Browse(); search.Focus(); search.SelectAll(); }
            else if (!watching) return;
            else if (e.KeyCode == Keys.F5 && channel != null) Watch(channel);
            else if (e.Alt && e.KeyCode == Keys.Left || e.KeyCode == Keys.Back && plain && !typing) Browse();
            else if (typing) return;
            else if (e.KeyCode == Keys.M && plain) muteBtn.PerformClick();
            else if (e.KeyCode == Keys.C && plain) chatOn.Checked = !chatOn.Checked;
            else if (e.Control && e.KeyCode == Keys.Up) { volBar.Set(volBar.Value + 5); SaveSettings(); }
            else if (e.Control && e.KeyCode == Keys.Down) { volBar.Set(volBar.Value - 5); SaveSettings(); }
            else return;
            e.Handled = true; e.SuppressKeyPress = true;
        };
    }

    // ---------- favourites going live ----------

    NotifyIcon tray;
    readonly LiveWatch favWatch = new LiveWatch();
    readonly System.Windows.Forms.Timer notifyTimer = new System.Windows.Forms.Timer { Interval = 120000 };
    bool notify; string quiet = "", notifyLogin;

    void InitNotify()
    {
        notifyTimer.Tick += (s, e) => CheckFavourites();
        FormClosed += (s, e) => { notifyTimer.Stop(); if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; } };
        Shown += (s, e) => ApplyNotify();
    }

    // On: a tray icon and a look every 2 minutes, also while minimised (that is the point); off: neither.
    void ApplyNotify()
    {
        notifyTimer.Enabled = notify;
        if (notify && tray == null)
        {
            tray = new NotifyIcon { Icon = Icon ?? SystemIcons.Application, Text = "LiteView - notifies when favourites go live", Visible = true };
            tray.BalloonTipClicked += (s, e) => { ShowWindow(); if (notifyLogin != null) Watch(notifyLogin); };
            tray.DoubleClick += (s, e) => ShowWindow();
            var m = new ContextMenuStrip(); DarkMenuColors.Apply(m); m.ShowImageMargin = false;
            m.Items.Add("Open LiteView", null, (s, e) => ShowWindow());
            m.Items.Add("Turn off live notifications", null, (s, e) => { notify = false; ApplyNotify(); SaveSettings(); });
            tray.ContextMenuStrip = m;
        }
        else if (!notify && tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
        if (!notify) favWatch.Reset();
        else CheckFavourites();
    }

    void ShowWindow()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    async void CheckFavourites()
    {
        if (!notify || Tw.Token == null || browse.Favourites.Count == 0) return;
        List<LiveStream> live;
        try { live = await new Following(q => Tw.Helix(q)).LiveNow(browse.Favourites.Items); }
        catch (Exception ex) { Log.Warn("favourites: " + ex.GetType().Name); return; }
        if (live == null || !notify || tray == null) return;   // unreachable: keep what we knew
        var started = favWatch.Update(live.Select(s => s.Login));
        if (watching) started.Remove(channel);
        if (started.Count == 0 || QuietHours.IsQuiet(quiet, DateTime.Now)) return;
        var first = live.First(s => s.Login == started[0]);
        notifyLogin = first.Login;
        if (started.Count == 1)
            tray.ShowBalloonTip(10000, first.Name + " is live", (first.Game.Length > 0 ? first.Game + " - " : "") + first.Title, ToolTipIcon.None);
        else
            tray.ShowBalloonTip(10000, started.Count + " favourites went live", string.Join(", ", started.Select(l => live.First(s => s.Login == l).Name)) + "\nClick to watch " + first.Name, ToolTipIcon.None);
        Log.Info("notified: " + started.Count + " favourite(s) live");
    }

    // ---------- settings: export, import, reset ----------

    void ExportSettings()
    {
        SaveSettings();
        using (var d = new SaveFileDialog { Filter = "LiteView settings (*.ini)|*.ini", FileName = "liteview-settings.ini", OverwritePrompt = true })
        {
            if (d.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                // the settings file holds no login (that is liteview.token, encrypted for this Windows user)
                File.Copy(iniPath, d.FileName, true);
                status.ForeColor = Dim; status.Text = "Settings exported to " + Path.GetFileName(d.FileName);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { ShowProblem("Could not export the settings", ex.Message, ExportSettings); }
        }
    }

    void ImportSettings()
    {
        string path;
        using (var d = new OpenFileDialog { Filter = "LiteView settings (*.ini)|*.ini|All files (*.*)|*.*" })
        {
            if (d.ShowDialog(this) != DialogResult.OK) return;
            path = d.FileName;
        }
        try
        {
            if (new FileInfo(path).Length > 1024 * 1024) { ShowProblem("Not a LiteView settings file", "The file is larger than 1 MB.", null); return; }
            bool unused;
            var found = SettingsImport.Filter(IniStore.Read(path, out unused));
            if (found.Count == 0) { ShowProblem("No LiteView settings in that file", path, null); return; }
            if (MessageBox.Show(this, "Use " + found.Count + " settings from " + Path.GetFileName(path) + "?\n\nLiteView restarts to apply them. Your login is not affected.",
                "Import settings", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            SaveSettings();
            var merged = IniStore.Read(iniPath, out unused);
            foreach (var kv in found) merged[kv.Key] = kv.Value;
            IniStore.Write(iniPath, "LiteView for Twitch (saved automatically)", merged);
            Log.Info("settings imported: " + found.Count + " values");
            RestartWithoutSaving();
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { ShowProblem("Could not import the settings", ex.Message, null); }
    }

    void ResetLists()
    {
        if (MessageBox.Show(this, "Forget your favourites, recently watched channels and recent searches?", "Reset", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        browse.Favourites.Clear();
        browse.ClearHistory();   // also saves and redraws
    }

    void ResetWindow()
    {
        if (fullscreen) ToggleFullscreen();
        WindowState = FormWindowState.Normal;
        var wa = Screen.FromControl(this).WorkingArea;
        int w = Math.Min(1296, wa.Width), h = Math.Min(760, wa.Height);
        Bounds = new Rectangle(wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - h) / 2, w, h);
        FitWindow();
        SaveSettings();
    }

    void ResetAll()
    {
        if (MessageBox.Show(this, "Reset every LiteView setting to its default and restart?\n\nYour Twitch login is kept (Log out for that).",
            "Reset all settings", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        try { File.Delete(iniPath); File.Delete(iniPath + ".bak"); }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { ShowProblem("Could not reset the settings", ex.Message, null); return; }
        Log.Info("settings reset");
        RestartWithoutSaving();
    }

    void RestartWithoutSaving()
    {
        settingsLoading = true;   // SaveSettings does nothing from here on: closing must not write the old values back
        Application.Restart();
    }

    // ---------- updates ----------

    const string UpdateRepo = "Slurms602/LiteView-for-Twitch";   // releases of this fork (docs/DEVELOPMENT.md: release workflow)
    bool autoUpdates; string updateChecked = "";

    void InitUpdates()
    {
        Shown += (s, e) =>
        {
            DateTime last;
            bool due = !DateTime.TryParse(updateChecked, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out last)
                || (DateTime.UtcNow - last.ToUniversalTime()).TotalHours >= 24;
            if (autoUpdates && due) CheckForUpdates(false);
        };
    }

    async void CheckForUpdates(bool asked)
    {
        string current = Application.ProductVersion;
        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 1024 * 1024 })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("LiteView-Enhanced/" + Versions.Parse(current));
                http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                using (var r = await http.GetAsync("https://api.github.com/repos/" + UpdateRepo + "/releases/latest"))
                {
                    updateChecked = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture); SaveSettings();
                    if (r.StatusCode == HttpStatusCode.NotFound) { if (asked) MessageBox.Show(this, "No LiteView Enhanced release has been published yet.", "Updates"); return; }
                    if (!r.IsSuccessStatusCode) throw new HttpRequestException("GitHub answered " + (int)r.StatusCode);
                    var j = Json.Parse(await r.Content.ReadAsStringAsync());
                    string tag = Json.S(j, "tag_name");
                    Uri page = SafeUrl.ForBrowser(Json.S(j, "html_url"));
                    if (Versions.IsNewer(tag, current) && page != null && page.Host == "github.com")
                    {
                        Log.Info("update available: " + tag);
                        if (MessageBox.Show(this, "LiteView Enhanced " + tag + " is available (you have " + Versions.Parse(current) + ").\n\nOpen the release page? "
                            + "Download the zip there and compare its SHA-256 with the .sha256 file next to it.", "Update available", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                            Launcher.Open(page);
                    }
                    else if (asked) MessageBox.Show(this, "You have the latest version (" + Versions.Parse(current) + ").", "Updates");
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is IOException)
        {
            Log.Warn("update check: " + ex.Message);
            if (asked) ShowProblem("Could not check for updates", ex.Message, () => CheckForUpdates(true));
        }
    }
}
