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

    void InitFork()
    {
        retry.Tick += (s, e) =>
        {
            retry.Stop();
            if (watching && retryGen == gen && channel != null && slProc == null) Watch(channel, true);
        };
        status.Click += (s, e) => { if (canRetry && watching && slProc == null && channel != null) Watch(channel); };
        Log.Info("window ready; CPU mask " + (mask == 0 ? "none (Windows decides)" : "0x" + mask.ToString("X")) + ", NVIDIA " + HasNvidia);
    }

    // ---------- embedded browser ----------

    static void Harden(CoreWebView2 core, bool messages)
    {
        core.IsMuted = true;
        var st = core.Settings;
        st.IsStatusBarEnabled = false;
        st.AreDevToolsEnabled = Environment.GetEnvironmentVariable("LITEVIEW_DEVTOOLS") == "1";
        st.AreHostObjectsAllowed = false;
        st.IsWebMessageEnabled = messages;
        st.IsGeneralAutofillEnabled = false;
        st.IsPasswordAutosaveEnabled = false;
        // camera, microphone, notifications, clipboard, location...: the Twitch pages here need none of them
        core.PermissionRequested += (s, e) => e.State = CoreWebView2PermissionState.Deny;
        core.DownloadStarting += (s, e) => e.Cancel = true;
        core.NavigationStarting += (s, e) =>
        {
            var nav = SafeUrl.ForWebView(e.Uri);
            if (nav == SafeUrl.Nav.Allow) return;
            e.Cancel = true;
            if (nav == SafeUrl.Nav.OpenInBrowser && e.IsUserInitiated) Launcher.Open(e.Uri);
            else Log.Info("web: blocked a navigation (" + Launcher.SchemeOf(e.Uri) + ")");
        };
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
        catch (ArgumentException ex) { status.Text = "Cannot play this channel."; Log.Error("stream command", ex); return; }
        try { File.Delete(slLog); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) { status.Text = "Could not start Streamlink."; Log.Error("CreateJobObject failed"); return; }
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
            status.ForeColor = Color.FromArgb(255, 140, 140); status.Text = "Could not start Streamlink.";
            Log.Error("stream " + g + ": CreateProcess failed, error " + err);
            canRetry = true;
        }
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
        status.ForeColor = Color.FromArgb(255, 140, 140);
        status.Text = StreamOutcome.Describe(end, err);
        if (wasPlaying) playBackoff.Ended(ran);
        ScheduleRetry(end);
    }

    // Offline: look again every minute. Errors: retry with growing waits (2 s ... 60 s, with jitter),
    // and after MaxQuickRetries failures in a row stop and let the user click the status to try again.
    void ScheduleRetry(StreamEnd end)
    {
        canRetry = channel != null;
        if (!watching || channel == null || end == StreamEnd.Fatal) { if (canRetry) status.Text += "  -  click to try again"; return; }
        TimeSpan wait;
        if (end == StreamEnd.Offline) wait = TimeSpan.FromSeconds(60);
        else
        {
            if (playBackoff.Attempt >= MaxQuickRetries) { status.Text += "  -  click to try again"; Log.Warn("stream: giving up after " + MaxQuickRetries + " quick retries"); return; }
            wait = playBackoff.Next();
        }
        retryGen = gen;
        retry.Interval = (int)Math.Max(500, Math.Min(int.MaxValue, wait.TotalMilliseconds));
        retry.Start();
        status.Text += "  -  trying again in " + Math.Max(1, (int)Math.Round(wait.TotalSeconds)) + " s";
    }

    void CancelRetry() { retry.Stop(); canRetry = false; playBackoff.Reset(); }

    // ---------- diagnostics ----------

    void AddForkMenuItems(ContextMenuStrip menu)
    {
        var diag = new ToolStripMenuItem("Export diagnostics...");
        diag.Click += (s, e) => ExportDiagnostics();
        var logs = new ToolStripMenuItem("Open log folder");
        logs.Click += (s, e) => { try { using (Process.Start("explorer.exe", WinArgs.Quote(root))) { } } catch (Exception ex) { Log.Error("open folder", ex); } };
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(diag);
        menu.Items.Add(logs);
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
            add("Streamlink", await FirstLine(streamlink, "--version"));
            add("CPU logical processors", Environment.ProcessorCount);
            add("CPU pinning", mask == 0 ? "none (Windows decides)" : "0x" + mask.ToString("X"));
            add("NVIDIA driver present", HasNvidia);
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
        catch (Exception ex) { Log.Error("diagnostics", ex); status.Text = "Could not save diagnostics: " + ex.Message; }
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
        AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Error("fatal: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (s, e) => { Log.Warn("background task: " + e.Exception.GetBaseException().Message); e.SetObserved(); };
    }
}
