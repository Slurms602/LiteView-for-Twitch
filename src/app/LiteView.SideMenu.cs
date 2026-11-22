// Right-click on a channel in the followed column while watching (owner's request: the menu was only on the Following
// page): watch it, watch it in a new window, its videos or clips, favourite, its Twitch page. Upstream switched to the
// channel on a right-click too; now only the left button switches.
using System;
using System.Drawing;
using System.Windows.Forms;
using LiteViewCore;

partial class LiteView
{
    readonly ContextMenuStrip sideMenu = new ContextMenuStrip { ShowImageMargin = false };

    void InitSideMenu()
    {
        DarkMenuColors.Apply(sideMenu);
        side.ItemMenu += ShowSideMenu;
    }

    void ShowSideMenu(string login, string name, Point at)
    {
        if (!ChannelName.IsValidLogin(login)) return;
        sideMenu.Items.Clear();
        bool current = login == channel && currentVideo == null;
        var watch = new ToolStripMenuItem(current ? "Watching " + name : "Watch " + name) { Enabled = !current };
        watch.Font = new Font(sideMenu.Font, FontStyle.Bold);
        watch.Click += (s, e) => Watch(login);
        var win = new ToolStripMenuItem("Watch in a new window");
        win.Click += (s, e) => OpenWindow(login, name);
        var vids = new ToolStripMenuItem("Videos (past broadcasts)");
        vids.Click += (s, e) => { Browse(); browse.ShowVideos(login, name, false); };
        var clips = new ToolStripMenuItem("Clips");
        clips.Click += (s, e) => { Browse(); browse.ShowVideos(login, name, true); };
        bool fav = browse.Favourites.Contains(login);
        var favItem = new ToolStripMenuItem(fav ? "Remove from favourites" : "Add to favourites");
        favItem.Click += (s, e) => { if (fav) browse.Favourites.Remove(login); else browse.Favourites.Add(login); SaveSettings(); };
        var site = new ToolStripMenuItem("Open on twitch.tv");
        site.Click += (s, e) => Launcher.Open("https://www.twitch.tv/" + login);
        var copy = new ToolStripMenuItem("Copy link");
        copy.Click += (s, e) => { try { Clipboard.SetText("https://www.twitch.tv/" + login); } catch (System.Runtime.InteropServices.ExternalException) { } };
        sideMenu.Items.AddRange(new ToolStripItem[] { watch, win, vids, clips, new ToolStripSeparator(), favItem, site, copy });
        sideMenu.Show(side, at);
        if (Fixture.On) Log.Info("side menu: " + login);
    }

    // UI fixture (CI): a stream, fullscreen and back (the column was gone after that), then a right-click on the first
    // channel in the followed column
    void SideMenuScene()
    {
        Watch("nightowl_plays");
        int waited = 0;
        var t = new Timer { Interval = 500 };
        t.Tick += (s, e) =>
        {
            waited += 500;
            if (waited == 3000) { ToggleFullscreen(); return; }
            if (waited == 4000) { ToggleFullscreen(); return; }
            if (waited < 5500) return;   // the followed column has loaded (made-up answers)
            t.Stop(); t.Dispose();
            Log.Info("side column after fullscreen: " + (side.Visible ? side.Width + " px" : "gone"));
            FixtureClick(side.PointToScreen(new Point(120, 42 + 21)), true);
        };
        t.Start();
    }
}
