// More streams at once (the brief's "multiple stream windows"; the owner chose separate windows over a grid): right-click
// a channel > Watch in a new window. Each extra window plays one channel with its own Streamlink and mpv in its own
// kill-on-close job, with its own sound. At most MaxExtra at once; closing LiteView closes them.
// The bar at the bottom: mute and volume (also M and the mouse wheel on the video), the chat, on top, and a menu with
// the quality and "keep the video's shape". These settings are shared by all extra windows (src/core/PopOptions.cs):
// on top of other windows (picture in picture; the owner's default), the quality (720p60 unless chosen otherwise, or
// the same as the main window), the chat beside the video (its own chat connection), and the window's shape kept at
// 16:9 when resized (no black bars; off by default, as the owner likes it free). A changed volume is kept for the
// next window. The main window's stream is not affected (two decoders run: each costs CPU/GPU).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using LiteViewCore;

sealed class ExtraPlayer : Form
{
    public const int MaxExtra = 3;
    const int BarH = 34, ChatW = 300;
    static readonly Color Bg = Theme.Page, BarC = Theme.Panel, Fg = Theme.Text, Dim = Theme.Dim, Hover = Theme.Hover, Accent = Theme.Accent;

    readonly Panel body = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };
    // two video surfaces: a new quality opens behind the one playing, which keeps playing until the new one shows its picture
    readonly Panel[] surfaces = { new Panel { Dock = DockStyle.Fill, BackColor = Color.Black }, new Panel { Dock = DockStyle.Fill, BackColor = Color.Black } };
    int front;
    readonly Label note = new Label { Dock = DockStyle.Fill, BackColor = Color.Black, ForeColor = Theme.OnVideo, TextAlign = ContentAlignment.MiddleCenter };
    readonly Panel bar = new Panel { Dock = DockStyle.Bottom, Height = BarH, BackColor = BarC };
    readonly Button muteBtn = new Button(), chatBtn = new Button(), pinBtn = new Button(), moreBtn = new Button();
    readonly VolumeBar volBar = new VolumeBar();
    readonly ContextMenuStrip more = new ContextMenuStrip { ShowImageMargin = false };
    readonly ToolTip tips = new ToolTip();
    readonly System.Windows.Forms.Timer watch = new System.Windows.Forms.Timer { Interval = 500 };
    readonly System.Windows.Forms.Timer saveLater = new System.Windows.Forms.Timer { Interval = 1500 };
    readonly Func<StreamRequest, KeyValuePair<IntPtr, Process>> launch;
    readonly Func<string> mainQuality;
    readonly Action save;
    readonly PopOptions opts;
    readonly string login, log;
    ChatPanel chat;
    sealed class Session { public IntPtr Job; public Process Proc; public MpvPipe Pipe; public int Surface; public DateTime Started; public string Quality, Log; public bool Seen; }
    Session cur, next;   // what plays; what opens behind it (a new quality)
    bool muted;
    bool onTop, chatOn; string quality;   // this window's own choices; a change also becomes the default for new windows (opts)

    public event Action LoginRequested, WatchHere;

    [DllImport("user32.dll")] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("kernel32.dll")] static extern bool TerminateJobObject(IntPtr job, uint code);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    /// <param name="launch">starts Streamlink for a request (log file, window, quality and volume filled in) and returns its job and process (job Zero: failed)</param>
    public ExtraPlayer(string login, string name, string log, PopOptions opts, Func<string> mainQuality, Action save, Func<StreamRequest, KeyValuePair<IntPtr, Process>> launch)
    {
        this.login = login; this.log = log; this.launch = launch; this.opts = opts; this.mainQuality = mainQuality; this.save = save;
        Text = name + "  -  LiteView"; BackColor = Bg; ClientSize = new Size(640, 360 + BarH); MinimumSize = new Size(360, 240);
        onTop = opts.OnTop; chatOn = opts.Chat; quality = opts.Quality;
        StartPosition = FormStartPosition.WindowsDefaultLocation; TopMost = onTop; KeyPreview = true;
        note.Cursor = Cursors.Hand;
        note.Click += (s, e) => { if (cur == null) Start(); };
        body.Controls.Add(surfaces[0]); body.Controls.Add(surfaces[1]); body.Controls.Add(note); surfaces[0].BringToFront(); note.BringToFront();

        Flat(muteBtn, "", "Mute (M on the video)"); muteBtn.Click += (s, e) => SetMute(!muted);
        volBar.Value = opts.Volume; volBar.Width = 90; volBar.BackColor = BarC;
        volBar.Changed += v => { opts.Volume = v; if (muted) SetMute(false); Send("{\"command\":[\"set_property\",\"volume\"," + v + "]}"); UpdateMute(); saveLater.Stop(); saveLater.Start(); };
        tips.SetToolTip(volBar, "Volume of this window (or the mouse wheel on the video)");
        Flat(chatBtn, "", "Chat beside the video"); chatBtn.Click += (s, e) => { chatOn = !chatOn; opts.Chat = chatOn; ShowChat(chatOn, true); save(); };
        Flat(pinBtn, "", "Keep this window on top of other windows"); pinBtn.Click += (s, e) => { onTop = !onTop; opts.OnTop = onTop; TopMost = onTop; UpdateButtons(); save(); };
        Flat(moreBtn, "", "Quality, shape, watch in the main window"); moreBtn.Click += (s, e) => { BuildMenu(); more.Show(moreBtn, new Point(moreBtn.Width - more.Width, -more.Height)); };
        DarkMenuColors.Apply(more);
        bar.Controls.AddRange(new Control[] { muteBtn, volBar, chatBtn, pinBtn, moreBtn });
        bar.Resize += (s, e) => LayoutBar();
        saveLater.Tick += (s, e) => { saveLater.Stop(); save(); };

        Controls.Add(body); Controls.Add(bar);   // the bar docks first: it spans the window's width under the video (and the chat)
        SetFonts();
        UpdateButtons(); UpdateMute(); LayoutBar();
        watch.Tick += (s, e) => Check();
        HandleCreated += (s, e) => Theme.Frame(Handle);
        Shown += (s, e) => { if (chatOn) ShowChat(true, true); else if (opts.Fit) FitToVideo(); Start(); };   // the chat widens the window, the video keeps its size
        KeyDown += (s, e) =>
        {
            if (e.Control || e.Alt || (chat != null && chat.ContainsFocus)) return;
            if (e.KeyCode == Keys.M) { SetMute(!muted); e.Handled = true; }
        };
        FormClosed += (s, e) =>
        {
            watch.Stop(); watch.Dispose(); saveLater.Stop(); saveLater.Dispose(); save();
            StopAll();
            if (chat != null) { chat.Part(); chat.Dispose(); chat = null; }
        };
    }

    public string Login { get { return login; } }

    /// <summary>UI fixture (CI): moves the volume bar, as a click would.</summary>
    public void FixtureVolume(int v) { volBar.Set(v); }

    /// <summary>UI fixture (CI): another quality, as the menu would.</summary>
    public void FixtureQuality(string q) { quality = q; if (cur == null || QualityNow() != cur.Quality) ChangeQuality(); }

    public void SetFonts()
    {
        Font = UiFont.ForControls(FontKind.Text, 9.5f);
        note.Font = UiFont.ForControls(FontKind.Text, 11f);
        foreach (var b in new[] { muteBtn, chatBtn, pinBtn, moreBtn }) b.Font = new Font("Segoe MDL2 Assets", 11f);
        if (chat != null) { chat.SetControlFonts(); chat.View.ReLayout(); }
    }

    public void LoginChanged() { if (chat != null) chat.UpdateLogin(); }

    void Flat(Button b, string glyph, string tip)
    {
        b.Text = glyph; b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderSize = 0; b.FlatAppearance.MouseOverBackColor = Hover;
        b.BackColor = BarC; b.ForeColor = Fg; b.Size = new Size(34, 30); b.Cursor = Cursors.Hand; b.TabStop = false; b.AccessibleName = tip;
        tips.SetToolTip(b, tip);
    }

    void LayoutBar()
    {
        int y = (BarH - 30) / 2;
        muteBtn.Location = new Point(6, y); volBar.Location = new Point(muteBtn.Right + 2, (BarH - volBar.Height) / 2);
        moreBtn.Location = new Point(bar.Width - 6 - moreBtn.Width, y);
        pinBtn.Location = new Point(moreBtn.Left - 2 - pinBtn.Width, y);
        chatBtn.Location = new Point(pinBtn.Left - 2 - chatBtn.Width, y);
    }

    void UpdateButtons()
    {
        pinBtn.ForeColor = onTop ? Accent : Dim; pinBtn.Text = onTop ? "" : "";
        tips.SetToolTip(pinBtn, onTop ? "On top of other windows (click: a normal window)" : "A normal window (click: keep it on top of other windows)");
        chatBtn.ForeColor = chatOn ? Accent : Dim;
    }

    void UpdateMute() { muteBtn.Text = muted || volBar.Value == 0 ? "" : ""; volBar.Invalidate(); }

    string QualityNow() { return new PopOptions { Quality = quality }.QualityFor(mainQuality()); }

    void SetMute(bool m)
    {
        muted = m; Send("{\"command\":[\"set_property\",\"mute\"," + (m ? "true" : "false") + "]}"); UpdateMute();
    }

    void BuildMenu()
    {
        more.Items.Clear();
        var q = new ToolStripMenuItem("Quality");
        foreach (var name in PopOptions.Qualities)
        {
            var choice = name;
            var it = new ToolStripMenuItem(PopOptions.QualityName(name) + (name == PopOptions.DefaultQuality ? "  (default)" : "")) { Checked = quality == name };
            it.Click += (s, e) => { if (quality == choice) return; quality = choice; opts.Quality = choice; save(); if (cur == null || QualityNow() != cur.Quality) ChangeQuality(); };
            q.DropDownItems.Add(it);
        }
        DarkMenuColors.Apply(q.DropDown);
        var fit = new ToolStripMenuItem("Keep the video's shape (no black bars)") { Checked = opts.Fit };
        fit.ToolTipText = "Resizing keeps the video at 16:9. Off: any size, with black bars where it does not fit.";
        fit.Click += (s, e) => { opts.Fit = !opts.Fit; save(); if (opts.Fit) FitToVideo(); };
        var top = new ToolStripMenuItem("On top of other windows") { Checked = onTop };
        top.Click += (s, e) => pinBtn.PerformClick();
        var chatItem = new ToolStripMenuItem("Chat") { Checked = chatOn };
        chatItem.Click += (s, e) => chatBtn.PerformClick();
        var main = new ToolStripMenuItem("Watch in the main window");
        main.Click += (s, e) => { if (WatchHere != null) WatchHere(); Close(); };
        var close = new ToolStripMenuItem("Close");
        close.Click += (s, e) => Close();
        more.Items.AddRange(new ToolStripItem[] { q, fit, top, chatItem, new ToolStripSeparator(), main, close });
    }

    // ---------- the chat ----------

    void ShowChat(bool on, bool resize)
    {
        if (on && chat == null)
        {
            chat = new ChatPanel { Dock = DockStyle.Right, Width = ChatW };
            chat.LoginRequested += () => { if (LoginRequested != null) LoginRequested(); };
            var hide = new ToolStripMenuItem("Hide the chat");   // the chat's gear (owner: it did nothing in an extra window)
            hide.Click += (s2, e2) => chatBtn.PerformClick();
            var site = new ToolStripMenuItem("Open " + login + "'s chat on twitch.tv");
            site.Click += (s2, e2) => Launcher.Open("https://www.twitch.tv/popout/" + login + "/chat");
            chat.Menu.Items.AddRange(new ToolStripItem[] { hide, site });
            chat.SetControlFonts();
            Controls.Add(chat);
            // docking goes from the last child to the first: the bar (bottom, full width), then the chat (right), then the video
            Controls.SetChildIndex(body, 0); Controls.SetChildIndex(chat, 1); Controls.SetChildIndex(bar, 2);
            chat.Join(login);
            if (Fixture.On) FixtureChat();
        }
        else if (!on && chat != null)
        {
            chat.Part(); Controls.Remove(chat); chat.Dispose(); chat = null;
        }
        if (resize && WindowState == FormWindowState.Normal)
        {
            var wa = Screen.FromControl(this).WorkingArea;
            int w = Width + (on ? ChatW : -ChatW);
            Width = Math.Max(MinimumSize.Width, Math.Min(wa.Width, w));
            if (Right > wa.Right) Left = Math.Max(wa.Left, wa.Right - Width);
            if (opts.Fit) FitToVideo();
        }
        UpdateButtons();
    }

    void FixtureChat()
    {   // UI fixture (CI): a few lines, as the main chat's fixture scenes have
        foreach (var t in new[] { "hello from the extra window", "gg 🔥", "this is the second channel" })
            chat.Inject("@badge-info=;badges=;color=#1E90FF;display-name=Viewer;id=" + Guid.NewGuid().ToString("D") + ";tmi-sent-ts=0 :viewer!viewer@viewer.tmi.twitch.tv PRIVMSG #" + login + " :" + t);
    }

    // ---------- the window's shape (setting "popfit") ----------

    int SideW { get { return chat != null ? ChatW : 0; } }

    void FitToVideo()
    {
        if (WindowState != FormWindowState.Normal) return;
        int vw = ClientSize.Width - SideW;
        ClientSize = new Size(ClientSize.Width, vw * 9 / 16 + BarH);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0214 && opts != null && opts.Fit)   // WM_SIZING: the video stays 16:9 (as in the main window)
        {
            var r = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
            int edge = m.WParam.ToInt32();
            int ncW = Width - ClientSize.Width, ncH = Height - ClientSize.Height, cw = SideW;
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

    // ---------- the stream ----------

    void Start()
    {
        StopAll();
        note.Text = "Opening " + login + "..."; note.Visible = true; note.BringToFront();
        cur = Launch(front, muted);
        if (cur == null) { note.Text = "Could not start Streamlink. Click to try again."; return; }
        watch.Start();
        Log.Info("window: " + login + " (" + cur.Quality + ")");
    }

    // a new quality: it opens behind the picture, muted, and takes over at its first frame (or a fresh start when nothing plays)
    void ChangeQuality()
    {
        if (cur == null || !cur.Seen) { Start(); return; }
        Stop(ref next);
        next = Launch(1 - front, true);
        if (next == null) { Log.Warn("window: " + login + ": could not start the new quality"); return; }
        Log.Info("window: " + login + " opening " + next.Quality + " behind the picture");
    }

    Session Launch(int surface, bool startMuted)
    {
        string q = QualityNow(), lf = surface == 0 ? log : Path.Combine(Path.GetDirectoryName(log), Path.GetFileNameWithoutExtension(log) + "-b.log");
        var req = new StreamRequest { Channel = login, LogFile = lf, WindowHandle = surfaces[surface].Handle.ToInt64(), Quality = q, Volume = volBar.Value, StartMuted = startMuted || muted };
        var r = launch(req);
        if (r.Key == IntPtr.Zero) return null;
        var ses = new Session { Job = r.Key, Proc = r.Value, Surface = surface, Started = DateTime.Now, Quality = q, Log = lf };
        var p = new MpvPipe(req.PipeName);
        p.Changed += (what, value) => OnMpvChange(ses, what, value);
        p.Playing += () => { try { BeginInvoke((Action)(() => OnPlaying(ses))); } catch (InvalidOperationException) { } };
        ses.Pipe = p;
        p.Start();
        return ses;
    }

    // a player's first frame (UI thread): the one opening behind the picture takes over
    void OnPlaying(Session ses)
    {
        if (ses == cur && !cur.Seen) { cur.Seen = true; note.Visible = false; Log.Info("window: " + login + " playing"); return; }
        if (ses != next) return;
        surfaces[ses.Surface].BringToFront(); front = ses.Surface;
        if (note.Visible) note.BringToFront();
        ses.Seen = true;
        ses.Pipe.Send("{\"command\":[\"set_property\",\"volume\"," + volBar.Value + "]}");   // as the bar is now (it may have moved meanwhile)
        ses.Pipe.Send("{\"command\":[\"set_property\",\"mute\"," + (muted ? "true" : "false") + "]}");
        var old = cur; cur = ses; next = null;
        Stop(ref old);
        Log.Info("window: " + login + " now at " + ses.Quality + " (the last one played until then)");
    }

    // mpv's volume or mute changed (its mouse wheel or M on the video): the bar follows (pipe thread)
    void OnMpvChange(Session from, string what, string value)
    {
        try
        {
            BeginInvoke((Action)(() =>
            {
                if (from != cur) return;   // a player opening behind the picture, or one already stopped
                double v;
                if (Fixture.On) Log.Info("window: mpv " + what + " " + value);
                if (what == "volume" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                {
                    int iv = (int)Math.Round(Math.Max(0, Math.Min(100, v)));
                    if (iv != volBar.Value) { volBar.Value = iv; opts.Volume = iv; UpdateMute(); saveLater.Stop(); saveLater.Start(); }
                }
                else if (what == "mute") { muted = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase); UpdateMute(); }
            }));
        }
        catch (InvalidOperationException) { }   // closing
    }

    void Send(string json) { var c = cur; if (c != null) c.Pipe.Send(json); }

    static bool Alive(Session ses)
    {
        try { return ses != null && ses.Proc != null && !ses.Proc.HasExited; } catch (InvalidOperationException) { return false; }
    }

    void Check()
    {
        if (next != null && (!Alive(next) || DateTime.Now - next.Started > TimeSpan.FromSeconds(15)))
        {
            Log.Warn("window: " + login + ": " + next.Quality + " did not open; " + (cur != null ? cur.Quality : "the last one") + " keeps playing");
            Stop(ref next);
        }
        if (!Alive(cur))
        {
            bool was = cur != null && cur.Seen;
            StopAll();
            note.Text = (was ? "The stream ended." : "The stream could not be opened (" + login + " may be offline).") + "  Click to try again.";
            note.Visible = true; note.BringToFront();
            return;
        }
        if (cur.Seen) return;
        if (FindWindowEx(surfaces[cur.Surface].Handle, IntPtr.Zero, null, null) != IntPtr.Zero) { OnPlaying(cur); return; }
        if (DateTime.Now - cur.Started > TimeSpan.FromSeconds(4))
        {
            string text = "";
            try { if (File.Exists(cur.Log)) using (var fs = new FileStream(cur.Log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) using (var rd = new StreamReader(fs)) text = rd.ReadToEnd(); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
            var lines = text.Split('\n');
            if (StreamOutcome.Waiting(lines)) note.Text = login + " is offline. It starts here by itself when the channel goes live.";
        }
    }

    void Stop(ref Session ses)
    {
        if (ses == null) return;
        if (ses.Pipe != null) ses.Pipe.Dispose();
        if (ses.Job != IntPtr.Zero) { TerminateJobObject(ses.Job, 0); Native.CloseHandle(ses.Job); }
        if (ses.Proc != null) ses.Proc.Dispose();
        ses = null;
    }

    void StopAll()
    {
        watch.Stop();
        Stop(ref next);
        Stop(ref cur);
    }
}

// A small client of mpv's JSON IPC pipe for an extra window: connects (retrying while Streamlink waits for an offline
// channel, until the window stops the player), watches volume and mute, and sends commands. Reading and writing have
// their own threads and commands wait in a short queue, so a stalled mpv never blocks the window; Dispose ends both
// (closing the pipe ends a write that hangs).
sealed class MpvPipe : IDisposable
{
    const int MaxQueued = 64;
    readonly string name;
    readonly System.Collections.Concurrent.ConcurrentQueue<string> queue = new System.Collections.Concurrent.ConcurrentQueue<string>();
    readonly AutoResetEvent wake = new AutoResetEvent(false);
    volatile NamedPipeClientStream stream;
    volatile bool stop;
    public event Action<string, string> Changed;   // property name, value (JSON text)
    public event Action Playing;                    // the first frame (once)
    bool played;

    public MpvPipe(string name) { this.name = name; }

    public void Start()
    {
        new Thread(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "extra window pipe" }.Start();
        new Thread(Write) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "extra window pipe writer" }.Start();
    }

    void Run()
    {
        while (!stop)
        {
            var s = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try { s.Connect(500); }
            catch (Exception ex) when (ex is TimeoutException || ex is IOException || ex is UnauthorizedAccessException) { s.Dispose(); Thread.Sleep(1000); continue; }
            stream = s;
            if (stop) { s.Dispose(); return; }
            Send("{\"command\":[\"observe_property\",1,\"volume\"]}");
            Send("{\"command\":[\"observe_property\",2,\"mute\"]}");
            Send("{\"command\":[\"get_property\",\"playback-time\"],\"request_id\":71}");   // already playing when connected?
            try
            {
                using (var rd = new StreamReader(s, Encoding.UTF8))
                {
                    string line;
                    while (!stop && (line = rd.ReadLine()) != null)
                    {
                        if (line.Length > 4096) continue;
                        if (!played && (line.IndexOf("\"event\":\"playback-restart\"", StringComparison.Ordinal) >= 0
                            || (line.IndexOf("\"request_id\":71", StringComparison.Ordinal) >= 0 && line.IndexOf("\"error\":\"success\"", StringComparison.Ordinal) >= 0)))
                        {
                            played = true;
                            var pl = Playing; if (pl != null) pl();
                            continue;
                        }
                        if (line.IndexOf("\"property-change\"", StringComparison.Ordinal) < 0) continue;
                        var j = Json.Parse(line);
                        string prop = Json.S(j, "name"), data = Json.S(j, "data");
                        var c = Changed;
                        if ((prop == "volume" || prop == "mute") && data != null && c != null) c(prop, data);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException) { }
            stream = null;
            try { s.Dispose(); } catch (IOException) { }
            if (!stop) Thread.Sleep(1000);   // mpv went away; Streamlink may start another (the window's own check sees the end)
        }
    }

    void Write()
    {
        while (!stop)
        {
            wake.WaitOne(1000);
            string json;
            while (!stop && queue.TryPeek(out json))
            {
                var s = stream;
                if (s == null) break;   // not connected yet: the commands wait
                try
                {
                    var b = Encoding.UTF8.GetBytes(json + "\n");
                    s.Write(b, 0, b.Length); s.Flush();
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException) { break; }
                queue.TryDequeue(out json);
            }
        }
    }

    /// <summary>Queues a command (the oldest go when mpv does not keep up).</summary>
    public void Send(string json)
    {
        if (stop) return;
        queue.Enqueue(json);
        string dropped;
        while (queue.Count > MaxQueued && queue.TryDequeue(out dropped)) { }
        wake.Set();
    }

    public void Dispose()
    {
        stop = true;
        wake.Set();
        var s = stream; stream = null;
        if (s != null) { try { s.Dispose(); } catch (IOException) { } }
    }
}
