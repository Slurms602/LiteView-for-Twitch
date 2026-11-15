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
    static readonly Color Bg = Color.FromArgb(14, 14, 16), BarC = Color.FromArgb(24, 24, 27), Fg = Color.FromArgb(239, 239, 241),
        Dim = Color.FromArgb(173, 173, 184), Hover = Color.FromArgb(38, 38, 44), Accent = Color.FromArgb(145, 71, 255);

    readonly Panel body = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };
    readonly Panel surface = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };
    readonly Label note = new Label { Dock = DockStyle.Fill, BackColor = Color.Black, ForeColor = Dim, TextAlign = ContentAlignment.MiddleCenter };
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
    MpvPipe pipe;
    IntPtr job; Process proc; DateTime started; bool seen, muted;
    string playingQuality = "";

    public event Action LoginRequested, WatchHere;

    [DllImport("user32.dll")] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("kernel32.dll")] static extern bool TerminateJobObject(IntPtr job, uint code);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    /// <param name="launch">starts Streamlink for a request (log file, window, quality and volume filled in) and returns its job and process (job Zero: failed)</param>
    public ExtraPlayer(string login, string name, string log, PopOptions opts, Func<string> mainQuality, Action save, Func<StreamRequest, KeyValuePair<IntPtr, Process>> launch)
    {
        this.login = login; this.log = log; this.launch = launch; this.opts = opts; this.mainQuality = mainQuality; this.save = save;
        Text = name + "  -  LiteView"; BackColor = Bg; ClientSize = new Size(640, 360 + BarH); MinimumSize = new Size(360, 240);
        StartPosition = FormStartPosition.WindowsDefaultLocation; TopMost = opts.OnTop; KeyPreview = true;
        note.Cursor = Cursors.Hand;
        note.Click += (s, e) => { if (job == IntPtr.Zero) Start(); };
        body.Controls.Add(surface); body.Controls.Add(note); note.BringToFront();

        Flat(muteBtn, "", "Mute (M on the video)"); muteBtn.Click += (s, e) => SetMute(!muted);
        volBar.Value = opts.Volume; volBar.Width = 90; volBar.BackColor = BarC;
        volBar.Changed += v => { opts.Volume = v; if (muted) SetMute(false); Send("{\"command\":[\"set_property\",\"volume\"," + v + "]}"); UpdateMute(); saveLater.Stop(); saveLater.Start(); };
        tips.SetToolTip(volBar, "Volume of this window (or the mouse wheel on the video)");
        Flat(chatBtn, "", "Chat beside the video"); chatBtn.Click += (s, e) => { opts.Chat = !opts.Chat; ShowChat(opts.Chat, true); save(); };
        Flat(pinBtn, "", "Keep this window on top of other windows"); pinBtn.Click += (s, e) => { opts.OnTop = !opts.OnTop; TopMost = opts.OnTop; UpdateButtons(); save(); };
        Flat(moreBtn, "", "Quality, shape, watch in the main window"); moreBtn.Click += (s, e) => { BuildMenu(); more.Show(moreBtn, new Point(moreBtn.Width - more.Width, -more.Height)); };
        DarkMenuColors.Apply(more);
        bar.Controls.AddRange(new Control[] { muteBtn, volBar, chatBtn, pinBtn, moreBtn });
        bar.Resize += (s, e) => LayoutBar();
        saveLater.Tick += (s, e) => { saveLater.Stop(); save(); };

        Controls.Add(body); Controls.Add(bar);   // the bar docks first: it spans the window's width under the video (and the chat)
        SetFonts();
        UpdateButtons(); UpdateMute(); LayoutBar();
        watch.Tick += (s, e) => Check();
        Shown += (s, e) => { if (opts.Chat) ShowChat(true, false); Start(); };
        KeyDown += (s, e) =>
        {
            if (e.Control || e.Alt || (chat != null && chat.ContainsFocus)) return;
            if (e.KeyCode == Keys.M) { SetMute(!muted); e.Handled = true; }
        };
        FormClosed += (s, e) =>
        {
            watch.Stop(); watch.Dispose(); saveLater.Stop(); saveLater.Dispose(); save();
            Stop();
            if (chat != null) { chat.Part(); chat.Dispose(); chat = null; }
        };
    }

    public string Login { get { return login; } }

    /// <summary>UI fixture (CI): moves the volume bar, as a click would.</summary>
    public void FixtureVolume(int v) { volBar.Set(v); }

    public void SetFonts()
    {
        Font = UiFont.ForControls(FontKind.Text, 9.5f);
        note.Font = UiFont.ForControls(FontKind.Text, 11f);
        foreach (var b in new[] { muteBtn, chatBtn, pinBtn, moreBtn }) b.Font = new Font("Segoe MDL2 Assets", 11f);
        if (chat != null) chat.SetControlFonts();
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
        pinBtn.ForeColor = opts.OnTop ? Accent : Dim; pinBtn.Text = opts.OnTop ? "" : "";
        tips.SetToolTip(pinBtn, opts.OnTop ? "On top of other windows (click: a normal window)" : "A normal window (click: keep it on top of other windows)");
        chatBtn.ForeColor = opts.Chat ? Accent : Dim;
    }

    void UpdateMute() { muteBtn.Text = muted || volBar.Value == 0 ? "" : ""; volBar.Invalidate(); }

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
            var it = new ToolStripMenuItem(PopOptions.QualityName(name) + (name == PopOptions.DefaultQuality ? "  (default)" : "")) { Checked = opts.Quality == name };
            it.Click += (s, e) => { if (opts.Quality == choice) return; opts.Quality = choice; save(); if (opts.QualityFor(mainQuality()) != playingQuality) Start(); };
            q.DropDownItems.Add(it);
        }
        DarkMenuColors.Apply(q.DropDown);
        var fit = new ToolStripMenuItem("Keep the video's shape (no black bars)") { Checked = opts.Fit };
        fit.ToolTipText = "Resizing keeps the video at 16:9. Off: any size, with black bars where it does not fit.";
        fit.Click += (s, e) => { opts.Fit = !opts.Fit; save(); if (opts.Fit) FitToVideo(); };
        var top = new ToolStripMenuItem("On top of other windows") { Checked = opts.OnTop };
        top.Click += (s, e) => pinBtn.PerformClick();
        var chatItem = new ToolStripMenuItem("Chat") { Checked = opts.Chat };
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
        Stop();
        playingQuality = opts.QualityFor(mainQuality());
        note.Text = "Opening " + login + "..."; note.Visible = true; note.BringToFront();
        var req = new StreamRequest { Channel = login, LogFile = log, WindowHandle = surface.Handle.ToInt64(), Quality = playingQuality, Volume = volBar.Value, StartMuted = muted };
        var r = launch(req);
        if (r.Key == IntPtr.Zero) { note.Text = "Could not start Streamlink. Click to try again."; return; }
        job = r.Key; proc = r.Value; started = DateTime.Now; seen = false;
        pipe = new MpvPipe(req.PipeName);
        pipe.Changed += OnMpvChange;
        pipe.Start();
        watch.Start();
        Log.Info("window: " + login + " (" + playingQuality + ")");
    }

    // mpv's volume or mute changed (its mouse wheel or M on the video): the bar follows (pipe thread)
    void OnMpvChange(string what, string value)
    {
        try
        {
            BeginInvoke((Action)(() =>
            {
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

    void Send(string json) { var p = pipe; if (p != null) p.Send(json); }

    void Check()
    {
        bool alive = false;
        try { alive = proc != null && !proc.HasExited; } catch (InvalidOperationException) { }
        if (!alive)
        {
            Stop();
            note.Text = (seen ? "The stream ended." : "The stream could not be opened (" + login + " may be offline).") + "  Click to try again.";
            note.Visible = true; note.BringToFront();
            return;
        }
        if (seen) return;
        if (FindWindowEx(surface.Handle, IntPtr.Zero, null, null) != IntPtr.Zero) { seen = true; note.Visible = false; Log.Info("window: " + login + " playing"); return; }
        if (DateTime.Now - started > TimeSpan.FromSeconds(4))
        {
            string text = "";
            try { if (File.Exists(log)) using (var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) using (var rd = new StreamReader(fs)) text = rd.ReadToEnd(); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
            var lines = text.Split('\n');
            if (StreamOutcome.Waiting(lines)) note.Text = login + " is offline. It starts here by itself when the channel goes live.";
        }
    }

    void Stop()
    {
        watch.Stop();
        if (pipe != null) { pipe.Dispose(); pipe = null; }
        if (job != IntPtr.Zero) { TerminateJobObject(job, 0); Native.CloseHandle(job); job = IntPtr.Zero; }
        if (proc != null) { proc.Dispose(); proc = null; }
    }
}

// A small client of mpv's JSON IPC pipe for an extra window: connects (retrying while mpv starts), watches volume and
// mute, and sends commands. Its own thread; Dispose ends it.
sealed class MpvPipe : IDisposable
{
    readonly string name;
    readonly object gate = new object();
    NamedPipeClientStream stream;
    StreamWriter writer;
    volatile bool stop;
    public event Action<string, string> Changed;   // property name, value (JSON text)

    public MpvPipe(string name) { this.name = name; }

    public void Start() { new Thread(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "extra window pipe" }.Start(); }

    void Run()
    {
        var until = DateTime.UtcNow.AddSeconds(90);   // Streamlink may take a while (an offline channel: it never comes)
        while (!stop && DateTime.UtcNow < until)
        {
            var s = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try { s.Connect(500); }
            catch (Exception ex) when (ex is TimeoutException || ex is IOException) { s.Dispose(); Thread.Sleep(500); continue; }
            lock (gate)
            {
                if (stop) { s.Dispose(); return; }
                stream = s; writer = new StreamWriter(s, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            }
            Send("{\"command\":[\"observe_property\",1,\"volume\"]}");
            Send("{\"command\":[\"observe_property\",2,\"mute\"]}");
            try
            {
                using (var rd = new StreamReader(s, Encoding.UTF8))
                {
                    string line;
                    while (!stop && (line = rd.ReadLine()) != null)
                    {
                        if (line.Length > 4096 || line.IndexOf("\"property-change\"", StringComparison.Ordinal) < 0) continue;
                        var j = Json.Parse(line);
                        string prop = Json.S(j, "name"), data = Json.S(j, "data");
                        if ((prop == "volume" || prop == "mute") && data != null && Changed != null) Changed(prop, data);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is FormatException) { }
            return;   // mpv went away: the window's own check notices
        }
    }

    public void Send(string json)
    {
        lock (gate)
        {
            if (writer == null) return;
            try { writer.WriteLine(json); } catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException) { }
        }
    }

    public void Dispose()
    {
        stop = true;
        lock (gate) { try { if (stream != null) stream.Dispose(); } catch (IOException) { } stream = null; writer = null; }
    }
}
