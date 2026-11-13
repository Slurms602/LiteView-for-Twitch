// Native Following page (P2, docs/ROADMAP.md): the followed channels that are live as preview cards, all followed
// channels, recently watched channels and search results as lists. Data from Helix through src/core/Browse.cs
// (official endpoints only); drawn here, only what is on screen. Previews and avatars are loaded for visible items
// only and kept within a small budget; nothing is polled while the page is hidden or the window minimised.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LiteViewCore;
using SortOrder = LiteViewCore.SortOrder;   // not System.Windows.Forms.SortOrder

class BrowsePage : Control
{
    public enum Tab { Live, Followed, Recent }

    static readonly Color Bg = Color.FromArgb(14, 14, 16), CardC = Color.FromArgb(24, 24, 27), HoverC = Color.FromArgb(38, 38, 44),
        SelC = Color.FromArgb(53, 53, 59), Fg = Color.FromArgb(239, 239, 241), Dim = Color.FromArgb(173, 173, 184), Faint = Color.FromArgb(110, 110, 122),
        Accent = Color.FromArgb(145, 71, 255), AccentHot = Color.FromArgb(119, 44, 232), Red = Color.FromArgb(235, 4, 0), Gold = Color.FromArgb(255, 200, 61),
        Shade = Color.FromArgb(200, 0, 0, 0);
    static readonly Font FHead = new Font("Segoe UI Semibold", 15f), FTab = new Font("Segoe UI Semibold", 10f), FTitle = new Font("Segoe UI Semibold", 10f),
        FText = new Font("Segoe UI", 9.25f), FSmall = new Font("Segoe UI Semibold", 8.25f), FMsg = new Font("Segoe UI", 10.5f), FIcon = new Font("Segoe MDL2 Assets", 10f),
        FIconSmall = new Font("Segoe MDL2 Assets", 7f), FMenuBold = new Font("Segoe UI", 9f, FontStyle.Bold);
    static readonly string Star = ((char)0xE735).ToString(), RefreshGlyph = ((char)0xE72C).ToString(), Down = ((char)0xE70D).ToString();
    const int MaxImageBytes = 1024 * 1024, MaxThumbs = 90, MaxAvatars = 600;
    static readonly TimeSpan ThumbAge = TimeSpan.FromMinutes(5), FollowedAge = TimeSpan.FromMinutes(10);

    public event Action<string> Pick;
    public event Action LoginRequested, UseWebsite, Changed, SearchWanted;   // Changed: lists or order changed (save settings)

    public readonly ChannelList Favourites = new ChannelList(500, false), Recent = new ChannelList(30, true), Searches = new ChannelList(20, true);
    public SortOrder Order { get; set; }   // Viewers (0) by default
    public bool FavouritesFirst = true, RememberRecent = true, RememberSearches = true;

    readonly Following feed = new Following(q => Tw.Helix(q));
    readonly System.Windows.Forms.Timer liveTimer = new System.Windows.Forms.Timer { Interval = 60000 }, searchDelay = new System.Windows.Forms.Timer { Interval = 250 },
        avatarDelay = new System.Windows.Forms.Timer { Interval = 120 };
    readonly Latest searchGuard = new Latest(), liveGuard = new Latest();
    readonly TimedCache<string, List<ChannelInfo>> searchCache = new TimedCache<string, List<ChannelInfo>>(50, TimeSpan.FromMinutes(2));
    readonly ToolTip tip = new ToolTip();
    readonly ContextMenuStrip menu = new ContextMenuStrip();
    static readonly SemaphoreSlim downloads = new SemaphoreSlim(6);

    sealed class Item
    {
        public string Login, Name, Game = "", Title = "", Thumb = "", Note = "";
        public long Viewers; public DateTimeOffset? Started; public bool Live, Card;
    }

    sealed class Pic { public Bitmap Bmp; public DateTime At = DateTime.MinValue; public bool Loading; public long Used; }

    Tab tab = Tab.Live;
    List<LiveStream> followedLive = new List<LiveStream>(), otherLive = new List<LiveStream>();   // otherLive: live favourites/recent you do not follow
    List<ChannelInfo> followed; DateTime followedAt = DateTime.MinValue; bool followedFailed;
    FeedState liveState = FeedState.Ok; bool liveLoaded, loadingLive, loadingFollowed;
    string query; List<ChannelInfo> results; bool searching, searchFailed, history;
    List<Item> items = new List<Item>();
    readonly Dictionary<string, Pic> thumbs = new Dictionary<string, Pic>(), avatars = new Dictionary<string, Pic>();
    readonly Dictionary<string, string> avatarUrl = new Dictionary<string, string>();   // login -> url ("" = none known)
    readonly Dictionary<string, string> names = new Dictionary<string, string>();       // login -> display name, from users
    readonly HashSet<string> avatarWanted = new HashSet<string>();
    bool running; long clock;

    int scroll, hover = -1, sel = -1, hot, cols = 1, cardW, cardH, contentH; string tipFor;
    bool dragging; int dragStart, dragScroll;
    Rectangle btnRect, linkRect, sortRect, refreshRect, clearRect, barRect; readonly Rectangle[] tabRects = new Rectangle[3];
    const int HotNone = 0, HotBtn = 1, HotLink = 2, HotSort = 3, HotRefresh = 4, HotClear = 5, HotTab = 10;

    public BrowsePage()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Bg; TabStop = true;
        liveTimer.Tick += (s, e) => { if (Active) ReloadLive(); };
        searchDelay.Tick += (s, e) => { searchDelay.Stop(); DoSearch(); };
        avatarDelay.Tick += (s, e) => { avatarDelay.Stop(); FetchAvatarUrls(); };
        DarkMenuColors.Apply(menu); menu.ShowImageMargin = false;
        InitAccessibility();
    }

    int S(int px) { return px * DeviceDpi / 96; }   // DPI-ready (the window is not DPI-aware yet: DeviceDpi is 96)
    int Pad { get { return S(20); } }
    int Gap { get { return S(14); } }
    int HeadH { get { return S(96); } }
    int RowH { get { return S(56); } }

    // Visible and the window not minimised: the only time anything is fetched.
    bool Active { get { var f = FindForm(); return running && Visible && f != null && f.WindowState != FormWindowState.Minimized; } }

    /// <summary>The page is shown (true) or hidden (false). Refreshes what is old when it comes back.</summary>
    public void Run(bool on)
    {
        running = on;
        liveTimer.Enabled = on;
        if (!on) { tip.Hide(this); return; }
        if (!liveLoaded || liveState != FeedState.Ok || (DateTime.UtcNow - lastLive).TotalSeconds > 55) ReloadLive();
        if (tab == Tab.Followed && DateTime.UtcNow - followedAt > FollowedAge) ReloadFollowed();
        Invalidate();
    }

    /// <summary>Logged in or out: everything that depends on the account is fetched again.</summary>
    public void LoginChanged()
    {
        followed = null; followedAt = DateTime.MinValue; liveLoaded = false; searchCache.Clear();
        if (running) { ReloadLive(); if (tab == Tab.Followed) ReloadFollowed(); if (query != null) DoSearch(); }
        Rebuild();
    }

    public void Reload()
    {
        followedAt = DateTime.MinValue;
        ReloadLive();
        if (tab == Tab.Followed) ReloadFollowed();
    }

    // ---------- data ----------

    DateTime lastLive = DateTime.MinValue;

    async void ReloadLive()
    {
        int t = liveGuard.Next();   // the newest request wins; an older answer (another account, an older list) is dropped
        if (Tw.Token == null) { followedLive.Clear(); otherLive.Clear(); liveState = FeedState.NotLoggedIn; liveLoaded = true; loadingLive = false; Rebuild(); return; }
        loadingLive = true; Invalidate();
        try
        {
            if (Tw.UserId == null) await Tw.Validate();
            var r = await feed.Live(Tw.UserId);
            if (!liveGuard.IsCurrent(t) || IsDisposed) return;
            liveState = r.Key;
            if (r.Key == FeedState.Ok) { followedLive = r.Value; lastLive = DateTime.UtcNow; }
            else if (r.Key == FeedState.NotLoggedIn) followedLive = new List<LiveStream>();
            // Unreachable: keep the last list; the header says so
            liveLoaded = true;
            Rebuild();
            var mine = new HashSet<string>(followedLive.Select(s => s.Login));
            var others = Favourites.Items.Concat(Recent.Items).Where(l => !mine.Contains(l)).Distinct().ToList();
            if (others.Count > 0 && r.Key == FeedState.Ok)
            {
                var o = await feed.LiveNow(others);
                if (o != null && liveGuard.IsCurrent(t)) otherLive = o;
            }
            else if (others.Count == 0) otherLive = new List<LiveStream>();
        }
        catch (Exception ex) { Log.Error("following page", ex); }
        finally { if (liveGuard.IsCurrent(t)) loadingLive = false; Rebuild(); }
    }

    async void ReloadFollowed()
    {
        if (loadingFollowed || Tw.Token == null) { Rebuild(); return; }
        loadingFollowed = true; Invalidate();
        try
        {
            if (Tw.UserId == null) await Tw.Validate();
            var r = await feed.Followed(Tw.UserId);
            if (IsDisposed) return;
            followedFailed = r.Key == FeedState.Unreachable;
            if (r.Key == FeedState.Ok) { followed = r.Value; followedAt = DateTime.UtcNow; }
        }
        catch (Exception ex) { Log.Error("followed channels", ex); }
        finally { loadingFollowed = false; Rebuild(); }
    }

    /// <summary>Text of the top bar's search box. Search starts after a short pause in typing.</summary>
    public void SetQuery(string text)
    {
        string q = SearchText.Clean(text);
        if (q == query) return;
        query = q; history = false; scroll = 0; sel = -1;
        searchDelay.Stop();
        if (q == null) { results = null; searching = false; searchFailed = false; searchGuard.Next(); Rebuild(); return; }
        List<ChannelInfo> cached;
        if (searchCache.TryGet(q, out cached)) { results = cached; searching = false; searchFailed = false; Rebuild(); return; }
        searching = true; Rebuild();
        searchDelay.Start();
    }

    /// <summary>The search box got focus while empty: show recent searches (until something else is chosen).</summary>
    public void ShowHistory()
    {
        if (query != null || !RememberSearches || Searches.Count == 0) return;
        history = true; scroll = 0; sel = -1; Rebuild();
    }

    /// <summary>A channel opened from the search box (Enter, or a picked result).</summary>
    public void Searched(string login)
    {
        if (RememberSearches && Searches.Add(login) && Changed != null) Changed();
    }

    public void Watched(string login)
    {
        if (RememberRecent && Recent.Add(login) && Changed != null) Changed();
    }

    public void ClearHistory()
    {
        Recent.Clear(); Searches.Clear(); history = false;
        if (Changed != null) Changed();
        Rebuild();
    }

    async void DoSearch()
    {
        string q = query;
        if (q == null) return;
        if (Tw.Token == null) { searching = false; Rebuild(); return; }   // Helix needs a login; Enter still opens a typed name
        int t = searchGuard.Next();
        searching = true; Invalidate();
        List<ChannelInfo> r = null;
        try { r = await feed.Search(q); }
        catch (Exception ex) { Log.Error("search", ex); }
        if (!searchGuard.IsCurrent(t) || q != query || IsDisposed) return;   // a newer search started meanwhile
        searching = false;
        searchFailed = r == null;
        results = r;
        if (r != null)
        {
            searchCache.Set(q, r);
            foreach (var c in r) { string a = HelixData.Avatar(c.Avatar); if (a.Length > 0) avatarUrl[c.Login] = a; }
        }
        Rebuild();
    }

    // ---------- items ----------

    bool InSearch { get { return query != null || history; } }

    void Rebuild()
    {
        var list = new List<Item>();
        var liveBy = new Dictionary<string, LiveStream>(StringComparer.Ordinal);
        foreach (var s in followedLive.Concat(otherLive)) liveBy[s.Login] = s;
        if (query != null)
        {
            if (results != null)
                foreach (var c in results)
                {
                    LiveStream s; liveBy.TryGetValue(c.Login, out s);
                    list.Add(new Item { Login = c.Login, Name = c.Name, Live = c.Live, Game = c.Game, Title = c.Title, Started = c.Started, Viewers = s != null ? s.Viewers : 0 });
                }
        }
        else if (history)
            foreach (var l in Searches.Items) list.Add(Row(l, null, liveBy));
        else if (tab == Tab.Live)
        {
            var favLive = otherLive.Where(s => Favourites.Contains(s.Login));
            foreach (var s in Sorting.Sort(followedLive.Concat(favLive), Order, Favourites.Items.ToList(), FavouritesFirst))
                list.Add(new Item { Login = s.Login, Name = s.Name, Game = s.Game, Title = s.Title, Viewers = s.Viewers, Started = s.Started, Live = true, Card = true, Thumb = s.Thumbnail });
        }
        else if (tab == Tab.Followed && followed != null)
        {
            foreach (var c in followed.OrderBy(c => liveBy.ContainsKey(c.Login) ? 0 : 1).ThenByDescending(c => liveBy.ContainsKey(c.Login) ? liveBy[c.Login].Viewers : 0)
                .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var it = Row(c.Login, c.Name, liveBy);
                if (!it.Live && c.Followed != null) it.Note = "Following since " + c.Followed.Value.ToLocalTime().ToString("MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture);
                list.Add(it);
            }
        }
        else if (tab == Tab.Recent)
            foreach (var l in Recent.Items) list.Add(Row(l, null, liveBy));
        items = list;
        if (sel >= items.Count) sel = items.Count - 1;
        hover = -1;
        DoLayout();
        Invalidate();
    }

    Item Row(string login, string name, Dictionary<string, LiveStream> liveBy)
    {
        LiveStream s; string known;
        if (liveBy.TryGetValue(login, out s)) return new Item { Login = login, Name = s.Name, Live = true, Game = s.Game, Title = s.Title, Viewers = s.Viewers, Started = s.Started };
        return new Item { Login = login, Name = name ?? (names.TryGetValue(login, out known) ? known : login) };
    }

    void DoLayout()
    {
        int w = Math.Max(1, ClientSize.Width - 2 * Pad);
        bool cards = items.Count > 0 && items[0].Card;
        if (cards)
        {
            int min = S(270);
            cols = Math.Max(1, (w + Gap) / (min + Gap));
            cardW = (w - (cols - 1) * Gap) / cols;
            cardH = cardW * 9 / 16 + S(66);
            contentH = (items.Count + cols - 1) / cols * (cardH + Gap);
        }
        else { cols = 1; cardW = w; cardH = RowH; contentH = items.Count * RowH; }
        ClampScroll();
    }

    int ViewH { get { return Math.Max(0, ClientSize.Height - HeadH); } }
    void ClampScroll() { scroll = Math.Max(0, Math.Min(scroll, Math.Max(0, contentH + Pad - ViewH))); }

    Rectangle ItemRect(int i)
    {
        if (items.Count > 0 && items[0].Card)
            return new Rectangle(Pad + i % cols * (cardW + Gap), HeadH + i / cols * (cardH + Gap) - scroll, cardW, cardH);
        return new Rectangle(Pad, HeadH + i * RowH - scroll, cardW, RowH);
    }

    int ItemAt(Point p)
    {
        if (p.Y < HeadH) return -1;
        for (int i = FirstVisible(); i < items.Count; i++)
        {
            var r = ItemRect(i);
            if (r.Top > ClientSize.Height) break;
            if (r.Contains(p)) return i;
        }
        return -1;
    }

    int FirstVisible()
    {
        if (items.Count == 0) return 0;
        int rowH = items[0].Card ? cardH + Gap : RowH;
        return Math.Max(0, scroll / Math.Max(1, rowH) * cols);
    }

    // ---------- drawing ----------

    protected override void OnResize(EventArgs e) { base.OnResize(e); DoLayout(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        clock++;
        g.SmoothingMode = SmoothingMode.AntiAlias; g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        DrawHeader(g);
        btnRect = linkRect = Rectangle.Empty;
        g.SetClip(new Rectangle(0, HeadH, ClientSize.Width, ViewH));
        string msg = Message();
        if (msg != null) DrawMessage(g, msg);
        else
        {
            for (int i = FirstVisible(); i < items.Count; i++)
            {
                var r = ItemRect(i);
                if (r.Top > ClientSize.Height) break;
                if (r.Bottom < HeadH) continue;
                if (items[i].Card) DrawCard(g, items[i], r, i); else DrawRow(g, items[i], r, i);
            }
            DrawScrollbar(g);
        }
        g.ResetClip();
        if (avatarWanted.Count > 0 && !avatarDelay.Enabled) avatarDelay.Start();
    }

    void DrawHeader(Graphics g)
    {
        int x = Pad, w = ClientSize.Width;
        string head = query != null ? "Search" : history ? "Recent searches" : "Following";
        Txt.At(g, head, FHead, Fg, x - 2, S(14));
        clearRect = sortRect = refreshRect = Rectangle.Empty;
        for (int i = 0; i < 3; i++) tabRects[i] = Rectangle.Empty;
        int y = S(58);
        if (query != null)
        {
            string hint = ChannelName.IsValidLogin(query.ToLowerInvariant()) ? "Enter opens twitch.tv/" + query.ToLowerInvariant() + "  -  Esc clears the search" : "Esc clears the search";
            Txt.At(g, hint, FText, Dim, x, y + S(6));
            return;
        }
        if (history)
        {
            int cw = Txt.Width("Clear history", FTab) + S(16);
            clearRect = new Rectangle(x, y, cw, S(30));
            Pill(g, clearRect, hot == HotClear ? HoverC : CardC);
            Txt.In(g, "Clear history", FTab, Fg, clearRect, true);
            Txt.At(g, "Esc goes back", FText, Dim, clearRect.Right + S(12), y + S(6));
            return;
        }
        string[] names = { "Live" + (liveLoaded && liveState == FeedState.Ok ? "  " + LiveCount : ""), "All followed", "Recently watched" };
        for (int i = 0; i < 3; i++)
        {
            int tw = Txt.Width(names[i], FTab) + S(24);
            var r = new Rectangle(x, y, tw, S(30));
            tabRects[i] = r;
            bool on = (int)tab == i;
            if (on || hot == HotTab + i) Pill(g, r, on ? SelC : HoverC);
            Txt.In(g, names[i], FTab, on ? Fg : Dim, r, true);
            x = r.Right + S(6);
        }
        int right = w - Pad;
        refreshRect = new Rectangle(right - S(30), y, S(30), S(30));
        if (hot == HotRefresh) Pill(g, refreshRect, HoverC);
        TextRenderer.DrawText(g, RefreshGlyph, FIcon, refreshRect, loadingLive || loadingFollowed ? Accent : Dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        if (tab == Tab.Live)
        {
            int sw = Txt.Width("Sort: " + SortName(Order), FText) + S(34);
            sortRect = new Rectangle(refreshRect.Left - S(8) - sw, y, sw, S(30));
            if (hot == HotSort) Pill(g, sortRect, HoverC);
            Txt.In(g, "Sort: " + SortName(Order), FText, Dim, new Rectangle(sortRect.X + S(10), y, sw - S(28), S(30)), false);
            TextRenderer.DrawText(g, Down, FIconSmall, new Rectangle(sortRect.Right - S(22), y, S(16), S(30)), Dim, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
        }
        if (liveState == FeedState.Unreachable && liveLoaded && tab == Tab.Live && followedLive.Count > 0)
            Txt.In(g, "Could not reach Twitch - showing the last list", FText, Gold, new Rectangle(x + S(10), y, Math.Max(0, sortRect.Left - x - S(20)), S(30)), false);
    }

    int LiveCount { get { return followedLive.Count + otherLive.Count(s => Favourites.Contains(s.Login)); } }

    static string SortName(SortOrder o)
    {
        switch (o) { case SortOrder.Name: return "Name"; case SortOrder.Newest: return "Recently started"; case SortOrder.Longest: return "Live the longest"; default: return "Viewers"; }
    }

    // null = items to show; otherwise the state to explain (with btnRect / linkRect as actions)
    string Message()
    {
        if (query != null)
        {
            if (Tw.Token == null) return "Log in with Twitch to search channels.\nOr type a channel name and press Enter.";
            if (searchFailed) return "Could not reach Twitch.";
            if (searching && items.Count == 0) return "Searching...";
            if (items.Count == 0) return "No channels found.";
            return null;
        }
        if (history) return items.Count == 0 ? "No recent searches." : null;
        if (tab == Tab.Recent)
        {
            if (items.Count > 0) return null;
            return RememberRecent ? "Channels you watch will appear here." : "Remembering watched channels is turned off (menu at the top right).";
        }
        if (Tw.Token == null || liveState == FeedState.NotLoggedIn) return "Log in with Twitch to see the channels you follow.";
        if (tab == Tab.Followed)
        {
            if (followed == null) return followedFailed ? "Could not reach Twitch." : "Loading the channels you follow...";
            return items.Count == 0 ? "You do not follow any channels yet." : null;
        }
        if (!liveLoaded) return "Loading...";
        if (items.Count > 0) return null;
        if (liveState == FeedState.Unreachable) return "Could not reach Twitch.";
        return "None of the channels you follow are live right now.";
    }

    void DrawMessage(Graphics g, string msg)
    {
        int w = Math.Min(ClientSize.Width - 2 * Pad, S(520)), x = (ClientSize.Width - w) / 2, y = HeadH + S(40);
        Txt.Wrap(g, msg, FMsg, Dim, new Rectangle(x, y, w, S(60)));
        y += S(66);
        string action = null;
        if (msg.StartsWith("Log in with Twitch", StringComparison.Ordinal)) action = "Log in with Twitch";
        else if (msg.StartsWith("Could not reach", StringComparison.Ordinal)) action = "Try again";
        if (action != null)
        {
            btnRect = new Rectangle(x, y, Txt.Width(action, FTitle) + S(36), S(34));
            using (var b = new SolidBrush(hot == HotBtn ? AccentHot : Accent)) Fill(g, b, btnRect, S(4));
            Txt.In(g, action, FTitle, Color.White, btnRect, true);
            y = btnRect.Bottom + S(16);
        }
        if (action == "Log in with Twitch" && query == null)
        {
            string l = "Use the Twitch website page instead";
            linkRect = new Rectangle(x, y, Txt.Width(l, FText) + 4, S(20));
            Txt.At(g, l, FText, hot == HotLink ? Fg : Color.FromArgb(191, 148, 255), x, y);
            Txt.Wrap(g, "Logging in here uses your normal browser: LiteView never sees your password.", FText, Faint, new Rectangle(x, y + S(30), w, S(40)));
        }
    }

    void DrawCard(Graphics g, Item it, Rectangle r, int i)
    {
        int th = r.Width * 9 / 16;
        var tr = new Rectangle(r.X, r.Y, r.Width, th);
        bool hi = i == hover || i == sel;
        var pic = Thumb(it, tr.Size);
        if (pic != null) g.DrawImage(pic, tr);
        else using (var b = new SolidBrush(CardC)) g.FillRectangle(b, tr);
        if (hi) using (var p = new Pen(i == sel && Focused ? Accent : Color.FromArgb(120, 145, 71, 255), S(3))) g.DrawRectangle(p, tr.X + 1, tr.Y + 1, tr.Width - 3, tr.Height - 3);
        // LIVE, uptime and viewers on the preview
        var lr = new Rectangle(tr.X + S(8), tr.Y + S(8), Txt.Width("LIVE", FSmall) + S(12), S(20));
        using (var b = new SolidBrush(Red)) Fill(g, b, lr, S(3));
        Txt.In(g, "LIVE", FSmall, Color.White, lr, true);
        string up = Fmt.Uptime(it.Started, DateTimeOffset.UtcNow);
        if (up.Length > 0) Badge(g, up, tr.Right - S(8), tr.Y + S(8), true);
        Badge(g, Fmt.Viewers(it.Viewers) + " viewers", tr.X + S(8), tr.Bottom - S(28), false);
        if (Favourites.Contains(it.Login)) TextRenderer.DrawText(g, Star, FIcon, new Rectangle(tr.Right - S(30), tr.Bottom - S(30), S(24), S(24)), Gold, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        // avatar, title, name, game
        int ay = tr.Bottom + S(10), az = S(36);
        DrawAvatar(g, it.Login, new Rectangle(r.X, ay, az, az));
        int tx = r.X + az + S(10), tw = r.Width - az - S(10);
        Txt.In(g, it.Title.Length > 0 ? it.Title : it.Name, FTitle, hi ? Color.FromArgb(191, 148, 255) : Fg, new Rectangle(tx, ay - S(2), tw, S(20)), false);
        Txt.In(g, it.Name, FText, Dim, new Rectangle(tx, ay + S(17), tw, S(18)), false);
        Txt.In(g, it.Game, FText, Dim, new Rectangle(tx, ay + S(34), tw, S(18)), false);
    }

    void Badge(Graphics g, string text, int x, int y, bool rightAligned)
    {
        int w = Txt.Width(text, FSmall) + S(12);
        var r = new Rectangle(rightAligned ? x - w : x, y, w, S(20));
        using (var b = new SolidBrush(Shade)) Fill(g, b, r, S(3));
        Txt.In(g, text, FSmall, Color.White, r, true);
    }

    void DrawRow(Graphics g, Item it, Rectangle r, int i)
    {
        if (i == hover || i == sel) using (var b = new SolidBrush(i == sel && Focused ? SelC : HoverC)) Fill(g, b, r, S(4));
        int az = S(36);
        DrawAvatar(g, it.Login, new Rectangle(r.X + S(8), r.Y + (r.Height - az) / 2, az, az));
        int tx = r.X + az + S(20);
        string right = it.Live ? (it.Viewers > 0 ? Fmt.Viewers(it.Viewers) + " viewers" : "") : "Offline";
        int rw = S(150);   // a fixed column: LIVE badges line up
        bool fav = Favourites.Contains(it.Login);
        int nameW = Math.Max(0, Math.Min(Txt.Width(it.Name, FTitle) + 2, r.Width - (tx - r.X) - rw - S(40)));
        Txt.In(g, it.Name, FTitle, Fg, new Rectangle(tx, r.Y + S(8), nameW, S(20)), false);
        if (fav) TextRenderer.DrawText(g, Star, FIcon, new Rectangle(tx + nameW + S(4), r.Y + S(6), S(20), S(22)), Gold, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        string line2 = it.Live ? (it.Game.Length > 0 ? it.Game + "  -  " : "") + it.Title : it.Note;
        Txt.In(g, line2, FText, Dim, new Rectangle(tx, r.Y + S(28), r.Width - (tx - r.X) - rw - S(10), S(18)), false);
        int rx = r.Right - rw;
        if (it.Live)
        {
            var lr = new Rectangle(rx, r.Y + (r.Height - S(20)) / 2, Txt.Width("LIVE", FSmall) + S(12), S(20));
            using (var b = new SolidBrush(Red)) Fill(g, b, lr, S(3));
            Txt.In(g, "LIVE", FSmall, Color.White, lr, true);
            rx = lr.Right + S(8);
        }
        Txt.In(g, right, FText, it.Live ? Fg : Faint, new Rectangle(rx, r.Y, r.Right - rx - S(8), r.Height), false);
    }

    void DrawScrollbar(Graphics g)
    {
        barRect = Rectangle.Empty;
        if (contentH + Pad <= ViewH || ViewH <= 0) return;
        int track = ViewH - S(8), thumbH = Math.Max(S(30), track * ViewH / (contentH + Pad));
        int y = HeadH + S(4) + (track - thumbH) * scroll / Math.Max(1, contentH + Pad - ViewH);
        barRect = new Rectangle(ClientSize.Width - S(10), y, S(6), thumbH);
        using (var b = new SolidBrush(dragging ? Dim : Faint)) Fill(g, b, barRect, S(3));
    }

    static void Pill(Graphics g, Rectangle r, Color c) { using (var b = new SolidBrush(c)) Fill(g, b, r, r.Height / 2); }

    static void Fill(Graphics g, Brush b, Rectangle r, int radius)
    {
        if (radius <= 0 || r.Width < 2 * radius || r.Height < 2 * radius) { g.FillRectangle(b, r); return; }
        using (var p = new GraphicsPath())
        {
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            g.FillPath(b, p);
        }
    }

    // ---------- images ----------

    Bitmap Thumb(Item it, Size size)
    {
        Pic p;
        thumbs.TryGetValue(it.Login, out p);
        if (p != null) p.Used = clock;
        if (Active && (p == null || !p.Loading && DateTime.UtcNow - p.At > ThumbAge))
        {
            string url = HelixData.Thumb(it.Thumb, 440, 248);
            if (url.Length > 0) Load(thumbs, it.Login, url, size, false, MaxThumbs);
        }
        return p != null ? p.Bmp : null;
    }

    void DrawAvatar(Graphics g, string login, Rectangle r)
    {
        Pic p; avatars.TryGetValue(login, out p);
        if (p != null) p.Used = clock;
        if (p != null && p.Bmp != null) { g.DrawImage(p.Bmp, r); return; }
        using (var b = new SolidBrush(SelC)) g.FillEllipse(b, r);
        Txt.In(g, login.Substring(0, 1).ToUpperInvariant(), FTitle, Dim, r, true);
        if (!Active || p != null) return;
        string url;
        if (avatarUrl.TryGetValue(login, out url)) { if (url.Length > 0) Load(avatars, login, url, r.Size, true, MaxAvatars); }
        else avatarWanted.Add(login);
    }

    async void FetchAvatarUrls()
    {
        var want = avatarWanted.Where(l => !avatarUrl.ContainsKey(l)).Take(100).ToList();
        avatarWanted.Clear();
        if (want.Count == 0 || Tw.Token == null) return;
        foreach (var l in want) avatarUrl[l] = "";   // asked once; a channel without a picture is not asked again
        try
        {
            var found = await feed.Users(want);
            foreach (var kv in found) { avatarUrl[kv.Key] = kv.Value.Avatar; names[kv.Key] = kv.Value.Name; }
            if (found.Count > 0 && !IsDisposed) Rebuild();   // display names for channels known only by login
        }
        catch (Exception ex) { Log.Warn("avatars: " + ex.GetType().Name); }
        if (!IsDisposed) Invalidate();
    }

    async void Load(Dictionary<string, Pic> cache, string key, string url, Size size, bool round, int max)
    {
        Pic p;
        if (!cache.TryGetValue(key, out p)) { p = new Pic { Used = clock }; cache[key] = p; }
        if (p.Loading) return;
        p.Loading = true;
        try
        {
            byte[] bytes;
            await downloads.WaitAsync();
            try { bytes = await Tw.GetBytes(url, MaxImageBytes); }
            finally { downloads.Release(); }
            if (bytes == null || !ImageSniff.Acceptable(bytes) || IsDisposed) return;
            int w = Math.Max(1, size.Width), h = Math.Max(1, size.Height);
            var bmp = await Task.Run(() => Decode(bytes, w, h, round));
            if (bmp == null) return;
            if (IsDisposed) { bmp.Dispose(); return; }
            if (p.Bmp != null) p.Bmp.Dispose();
            p.Bmp = bmp;
            Trim(cache, max);
            Invalidate();
        }
        catch (Exception ex) { Log.Warn("image: " + ex.GetType().Name); }
        finally { p.Loading = false; p.At = DateTime.UtcNow; }   // a failed download is tried again only after ThumbAge
    }

    // decoded at the drawn size: a preview card costs its own pixels, not the download's
    static Bitmap Decode(byte[] bytes, int w, int h, bool round)
    {
        try
        {
            using (var ms = new System.IO.MemoryStream(bytes))
            using (var img = Image.FromStream(ms, false, true))
            {
                if (img.Width > 4096 || img.Height > 4096) return null;
                var b = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(b))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.SmoothingMode = SmoothingMode.AntiAlias;
                    if (round)
                        using (var path = new GraphicsPath())
                        using (var tb = new TextureBrush(img, WrapMode.Clamp))
                        {
                            path.AddEllipse(0, 0, w, h);
                            tb.ScaleTransform((float)w / img.Width, (float)h / img.Height);
                            g.FillPath(tb, path);
                        }
                    else g.DrawImage(img, new Rectangle(0, 0, w, h));
                }
                return b;
            }
        }
        catch (ArgumentException) { return null; }
        catch (OutOfMemoryException) { return null; }   // GDI+'s answer to a damaged image
    }

    static void Trim(Dictionary<string, Pic> cache, int max)
    {
        if (cache.Count <= max) return;
        foreach (var kv in cache.Where(kv => !kv.Value.Loading).OrderBy(kv => kv.Value.Used).Take(cache.Count - max).ToList())
        {
            if (kv.Value.Bmp != null) kv.Value.Bmp.Dispose();
            cache.Remove(kv.Key);
        }
    }

    // ---------- input ----------

    int HotAt(Point p)
    {
        if (btnRect.Contains(p)) return HotBtn;
        if (linkRect.Contains(p)) return HotLink;
        if (sortRect.Contains(p)) return HotSort;
        if (refreshRect.Contains(p)) return HotRefresh;
        if (clearRect.Contains(p)) return HotClear;
        for (int i = 0; i < 3; i++) if (tabRects[i].Contains(p)) return HotTab + i;
        return HotNone;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (dragging)
        {
            int track = Math.Max(1, ViewH - S(8) - barRect.Height);
            scroll = dragScroll + (e.Y - dragStart) * Math.Max(0, contentH + Pad - ViewH) / track;
            ClampScroll(); Invalidate(); return;
        }
        int h = HotAt(e.Location), i = h == HotNone && Message() == null ? ItemAt(e.Location) : -1;
        Cursor = h != HotNone || i >= 0 ? Cursors.Hand : Cursors.Default;
        if (h != hot || i != hover) { hot = h; hover = i; Invalidate(); }
        string t = i >= 0 ? items[i].Name + (items[i].Game.Length > 0 ? "  -  " + items[i].Game : "") + (items[i].Title.Length > 0 ? "\n" + items[i].Title : "") : null;
        if (t == tipFor) return;
        tipFor = t;
        if (t == null) tip.Hide(this); else tip.Show(t, this, e.X + 14, e.Y + 20, 8000);
    }

    protected override void OnMouseLeave(EventArgs e) { hot = HotNone; hover = -1; tipFor = null; tip.Hide(this); Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        if (e.Button == MouseButtons.Left && !barRect.IsEmpty && new Rectangle(barRect.X - S(4), HeadH, barRect.Width + S(8), ViewH).Contains(e.Location))
        {
            if (!barRect.Contains(e.Location)) { scroll += (e.Y < barRect.Y ? -1 : 1) * ViewH; ClampScroll(); Invalidate(); return; }
            dragging = true; dragStart = e.Y; dragScroll = scroll; Capture = true; Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (dragging) { dragging = false; Capture = false; Invalidate(); return; }
        int h = HotAt(e.Location);
        if (e.Button == MouseButtons.Left)
        {
            if (h == HotBtn) { if (Message() != null && Message().StartsWith("Log in", StringComparison.Ordinal)) { if (LoginRequested != null) LoginRequested(); } else Retry(); return; }
            if (h == HotLink) { if (UseWebsite != null) UseWebsite(); return; }
            if (h == HotSort) { ShowSortMenu(); return; }
            if (h == HotRefresh) { Reload(); return; }
            if (h == HotClear) { ClearHistory(); return; }
            if (h >= HotTab) { SetTab((Tab)(h - HotTab)); return; }
        }
        int i = Message() == null ? ItemAt(e.Location) : -1;
        if (i < 0) return;
        sel = i; Invalidate(); Announce();
        if (e.Button == MouseButtons.Left) Open(items[i].Login);
        else if (e.Button == MouseButtons.Right) ShowItemMenu(i, e.Location);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        scroll -= e.Delta / 120 * (items.Count > 0 && items[0].Card ? (cardH + Gap) / 2 : RowH * 2);
        ClampScroll(); Invalidate();
        base.OnMouseWheel(e);
    }

    protected override bool IsInputKey(Keys k)
    {
        switch (k & Keys.KeyCode) { case Keys.Up: case Keys.Down: case Keys.Left: case Keys.Right: return true; }
        return base.IsInputKey(k);
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); if (sel < 0 && items.Count > 0 && Message() == null) sel = FirstVisible(); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        int n = items.Count, s = sel;
        switch (e.KeyCode)
        {
            case Keys.Right: s++; break;
            case Keys.Left: s--; break;
            case Keys.Down: s += cols; break;
            case Keys.Up: if (s - cols < 0 && SearchWanted != null) { SearchWanted(); e.Handled = true; return; } s -= cols; break;
            case Keys.PageDown: s += cols * Math.Max(1, ViewH / Math.Max(1, cardH + (cols > 1 ? Gap : 0))); break;
            case Keys.PageUp: s -= cols * Math.Max(1, ViewH / Math.Max(1, cardH + (cols > 1 ? Gap : 0))); break;
            case Keys.Home: s = 0; break;
            case Keys.End: s = n - 1; break;
            case Keys.Enter: if (sel >= 0 && sel < n) Open(items[sel].Login); e.Handled = true; return;
            case Keys.Apps: if (sel >= 0 && sel < n) ShowItemMenu(sel, ItemRect(sel).Location); e.Handled = true; return;
            case Keys.Escape: if (history) { history = false; Rebuild(); e.Handled = true; } return;
            case Keys.F5: Reload(); e.Handled = true; return;
            default: base.OnKeyDown(e); return;
        }
        e.Handled = true;
        if (n == 0) return;
        sel = Math.Max(0, Math.Min(n - 1, s));
        var r = ItemRect(sel);   // keep it in view
        if (r.Top < HeadH) scroll -= HeadH - r.Top + S(8);
        else if (r.Bottom > ClientSize.Height) scroll += r.Bottom - ClientSize.Height + S(8);
        ClampScroll(); Invalidate();
        Announce();
    }

    void Open(string login)
    {
        if (InSearch) Searched(login);
        history = false;
        if (Pick != null) Pick(login);
    }

    void Retry()
    {
        if (query != null) { searchFailed = false; DoSearch(); }
        else if (tab == Tab.Followed) ReloadFollowed();
        else ReloadLive();
    }

    public void SetTab(Tab t)
    {
        history = false;
        if (tab != t) { tab = t; scroll = 0; sel = -1; }
        if (t == Tab.Followed && (followed == null || DateTime.UtcNow - followedAt > FollowedAge)) ReloadFollowed();
        if (t == Tab.Recent && Active) ReloadLive();   // live status of the recent channels
        Rebuild();
    }

    public Tab CurrentTab { get { return tab; } }

    void ShowSortMenu()
    {
        menu.Items.Clear();
        foreach (SortOrder o in Enum.GetValues(typeof(SortOrder)))
        {
            var it = new ToolStripMenuItem(SortName(o)) { Checked = o == Order };
            var order = o;
            it.Click += (s, e) => { Order = order; Rebuild(); if (Changed != null) Changed(); };
            menu.Items.Add(it);
        }
        menu.Items.Add(new ToolStripSeparator());
        var fav = new ToolStripMenuItem("Favourites first") { Checked = FavouritesFirst };
        fav.Click += (s, e) => { FavouritesFirst = !FavouritesFirst; Rebuild(); if (Changed != null) Changed(); };
        menu.Items.Add(fav);
        menu.Show(this, new Point(sortRect.Left, sortRect.Bottom + S(2)));
    }

    void ShowItemMenu(int i, Point at)
    {
        var it = items[i];
        menu.Items.Clear();
        var watch = new ToolStripMenuItem("Watch " + it.Name) { Font = FMenuBold };
        watch.Click += (s, e) => Open(it.Login);
        bool isFav = Favourites.Contains(it.Login);
        var fav = new ToolStripMenuItem(isFav ? "Remove from favourites" : "Add to favourites");
        fav.Click += (s, e) =>
        {
            if (isFav) Favourites.Remove(it.Login); else Favourites.Add(it.Login);
            Rebuild();
            if (Changed != null) Changed();
            if (!isFav && running) ReloadLive();   // a favourite you do not follow shows when live
        };
        var site = new ToolStripMenuItem("Open on twitch.tv");
        site.Click += (s, e) => Launcher.Open("https://www.twitch.tv/" + it.Login);
        var copy = new ToolStripMenuItem("Copy link");
        copy.Click += (s, e) => { try { Clipboard.SetText("https://www.twitch.tv/" + it.Login); } catch (System.Runtime.InteropServices.ExternalException) { } };
        menu.Items.AddRange(new ToolStripItem[] { watch, fav, site, copy });
        if (!InSearch && tab == Tab.Recent)
        {
            var forget = new ToolStripMenuItem("Remove from recently watched");
            forget.Click += (s, e) => { Recent.Remove(it.Login); Rebuild(); if (Changed != null) Changed(); };
            menu.Items.Add(forget);
        }
        else if (history)
        {
            var forget = new ToolStripMenuItem("Remove from recent searches");
            forget.Click += (s, e) => { Searches.Remove(it.Login); Rebuild(); if (Changed != null) Changed(); };
            menu.Items.Add(forget);
        }
        menu.Show(this, at);
    }

    // ---------- screen readers (UI-4) ----------

    ListModel a11y; UiaList uia;

    void InitAccessibility()
    {
        a11y = new ListModel
        {
            Owner = this,
            Name = () => { string m = Message(); return (query != null ? "Search results" : history ? "Recent searches" : "Following, " + TabName(tab)) + (m != null ? ". " + m : ""); },
            Count = () => Message() == null ? items.Count : 0, Item = i => Describe(items[i]), Bounds = ItemRect, Selected = () => sel, Open = i => Open(items[i].Login),
        };
        uia = a11y.Attach();
    }

    protected override AccessibleObject CreateAccessibilityInstance() { return new ListAccessible(a11y); }

    static string TabName(Tab t) { return t == Tab.Live ? "live channels" : t == Tab.Followed ? "all followed channels" : "recently watched"; }

    string Describe(Item it)
    {
        string s = it.Name + (Favourites.Contains(it.Login) ? ", favourite" : "");
        if (!it.Live) return s + ", offline" + (it.Note.Length > 0 ? ", " + it.Note : "");
        string up = Fmt.Uptime(it.Started, DateTimeOffset.UtcNow);
        return s + ", live" + (it.Viewers > 0 ? ", " + Fmt.Viewers(it.Viewers) + " viewers" : "") + (up.Length > 0 ? ", for " + up : "")
            + (it.Game.Length > 0 ? ", " + it.Game : "") + (it.Title.Length > 0 ? ". " + it.Title : "");
    }

    int lastAnnounced = -1;
    void Announce()   // the selection moved: screen readers follow it
    {
        if (sel == lastAnnounced || sel < 0 || !IsHandleCreated) return;
        lastAnnounced = sel;
        AccessibilityNotifyClients(AccessibleEvents.Focus, sel);
        AccessibilityNotifyClients(AccessibleEvents.Selection, sel);
        uia.Selected(sel);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            liveTimer.Dispose(); searchDelay.Dispose(); avatarDelay.Dispose(); tip.Dispose(); menu.Dispose();
            foreach (var p in thumbs.Values.Concat(avatars.Values)) if (p.Bmp != null) p.Bmp.Dispose();
            thumbs.Clear(); avatars.Clear();
        }
        base.Dispose(disposing);
    }
}
