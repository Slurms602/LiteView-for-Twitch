// Hover previews (src/app/BrowsePreview.cs asks for them): the card's live stream at the lowest quality, muted, in
// a box over the card's picture, through its own Streamlink + mpv in their own kill-on-close job. Only one at a time,
// only on the Following page; it ends when the mouse leaves the card, the page scrolls or changes, a stream starts,
// or LiteView closes. The box and mpv's window are disabled, so the mouse still reaches the page (a click on the
// card opens the channel as usual). Off: menu > Playback > Previews on hover.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using LiteViewCore;

partial class LiteView
{
    readonly Panel previewBox = new Panel { Visible = false, BackColor = Color.Black };
    readonly System.Windows.Forms.Timer previewWatch = new System.Windows.Forms.Timer { Interval = 200 };
    IntPtr previewJob; Process previewProc; string previewLogin; DateTime previewStart; bool previewSeen;

    [DllImport("user32.dll")] static extern bool EnableWindow(IntPtr h, bool enable);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr h, uint code);

    string PreviewLog { get { return Path.Combine(Path.GetDirectoryName(slLog), Path.GetFileNameWithoutExtension(slLog) + "-preview.log"); } }

    void InitPreview()
    {
        previewBox.Enabled = false;
        browse.Controls.Add(previewBox);
        browse.PreviewWanted += StartPreview;
        browse.PreviewGone += StopPreview;
        previewWatch.Tick += (s, e) => WatchPreview();
        FormClosing += (s, e) => { if (!e.Cancel) StopPreview(); };
    }

    void StartPreview(string login, Rectangle r)
    {
        if (watching || !browse.Visible || WindowState == FormWindowState.Minimized || (Fixture.On && !Fixture.Streams)) return;
        if (MissingPlayerFiles() != null) return;
        StopPreview();
        previewBox.Bounds = r; previewBox.Visible = true; previewBox.BringToFront();
        var started = LaunchPlayer(new StreamRequest { Channel = login, LogFile = PreviewLog, WindowHandle = previewBox.Handle.ToInt64(), Preview = true }, 0x40);   // idle priority: a preview never competes
        if (started.Key == IntPtr.Zero) { previewBox.Visible = false; return; }
        previewJob = started.Key; previewProc = started.Value; previewLogin = login; previewStart = DateTime.Now; previewSeen = false;
        previewWatch.Start();
        Log.Info("preview: " + login);
    }

    // Streamlink (and the mpv it starts) for a preview or an extra window, suspended, in a new kill-on-close job, then
    // resumed; the job and Streamlink's process, or (Zero, null). The request's paths and pipe name are filled in here.
    KeyValuePair<IntPtr, Process> LaunchPlayer(StreamRequest r, uint priority)
    {
        var none = new KeyValuePair<IntPtr, Process>(IntPtr.Zero, null);
        r.Streamlink = streamlink; r.Mpv = mpv; r.PipeName = StreamCommand.NewPipeName();
        string cmdline;
        try { cmdline = StreamCommand.Build(r); } catch (ArgumentException) { return none; }
        try { File.Delete(r.LogFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return RunInJob(cmdline, priority);
    }

    // a Streamlink command line, suspended in a new kill-on-close job, then resumed (also downloads: src/app/LiteView.Downloads.cs)
    KeyValuePair<IntPtr, Process> RunInJob(string cmdline, uint priority)
    {
        var none = new KeyValuePair<IntPtr, Process>(IntPtr.Zero, null);
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return none;
        var lim = new JOBLIMITS { flags = 0x2000 };   // kill on close
        SetInformationJobObject(job, 9, ref lim, Marshal.SizeOf(typeof(JOBLIMITS)));
        var si = new Native.STARTUPINFO(); si.cb = Marshal.SizeOf(si);
        Native.PROCESS_INFORMATION pi;
        if (!CreateProcessW(streamlink, new StringBuilder(cmdline), IntPtr.Zero, IntPtr.Zero, false, Native.CREATE_SUSPENDED | priority, IntPtr.Zero, root, ref si, out pi))
        {
            Native.CloseHandle(job); return none;
        }
        if (mask != 0) Native.SetProcessAffinityMask(pi.hProcess, new UIntPtr(mask));
        if (!AssignProcessToJobObject(job, pi.hProcess))
        {   // outside the job it could outlive LiteView: never let it run
            TerminateProcess(pi.hProcess, 1); Native.CloseHandle(pi.hThread); Native.CloseHandle(pi.hProcess); Native.CloseHandle(job);
            return none;
        }
        Process p = null;
        try { p = Process.GetProcessById(pi.pid); var unused = p.Handle; } catch (Exception) { if (p != null) p.Dispose(); p = null; }
        Native.ResumeThread(pi.hThread); Native.CloseHandle(pi.hThread); Native.CloseHandle(pi.hProcess);
        if (p == null) { TerminateJobObject(job, 0); Native.CloseHandle(job); return none; }
        return new KeyValuePair<IntPtr, Process>(job, p);
    }

    // every 200 ms: mpv's window, once there, is disabled (the mouse goes on to the page); a preview that cannot play goes
    void WatchPreview()
    {
        if (previewJob == IntPtr.Zero) { previewWatch.Stop(); return; }
        if (watching || !Visible || !browse.Visible || WindowState == FormWindowState.Minimized) { StopPreview(); return; }   // minimised, in the tray, or a stream started
        bool alive = false;
        try { alive = previewProc != null && !previewProc.HasExited; } catch (InvalidOperationException) { }
        if (!alive || (!previewSeen && DateTime.Now - previewStart > TimeSpan.FromSeconds(15))) { StopPreview(); return; }
        if (previewSeen) return;
        var h = FindWindowEx(previewBox.Handle, IntPtr.Zero, null, null);
        if (h == IntPtr.Zero) return;
        EnableWindow(h, false);
        previewSeen = true;
        Log.Info("preview: playing");
    }

    void StopPreview()
    {
        previewWatch.Stop();
        previewBox.Visible = false;
        if (previewJob != IntPtr.Zero) { TerminateJobObject(previewJob, 0); Native.CloseHandle(previewJob); previewJob = IntPtr.Zero; }
        if (previewProc != null) { previewProc.Dispose(); previewProc = null; }
        previewLogin = null;
    }
}

// Extra windows (src/app/ExtraPlayer.cs): one more channel each, at most ExtraPlayer.MaxExtra. Not owned by the main
// window (an owned window hides when the main one is minimised, and always stays above it): on top of every window or
// a normal one, as chosen; they close with LiteView.
partial class LiteView
{
    readonly List<ExtraPlayer> extras = new List<ExtraPlayer>();
    PopOptions popOptions = new PopOptions();
    bool extrasInit;

    void OpenWindow(string login, string name)
    {
        if (!ChannelName.IsValidLogin(login) || (Fixture.On && !Fixture.Streams)) return;
        if (!extrasInit) { extrasInit = true; FormClosed += (s, e) => { foreach (var x in extras.ToArray()) if (!x.IsDisposed) x.Close(); }; }
        extras.RemoveAll(x => x.IsDisposed);
        var open = extras.Find(x => x.Login == login);
        if (open != null) { if (open.WindowState == FormWindowState.Minimized) open.WindowState = FormWindowState.Normal; open.Activate(); return; }
        if (extras.Count >= ExtraPlayer.MaxExtra) { MessageBox.Show(this, "LiteView plays at most " + ExtraPlayer.MaxExtra + " channels in extra windows. Close one first.", "Watch in a new window"); return; }
        string missing = MissingPlayerFiles();
        if (missing != null) { ShowProblem(missing, missing, null); return; }
        int slot = 1;
        while (extras.Exists(x => x.Tag is int && (int)x.Tag == slot)) slot++;
        string log = Path.Combine(Path.GetDirectoryName(slLog), Path.GetFileNameWithoutExtension(slLog) + "-window" + slot + ".log");
        var w = new ExtraPlayer(login, string.IsNullOrEmpty(name) ? login : name, log, popOptions, () => quality.SelectedItem as string, SaveSettings,
            r => LaunchPlayer(r, ChildPriorityFlag)) { Tag = slot, Icon = Icon };
        w.LoginRequested += Login;
        w.WatchHere += () => { Activate(); Watch(login); };
        w.FormClosed += (s, e) => extras.Remove(w);
        extras.Add(w);
        w.Show();
    }
}
