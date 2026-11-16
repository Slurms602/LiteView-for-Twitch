// The Following page's "Videos and clips" view of one channel (right-click a channel > Videos and clips): its newest
// videos (past broadcasts, highlights, uploads; or one kind of them) or its most viewed clips (of the last week, month,
// year or of all time), as cards, 40 at a time: scrolling to the end loads the next 40 (owner's request: older videos,
// e.g. years of marathons, are still on Twitch but hard to find). A red bar under a video's picture shows how much of it
// was watched (src/core/WatchedVideos.cs). A click plays it (VideoPicked; LiteView plays it with Streamlink like a
// stream, but mpv can seek). Back, Esc or Backspace returns to where you were. Uses the header's back pill, its first two
// tab pills (Videos / Clips), the filter pill (where the Following page has its sort pill) and the refresh button.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LiteViewCore;

partial class BrowsePage
{
    public event Action<VideoInfo> VideoPicked;
    public event Action<string, string> WindowWanted;   // Watch in a new window (src/app/ExtraPlayer.cs)
    public event Action<VideoInfo> VideoFromStart, VideoForget;   // src/app/LiteView.Watched.cs
    public event Action<VideoInfo, string> DownloadWanted;        // a video or clip, a quality (src/app/LiteView.Downloads.cs)
    /// <summary>How much of a video was watched, 0 to 1 (video id, its length): the red bar on its card.</summary>
    public Func<string, int, double> WatchedFraction;
    string vidKind = "all", vidNext; int clipDays = ChannelVideos.ClipDays; DateTime vidMoreAfter;
    DateTimeOffset vidRangeNow;   // the clips' time range is fixed for all pages of one list (a cursor belongs to one query)
    readonly ChannelVideos videoFeed = new ChannelVideos(q => Tw.Helix(q));
    readonly Latest videoGuard = new Latest();
    string vidLogin, vidName; bool vidClips, loadingVids, vidsFailed; List<VideoInfo> vids; int vidScroll, vidSel;
    string vidQuery; bool vidHistory;   // the search (or recent searches) it was opened from: Back returns there

    bool InVideos { get { return vidLogin != null; } }

    /// <summary>Shows a channel's videos (or clips) on the page.</summary>
    public void ShowVideos(string login, string name, bool clips)
    {
        if (!ChannelName.IsValidLogin(login)) return;
        if (!InVideos) { vidScroll = scroll; vidSel = sel; vidQuery = query; vidHistory = history; }
        vidLogin = login; vidName = string.IsNullOrEmpty(name) ? login : name; vidClips = clips; vidNext = null;
        query = null; history = false; vids = null; vidsFailed = false; scroll = 0; sel = -1;
        ReloadVideos();
        Rebuild();
    }

    void CloseVideos()
    {
        if (!InVideos) return;
        vidLogin = null; vids = null; videoGuard.Next(); loadingVids = false;
        string q = vidQuery; history = vidHistory; vidQuery = null; vidHistory = false;
        if (q != null) SetQuery(q);   // the search again (from its cache, or asked again if it was still on its way)
        Rebuild();
        scroll = vidScroll; sel = Math.Min(vidSel, items.Count - 1); ClampScroll();
        Invalidate(); Announce();
    }

    void SetVideoKind(bool clips)
    {
        if (clips == vidClips) return;
        vidClips = clips; vids = null; vidNext = null; vidsFailed = false; scroll = 0; sel = -1;
        ReloadVideos(); Rebuild();
    }

    async void ReloadVideos()
    {
        if (!InVideos || Tw.Token == null) { Rebuild(); return; }
        int t = videoGuard.Next();
        string login = vidLogin, name = vidName; bool clips = vidClips;
        loadingVids = true; Invalidate();
        try
        {
            vidRangeNow = DateTimeOffset.UtcNow;
            var r = clips ? await videoFeed.Clips(login, name, vidRangeNow, clipDays) : await videoFeed.Videos(login, vidKind);
            if (!videoGuard.IsCurrent(t) || IsDisposed) return;   // another channel or kind meanwhile
            vidsFailed = r == null;
            if (r != null) { vids = r.Items; vidNext = r.Next; }
        }
        catch (Exception ex) { Log.Error("videos", ex); }
        finally { if (videoGuard.IsCurrent(t)) loadingVids = false; Rebuild(); }
    }

    // OnPaint: the last card is on screen and Twitch has more: the next 40
    void MaybeMoreVideos(int lastDrawn)
    {
        if (!InVideos || vidNext == null || loadingVids || vids == null || lastDrawn < items.Count - 1 || DateTime.UtcNow < vidMoreAfter) return;
        MoreVideos();
    }

    async void MoreVideos()
    {
        int t = videoGuard.Next();
        string login = vidLogin, name = vidName, after = vidNext; bool clips = vidClips;
        loadingVids = true; Invalidate();
        try
        {
            var r = clips ? await videoFeed.Clips(login, name, vidRangeNow, clipDays, after) : await videoFeed.Videos(login, vidKind, after);
            if (!videoGuard.IsCurrent(t) || IsDisposed || vids == null) return;
            if (r == null) { vidsFailed = true; vidMoreAfter = DateTime.UtcNow.AddSeconds(30); return; }   // the header says so; tried again in 30 s
            var have = new HashSet<string>(vids.Select(v => v.Id));
            int before = vids.Count;
            vids.AddRange(r.Items.Where(v => have.Add(v.Id)));
            vidNext = vids.Count > before && r.Next != after ? r.Next : null;   // a page with nothing new, or the same cursor again: the end
            Log.Info("videos: " + vids.Count + " shown" + (vidNext != null ? ", more on Twitch" : ""));
        }
        catch (Exception ex) { Log.Error("videos", ex); }
        finally { if (videoGuard.IsCurrent(t)) loadingVids = false; Rebuild(); }
    }

    void ShowVideoFilterMenu()
    {
        menu.Items.Clear();
        if (vidClips)
            foreach (int d in ChannelVideos.ClipRanges)
            {
                int k = d;
                var it = new ToolStripMenuItem(ChannelVideos.RangeName(d)) { Checked = clipDays == d };
                it.Click += (s, e) => { if (clipDays == k) return; clipDays = k; vids = null; vidNext = null; scroll = 0; sel = -1; ReloadVideos(); Rebuild(); };
                menu.Items.Add(it);
            }
        else
            foreach (var kind in ChannelVideos.Kinds)
            {
                string k = kind;
                var it = new ToolStripMenuItem(ChannelVideos.KindsName(kind)) { Checked = vidKind == kind };
                it.Click += (s, e) => { if (vidKind == k) return; vidKind = k; vids = null; vidNext = null; scroll = 0; sel = -1; ReloadVideos(); Rebuild(); };
                menu.Items.Add(it);
            }
        menu.Show(this, new Point(sortRect.Left, sortRect.Bottom + S(2)));
    }

    void AddVideoItems(List<Item> list)
    {
        if (vids == null) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var v in vids)
            list.Add(new Item { Login = v.Login, Name = v.Name, Title = v.Title, Game = v.IsClip && v.Creator.Length > 0 ? "Clip by " + v.Creator : VideoData.KindName(v.Kind) + "  -  " + VideoData.Ago(v.Created, now), Card = true, Video = v, Thumb = v.Thumb });
    }

    string VideosMessage()
    {
        if (Tw.Token == null) return "Log in with Twitch to see a channel's videos and clips.";
        if (vids == null) return vidsFailed ? "Could not reach Twitch." : "Loading...";
        if (items.Count > 0) return null;
        return vidClips ? vidName + " has no clips " + (clipDays == 0 ? "" : "from the " + ChannelVideos.RangeName(clipDays).ToLowerInvariant() + " ") + "(the pill on the right: a longer time)."
            : vidName + " has no " + (vidKind == "all" ? "videos" : ChannelVideos.KindsName(vidKind).ToLowerInvariant()) + " (or keeps them for subscribers).";
    }

    void DrawVideosHeader(Graphics g)
    {
        int x = Pad, w = ClientSize.Width;
        string head = vidName;
        const string back = "‹  Back";
        int bw = Txt.Width(back, FTab) + S(24), hw = Math.Min(Txt.Width(head, FHead) + S(4), w - 2 * Pad - bw - S(16));
        Txt.In(g, head, FHead, Fg, new Rectangle(x - 2, S(10), Math.Max(0, hw), S(36)), false);
        backRect = new Rectangle(x + hw + S(12), S(15), bw, S(28));
        Pill(g, backRect, hot == HotBack ? SelC : HoverC);
        Txt.In(g, back, FTab, Fg, backRect, true);
        int y = S(58);
        string[] names = { "Videos", "Clips" };
        for (int i = 0; i < names.Length; i++)
        {
            var r = new Rectangle(x, y, Txt.Width(names[i], FTab) + S(24), S(30));
            tabRects[i] = r;
            bool on = vidClips == (i == 1);
            if (on || hot == HotTab + i) Pill(g, r, on ? SelC : HoverC);
            Txt.In(g, names[i], FTab, on ? Fg : Dim, r, true);
            x = r.Right + S(6);
        }
        refreshRect = new Rectangle(w - Pad - S(30), y, S(30), S(30));
        if (hot == HotRefresh) Pill(g, refreshRect, HoverC);
        TextRenderer.DrawText(g, RefreshGlyph, FIcon, refreshRect, loadingVids ? Accent : Dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        string f = vidClips ? ChannelVideos.RangeName(clipDays) : ChannelVideos.KindsName(vidKind);
        sortRect = MenuPill(g, (vidClips ? "From: " : "Show: ") + f, f, x, y, hot == HotSort, Dim);   // the filter (ShowVideoFilterMenu)
        if (vidsFailed && vids != null) Txt.In(g, "Could not reach Twitch - showing the last list", FText, Gold, new Rectangle(x + S(10), y, Math.Max(0, refreshRect.Left - x - S(20)), S(30)), false);
    }

    void DrawVideo(Graphics g, Item it, Rectangle r, int i)
    {
        var v = it.Video;
        int th = r.Width * 9 / 16;
        var tr = new Rectangle(r.X, r.Y, r.Width, th);
        bool hi = i == hover || i == sel;
        var pic = Thumb(it, tr.Size);
        if (pic != null) g.DrawImage(pic, tr);
        else
        {
            using (var b = new SolidBrush(CardC)) g.FillRectangle(b, tr);
            if (VideoData.Thumb(v.Thumb, 320, 180).Length == 0)   // Twitch has no picture yet (a broadcast still being recorded)
                Txt.In(g, v.Kind == VideoKind.Archive ? "No picture yet (still recording?)" : "No picture", FText, Faint, tr, true);
        }
        double seen = WatchedFraction != null && !v.IsClip ? WatchedFraction(v.Id, v.Seconds) : 0;
        if (seen > 0)
        {   // how much of it was watched: a red bar along the picture's bottom (like Twitch)
            int bh = Math.Max(3, S(4));
            using (var b = new SolidBrush(Color.FromArgb(150, 0, 0, 0))) g.FillRectangle(b, tr.X, tr.Bottom - bh, tr.Width, bh);
            using (var b = new SolidBrush(Red)) g.FillRectangle(b, tr.X, tr.Bottom - bh, Math.Max(2, (int)(tr.Width * seen)), bh);
        }
        if (hi) using (var p = new Pen(i == sel && Focused ? Accent : Color.FromArgb(120, Theme.Accent), S(3))) g.DrawRectangle(p, tr.X + 1, tr.Y + 1, tr.Width - 3, tr.Height - 3);
        string len = VideoData.Length(v.Seconds);
        if (len.Length > 0) Badge(g, len, tr.X + S(8), tr.Y + S(8), false);
        Badge(g, Fmt.Viewers(v.Views) + (v.Views == 1 ? " view" : " views"), tr.X + S(8), tr.Bottom - S(28), false);
        string ago = VideoData.Ago(v.Created, DateTimeOffset.UtcNow);
        if (ago.Length > 0) Badge(g, ago, tr.Right - S(8), tr.Bottom - S(28), true);
        int ty = tr.Bottom + S(10);
        Txt.In(g, v.Title.Length > 0 ? v.Title : VideoData.KindName(v.Kind), FTitle, hi ? Theme.AccentText : Fg, new Rectangle(r.X, ty - S(2), r.Width, S(20)), false);
        Txt.In(g, v.IsClip ? (v.Creator.Length > 0 ? "Clipped by " + v.Creator : "Clip") : VideoData.KindName(v.Kind), FText, Dim, new Rectangle(r.X, ty + S(17), r.Width, S(18)), false);
    }

    string DescribeVideo(Item it)
    {
        var v = it.Video;
        return (v.Title.Length > 0 ? v.Title : VideoData.KindName(v.Kind)) + ", " + (v.IsClip ? "clip" + (v.Creator.Length > 0 ? " by " + v.Creator : "") : VideoData.KindName(v.Kind).ToLowerInvariant())
            + (v.Seconds > 0 ? ", " + VideoData.Length(v.Seconds) : "") + ", " + Fmt.Viewers(v.Views) + " views" + (v.Created != null ? ", " + VideoData.Ago(v.Created, DateTimeOffset.UtcNow) : "");
    }

    void AddVideosMenuItem(Item it)
    {
        var vids = new ToolStripMenuItem("Videos and clips");
        vids.Click += (s, e) => ShowVideos(it.Login, it.Name, false);
        menu.Items.Insert(1, vids);
        var win = new ToolStripMenuItem("Watch in a new window");
        win.Click += (s, e) => { if (WindowWanted != null) WindowWanted(it.Login, it.Name); };
        menu.Items.Insert(1, win);
    }

    void ShowVideoMenu(Item it, Point at)
    {
        var v = it.Video;
        string url = v.IsClip ? "https://clips.twitch.tv/" + v.Id : "https://www.twitch.tv/videos/" + v.Id;
        var play = new ToolStripMenuItem(v.IsClip ? "Play the clip" : "Play the video") { Font = FMenuBold };
        play.Click += (s, e) => { if (VideoPicked != null) VideoPicked(v); };
        var live = new ToolStripMenuItem("Watch " + v.Name + " live");
        live.Click += (s, e) => Open(v.Login);
        var site = new ToolStripMenuItem("Open on twitch.tv");
        site.Click += (s, e) => Launcher.Open(url);
        var copy = new ToolStripMenuItem("Copy link");
        copy.Click += (s, e) => { try { Clipboard.SetText(url); } catch (System.Runtime.InteropServices.ExternalException) { } };
        menu.Items.Add(play);
        double seen = WatchedFraction != null && !v.IsClip ? WatchedFraction(v.Id, v.Seconds) : 0;
        if (seen > 0)
        {
            var start = new ToolStripMenuItem("Play from the start");
            start.Click += (s, e) => { if (VideoFromStart != null) VideoFromStart(v); };
            var forget = new ToolStripMenuItem("Forget where I stopped");
            forget.Click += (s, e) => { if (VideoForget != null) VideoForget(v); Invalidate(); };
            menu.Items.Add(start); menu.Items.Add(forget);
        }
        var dl = new ToolStripMenuItem("Download");
        foreach (var q in DownloadCommand.Qualities)
        {
            string k = q;
            long est = DownloadCommand.EstimateBytes(v.Seconds, q);
            var qi = new ToolStripMenuItem(DownloadCommand.QualityName(q) + (est > 0 && !v.IsClip ? "  (about " + DownloadCommand.Size(est) + ")" : ""));
            qi.Click += (s, e) => { if (DownloadWanted != null) DownloadWanted(v, k); };
            dl.DropDownItems.Add(qi);
        }
        DarkMenuColors.Apply(dl.DropDown);
        menu.Items.AddRange(new ToolStripItem[] { dl, new ToolStripSeparator(), live, site, copy });
        menu.Show(this, at);
    }

    /// <summary>For the UI fixture: downloads the first video in the list at 480p (false: not loaded yet).</summary>
    public bool DownloadFirstVideo()
    {
        if (vids == null || vids.Count == 0 || DownloadWanted == null) return false;
        DownloadWanted(vids[0], "480p");
        return true;
    }

    /// <summary>For the UI fixture: plays the first video in the list (false: not loaded yet).</summary>
    public bool PlayFirstVideo()
    {
        if (vids == null || vids.Count == 0 || VideoPicked == null) return false;
        VideoPicked(vids[0]);
        return true;
    }
}
