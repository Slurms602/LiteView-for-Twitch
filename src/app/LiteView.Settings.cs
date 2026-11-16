// The Settings window's pages (src/app/SettingsWindow.cs), made from the same menu builders as the menus, and the menus
// that are new with it: Appearance (themes, src/app/Theme.cs), Extra windows (src/app/ExtraPlayer.cs) and Back up and
// reset.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LiteViewCore;

partial class LiteView
{
    SettingsWindow settingsWin;
    string themeSaved;   // the theme for the next start (the one in use until then: Theme.Name)

    void OpenSettings(string page)
    {
        if (settingsWin != null && !settingsWin.IsDisposed) { settingsWin.Activate(); if (page != null) settingsWin.FixtureShow(page); return; }
        settingsWin = new SettingsWindow(SettingsPages, page) { Icon = Icon };
        settingsWin.FormClosed += (s, e) => settingsWin = null;
        settingsWin.Show(this);
        Log.Info("settings: open");
    }

    static KeyValuePair<string, List<ToolStripItem>> Page(string name, IEnumerable<ToolStripItem> items) { return new KeyValuePair<string, List<ToolStripItem>>(name, items.ToList()); }
    static IEnumerable<ToolStripItem> Children(ToolStripMenuItem m) { return m.DropDownItems.Cast<ToolStripItem>().ToList(); }

    List<KeyValuePair<string, List<ToolStripItem>>> SettingsPages()
    {
        var chatKind = new List<ToolStripItem>();
        var native = new ToolStripMenuItem("LiteView's chat (emotes from 7TV, BetterTTV and FFZ)") { Checked = !webChat };
        native.Click += (s, e) => { if (webChat) SetWebChat(false); };
        var web = new ToolStripMenuItem("Twitch web chat") { Checked = webChat };
        web.Click += (s, e) => { if (!webChat) SetWebChat(true); };
        chatKind.Add(native); chatKind.Add(web); chatKind.Add(new ToolStripSeparator());

        var following = new List<ToolStripItem>();
        var pn = new ToolStripMenuItem("LiteView's own Following page (recommended)") { Checked = nativeBrowse };
        pn.Click += (s, e) => SetNativeBrowse(true);
        var pw = new ToolStripMenuItem("Twitch website") { Checked = !nativeBrowse };
        pw.Click += (s, e) => SetNativeBrowse(false);
        following.AddRange(new ToolStripItem[] { pn, pw, new ToolStripSeparator(),
            Toggle("Remember watched channels", browse.RememberRecent, v => { browse.RememberRecent = v; if (!v) browse.Recent.Clear(); }),
            Toggle("Remember searches", browse.RememberSearches, v => { browse.RememberSearches = v; if (!v) browse.Searches.Clear(); }),
            Toggle("Favourites first", browse.FavouritesFirst, v => { browse.FavouritesFirst = v; browse.Reload(); }) });
        var clear = new ToolStripMenuItem("Clear watched channels and searches") { Enabled = browse.Recent.Count + browse.Searches.Count > 0 };
        clear.Click += (s, e) => browse.ClearHistory();
        following.Add(new ToolStripSeparator()); following.Add(clear);

        var notes = new List<ToolStripItem>();
        var live = Toggle("Notify me when a favourite goes live", notify, v => { notify = v; ApplyNotify(); });
        live.ToolTipText = "Favourites: right-click a channel on the Following page. Checked every 2 minutes, also while minimised.";
        var quietMenu = new ToolStripMenuItem("Quiet hours (no notifications)");
        foreach (var q in new[] { "", "22-8", "23-8", "0-8", "23-10" })
        {
            string v = q; int f, t;
            var it = new ToolStripMenuItem(v.Length == 0 ? "None" : QuietHours.TryParse(v, out f, out t) ? f + ":00 to " + t + ":00" : v) { Checked = quiet == v, Enabled = notify };
            it.Click += (s, e) => { quiet = v; SaveSettings(); };
            quietMenu.DropDownItems.Add(it);
        }
        notes.Add(live); notes.Add(new ToolStripSeparator()); notes.Add(quietMenu);

        var account = new List<ToolStripItem>();
        var login = new ToolStripMenuItem(Tw.Token == null ? "Log in with Twitch" : "Log out (" + (Tw.Login ?? "Twitch") + ")");
        login.Click += (s, e) => { if (Tw.Token == null) Login(); else { Tw.LogOut(); OnLogin(); } };
        var site = new ToolStripMenuItem("Sign out of twitch.tv in LiteView");
        site.Click += (s, e) => SignOutOfWebsite();
        var upd = new ToolStripMenuItem("Check for updates...");
        upd.Click += (s, e) => CheckForUpdates(true);
        var autoUpd = Toggle("Check for updates once a day", autoUpdates, v => autoUpdates = v);
        autoUpd.ToolTipText = "Asks GitHub for the latest release of this fork. Nothing is downloaded or installed by itself.";
        var diag = new ToolStripMenuItem("Export diagnostics...");
        diag.Click += (s, e) => ExportDiagnostics();
        var logs = new ToolStripMenuItem("Open log folder");
        logs.Click += (s, e) => OpenLogFolder();
        var keys = new ToolStripMenuItem("Keyboard shortcuts");
        keys.Click += (s, e) => MessageBox.Show(this, Shortcuts, "Keyboard shortcuts");
        account.AddRange(new ToolStripItem[] { login, site, new ToolStripSeparator(), upd, autoUpd, new ToolStripSeparator(), keys, diag, logs });

        return new List<KeyValuePair<string, List<ToolStripItem>>>
        {
            Page("Playback", Children(PlaybackMenu())),
            Page("Chat", chatKind.Concat(ChatMenuItems())),
            Page("Text and font", Children(TextSizeMenu())),
            Page("Appearance", Children(AppearanceMenu())),
            Page("Following page", following),
            Page("Notifications", notes),
            Page("Start and tray", Children(StartMenu())),
            Page("Extra windows", Children(ExtraWindowsMenu())),
            Page("Performance", Children(PerformanceMenu())),
            Page("Account and help", account),
            Page("Back up and reset", BackupItems()),
        };
    }

    List<ToolStripItem> BackupItems()
    {
        var export = new ToolStripMenuItem("Export settings...");
        export.Click += (s, e) => ExportSettings();
        var import = new ToolStripMenuItem("Import settings...");
        import.Click += (s, e) => ImportSettings();
        var lists = new ToolStripMenuItem("Reset favourites, watched channels and searches...");
        lists.Click += (s, e) => ResetLists();
        var win = new ToolStripMenuItem("Reset window size and position");
        win.Click += (s, e) => ResetWindow();
        var all = new ToolStripMenuItem("Reset all settings...");
        all.Click += (s, e) => ResetAll();
        return new List<ToolStripItem> { export, import, new ToolStripSeparator(), lists, win, all };
    }

    ToolStripMenuItem AppearanceMenu()
    {
        return Theme.Menu(() => themeSaved ?? Theme.Name, ChooseTheme);
    }

    void ChooseTheme(string name)
    {
        if ((themeSaved ?? Theme.Name) == name) return;
        themeSaved = name;
        SaveSettings();
        Log.Info("theme: " + name + " from the next start");
        if (Fixture.On) return;
        if (MessageBox.Show(this, "The theme " + Theme.Title(name) + " applies when LiteView starts again. Restart now?", "Appearance",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) Application.Restart();
    }

    // the extra windows' shared choices (each open window keeps its own until it closes)
    ToolStripMenuItem ExtraWindowsMenu()
    {
        var m = new ToolStripMenuItem("Extra windows");
        var note = new ToolStripMenuItem("Right-click a channel > Watch in a new window. New windows start like this:") { Enabled = false, Tag = "label" };
        var top = Toggle("On top of other windows", popOptions.OnTop, v => popOptions.OnTop = v);
        var chat = Toggle("With the chat", popOptions.Chat, v => popOptions.Chat = v);
        var fit = Toggle("Keep the video's shape (no black bars)", popOptions.Fit, v => popOptions.Fit = v);
        fit.ToolTipText = "Resizing keeps the video at 16:9. Off: any size, with black bars where it does not fit.";
        var q = new ToolStripMenuItem("Quality");
        foreach (var name in PopOptions.Qualities)
        {
            string k = name;
            var it = new ToolStripMenuItem(PopOptions.QualityName(name) + (name == PopOptions.DefaultQuality ? "  (default)" : "")) { Checked = popOptions.Quality == name };
            it.Click += (s, e) => { popOptions.Quality = k; SaveSettings(); };
            q.DropDownItems.Add(it);
        }
        DarkMenuColors.Apply(q.DropDown);
        m.DropDownItems.AddRange(new ToolStripItem[] { note, top, chat, fit, new ToolStripSeparator(), q });
        DarkMenuColors.Apply(m.DropDown);
        return m;
    }

    // UI fixture (CI), scene "settings": the window on its Playback page
    void SettingsScene()
    {
        var t = new Timer { Interval = 1500 };
        t.Tick += (s, e) => { t.Stop(); t.Dispose(); OpenSettings("Playback"); };
        t.Start();
    }
}
