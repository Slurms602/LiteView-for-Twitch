// Native Following page wiring (P2): which page shows while browsing (LiteView's own, or Twitch's website as the
// fallback), the top bar's search box driving the page's search, the app menu (top right) and the saved settings
// of the page. Hooks in src/liteview.cs: InitBrowse (constructor), ShowBrowse (SetMode), BrowseNative (Browse),
// browse.Watched (Watch), browse.LoginChanged (OnLogin), LoadForkSettings / ForkSettings (settings).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using LiteViewCore;
using SortOrder = LiteViewCore.SortOrder;   // not System.Windows.Forms.SortOrder

partial class LiteView
{
    readonly BrowsePage browse = new BrowsePage();
    readonly Button menuBtn = new Button();
    readonly ContextMenuStrip appMenu = new ContextMenuStrip();
    bool nativeBrowse = true, openLast;

    void InitBrowse()
    {
        browse.Dock = DockStyle.Fill; browse.Visible = !watching && nativeBrowse;
        body.Controls.Add(browse); browse.BringToFront();
        browse.Pick += n => { search.Text = ""; Watch(n); };
        browse.LoginRequested += Login;
        browse.UseWebsite += () => SetNativeBrowse(false);
        browse.Changed += SaveSettings;
        browse.SearchWanted += () => { search.Focus(); search.SelectAll(); };
        search.TextChanged += (s, e) => { if (!watching) browse.SetQuery(search.Text); };
        search.Enter += (s, e) => { if (!watching && search.Text.Length == 0) browse.ShowHistory(); };
        search.PreviewKeyDown += (s, e) =>   // before upstream's KeyDown, which opens the channel and clears the box
        {
            if (e.KeyCode != Keys.Enter) return;
            string n = ChannelName.Normalize(search.Text);
            if (n != null) browse.Searched(n);
        };
        search.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Escape) { search.Text = ""; e.SuppressKeyPress = true; if (browse.Visible) browse.Focus(); }
            else if (e.KeyCode == Keys.Down && browse.Visible) { browse.Focus(); e.SuppressKeyPress = true; }
        };
        KeyDown += (s, e) =>   // the form previews keys
        {
            if (watching || !nativeBrowse) return;
            if (e.Control && e.KeyCode == Keys.F) { search.Focus(); search.SelectAll(); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.F5) { browse.Reload(); e.SuppressKeyPress = true; }
        };

        Flat(menuBtn, ((char)0xE712).ToString()); menuBtn.Font = new Font("Segoe MDL2 Assets", 11f); menuBtn.Width = 34;
        menuBtn.Click += (s, e) => { BuildAppMenu(); appMenu.Show(menuBtn, new Point(menuBtn.Width, menuBtn.Height), ToolStripDropDownDirection.BelowLeft); };
        new ToolTip().SetToolTip(menuBtn, "Menu");
        DarkMenuColors.Apply(appMenu); appMenu.ShowImageMargin = false;
        bar.Controls.Add(menuBtn);
        LayoutBar();

        if (!nativeBrowse) InitWeb();
        InitKeys(); InitNotify(); InitUpdates();   // src/app/LiteView.Extras.cs
        InitChatPrefs();
        Shown += (s, e) => { if (startChannel == null && openLast && browse.Recent.Count > 0) Watch(browse.Recent.Items[0]); };
        if (Fixture.On) Shown += (s, e) =>
        {
            Fixture.Scene(this, browse, search);
            if (Fixture.SceneName == "chat")
            {   // the watch view with synthetic chat (no video in fixture mode), timestamps and a highlight word on
                ChatPrefs.Timestamps = true; ChatPrefs.Filter.SetWords("pog");
                Watch("nightowl_plays");
                stressRate = 15; StartStress();
                return;
            }
            if (Fixture.SceneName != "problem") return;
            ShowProblem("Could not open the stream: Unable to open URL: https://usher.ttvnw.net/api/channel/hls/x.m3u8  -  trying again in 4 s",
                "error: Unable to open URL: https://usher.ttvnw.net/api/channel/hls/<channel>.m3u8 (HTTPSConnectionPool(host='usher.ttvnw.net', port=443): Read timed out.)\n\n" +
                "Last lines of streamlink.log:\n[cli][info] Found matching plugin twitch for URL twitch.tv/<channel>\n[cli][error] Unable to open URL (made-up example)", () => { });
            ShowProblemDetails();
        };
    }

    // SetMode: browsing (true) or watching (false)
    void ShowBrowse(bool browsing)
    {
        browse.Visible = browsing && nativeBrowse;
        if (web != null) web.Visible = browsing && !nativeBrowse;
        browse.Run(browsing && nativeBrowse);
        if (browsing && nativeBrowse) browse.SetQuery(search.Text);
    }

    // Browse: true when the native page is the Following page (the website page is then not created at all)
    bool BrowseNative()
    {
        if (!nativeBrowse) return false;
        status.Text = "";
        browse.Focus();
        return true;
    }

    void SetNativeBrowse(bool native)
    {
        if (native == nativeBrowse) return;
        nativeBrowse = native;
        if (native) DropWeb();   // the browser closes: the native page needs none
        else if (!watching && web == null) InitWeb();
        if (!watching) ShowBrowse(true);
        SaveSettings();
    }

    void BuildAppMenu()
    {
        appMenu.Items.Clear();
        var login = new ToolStripMenuItem(Tw.Token == null ? "Log in with Twitch" : "Log out (" + (Tw.Login ?? "Twitch") + ")");
        login.Click += (s, e) => { if (Tw.Token == null) Login(); else { Tw.LogOut(); OnLogin(); } };

        var page = new ToolStripMenuItem("Following page");
        var pn = new ToolStripMenuItem("LiteView's own (recommended)") { Checked = nativeBrowse };
        pn.Click += (s, e) => SetNativeBrowse(true);
        var pw = new ToolStripMenuItem("Twitch website") { Checked = !nativeBrowse };
        pw.Click += (s, e) => SetNativeBrowse(false);
        page.DropDownItems.AddRange(new ToolStripItem[] { pn, pw });
        DarkMenuColors.Apply(page.DropDown);

        var last = Toggle("Open the last channel at start", openLast, v => openLast = v);
        var rec = Toggle("Remember watched channels", browse.RememberRecent, v => { browse.RememberRecent = v; if (!v) browse.Recent.Clear(); });
        var srch = Toggle("Remember searches", browse.RememberSearches, v => { browse.RememberSearches = v; if (!v) browse.Searches.Clear(); });
        var clear = new ToolStripMenuItem("Clear watched channels and searches") { Enabled = browse.Recent.Count + browse.Searches.Count > 0 };
        clear.Click += (s, e) => browse.ClearHistory();

        var live = Toggle("Notify me when a favourite goes live", notify, v => { notify = v; ApplyNotify(); });
        live.ToolTipText = "Favourites: right-click a channel on the Following page. Checked every 2 minutes, also while minimised.";
        var quietMenu = new ToolStripMenuItem("Quiet hours") { Enabled = notify };
        foreach (var q in new[] { "", "22-8", "23-8", "0-8", "23-10" })
        {
            string v = q;
            int f, t;
            var it = new ToolStripMenuItem(v.Length == 0 ? "None" : QuietHours.TryParse(v, out f, out t) ? f + ":00 to " + t + ":00" : v) { Checked = quiet == v };
            it.Click += (s, e) => { quiet = v; SaveSettings(); };
            quietMenu.DropDownItems.Add(it);
        }
        DarkMenuColors.Apply(quietMenu.DropDown);

        var settings = new ToolStripMenuItem("Settings");
        settings.DropDownItems.Add("Export settings...", null, (s, e) => ExportSettings());
        settings.DropDownItems.Add("Import settings...", null, (s, e) => ImportSettings());
        settings.DropDownItems.Add(new ToolStripSeparator());
        settings.DropDownItems.Add("Reset favourites, watched channels and searches...", null, (s, e) => ResetLists());
        settings.DropDownItems.Add("Reset window size and position", null, (s, e) => ResetWindow());
        settings.DropDownItems.Add("Reset all settings...", null, (s, e) => ResetAll());
        DarkMenuColors.Apply(settings.DropDown);

        var upd = new ToolStripMenuItem("Check for updates...");
        upd.Click += (s, e) => CheckForUpdates(true);
        var autoUpd = Toggle("Check for updates once a day", autoUpdates, v => autoUpdates = v);
        autoUpd.ToolTipText = "Asks GitHub for the latest release of this fork. Nothing is downloaded or installed by itself.";
        var keys = new ToolStripMenuItem("Keyboard shortcuts");
        keys.Click += (s, e) => MessageBox.Show(this, Shortcuts, "Keyboard shortcuts");

        appMenu.ShowItemToolTips = true;
        appMenu.Items.AddRange(new ToolStripItem[] { login, new ToolStripSeparator(), page, last, new ToolStripSeparator(), live, quietMenu,
            new ToolStripSeparator(), rec, srch, clear, new ToolStripSeparator(), settings, upd, autoUpd, keys });
        AddForkMenuItems(appMenu);
    }

    ToolStripMenuItem Toggle(string text, bool on, Action<bool> set)
    {
        var it = new ToolStripMenuItem(text) { Checked = on };
        it.Click += (s, e) => { set(!on); SaveSettings(); if (!watching) browse.Invalidate(); };
        return it;
    }

    // ---------- settings (liteview.ini) ----------

    IEnumerable<KeyValuePair<string, string>> ForkSettings()
    {
        yield return Kv("following", nativeBrowse ? "native" : "web");
        yield return Kv("sort", browse.Order.ToString().ToLowerInvariant());
        yield return Kv("favfirst", browse.FavouritesFirst ? "1" : "0");
        yield return Kv("favorites", browse.Favourites.Save());
        yield return Kv("keeprecent", browse.RememberRecent ? "1" : "0");
        yield return Kv("recent", browse.RememberRecent ? browse.Recent.Save() : "");
        yield return Kv("keepsearches", browse.RememberSearches ? "1" : "0");
        yield return Kv("searches", browse.RememberSearches ? browse.Searches.Save() : "");
        yield return Kv("openlast", openLast ? "1" : "0");
        yield return Kv("notify", notify ? "1" : "0");
        yield return Kv("quiet", quiet);
        yield return Kv("updates", autoUpdates ? "1" : "0");
        yield return Kv("updatechecked", updateChecked);
        foreach (var kv in ChatSettings()) yield return kv;
    }

    void LoadForkSettings(Dictionary<string, string> ini)
    {
        nativeBrowse = Native.Get(ini, "following", "native") != "web";
        SortOrder o;
        browse.Order = Enum.TryParse(Native.Get(ini, "sort", "viewers"), true, out o) && Enum.IsDefined(typeof(SortOrder), o) ? o : SortOrder.Viewers;
        browse.FavouritesFirst = Native.Get(ini, "favfirst", "1") == "1";
        browse.Favourites.Load(Native.Get(ini, "favorites", ""));
        browse.RememberRecent = Native.Get(ini, "keeprecent", "1") == "1";
        browse.RememberSearches = Native.Get(ini, "keepsearches", "1") == "1";
        browse.Recent.Load(browse.RememberRecent ? Native.Get(ini, "recent", "") : "");
        browse.Searches.Load(browse.RememberSearches ? Native.Get(ini, "searches", "") : "");
        openLast = Native.Get(ini, "openlast", "0") == "1";
        notify = Native.Get(ini, "notify", "0") == "1";
        int qf, qt; string q = Native.Get(ini, "quiet", "");
        quiet = QuietHours.TryParse(q, out qf, out qt) ? q : "";
        autoUpdates = Native.Get(ini, "updates", "0") == "1";
        updateChecked = Native.Get(ini, "updatechecked", "");
        LoadChatSettings(ini);
    }

    static KeyValuePair<string, string> Kv(string k, string v) { return new KeyValuePair<string, string>(k, v); }
}
