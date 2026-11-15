// Hover previews (src/app/BrowsePreview.cs asks for them): the card's live stream at the lowest quality, muted, in
// a box over the card's picture, through its own Streamlink + mpv in their own kill-on-close job. Only one at a time,
// only on the Following page; it ends when the mouse leaves the card, the page scrolls or changes, a stream starts,
// or LiteView closes. The box and mpv's window are disabled, so the mouse still reaches the page (a click on the
// card opens the channel as usual). Off: menu > Playback > Previews on hover.
using System;
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

    string PreviewLog { get { return Path.Combine(Path.GetDirectoryName(slLog), Path.GetFileNameWithoutExtension(slLog) + "-preview.log"); } }

    void InitPreview()
    {
        previewBox.Enabled = false;
        browse.Controls.Add(previewBox);
        browse.PreviewWanted += StartPreview;
        browse.PreviewGone += StopPreview;
        previewWatch.Tick += (s, e) => WatchPreview();
        FormClosing += (s, e) => StopPreview();
    }

    void StartPreview(string login, Rectangle r)
    {
        if (watching || !browse.Visible || WindowState == FormWindowState.Minimized || (Fixture.On && !Fixture.Streams)) return;
        if (MissingPlayerFiles() != null) return;
        StopPreview();
        previewBox.Bounds = r; previewBox.Visible = true; previewBox.BringToFront();
        string cmdline;
        try
        {
            cmdline = StreamCommand.Build(new StreamRequest
            {
                Streamlink = streamlink, Mpv = mpv, LogFile = PreviewLog, Channel = login, PipeName = StreamCommand.NewPipeName(),
                WindowHandle = previewBox.Handle.ToInt64(), Preview = true,
            });
        }
        catch (ArgumentException) { previewBox.Visible = false; return; }
        try { File.Delete(PreviewLog); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) { previewBox.Visible = false; return; }
        var lim = new JOBLIMITS { flags = 0x2000 };   // kill on close
        SetInformationJobObject(job, 9, ref lim, Marshal.SizeOf(typeof(JOBLIMITS)));
        var si = new Native.STARTUPINFO(); si.cb = Marshal.SizeOf(si);
        Native.PROCESS_INFORMATION pi;
        const uint IdlePriority = 0x40;   // a preview never competes with anything
        if (!CreateProcessW(streamlink, new StringBuilder(cmdline), IntPtr.Zero, IntPtr.Zero, false, Native.CREATE_SUSPENDED | IdlePriority, IntPtr.Zero, root, ref si, out pi))
        {
            Native.CloseHandle(job); previewBox.Visible = false; return;
        }
        AssignProcessToJobObject(job, pi.hProcess);
        try { previewProc = Process.GetProcessById(pi.pid); var unused = previewProc.Handle; } catch (Exception) { if (previewProc != null) previewProc.Dispose(); previewProc = null; }
        Native.ResumeThread(pi.hThread); Native.CloseHandle(pi.hThread); Native.CloseHandle(pi.hProcess);
        previewJob = job; previewLogin = login; previewStart = DateTime.Now; previewSeen = false;
        previewWatch.Start();
        Log.Info("preview: " + login);
    }

    // every 200 ms: mpv's window, once there, is disabled (the mouse goes on to the page); a preview that cannot play goes
    void WatchPreview()
    {
        if (previewJob == IntPtr.Zero) { previewWatch.Stop(); return; }
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
