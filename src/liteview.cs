// LiteView for Twitch (MIT licence): one window, like Twitch.
//   Browse: your Twitch "Following > Live" page (Edge WebView2, you log in once).
//   Watch:  click a streamer -> followed channels | video (Streamlink + mpv) | native chat
//           (Twitch IRC + 7TV/BTTV/FFZ emotes) in this window; the video area stays 16:9.
// Everything runs off the game's CPU die and below the game in priority.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using LiteViewCore;

partial class LiteView : Form   // fork additions: src/app/LiteView.Fork.cs
{
    public struct RECT { public int L, T, R, B; }
    [DllImport("kernel32.dll")] static extern IntPtr CreateJobObject(IntPtr a, string name);
    [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr proc);
    [DllImport("kernel32.dll")] static extern bool TerminateJobObject(IntPtr job, uint code);
    [StructLayout(LayoutKind.Sequential)] struct JOBLIMITS { public long a, b; public uint flags; public UIntPtr minWs, maxWs; public uint procs; public UIntPtr aff; public uint prio, sched; public ulong io1, io2, io3, io4, io5, io6; public UIntPtr pm, jm, ppm, pjm; }
    [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int cls, ref JOBLIMITS info, int len);
    [DllImport("user32.dll")] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll")] static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessW(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string dir, ref Native.STARTUPINFO si, out Native.PROCESS_INFORMATION pi);

    static readonly Color Bg = Color.FromArgb(14, 14, 16), Bar = Color.FromArgb(24, 24, 27), Hover = Color.FromArgb(38, 38, 44),
        Fg = Color.FromArgb(239, 239, 241), Dim = Color.FromArgb(173, 173, 184), Accent = Color.FromArgb(145, 71, 255), Live = Color.FromArgb(235, 4, 0);
    const int BarH = 40, ChatW = 340;
    const string Home = "https://www.twitch.tv/directory/following/live";
    static readonly string[] Qualities = Quality.Choices;

    readonly string root, tw, iniPath, streamlink, mpv, slLog;
    volatile string pipeName = "";   // a new random name per stream (see Watch)
    readonly ulong mask = Native.NonCacheDieMask();

    readonly Panel bar = new Panel(), body = new Panel(), video = new Panel(), chatBox = new Panel();
    WebView2 web, chatWeb = new WebView2();
    CoreWebView2Environment env; bool chatReady, chatStarting, webChat, loggingIn; string chatUrl;
    readonly ChatPanel nchat = new ChatPanel();
    readonly FollowBar side = new FollowBar();
    readonly System.Windows.Forms.Timer hourly = new System.Windows.Forms.Timer { Interval = 3600000 };
    readonly Button back = new Button(), fsBtn = new Button();
    readonly Label title = new Label(), liveDot = new Label(), status = new Label();
    readonly TextBox search = new TextBox();
    readonly ComboBox quality = new ComboBox();
    readonly CheckBox chatOn = new CheckBox(), rtx = new CheckBox();
    readonly ComboBox picture = new ComboBox();
    static readonly string[] Pictures = { "Picture: Off", "Picture: Enhance", "Picture: High", "Picture: Ultra" };
    static readonly bool HasNvidia = HasRtxCard();   // fork: an NVIDIA RTX card, not only an NVIDIA driver (src/app/LiteView.Screens.cs)
    int videoH; string appliedPic, appliedVf; DateTime lastResize = DateTime.MinValue;
    readonly Button muteBtn = new Button();
    readonly VolumeBar volBar = new VolumeBar();
    int volume = 70; bool muted;
    NamedPipeClientStream mpvPipe; readonly object pipeLock = new object();
    readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer();

    bool watching, fullscreen, webReady;
    string channel; int slPid; DateTime slStart; IntPtr job = IntPtr.Zero;
    Rectangle normalBounds; FormWindowState normalState;
    DateTime hiddenAt = DateTime.MinValue;

    LiteView()
    {
        root = Path.GetDirectoryName(typeof(LiteView).Assembly.Location);
        tw = Path.Combine(root, "twitch");
        string exe = Path.GetFileNameWithoutExtension(typeof(LiteView).Assembly.Location);   // LiteView.exe -> liteview.ini (a test copy gets its own)
        iniPath = Path.Combine(root, exe.ToLowerInvariant() + ".ini");
        Tw.Init(Path.Combine(root, "liteview.token"));
        ChatPanel.RecentPath = Path.Combine(root, "liteview.emotes");
        try { streamlink = Directory.GetFiles(Path.Combine(tw, "streamlink"), "streamlinkw.exe", SearchOption.AllDirectories).FirstOrDefault(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        mpv = Path.Combine(tw, "mpv", "mpv.exe");
        slLog = Path.Combine(root, exe.ToLowerInvariant() == "liteview" ? "streamlink.log" : exe + "-streamlink.log");

        Text = "LiteView for Twitch"; BackColor = Bg; ForeColor = Fg; Font = new Font("Segoe UI", 9.5f);
        KeyPreview = true; StartPosition = FormStartPosition.Manual; MinimumSize = new Size(640, 400);
        try { Icon = Icon.ExtractAssociatedIcon(mpv); } catch { }

        // top bar
        bar.Dock = DockStyle.Top; bar.Height = BarH; bar.BackColor = Bar;
        Flat(back, "◀  Following"); back.SetBounds(6, 5, 112, 30); back.Click += (s, e) => Browse();
        liveDot.Text = "●"; liveDot.ForeColor = Live; liveDot.AutoSize = true; liveDot.Font = new Font("Segoe UI", 9f); liveDot.Location = new Point(126, 11);
        title.AutoSize = true; title.Font = new Font("Segoe UI Semibold", 10.5f); title.Location = new Point(140, 9); title.ForeColor = Fg;
        search.SetBounds(12, 9, 220, 24); search.BackColor = Hover; search.ForeColor = Fg; search.BorderStyle = BorderStyle.FixedSingle;
        search.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Watch(search.Text); } };
        SetCue(search, "Go to channel...");
        status.AutoSize = true; status.ForeColor = Dim; status.Location = new Point(250, 12);
        quality.DropDownStyle = ComboBoxStyle.DropDownList; quality.FlatStyle = FlatStyle.Flat; quality.BackColor = Hover; quality.ForeColor = Fg;
        quality.Items.AddRange(Qualities); quality.Width = 96; quality.HandleCreated += (s, e) => SetWindowTheme(quality.Handle, "DarkMode_CFD", null); quality.DrawMode = DrawMode.OwnerDrawFixed; quality.ItemHeight = 20;
        quality.DrawItem += (s, e) => { if (e.Index < 0) return; bool sel = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
            using (var b = new SolidBrush(sel ? Accent : Hover)) e.Graphics.FillRectangle(b, e.Bounds);
            TextRenderer.DrawText(e.Graphics, (string)quality.Items[e.Index], quality.Font, e.Bounds, Fg, TextFormatFlags.VerticalCenter | TextFormatFlags.Left); }; quality.SelectedIndexChanged += (s, e) => { if (watching && quality.Focused) Watch(channel); };
        chatOn.Text = "Chat"; chatOn.ForeColor = Dim; chatOn.AutoSize = true; chatOn.ContextMenuStrip = nchat.Menu; chatOn.CheckedChanged += (s, e) => { if (watching) { if (chatOn.Checked) StartChat(); else StopChat(); Relayout(); FitWindow(); } };
        Flat(fsBtn, "⛶"); fsBtn.Width = 34; fsBtn.Click += (s, e) => ToggleFullscreen();
        Flat(muteBtn, "\uE767"); muteBtn.Font = new Font("Segoe MDL2 Assets", 11f); muteBtn.Width = 34;
        muteBtn.Click += (s, e) => { muted = !muted; SendMpv("{\"command\":[\"set_property\",\"mute\"," + (muted ? "true" : "false") + "]}"); UpdateMute(); SaveSettings(); };
        new ToolTip().SetToolTip(muteBtn, "Mute (M on the video)");
        volBar.Changed += v => { volume = v; if (muted) { muted = false; SendMpv("{\"command\":[\"set_property\",\"mute\",false]}"); }
            SendMpv("{\"command\":[\"set_property\",\"volume\"," + v + "]}"); UpdateMute(); };
        volBar.MouseUp += (s, e) => SaveSettings();
        new ToolTip().SetToolTip(volBar, "Volume (or the mouse wheel on the video)");
        DarkCombo(picture, Pictures, 128); picture.SelectedIndexChanged += (s, e) => { ApplyEnhance(true); SaveSettings(); };
        new ToolTip().SetToolTip(picture, "Enhance: AMD FSR 1 upscaling + sharpening, any graphics card (light).\nHigh: FSRCNNX neural upscaling, any graphics card (more GPU).\nHigh: + Adaptive Sharpen on the final picture.\nUltra: best possible - NLMeans clean-up, bigger FSRCNNX, KrigBilateral colour, SSimDownscaler, strong sharpening, motion smoothing (most GPU).\nBoth add AMD CAS sharpening at 1:1 and debanding.");
        rtx.Text = "RTX"; rtx.ForeColor = Dim; rtx.AutoSize = true; rtx.CheckedChanged += (s, e) => { ApplyEnhance(true); SaveSettings(); };
        new ToolTip().SetToolTip(rtx, "NVIDIA RTX Video AI clean-up. With High/Ultra it cleans the stream first and FSRCNNX upscales after it;\nwith Off/Enhance RTX does the upscaling. Uses a lot more GPU (Ultra + RTX: about 25% of an RTX 5080).\nNeeds RTX Video Enhancement > Super Resolution on in the NVIDIA app.");
        new ToolTip().SetToolTip(chatOn, "Show the chat. Right-click: native chat or Twitch web chat, log in or out.");
        new ToolTip().SetToolTip(fsBtn, "Fullscreen (or double-click the video). Esc to leave.");
        bar.Controls.AddRange(new Control[] { back, liveDot, title, search, status, quality, chatOn, picture, rtx, muteBtn, volBar, fsBtn });
        bar.Resize += (s, e) => LayoutBar();

        // body: the Following page, or followed channels + video + chat
        body.Dock = DockStyle.Fill; body.BackColor = Bg;
        video.BackColor = Color.Black; chatBox.BackColor = Bg;
        video.Resize += (s, e) => lastResize = DateTime.Now;
        chatWeb.Dock = DockStyle.Fill; chatWeb.DefaultBackgroundColor = Bg; chatBox.Controls.Add(chatWeb);
        nchat.Dock = DockStyle.Fill; chatBox.Controls.Add(nchat);
        nchat.LoginRequested += Login; side.LoginRequested += Login;
        side.Pick += n => Watch(n);
        side.Toggled += () => { Relayout(); FitWindow(); SaveSettings(); };
        var webItem = new ToolStripMenuItem("Twitch web chat (with 7TV)") { CheckOnClick = false };
        var logItem = new ToolStripMenuItem();
        webItem.Click += (s, e) => SetWebChat(!webChat);
        logItem.Click += (s, e) => { if (Tw.Token == null) Login(); else { Tw.LogOut(); OnLogin(); } };
        nchat.Menu.Items.AddRange(new ToolStripItem[] { webItem, logItem });
        AddForkMenuItems(nchat.Menu);
        nchat.Menu.Opening += (s, e) => { webItem.Checked = webChat; logItem.Text = Tw.Token == null ? "Log in with Twitch" : "Log out (" + (Tw.Login ?? "Twitch") + ")"; };
        body.Controls.AddRange(new Control[] { side, video, chatBox });
        body.Resize += (s, e) => Relayout();
        Controls.Add(body); Controls.Add(bar);

        tick.Interval = 500; tick.Tick += (s, e) => OnTick();
        InitFork();
        hourly.Tick += (s, e) => CheckLogin(); hourly.Start();   // Twitch asks apps to re-check tokens hourly
        KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape && fullscreen) ToggleFullscreen(); else if (e.KeyCode == Keys.F11 && watching) ToggleFullscreen(); };
        FormClosing += (s, e) => { SaveSettings(); CancelRetry(); StopStream(); nchat.Part(); Log.Info("closing"); };
        Resize += (s, e) => OnMinimizeChange();

        LoadSettings();
        SetMode(false);
        InitBrowse();   // LiteView's Following page, or the Twitch website page (src/app/LiteView.Browse.cs)
        CheckLogin();
        Shown += (s, e) => { if (startChannel != null) Watch(startChannel); };
        new Thread(PipeLoop) { IsBackground = true, Priority = ThreadPriority.BelowNormal }.Start();
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr h, string app, string idList);

    static void Flat(Button b, string text)
    {
        b.Text = text; b.FlatStyle = FlatStyle.Flat; b.BackColor = Bar; b.ForeColor = Fg; b.Cursor = Cursors.Hand; b.Height = 30;
        b.FlatAppearance.BorderSize = 0; b.FlatAppearance.MouseOverBackColor = Hover; b.FlatAppearance.MouseDownBackColor = Accent;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);
    static void SetCue(TextBox t, string cue) { t.HandleCreated += (s, e) => SendMessage(t.Handle, 0x1501, (IntPtr)1, cue); }

    void LayoutBar()
    {
        int x = bar.Width - 6;
        foreach (Control c in new Control[] { menuBtn, fsBtn, volBar, muteBtn, rtx, picture, chatOn, quality })
        {
            x -= c.Width + 6;
            c.Location = new Point(x, (BarH - c.Height) / 2);
        }
        FitStatus();
    }

    void SetMode(bool watch)
    {
        watching = watch;
        back.Visible = liveDot.Visible = title.Visible = fsBtn.Visible = muteBtn.Visible = volBar.Visible = picture.Visible = watch; rtx.Visible = watch && HasNvidia;
        search.Visible = !watch;
        status.Location = new Point(watch ? title.Right + 14 : search.Right + 14, 12);
        if (web != null) web.Visible = !watch;
        ShowBrowse(!watch);
        video.Visible = watch; chatBox.Visible = watch && chatOn.Checked; side.Visible = watch;
        side.Run(watch);
        Relayout(); LayoutBar();
    }

    void Relayout()
    {
        if (!watching) return;
        int cw = chatOn.Checked && !fullscreen ? ChatW : 0, sw = SideW;
        if (cw > 0 && WindowState == FormWindowState.Maximized) cw = Math.Max(300, Math.Min(640, body.Width - sw - body.Height * 16 / 9));
        side.Visible = sw > 0; side.SetBounds(0, 0, sw, body.Height);
        video.SetBounds(sw, 0, Math.Max(1, body.Width - cw - sw), body.Height);
        chatBox.SetBounds(body.Width - cw, 0, cw, body.Height);
        chatBox.Visible = cw > 0;
    }

    int SideW { get { return watching && !fullscreen ? side.Width : 0; } }

    // Keep the video area 16:9 while the window is resized (no black bars).
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0214 && watching && !fullscreen)   // WM_SIZING
        {
            var r = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
            int edge = m.WParam.ToInt32();
            int ncW = Width - ClientSize.Width, ncH = Height - ClientSize.Height, cw = (chatOn.Checked ? ChatW : 0) + SideW;
            if (edge == 3 || edge == 6) { int vw = (r.B - r.T - ncH - BarH) * 16 / 9; r.R = r.L + vw + cw + ncW; }
            else
            {
                int h = (r.R - r.L - ncW - cw) * 9 / 16 + BarH + ncH;
                if (edge == 3 || edge == 4 || edge == 5) r.T = r.B - h; else r.B = r.T + h;
            }
            Marshal.StructureToPtr(r, m.LParam, false);
        }
        base.WndProc(ref m);
    }

    void FitWindow()
    {
        if (!watching || fullscreen || WindowState != FormWindowState.Normal) return;
        var wa = Screen.FromControl(this).WorkingArea;
        int ncW = Width - ClientSize.Width, ncH = Height - ClientSize.Height, cw = (chatOn.Checked ? ChatW : 0) + SideW;
        int vh = ClientSize.Height - BarH, vw = vh * 16 / 9;
        if (vw + cw + ncW > wa.Width) { vw = wa.Width - cw - ncW; vh = vw * 9 / 16; }
        var b = new Rectangle(Left, Top, vw + cw + ncW, vh + BarH + ncH);
        if (b.Bottom > wa.Bottom) b.Y = wa.Bottom - b.Height; if (b.Y < wa.Top) b.Y = wa.Top;
        if (b.Right > wa.Right) b.X = wa.Right - b.Width; if (b.X < wa.Left) b.X = wa.Left;
        Bounds = b;
    }

    // One WebView2 environment (browser) for the Following page, the web chat and the login page.
    // While you watch with the native chat no browser runs at all: it closes and starts again on Browse.
    async System.Threading.Tasks.Task<CoreWebView2Environment> Env()
    {
        if (env == null)
        {
            // no browser extensions: the fork never copies or downloads them (docs/BASELINE_AUDIT.md SEC-1)
            var opts = new CoreWebView2EnvironmentOptions(); opts.AreBrowserExtensionsEnabled = false;
            // darker, crisper web text (Edge's low-contrast text tuning off); every WebView2 on this profile uses the same options
            opts.AdditionalBrowserArguments = "--disable-features=LowTextContrast --text-contrast=200";
            env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(root, "webview"), opts);
        }
        return env;
    }

    void DropWeb()
    {
        if (web == null) return;
        var w = web; web = null; webReady = false;
        body.Controls.Remove(w); w.Dispose();
        if (!chatReady && !loggingIn) env = null;
    }

    async void InitWeb()
    {
        if (web != null) return;
        var w = new WebView2(); w.Dock = DockStyle.Fill; w.DefaultBackgroundColor = Bg; w.Visible = !watching;
        web = w; body.Controls.Add(w); w.BringToFront();
        try
        {
            await w.EnsureCoreWebView2Async(await Env());
            if (web != w) return;
            var core = w.CoreWebView2;
            Harden(core, true);
            core.NewWindowRequested += (s, e) => { e.Handled = true; if (SafeUrl.ForWebView(e.Uri) == SafeUrl.Nav.Allow) core.Navigate(e.Uri); else if (e.IsUserInitiated) Launcher.Open(e.Uri); };
            core.WebMessageReceived += (s, e) => { string n = WebMessage.Watch(TryMessage(e), e.Source, webSecret); if (n != null) BeginInvoke((Action)(() => Watch(n))); };
            // A plain click on a channel (card, sidebar, search result) watches it here instead of opening the heavy page.
            await core.AddScriptToExecuteOnDocumentCreatedAsync(@"(function(){
var K='" + WebMessage.WatchPrefix + webSecret + @":',W=window.chrome&&window.chrome.webview,P=W&&W.postMessage.bind(W);if(!P)return;
var R={directory:1,videos:1,settings:1,subscriptions:1,inventory:1,wallet:1,drops:1,search:1,downloads:1,jobs:1,p:1,store:1,turbo:1,prime:1,friends:1,messages:1,popout:1,moderator:1,u:1,login:1,signup:1,logout:1,team:1,bits:1,embed:1,following:1,activate:1};
function ch(a){try{var u=new URL(a.getAttribute('href'),location.href);if(!/(^|\.)twitch\.tv$/.test(u.hostname))return null;
var p=u.pathname.split('/').filter(function(x){return x;});if(p.length!==1)return null;var n=p[0].toLowerCase();
if(R[n]||!/^[a-z0-9_]{2,25}$/.test(n))return null;return n;}catch(e){return null;}}
document.addEventListener('click',function(e){if(e.button!==0||e.ctrlKey||e.shiftKey||e.metaKey||e.altKey)return;
var a=e.target&&e.target.closest?e.target.closest('a[href]'):null;if(!a)return;var n=ch(a);if(!n)return;
e.preventDefault();e.stopImmediatePropagation();P(K+n);},true);})();");
            if (web != w) return;
            await ApplyPendingSignOut(core);
            if (web != w) return;
            webReady = true;
            core.Navigate(Home);
        }
        catch (Exception ex) { Log.Error("Following page", ex); if (web == w) ShowProblem("Following page unavailable", ex.Message + "\n\nThe Twitch website page needs the Microsoft Edge WebView2 Runtime. LiteView's own Following page does not (menu at the top right > Following page).", null); }
    }

    void Browse()
    {
        StopStream(); StopChat();
        if (fullscreen) ToggleFullscreen();
        SetMode(false);
        if (BrowseNative()) return;
        if (web == null) InitWeb();
        else if (webReady)
        {
            try { web.CoreWebView2.Resume(); } catch { }
            web.CoreWebView2.Navigate(Home);   // fresh "who's live"
        }
        status.Text = "";
    }

    void Watch(string raw) { Watch(raw, false); }

    void Watch(string raw, bool isRetry)
    {
        string name = ChannelName.Normalize(raw);
        if (name == null)
        {
            if (!string.IsNullOrWhiteSpace(raw)) { status.ForeColor = Color.FromArgb(255, 140, 140); status.Text = "That is not a Twitch channel name."; }
            return;
        }
        if (!isRetry) CancelRetry();
        bool wasWatching = watching;
        channel = name; title.Text = name; search.Text = "";
        SetMode(true);
        if (!wasWatching) FitWindow();
        side.Current = name; side.Invalidate();
        if (!webChat) DropWeb();   // native chat: no browser at all while watching
        else if (webReady) { hiddenAt = DateTime.Now; web.CoreWebView2.Navigate("about:blank"); }   // nothing runs behind the video

        StopStream();
        string missing = MissingPlayerFiles();
        if (missing != null) { ShowProblem(missing, missing + "\n\nLiteView plays streams with the Streamlink and mpv copies in its twitch folder. An antivirus may have removed a file: unzip LiteView again into a new folder.", null); Log.Error(missing); }
        else StartStreamProcess(name, (string)quality.SelectedItem);

        if (chatOn.Checked) StartChat();
        tick.Start();
        browse.Watched(name);
        SaveSettings();
    }

    async void SuspendWeb() { try { if (web != null) await web.CoreWebView2.TrySuspendAsync(); } catch { } }

    // Chat: the native chat, or (fallback) Twitch's own popout chat with 7TV in a WebView2.
    async void StartChat()
    {
        if (channel == null) return;
        nchat.Visible = !webChat; chatWeb.Visible = webChat;
        if (!webChat) { nchat.Join(channel); return; }
        var cw = chatWeb;   // SetWebChat replaces the control: work on this one only, and check after every await
        try
        {
            if (!chatReady)
            {
                if (chatStarting) return;
                chatStarting = true;
                try
                {
                    var en = await Env();
                    if (cw != chatWeb || cw.IsDisposed || !webChat || !watching) return;   // native chat chosen / left meanwhile
                    await cw.EnsureCoreWebView2Async(en);
                    if (cw != chatWeb || cw.IsDisposed) return;
                    Harden(cw.CoreWebView2, false);
                    cw.CoreWebView2.NewWindowRequested += (s, e) => { e.Handled = true; if (e.IsUserInitiated) Launcher.Open(e.Uri); };   // upstream: Process.Start(any URI)
                    chatReady = true;
                }
                finally { chatStarting = false; }
            }
            // still wanted? (chat hidden, back to Following, or native chat chosen while it started)
            if (cw != chatWeb || !watching || !webChat || !chatOn.Checked || channel == null) return;
            chatUrl = "https://www.twitch.tv/popout/" + channel + "/chat?popout=&darkpopout";
            if (cw.Source == null || cw.Source.AbsoluteUri != chatUrl) cw.CoreWebView2.Navigate(chatUrl);
        }
        catch (Exception ex) { Log.Error("web chat", ex); }
    }

    void StopChat()
    {
        nchat.Part();
        if (chatReady) { chatWeb.CoreWebView2.Navigate("about:blank"); chatUrl = null; }
    }

    // gear menu / right-click on "Chat": native chat or Twitch's web chat
    void SetWebChat(bool on)
    {
        if (on == webChat) return;
        StopChat();
        webChat = on;
        if (!on && chatReady)
        {   // free the web chat's browser completely
            chatBox.Controls.Remove(chatWeb); chatWeb.Dispose(); chatReady = false;
            chatWeb = new WebView2(); chatWeb.Dock = DockStyle.Fill; chatWeb.DefaultBackgroundColor = Bg; chatBox.Controls.Add(chatWeb);
            if (web == null && !loggingIn) env = null;
        }
        if (watching && chatOn.Checked) StartChat();
        SaveSettings();
    }

    async void CheckLogin()
    {
        await Tw.Validate();
        nchat.UpdateLogin();
        if (watching) side.Reload();
    }

    void OnLogin()
    {
        nchat.UpdateLogin();
        if (watching && !webChat && chatOn.Checked) nchat.Reconnect();
        side.Reload();
        browse.LoginChanged();
        status.ForeColor = Dim; status.Text = Tw.Login != null ? "Logged in as " + Tw.Login : "";
    }

    // Twitch login (Device Code Grant Flow): Twitch's own "activate" page opens in the system browser,
    // LiteView shows the code to check and waits for "Activate" + "Authorize" (src/app/LoginDialog.cs).
    async void Login()
    {
        if (loggingIn) return;
        loggingIn = true;
        LoginDialog f = null;
        try
        {
            var d = await Tw.StartDevice();
            if (d == null) { ShowProblem("Could not reach Twitch to log in", "Twitch's login service did not answer. Check the internet connection (a VPN can also be the cause) and try again.", Login); return; }
            string code = Tw.S(d, "device_code"), user = Tw.S(d, "user_code");
            Uri page = SafeUrl.ActivationPage(Tw.S(d, "verification_uri"));
            int every = Math.Max(1, (int)Tw.N(d, "interval")); long life = Tw.N(d, "expires_in");
            f = new LoginDialog(user, page) { Icon = Icon };
            f.Location = new Point(Left + Math.Max(0, (Width - f.Width) / 2), Top + Math.Max(0, (Height - f.Height) / 2));
            f.Show(this);
            Launcher.Open(page);
            Log.Info("login: device code requested, browser opened");
            var end = DateTime.Now.AddSeconds(life > 0 ? life : 1800);
            while (!f.IsDisposed && DateTime.Now < end)
            {
                await System.Threading.Tasks.Task.Delay(every * 1000);
                if (f.IsDisposed) break;
                string r = await Tw.PollDevice(code);
                if (r == "ok") { Log.Info("login: signed in"); f.Close(); OnLogin(); break; }
                if (r == "slow_down") every++;
                else if (r != "authorization_pending") { Log.Warn("login: " + r); ShowProblem("Twitch login did not finish", "Twitch answered: " + r + "\n\nThe code may have expired or been denied. Try again.", Login); break; }
            }
        }
        catch (Exception ex) { Log.Error("login", ex); ShowProblem("Twitch login failed", ex.Message, Login); }
        finally
        {
            loggingIn = false;
            if (f != null && !f.IsDisposed) f.Close();
        }
    }
    void StopStream()
    {
        streamOn.Reset();   // the mpv pipe thread goes idle
        if (job != IntPtr.Zero) { TerminateJobObject(job, 0); Native.CloseHandle(job); job = IntPtr.Zero; }
        slPid = 0;
        if (slProc != null) { slProc.Dispose(); slProc = null; }
    }

    void OnTick()
    {
        if (!watching) { tick.Stop(); return; }
        ResyncMpv();
        if ((DateTime.Now - lastResize).TotalMilliseconds > 600) ApplyEnhance(false);
        if (slPid == 0 || slProc == null) return;
        bool alive = false;
        try { alive = !slProc.HasExited; } catch { }
        // playing = OUR mpv answered on this stream's pipe (upstream looked for any mpv.exe on the PC)
        if (alive) { if (mpvGen == gen) { if (!played) { played = true; Log.Info("stream " + gen + ": playing"); } status.Text = ""; } else OpeningStatus(); return; }
        OnStreamExited();
    }

    void ToggleFullscreen()
    {
        if (!watching) return;
        if (!fullscreen)
        {
            normalState = WindowState; normalBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            fullscreen = true; bar.Visible = false;
            FormBorderStyle = FormBorderStyle.None; WindowState = FormWindowState.Normal;
            Bounds = Screen.FromControl(this).Bounds;
        }
        else
        {
            fullscreen = false; bar.Visible = true;
            FormBorderStyle = FormBorderStyle.Sizable; Bounds = RestoreTarget(normalBounds); WindowState = normalState;
        }
        Relayout();
    }

    // Picture chain (shaders sent once per stream / setting; RTX re-sent after resizes):
    //   Enhance: AMD FSR 1 upscale + AMD CAS at 1:1 + debanding
    //   High:    FSRCNNX (8) upscale + Adaptive Sharpen on the final picture + debanding
    //   Ultra:   KrigBilateral colour + NLMeans clean-up/sharpen + FSRCNNX (16) + SSimDownscaler
    //            + strong Adaptive Sharpen + debanding + high-quality scaling + motion smoothing
    //   The FSRCNNX copies run for ANY upscale, so they work windowed/maximized too, not only fullscreen.
    //   RTX (NVIDIA): with High/Ultra it does its AI clean-up (a 1.05x step, the smallest that switches it
    //   on) and FSRCNNX upscales after it; with Off/Enhance RTX does the whole upscale.
    string Shader(string name) { return Path.Combine(tw, "mpv", "portable_config", "shaders", name).Replace('\\', '/'); }

    void ApplyEnhance(bool force)
    {
        if (!watching) return;
        int pic = Math.Max(0, picture.SelectedIndex);
        if (force || appliedPic != pic.ToString())
        {
            bool ultra = pic == 3;
            SendMpv("{\"command\":[\"apply-profile\",\"" + (ultra ? "high-quality" : "fast") + "\"]}");
            var list = new List<string>();
            if (pic == 1) { list.Add("FSR.glsl"); list.Add("CAS.glsl"); }
            else if (pic == 2) { list.Add("FSRCNNX_x2_8-0-4-1_any.glsl"); list.Add("adaptive-sharpen.glsl"); }
            else if (ultra) { list.Add("KrigBilateral.glsl"); list.Add("nlmeans_sharpen_denoise.glsl"); list.Add("FSRCNNX_x2_16-0-4-1_any.glsl"); list.Add("SSimDownscaler.glsl"); list.Add("adaptive-sharpen_strong.glsl"); }
            SendMpv("{\"command\":[\"change-list\",\"glsl-shaders\",\"clr\",\"\"]}");
            foreach (var n in list) SendMpv("{\"command\":[\"change-list\",\"glsl-shaders\",\"append\",\"" + Shader(n) + "\"]}");
            SendMpv("{\"command\":[\"set_property\",\"deband\"," + (pic > 0 ? "true" : "false") + "]}");
            // Ultra: motion smoothing - frames timed to the monitor (60 fps streams even on 144/240 Hz screens)
            SendMpv("{\"command\":[\"set_property\",\"video-sync\",\"" + (ultra ? "display-resample" : "audio") + "\"]}");
            SendMpv("{\"command\":[\"set_property\",\"interpolation\"," + (ultra ? "true" : "false") + "]}");
            if (ultra) SendMpv("{\"command\":[\"set_property\",\"tscale\",\"oversample\"]}");
            appliedPic = pic.ToString();
        }
        double s = videoH > 0 ? Math.Max(1.0, Math.Round((double)video.Height / videoH, 2)) : 1.0;
        bool useRtx = rtx.Checked && HasNvidia;
        string vf = !useRtx ? "off" : pic >= 2 ? "1.05" : s > 1.04 ? s.ToString("0.00", CultureInfo.InvariantCulture) : "off";
        double prev, now;
        bool near = vf != "off" && double.TryParse(appliedVf, NumberStyles.Float, CultureInfo.InvariantCulture, out prev)
            && double.TryParse(vf, NumberStyles.Float, CultureInfo.InvariantCulture, out now) && Math.Abs(prev - now) < 0.04;
        if (force || (appliedVf != vf && !near))
        {
            SendMpv(vf == "off" ? "{\"command\":[\"vf\",\"clr\",\"\"]}" : "{\"command\":[\"vf\",\"set\",\"d3d11vpp=scaling-mode=nvidia:scale=" + vf + "\"]}");
            appliedVf = vf;
        }
    }
    void DarkCombo(ComboBox cb, string[] items, int width)
    {
        cb.DropDownStyle = ComboBoxStyle.DropDownList; cb.FlatStyle = FlatStyle.Flat; cb.BackColor = Hover; cb.ForeColor = Fg;
        cb.Items.AddRange(items); cb.Width = width; cb.DrawMode = DrawMode.OwnerDrawFixed; cb.ItemHeight = 20;
        cb.HandleCreated += (s, e) => SetWindowTheme(cb.Handle, "DarkMode_CFD", null);
        cb.DrawItem += (s, e) => { if (e.Index < 0) return; bool sel = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
            using (var b = new SolidBrush(sel ? Accent : Hover)) e.Graphics.FillRectangle(b, e.Bounds);
            TextRenderer.DrawText(e.Graphics, (string)cb.Items[e.Index], cb.Font, e.Bounds, Fg, TextFormatFlags.VerticalCenter | TextFormatFlags.Left); };
    }
    void UpdateMute() { muteBtn.Text = muted || volume == 0 ? "\uE74F" : "\uE767"; volBar.Value = Math.Min(100, volume); volBar.Invalidate(); }

    // Queued for the mpv writer thread (src/app/LiteView.Fork.cs): a stalled mpv can no longer block the UI.
    void SendMpv(string json) { QueueMpv(gen, json); }

    // mpv's IPC pipe: set our volume/mute when a stream starts; hear double-click / Esc / F
    // on the video and volume changes from the mouse wheel. The thread sleeps while nothing plays,
    // and every callback carries the stream's generation so a dying stream cannot touch the new one.
    void PipeLoop()
    {
        while (true)
        {
            streamOn.WaitOne();
            int g = gen; string name = pipeName;
            if (name.Length == 0) { Thread.Sleep(200); continue; }
            try
            {
                using (var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    pipe.Connect(1000);
                    if (g != gen) continue;
                    lock (pipeLock) { mpvPipe = pipe; mpvPipeGen = g; }
                    SendMpv("{\"command\":[\"set_property\",\"volume\"," + volume + "]}");
                    SendMpv("{\"command\":[\"set_property\",\"mute\"," + (muted ? "true" : "false") + "]}");
                    SendMpv("{\"command\":[\"observe_property\",1,\"volume\"]}");
                    SendMpv("{\"command\":[\"observe_property\",2,\"mute\"]}");
                    SendMpv("{\"command\":[\"observe_property\",3,\"height\"]}");
                    Ui(g, () => { appliedPic = appliedVf = null; videoH = 0; mpvGen = g; });
                    using (var rd = new StreamReader(pipe))
                    {
                        string line;
                        while ((line = rd.ReadLine()) != null)
                        {
                            if (line.Length > 65536) continue;
                            if (line.Contains("client-message"))
                            {
                                if (line.Contains("fs-toggle")) Ui(g, ToggleFullscreen);
                                else if (line.Contains("fs-exit")) Ui(g, () => { if (fullscreen) ToggleFullscreen(); });
                            }
                            else if (line.Contains("property-change"))
                            {
                                var d = Regex.Match(line, "\"data\":([0-9.]+|true|false)");
                                if (!d.Success) continue;
                                double num;
                                if (line.Contains("\"name\":\"volume\""))
                                {
                                    if (!double.TryParse(d.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out num)) continue;
                                    int v = (int)Math.Round(Math.Max(0, Math.Min(1000, num)));
                                    Ui(g, () => { if (mpvResync) return; volume = v; UpdateMute(); });   // stale echo while commands were dropped
                                }
                                else if (line.Contains("\"name\":\"height\""))
                                {
                                    if (!double.TryParse(d.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out num)) continue;
                                    int hh = (int)Math.Max(0, Math.Min(100000, num));
                                    Ui(g, () => { videoH = hh; ApplyEnhance(false); });
                                }
                                else if (line.Contains("\"name\":\"mute\""))
                                {
                                    bool m = d.Groups[1].Value == "true";
                                    Ui(g, () => { if (mpvResync) return; muted = m; UpdateMute(); });
                                }
                            }
                        }
                    }
                }
            }
            catch { Thread.Sleep(500); }   // mpv not up yet (Streamlink is still opening the stream), or it ended
            finally { lock (pipeLock) mpvPipe = null; }
        }
    }

    void Ui(int g, Action a)
    {
        try { if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)(() => { if (g == gen) a(); })); } catch (InvalidOperationException) { }
    }

    void OnMinimizeChange()
    {
        if (!webReady || watching) return;
        if (WindowState == FormWindowState.Minimized) { hiddenAt = DateTime.Now; SuspendWeb(); }
    }

    void LoadSettings()
    {
        settingsLoading = true;   // the controls' change events below must not save a half-loaded state
        bool fromBackup;
        var ini = IniStore.Read(iniPath, out fromBackup);
        if (fromBackup) Log.Warn("settings: " + Path.GetFileName(iniPath) + " was unreadable, used the last good copy");
        int q = Array.IndexOf(Qualities, Native.Get(ini, "quality", "Best")); quality.SelectedIndex = q < 0 ? 0 : q;
        chatOn.Checked = Native.Get(ini, "chat", "1") == "1";
        int.TryParse(Native.Get(ini, "volume", "70"), out volume); volume = Math.Max(0, Math.Min(100, volume));
        muted = Native.Get(ini, "muted", "0") == "1"; UpdateMute();
        int pi = Array.IndexOf(Pictures, "Picture: " + Native.Get(ini, "picture", "Enhance")); picture.SelectedIndex = pi < 0 ? 1 : pi;
        rtx.Checked = Native.Get(ini, "rtx", "1") == "1";
        webChat = Native.Get(ini, "chatmode", "native") == "web";
        side.Collapsed = Native.Get(ini, "sidebar", "open") == "closed";
        int x, y, w, h;
        if (int.TryParse(Native.Get(ini, "x", ""), out x) && int.TryParse(Native.Get(ini, "y", ""), out y) &&
            int.TryParse(Native.Get(ini, "w", ""), out w) && int.TryParse(Native.Get(ini, "h", ""), out h) &&
            Screen.AllScreens.Any(s => s.WorkingArea.Contains(x + 60, y + 20)))
            Bounds = new Rectangle(x, y, w, h);
        else
        {
            // first run: centre on the screen of the last saved position (an older install's), else the main one
            var scr = int.TryParse(Native.Get(ini, "x", ""), out x) && int.TryParse(Native.Get(ini, "y", ""), out y)
                ? Screen.FromPoint(new Point(x + 60, y + 20)) : Screen.PrimaryScreen;
            var wa = scr.WorkingArea;
            Bounds = new Rectangle(wa.Left + (wa.Width - 1296) / 2, wa.Top + (wa.Height - 760) / 2, 1296, 760);
        }
        if (Native.Get(ini, "max", "0") == "1") WindowState = FormWindowState.Maximized;
        LoadForkSettings(ini);
        settingsLoading = false;
    }

    void SaveSettings()
    {
        if (settingsLoading) return;
        var b = fullscreen ? normalBounds : WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        try
        {
            // written atomically, the previous file kept as liteview.ini.bak (upstream: File.WriteAllLines)
            IniStore.Write(iniPath, "LiteView for Twitch (saved automatically)", new[] { "quality=" + quality.SelectedItem, "chat=" + (chatOn.Checked ? "1" : "0"), "volume=" + volume, "muted=" + (muted ? "1" : "0"), "picture=" + ((string)picture.SelectedItem ?? "Picture: Enhance").Replace("Picture: ", ""), "rtx=" + (rtx.Checked ? "1" : "0"), "chatmode=" + (webChat ? "web" : "native"), "sidebar=" + (side.Collapsed ? "closed" : "open"),
                "x=" + b.X, "y=" + b.Y, "w=" + b.Width, "h=" + b.Height, "max=" + (WindowState == FormWindowState.Maximized ? "1" : "0") }
                .Select(l => { int i = l.IndexOf('='); return new KeyValuePair<string, string>(l.Substring(0, i), l.Substring(i + 1)); }).Concat(ForkSettings()));
        }
        catch (Exception ex) { Log.Error("saving settings", ex); }
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int on = 1, col = Bar.R | (Bar.G << 8) | (Bar.B << 16);
        DwmSetWindowAttribute(Handle, 20, ref on, 4);    // dark mode frame
        DwmSetWindowAttribute(Handle, 35, ref col, 4);   // caption = top bar colour
        DwmSetWindowAttribute(Handle, 34, ref col, 4);   // border
    }

    static string startChannel;   // "LiteView.exe xqc" opens that stream directly

    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        CatchCrashes();    // first: a failure while starting is shown, not a window that silently never opens
        ParseArgs(args);   // "LiteView.exe xqc", plus fork options (src/app/LiteView.Fork.cs)
        StartLogging();
        ulong m = Native.NonCacheDieMask();
        using (var self = Process.GetCurrentProcess())   // WebView2, Streamlink, mpv, Chatterino inherit this
        {
            if (m != 0) self.ProcessorAffinity = new IntPtr((long)m);
            self.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        Application.Run(new LiteView());
    }
}

// Dark volume slider (0-100) for the top bar.
class VolumeBar : Control
{
    public int Value = 70;
    public event Action<int> Changed;
    static readonly Color Track = Color.FromArgb(53, 53, 59), Fill = Color.FromArgb(145, 71, 255), Knob = Color.FromArgb(239, 239, 241);
    public VolumeBar() { DoubleBuffered = true; Size = new Size(96, 30); Cursor = Cursors.Hand; }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        int y = Height / 2, x0 = 7, x1 = Width - 7, xv = x0 + (x1 - x0) * Value / 100;
        using (var p = new Pen(Track, 4)) g.DrawLine(p, x0, y, x1, y);
        using (var p = new Pen(Fill, 4)) g.DrawLine(p, x0, y, xv, y);
        using (var b = new SolidBrush(Knob)) g.FillEllipse(b, xv - 6, y - 6, 12, 12);
    }
    void Set(int v) { v = Math.Max(0, Math.Min(100, v)); if (v == Value) return; Value = v; Invalidate(); if (Changed != null) Changed(v); }
    protected override void OnMouseDown(MouseEventArgs e) { Capture = true; Set((e.X - 7) * 100 / Math.Max(1, Width - 14)); }
    protected override void OnMouseMove(MouseEventArgs e) { if (e.Button == MouseButtons.Left) Set((e.X - 7) * 100 / Math.Max(1, Width - 14)); }
    protected override void OnMouseWheel(MouseEventArgs e) { Set(Value + (e.Delta > 0 ? 5 : -5)); }
}