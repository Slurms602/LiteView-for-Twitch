// The Following page's "Videos and clips" view of one channel (right-click a channel > Videos and clips): its newest
// videos (past broadcasts, highlights, uploads) or its most viewed clips of the last 30 days, as cards. A click plays it
// (VideoPicked; LiteView plays it with Streamlink like a stream, but mpv can seek). Back, Esc or Backspace returns to
// where you were. Uses the header's back pill, its first two tab pills (Videos / Clips) and the refresh button.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using LiteViewCore;

partial class BrowsePage
{
    public event Action<VideoInfo> VideoPicked;
    readonly ChannelVideos videoFeed = new ChannelVideos(q => Tw.Helix(q));
    readonly Latest videoGuard = new Latest();
    string vidLogin, vidName; bool vidClips, loadingVids, vidsFailed; List<VideoInfo> vids; int vidScroll, vidSel;

    bool InVideos { get { return vidLogin != null; } }

    /// <summary>Shows a channel's videos (or clips) on the page.</summary>
    public void ShowVideos(string login, string name, bool clips)
    {
        if (!ChannelName.IsValidLogin(login)) return;
        if (!InVideos) { vidScroll = scroll; vidSel = sel; }
        vidLogin = login; vidName = string.IsNullOrEmpty(name) ? login : name; vidClips = clips;
        query = null; history = false; vids = null; vidsFailed = false; scroll = 0; sel = -1;
        ReloadVideos();
        Rebuild();
    }

    void CloseVideos()
    {
        if (!InVideos) return;
        vidLogin = null; vids = null; videoGuard.Next(); loadingVids = false;
        Rebuild();
        scroll = vidScroll; sel = Math.Min(vidSel, items.Count - 1); ClampScroll();
        Invalidate(); Announce();
    }

    void SetVideoKind(bool clips)
    {
        if (clips == vidClips) return;
        vidClips = clips; vids = null; vidsFailed = false; scroll = 0; sel = -1;
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
            var r = clips ? await videoFeed.Clips(login, name, DateTimeOffset.UtcNow) : await videoFeed.Videos(login);
            if (!videoGuard.IsCurrent(t) || IsDisposed) return;   // another channel or kind meanwhile
            vidsFailed = r == null;
            if (r != null) vids = r;
        }
        catch (Exception ex) { Log.Error("videos", ex); }
        finally { if (videoGuard.IsCurrent(t)) loadingVids = false; Rebuild(); }
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
        return vidClips ? vidName + " has no clips from the last " + ChannelVideos.ClipDays + " days." : vidName + " has no videos (or keeps them for subscribers).";
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
        string[] names = { "Videos", "Clips (" + ChannelVideos.ClipDays + " days)" };
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
        else using (var b = new SolidBrush(CardC)) g.FillRectangle(b, tr);
        if (hi) using (var p = new Pen(i == sel && Focused ? Accent : Color.FromArgb(120, 145, 71, 255), S(3))) g.DrawRectangle(p, tr.X + 1, tr.Y + 1, tr.Width - 3, tr.Height - 3);
        string len = VideoData.Length(v.Seconds);
        if (len.Length > 0) Badge(g, len, tr.X + S(8), tr.Y + S(8), false);
        Badge(g, Fmt.Viewers(v.Views) + (v.Views == 1 ? " view" : " views"), tr.X + S(8), tr.Bottom - S(28), false);
        string ago = VideoData.Ago(v.Created, DateTimeOffset.UtcNow);
        if (ago.Length > 0) Badge(g, ago, tr.Right - S(8), tr.Bottom - S(28), true);
        int ty = tr.Bottom + S(10);
        Txt.In(g, v.Title.Length > 0 ? v.Title : VideoData.KindName(v.Kind), FTitle, hi ? Color.FromArgb(191, 148, 255) : Fg, new Rectangle(r.X, ty - S(2), r.Width, S(20)), false);
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
        menu.Items.AddRange(new ToolStripItem[] { play, live, site, copy });
        menu.Show(this, at);
    }

    /// <summary>For the UI fixture: plays the first video in the list (false: not loaded yet).</summary>
    public bool PlayFirstVideo()
    {
        if (vids == null || vids.Count == 0 || VideoPicked == null) return false;
        VideoPicked(vids[0]);
        return true;
    }
}
