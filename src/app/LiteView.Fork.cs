// LiteView Enhanced additions to the main window. They live in this file (a part of the same class as
// src/liteview.cs) so that upstream's liteview.cs keeps only small edits and upstream changes merge cleanly.
//   - WebView2 hardening (navigation policy, no permissions, no downloads, no dev tools, checked messages)
//   - starting Streamlink with a quoted command line, a per-stream pipe name and generation id
//   - what happens when a stream stops: a clear status, then a bounded retry (backoff with jitter)
//   - diagnostics export, log, crash logging
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LiteViewCore;
using Microsoft.Web.WebView2.Core;

partial class LiteView
{
    // secret our click script adds to "watch" messages (src/core/WebMessage.cs)
    readonly string webSecret = WebMessage.NewSecret();
    // set while a stream runs: the mpv pipe thread waits on it instead of polling every second
    readonly ManualResetEvent streamOn = new ManualResetEvent(false);
    // generation of the current stream; callbacks for an older one are dropped
    volatile int gen, mpvGen = -1;
    bool played, canRetry;
    Process slProc;
    readonly System.Windows.Forms.Timer retry = new System.Windows.Forms.Timer();
    readonly Backoff playBackoff = new Backoff(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(2));
    int retryGen;
    const int MaxQuickRetries = 8;
    DateTime lastCrashBox = DateTime.MinValue;
    bool settingsLoading;
    // mpv commands: queued by any thread, written by one background thread, dropped if meant for an older stream
    readonly System.Collections.Concurrent.BlockingCollection<KeyValuePair<int, string>> mpvOut =
        new System.Collections.Concurrent.BlockingCollection<KeyValuePair<int, string>>(256);
    int mpvPipeGen = -1, mpvDropped;
    volatile bool mpvResync;   // commands were dropped: send the whole state again once mpv reads again

    // ---------- command line ----------

    static int stressRate;   // --chat-stress=<messages per second>

    static void ParseArgs(string[] args)
    {
        foreach (var a in args)
        {
            if (a.StartsWith("--chat-stress", StringComparison.OrdinalIgnoreCase))
            {
                int eq = a.IndexOf('='), r;
                stressRate = eq > 0 && int.TryParse(a.Substring(eq + 1), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out r) ? Math.Max(1, Math.Min(5000, r)) : 100;
            }
            else if (!a.StartsWith("--", StringComparison.Ordinal) && startChannel == null) startChannel = a;
        }
    }

    void InitFork()
    {
        Shown += (s, e) => windowShown = true;
        if (stressRate > 0) StartStress();
        new Thread(MpvWriter) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "mpv writer" }.Start();
        retry.Tick += (s, e) =>
        {
            retry.Stop();
            if (watching && retryGen == gen && channel != null && slProc == null) Watch(channel, true);
        };
        InitProblems();
        InitScreens();
        status.Click += (s, e) => { if (problemTitle != null) ShowProblemDetails(); else if (canRetry && watching && slProc == null && channel != null) Watch(channel); };
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPower;
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += OnNetwork;
        FormClosed += (s, e) =>
        {
            Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPower;
            System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= OnNetwork;
        };
        Log.Info("window ready; CPU mask " + (mask == 0 ? "none (Windows decides)" : "0x" + mask.ToString("X")) + ", NVIDIA RTX " + HasNvidia);
    }

    void QueueMpv(int g, string json)
    {
        if (mpvOut.TryAdd(new KeyValuePair<int, string>(g, json))) return;
        mpvResync = true;
        if (Interlocked.Increment(ref mpvDropped) == 1) Log.Warn("mpv is not reading its commands; dropping them and resending the state later");
    }

    // From OnTick: when commands were dropped and the queue has drained, send volume, mute and the observed
    // properties again, and forget what was applied so ApplyEnhance sends the picture settings again.
    void ResyncMpv()
    {
        if (!mpvResync || mpvOut.Count > 0 || mpvGen != gen) return;
        mpvResync = false;
        appliedPic = appliedVf = null;
        SendMpv("{\"command\":[\"set_property\",\"volume\"," + volume + "]}");
        SendMpv("{\"command\":[\"set_property\",\"mute\"," + (muted ? "true" : "false") + "]}");
        SendMpv("{\"command\":[\"observe_property\",1,\"volume\"]}");
        SendMpv("{\"command\":[\"observe_property\",2,\"mute\"]}");
        SendMpv("{\"command\":[\"observe_property\",3,\"height\"]}");
        Log.Info("mpv: state sent again after dropped commands");
    }

    void MpvWriter()
    {
        foreach (var item in mpvOut.GetConsumingEnumerable())
        {
            System.IO.Pipes.NamedPipeClientStream p; int pg;
            lock (pipeLock) { p = mpvPipe; pg = mpvPipeGen; }
            if (p == null || item.Key != pg) continue;   // no player yet, or a command for a stream that has ended
            try { var b = Encoding.UTF8.GetBytes(item.Value + "\n"); p.Write(b, 0, b.Length); p.Flush(); }
            catch (Exception) { }   // the pipe broke (mpv ended): PipeLoop notices and reconnects
        }
    }

    // ---------- chat stress mode ----------
    // LiteView.exe <channel> --chat-stress=200: while watching with the native chat, synthetic messages (emotes of the
    // channel, long ones, Unicode, deletions, timeouts, sub notices) are fed into the chat as if they came from Twitch.
    // Every 10 s liteview.log gets a "stress:" line with memory, handles and the emote cache, to compare runs.

    System.Threading.Timer stressTimer;

    void StartStress()
    {
        var gen0 = new ChatStress("stress", Environment.TickCount);
        List<string> emotes = new List<string>(); string emotesFor = null;
        double due = 0; var sw = Stopwatch.StartNew(); long lastLog = 0, lastCount = 0;
        Text = Text + "  -  chat stress " + stressRate + "/s";
        Log.Info("chat stress mode: " + stressRate + " messages per second");
        int busy = 0;
        stressTimer = new System.Threading.Timer(_ =>
        {
            if (Interlocked.Exchange(ref busy, 1) == 1) return;   // the previous tick is still feeding
            try
            {
                string ch = nchat.Channel;
                if (!watching || webChat || ch == null) return;
                if (ch != emotesFor || sw.ElapsedMilliseconds % 5000 < 60) { emotes = nchat.EmoteNames(); emotesFor = ch; }
                due += stressRate / 20.0;
                for (; due >= 1; due--) nchat.Inject(gen0.Next(emotes).Replace(" #stress", " #" + ch));
                long now = sw.ElapsedMilliseconds;
                if (now - lastLog >= 10000)
                {
                    using (var me = Process.GetCurrentProcess())
                        Log.Info("stress: " + gen0.Count + " messages (" + ((gen0.Count - lastCount) * 1000 / Math.Max(1, now - lastLog)) + "/s), working set "
                            + me.WorkingSet64 / (1024 * 1024) + " MB, private " + me.PrivateMemorySize64 / (1024 * 1024) + " MB, threads " + me.Threads.Count
                            + ", handles " + me.HandleCount + ", GDI " + GetGuiResources(me.Handle, 0) + ", USER " + GetGuiResources(me.Handle, 1)
                            + ", CPU " + (int)me.TotalProcessorTime.TotalSeconds + " s, emotes: " + Images.Stats());
                    lastLog = now; lastCount = gen0.Count;
                }
            }
            catch (Exception ex) { Log.Error("chat stress", ex); }
            finally { Volatile.Write(ref busy, 0); }
        }, null, 1000, 50);
        FormClosing += (s, e) => { if (stressTimer != null) stressTimer.Dispose(); };
    }

    // ---------- sleep / resume and network changes ----------

    void Later(Action a) { try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch (InvalidOperationException) { } }

    void OnPower(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume) Later(AfterResume);
    }

    // After sleep the stream is minutes behind (or its connections are dead) and the chat socket may be half-open:
    // check the login, then start chat and the stream again at the live edge.
    void AfterResume()
    {
        Log.Info("resumed from sleep");
        CheckLogin();
        if (!watching || channel == null) return;
        if (chatOn.Checked && !webChat) nchat.Reconnect();
        Watch(channel, true);
    }

    void OnNetwork(object sender, System.Net.NetworkInformation.NetworkAvailabilityEventArgs e)
    {
        if (!e.IsAvailable) { Log.Info("network: no connection"); return; }
        Later(() =>
        {
            Log.Info("network: connected again");
            if (!watching || channel == null) return;
            if (chatOn.Checked && !webChat) nchat.Reconnect();
            if (slProc == null && canRetry) Watch(channel);   // a stream that gave up or is waiting to retry: now
        });
    }

    // ---------- embedded browser ----------

    static void Harden(CoreWebView2 core, bool messages)
    {
        // The navigation policy comes first and must not fail; the other settings are each optional, so an
        // older WebView2 runtime without one of them loses only that setting, not the page.
        core.NavigationStarting += (s, e) =>
        {
            var nav = SafeUrl.ForWebView(e.Uri);
            if (nav == SafeUrl.Nav.Allow) return;
            e.Cancel = true;
            if (nav == SafeUrl.Nav.OpenInBrowser && e.IsUserInitiated) Launcher.Open(e.Uri);
            else Log.Info("web: blocked a navigation (" + Launcher.SchemeOf(e.Uri) + ")");
        };
        core.IsMuted = true;
        var st = core.Settings;
        Try("web messages", () => st.IsWebMessageEnabled = messages);
        Try("host objects", () => st.AreHostObjectsAllowed = false);
        Try("status bar", () => st.IsStatusBarEnabled = false);
        Try("dev tools", () => st.AreDevToolsEnabled = Environment.GetEnvironmentVariable("LITEVIEW_DEVTOOLS") == "1");
        Try("autofill", () => st.IsGeneralAutofillEnabled = false);
        Try("password saving", () => st.IsPasswordAutosaveEnabled = false);
        // camera, microphone, notifications, clipboard, location...: the Twitch pages here need none of them
        Try("permissions", () => core.PermissionRequested += (s, e) => e.State = CoreWebView2PermissionState.Deny);
        Try("downloads", () => core.DownloadStarting += (s, e) => e.Cancel = true);
    }

    static void Try(string what, Action a)
    {
        try { a(); } catch (Exception ex) { Log.Warn("web: could not set " + what + " (" + ex.GetType().Name + ")"); }
    }

    static string TryMessage(CoreWebView2WebMessageReceivedEventArgs e)
    {
        try { return e.TryGetWebMessageAsString(); } catch (ArgumentException) { return null; }   // not a string: not ours
    }

    // ---------- starting and watching the stream ----------

    string MissingPlayerFiles()
    {
        if (string.IsNullOrEmpty(streamlink) || !File.Exists(streamlink)) return "Streamlink is missing (twitch\\streamlink). Unzip LiteView again.";
        if (!File.Exists(mpv)) return "mpv is missing (twitch\\mpv\\mpv.exe). Unzip LiteView again.";
        return null;
    }

    void StartStreamProcess(string name, string q)
    {
        int g = ++gen; played = false; mpvGen = -1; canRetry = false;
        pipeName = StreamCommand.NewPipeName();
        string cmdline;
        try
        {
            cmdline = StreamCommand.Build(new StreamRequest
            {
                Streamlink = streamlink, Mpv = mpv, LogFile = slLog, Channel = name, Quality = q,
                PipeName = pipeName, WindowHandle = video.Handle.ToInt64(),
            });
        }
        catch (ArgumentException ex) { ShowProblem("Cannot play this channel", ex.Message, null); Log.Error("stream command", ex); return; }
        try { File.Delete(slLog); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) { ShowProblem("Could not start Streamlink", "Windows refused to create a job object (error " + Marshal.GetLastWin32Error() + ").", () => Watch(channel)); Log.Error("CreateJobObject failed"); return; }
        var lim = new JOBLIMITS { flags = 0x2000 };   // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE: closing LiteView also ends its stream
        if (!SetInformationJobObject(job, 9, ref lim, Marshal.SizeOf(typeof(JOBLIMITS)))) Log.Warn("job: kill-on-close could not be set");
        var si = new Native.STARTUPINFO(); si.cb = Marshal.SizeOf(si);
        Native.PROCESS_INFORMATION pi;
        if (CreateProcessW(streamlink, new StringBuilder(cmdline), IntPtr.Zero, IntPtr.Zero, false, Native.CREATE_SUSPENDED | Native.BELOW_NORMAL, IntPtr.Zero, root, ref si, out pi))
        {
            if (mask != 0) Native.SetProcessAffinityMask(pi.hProcess, new UIntPtr(mask));
            if (!AssignProcessToJobObject(job, pi.hProcess)) Log.Warn("job: Streamlink could not be added");
            // Open our own handle while the suspended process still exists, so a reused PID can never be mistaken for it.
            try { slProc = Process.GetProcessById(pi.pid); var unused = slProc.Handle; }
            catch (Exception ex) { Log.Warn("stream " + g + ": no process handle: " + ex.Message); if (slProc != null) slProc.Dispose(); slProc = null; }
            Native.ResumeThread(pi.hThread); Native.CloseHandle(pi.hThread); Native.CloseHandle(pi.hProcess);
            slPid = pi.pid; slStart = DateTime.Now;
            streamOn.Set();
            status.ForeColor = Dim; status.Text = "opening...";
            Log.Info("stream " + g + ": Streamlink started (quality " + q + ")");
        }
        else
        {
            int err = Marshal.GetLastWin32Error();
            ShowProblem("Could not start Streamlink", "Windows error " + err + " starting\n" + streamlink + "\n\nAn antivirus quarantine or a damaged unzip can cause this: unzip LiteView again.", () => Watch(channel));
            Log.Error("stream " + g + ": CreateProcess failed, error " + err);
            canRetry = true;
        }
    }

    int openingTicks;
    // While Streamlink runs but the player is not up yet: say whether the channel is offline (Streamlink waits and
    // starts it when it goes live) or what error it is retrying. Reads the log every 2 s (every 4th tick).
    void OpeningStatus()
    {
        if (++openingTicks % 4 != 0) return;
        try
        {
            if (!File.Exists(slLog)) return;
            var lines = ReadShared(slLog);
            string err = StreamOutcome.LastError(lines);
            status.ForeColor = Dim;
            if (StreamOutcome.Waiting(lines)) status.Text = "Offline - it starts by itself when the channel goes live";
            else if (err.Length > 0) status.Text = "Retrying: " + err;
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static IEnumerable<string> ReadShared(string path)
    {
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var rd = new StreamReader(fs, Encoding.UTF8))
        {
            if (fs.Length > 8 * 1024 * 1024) fs.Seek(-8 * 1024 * 1024, SeekOrigin.End);
            return rd.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        }
    }

    void OnStreamExited()
    {
        var ran = DateTime.Now - slStart;
        string err = "";
        try { if (File.Exists(slLog)) err = StreamOutcome.LastError(ReadShared(slLog)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        var end = StreamOutcome.Classify(err, ran, played);
        int code = int.MinValue; try { code = slProc.ExitCode; } catch (InvalidOperationException) { }
        Log.Warn("stream " + gen + ": Streamlink exited (code " + code + ", after " + (int)ran.TotalSeconds + " s, " + end + ")" + (err.Length > 0 ? ": " + Redact.ForSharing(err) : ""));
        bool wasPlaying = played;
        StopStream();   // also ends an mpv left behind if Streamlink died
        if (wasPlaying) playBackoff.Ended(ran);
        ScheduleRetry(end, StreamOutcome.Describe(end, err), err);
    }

    // Offline: look again every minute. Errors: retry with growing waits (2 s ... 60 s, with jitter),
    // and after MaxQuickRetries failures in a row stop and let the user click the status to try again.
    void ScheduleRetry(StreamEnd end, string summary, string err)
    {
        canRetry = channel != null;
        string ch = channel;
        Action again = ch != null ? () => { if (channel == ch) Watch(ch); } : (Action)null;
        if (!watching || channel == null || end == StreamEnd.Fatal) { ShowProblem(summary, StreamDetail(err), again); return; }
        TimeSpan wait;
        if (end == StreamEnd.Offline) wait = TimeSpan.FromSeconds(60);
        else
        {
            if (playBackoff.Attempt >= MaxQuickRetries)
            {
                Log.Warn("stream: giving up after " + MaxQuickRetries + " quick retries");
                ShowProblem(summary + "  -  stopped retrying", StreamDetail(err) + "\n\nLiteView tried " + MaxQuickRetries + " times in a row.", again);
                return;
            }
            wait = playBackoff.Next();
        }
        retryGen = gen;
        retry.Interval = (int)Math.Max(500, Math.Min(int.MaxValue, wait.TotalMilliseconds));
        retry.Start();
        string next = "  -  trying again in " + Math.Max(1, (int)Math.Round(wait.TotalSeconds)) + " s";
        if (end == StreamEnd.Offline) { status.ForeColor = Dim; status.Text = summary + next; }   // not an error
        else ShowProblem(summary + next, StreamDetail(err), again);
    }

    void CancelRetry() { retry.Stop(); canRetry = false; playBackoff.Reset(); }

    // ---------- problems (UI-5) ----------
    // An error shows as "⚠ summary" in the status line (shortened to fit); a click opens the whole message with
    // Try again / Open log folder / Copy details (src/app/ProblemPopup.cs). Any other status text ends it.

    static readonly Color ErrorC = Color.FromArgb(255, 140, 140);
    static readonly string WarnSign = ((char)0x26A0).ToString();
    string problemTitle, problemDetail; Action problemRetry; bool settingProblem;
    readonly ToolTip statusTip = new ToolTip();

    void InitProblems()
    {
        status.AutoSize = false; status.AutoEllipsis = true; status.Height = 20;
        status.TextChanged += (s, e) =>
        {
            if (settingProblem) return;
            problemTitle = null; problemRetry = null; status.Cursor = Cursors.Default; statusTip.SetToolTip(status, null);
            FitStatus();
        };
    }

    void ShowProblem(string summary, string detail, Action retry)
    {
        problemTitle = summary; problemDetail = detail; problemRetry = retry;
        settingProblem = true;
        try { status.ForeColor = ErrorC; status.Text = WarnSign + " " + summary; }
        finally { settingProblem = false; }
        status.Cursor = Cursors.Hand;
        statusTip.SetToolTip(status, "Click for details");
        FitStatus();
    }

    void ShowProblemDetails()
    {
        if (problemTitle == null) return;
        var r = problemRetry;
        new ProblemPopup(problemTitle, problemDetail, r, OpenLogFolder) { Icon = Icon }.ShowBelow(status, this);
    }

    // the status line gets the room between what is on its left and the controls on the right
    void FitStatus()
    {
        int right = bar.Width - 6;
        foreach (Control c in bar.Controls) if (c.Visible && c != status && c.Left > status.Left && c.Left < right) right = c.Left;
        status.Width = Math.Max(0, right - status.Left - 10);
    }

    void OpenLogFolder()
    {
        try { using (Process.Start("explorer.exe", WinArgs.Quote(root))) { } } catch (Exception ex) { Log.Error("open folder", ex); }
    }

    // the end of streamlink.log, without credentials or channel names, for the details of a stream problem
    string StreamDetail(string err)
    {
        var sb = new StringBuilder();
        if (err.Length > 0) sb.Append(Redact.ForSharing(err)).Append("\n\n");
        try
        {
            if (File.Exists(slLog))
            {
                var tail = ReadShared(slLog).Where(l => l.Trim().Length > 0).Reverse().Take(12).Reverse().ToList();
                if (tail.Count > 0) { sb.Append("Last lines of streamlink.log:\n"); foreach (var l in tail) sb.Append(Redact.ForSharing(l)).Append('\n'); }
            }
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
        if (sb.Length == 0) sb.Append("Streamlink wrote no error. See liteview.log.");
        return sb.ToString().TrimEnd();
    }

    // ---------- diagnostics ----------

    void AddForkMenuItems(ContextMenuStrip menu)
    {
        var diag = new ToolStripMenuItem("Export diagnostics...");
        diag.Click += (s, e) => ExportDiagnostics();
        var logs = new ToolStripMenuItem("Open log folder");
        logs.Click += (s, e) => OpenLogFolder();
        var site = new ToolStripMenuItem("Sign out of twitch.tv in LiteView");
        site.Click += (s, e) => SignOutOfWebsite();
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(site);
        menu.Items.Add(diag);
        menu.Items.Add(logs);
    }

    // The Following page and the web chat keep their own Twitch website login (cookies in the webview folder).
    // "Log out" only ends the chat/API login (SEC-17), so this clears the website session too.
    bool signOutPending;

    async void SignOutOfWebsite()
    {
        if (MessageBox.Show(this, "Sign out of the Twitch website inside LiteView (the Following page and the web chat)?\n\n"
            + "Your chat login is separate: use Log out for that.", "LiteView", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        signOutPending = true;
        if (web != null && webReady)
        {
            try { await ApplyPendingSignOut(web.CoreWebView2); web.CoreWebView2.Navigate(Home); }
            catch (Exception ex) { Log.Error("website sign-out", ex); }
        }
        else { status.ForeColor = Dim; status.Text = "Signed out of twitch.tv; it applies when the Following page opens"; }
    }

    async System.Threading.Tasks.Task ApplyPendingSignOut(CoreWebView2 core)
    {
        if (!signOutPending) return;
        try
        {
            await core.Profile.ClearBrowsingDataAsync();   // cookies, storage, cache of the LiteView profile only
            signOutPending = false;
            Log.Info("signed out of the Twitch website (WebView2 profile data cleared)");
            status.ForeColor = Dim; status.Text = "Signed out of twitch.tv in LiteView";
        }
        catch (Exception ex) { Log.Error("website sign-out", ex); }
    }

    [DllImport("user32.dll")] static extern uint GetGuiResources(IntPtr process, uint flags);

    async void ExportDiagnostics()
    {
        try
        {
            status.ForeColor = Dim; status.Text = "collecting diagnostics...";
            var f = new List<KeyValuePair<string, string>>();
            Action<string, object> add = (k, v) => f.Add(new KeyValuePair<string, string>(k, Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)));
            add("App", "LiteView Enhanced " + Application.ProductVersion);
            add("Based on upstream LiteView", AppMeta("UpstreamBase"));
            add("Windows", Environment.OSVersion.VersionString + (Environment.Is64BitOperatingSystem ? " (64-bit)" : ""));
            add("Runtime", RuntimeInformation.FrameworkDescription);
            string wv; try { wv = CoreWebView2Environment.GetAvailableBrowserVersionString(); } catch (Exception ex) { wv = "not available (" + ex.GetType().Name + ")"; }
            add("WebView2 runtime", wv);
            add("mpv", await FirstLine(mpv, "--version"));
            // the console streamlink.exe next to streamlinkw.exe prints its version reliably
            string slCon = string.IsNullOrEmpty(streamlink) ? null : Path.Combine(Path.GetDirectoryName(streamlink), "streamlink.exe");
            add("Streamlink", await FirstLine(slCon != null && File.Exists(slCon) ? slCon : streamlink, "--version"));
            add("CPU logical processors", Environment.ProcessorCount);
            add("CPU pinning", mask == 0 ? "none (Windows decides)" : "0x" + mask.ToString("X"));
            add("NVIDIA RTX card (RTX option)", HasNvidia);
            using (var gr = CreateGraphics()) add("Display DPI (this window)", gr.DpiX);
            add("Screens", string.Join("; ", Screen.AllScreens.Select(sc => sc.Bounds.Width + "x" + sc.Bounds.Height + " at " + sc.Bounds.X + "," + sc.Bounds.Y + (sc.Primary ? " (primary)" : ""))));
            add("Logged in to Twitch", Tw.Token != null ? "yes" : "no");
            add("Granted scopes", string.Join(" ", Tw.Granted));
            add("Watching", watching ? "yes" : "no");
            add("Quality / picture / RTX", quality.SelectedItem + " / " + picture.SelectedItem + " / " + (rtx.Checked ? "on" : "off"));
            add("Chat", chatOn.Checked ? (webChat ? "Twitch web chat" : "native") : "hidden");
            add("Stream", "generation " + gen + ", player connected " + (mpvGen == gen) + ", played " + played + ", status '" + status.Text + "'");
            add("Video source height", videoH);
            add("Enhancement applied", appliedPic + " / vf " + appliedVf);
            using (var me = Process.GetCurrentProcess())
            {
                add("LiteView working set MB", me.WorkingSet64 / (1024 * 1024));
                add("LiteView private MB", me.PrivateMemorySize64 / (1024 * 1024));
                add("Threads / handles", me.Threads.Count + " / " + me.HandleCount);
                add("GDI / USER objects", GetGuiResources(me.Handle, 0) + " / " + GetGuiResources(me.Handle, 1));
            }
            add("Emote image cache", ImagesStats());
            var tail = new List<string>(Log.Tail(300));
            try { if (File.Exists(slLog)) { tail.Add("--- streamlink.log (last 60 lines) ---"); tail.AddRange(ReadShared(slLog).Where(l => l.Length > 0).Reverse().Take(60).Reverse()); } }
            catch (IOException) { }
            string text = Diagnostics.Report(f, IniStore.Read(iniPath), tail);
            string path = Path.Combine(root, "diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".txt");
            AtomicFile.WriteText(path, text, false);
            Log.Info("diagnostics exported");
            status.Text = "Diagnostics saved: " + Path.GetFileName(path);
            using (Process.Start("explorer.exe", "/select," + WinArgs.Quote(path))) { }
        }
        catch (Exception ex) { Log.Error("diagnostics", ex); ShowProblem("Could not save diagnostics", ex.Message, ExportDiagnostics); }
    }

    static string ImagesStats() { return Images.Stats(); }

    static string AppMeta(string key)
    {
        foreach (var a in typeof(LiteView).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false))
        {
            var m = (System.Reflection.AssemblyMetadataAttribute)a;
            if (m.Key == key) return m.Value;
        }
        return "";
    }

    // first line of "<exe> --version", run hidden with a time limit
    static Task<string> FirstLine(string exe, string args)
    {
        return Task.Run(() =>
        {
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return "missing";
            try
            {
                var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    var outTask = p.StandardOutput.ReadToEndAsync();
                    if (!p.WaitForExit(10000)) { try { p.Kill(); } catch (InvalidOperationException) { } return "no answer in 10 s"; }
                    string o = outTask.Wait(2000) ? outTask.Result : "";
                    return o.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "(no output)";
                }
            }
            catch (Exception ex) { return "error: " + ex.Message; }
        });
    }

    // ---------- logging and crashes ----------

    static void StartLogging()
    {
        string exePath = typeof(LiteView).Assembly.Location;
        string dir = Path.GetDirectoryName(exePath), name = Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();
        Log.Init(dir, name);
        Log.Info("start: LiteView Enhanced " + Application.ProductVersion + " (upstream " + AppMeta("UpstreamBase") + "), "
            + Environment.OSVersion.VersionString + ", " + RuntimeInformation.FrameworkDescription);
    }

    static volatile bool windowShown;
    static int fatalShown;

    // LiteView is about to end. Before the window is up, a message is the only sign of it (upstream: nothing).
    static void Fatal(object ex)
    {
        string log = "liteview.log";
        try { log = LogFatal(ex); } catch { }   // LiteView.Core.dll itself may be what failed to load
        if (Interlocked.Exchange(ref fatalShown, 1) != 0) return;
        try
        {
            var x = ex as Exception;
            MessageBox.Show((windowShown ? "LiteView stopped because of an error" : "LiteView could not start") + ":\n\n"
                + (x != null ? x.GetType().Name + ": " + x.Message : "unknown error") + "\n\nDetails: " + log, "LiteView",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static string LogFatal(object ex) { Log.Error("fatal: " + ex); return Log.File ?? "liteview.log"; }

    static void CatchCrashes()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (s, e) =>
        {
            Log.Error("unhandled: " + e.Exception);
            var form = Application.OpenForms.OfType<LiteView>().FirstOrDefault();
            if (form != null && (DateTime.Now - form.lastCrashBox).TotalSeconds < 30) return;   // no message box storms
            if (form != null) form.lastCrashBox = DateTime.Now;
            MessageBox.Show("Something went wrong: " + e.Exception.Message + "\n\nLiteView keeps running. The details are in " + Path.GetFileName(Log.File ?? "liteview.log")
                + " (chat gear menu > Open log folder).", "LiteView", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        AppDomain.CurrentDomain.UnhandledException += (s, e) => Fatal(e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (s, e) => { Log.Warn("background task: " + e.Exception.GetBaseException().Message); e.SetObserved(); };
    }
}
