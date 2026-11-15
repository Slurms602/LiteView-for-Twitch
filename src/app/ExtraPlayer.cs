// More streams at once (the brief's "multiple stream windows"; the owner chose separate windows over a grid): right-click
// a channel > Watch in a new window. Each extra window plays one channel with its own Streamlink and mpv in its own
// kill-on-close job, at 720p unless the stream has less, without chat, with its own sound (M and the mouse wheel on
// the video, as in the main window). At most MaxExtra at once; closing LiteView closes them (they are owned windows).
// The main window's stream is not affected (two decoders run, so the brief's warning applies: each costs CPU/GPU).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using LiteViewCore;

sealed class ExtraPlayer : Form
{
    public const int MaxExtra = 3;
    readonly Panel surface = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };
    readonly Label note = new Label { Dock = DockStyle.Fill, BackColor = Color.Black, ForeColor = Color.FromArgb(173, 173, 184), TextAlign = ContentAlignment.MiddleCenter };
    readonly System.Windows.Forms.Timer watch = new System.Windows.Forms.Timer { Interval = 500 };
    readonly Func<string, IntPtr, KeyValuePair<IntPtr, Process>> launch;
    readonly string login, log;
    IntPtr job; Process proc; DateTime started; bool seen;

    [DllImport("user32.dll")] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("kernel32.dll")] static extern bool TerminateJobObject(IntPtr job, uint code);

    /// <param name="launch">starts Streamlink for (log file, window) and returns its job and process (job Zero: failed)</param>
    public ExtraPlayer(string login, string name, string log, Func<string, IntPtr, KeyValuePair<IntPtr, Process>> launch)
    {
        this.login = login; this.log = log; this.launch = launch;
        Text = name + "  -  LiteView"; BackColor = Color.Black; ClientSize = new Size(640, 360); MinimumSize = new Size(320, 200);
        StartPosition = FormStartPosition.WindowsDefaultLocation;
        note.Font = new Font("Segoe UI", 11f);   // a Label draws with GDI, which does not see the private Inter
        note.Cursor = Cursors.Hand;
        note.Click += (s, e) => { if (job == IntPtr.Zero) Start(); };
        Controls.Add(surface); Controls.Add(note); note.BringToFront();
        watch.Tick += (s, e) => Check();
        Shown += (s, e) => Start();
        FormClosed += (s, e) => { watch.Stop(); watch.Dispose(); Stop(); };
    }

    public string Login { get { return login; } }

    void Start()
    {
        Stop();
        note.Text = "Opening " + login + "..."; note.Visible = true; note.BringToFront();
        var r = launch(log, surface.Handle);
        if (r.Key == IntPtr.Zero) { note.Text = "Could not start Streamlink. Click to try again."; return; }
        job = r.Key; proc = r.Value; started = DateTime.Now; seen = false;
        watch.Start();
        Log.Info("window: " + login);
    }

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
        if (job != IntPtr.Zero) { TerminateJobObject(job, 0); Native.CloseHandle(job); job = IntPtr.Zero; }
        if (proc != null) { proc.Dispose(); proc = null; }
    }
}
