// Seamless channel switching (P3 "improved stream switching"). Upstream stopped the playing stream and then started the
// next one, so the video stayed black for the seconds Streamlink and mpv need to open a stream. Now the video area
// holds two surfaces: the playing stream keeps playing on the front one while the next one opens, muted, on the one
// behind it. At the next stream's first frame the surfaces swap, it gets the sound, and the old Streamlink and mpv end
// (their job object). The old one also ends when the next channel turns out offline, when its stream fails, when
// anything else stops the stream (pause, the Following page, closing) or after HandOverLimit at most, so two streams
// only ever play together while one opens. Also used for a quality change. Off: menu > Playback > Seamless switching.
// Each log line "stream N: first frame after X s" measures how long a stream took to show (TESTING.md, CI).
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using LiteViewCore;

partial class LiteView
{
    static readonly TimeSpan HandOverLimit = TimeSpan.FromSeconds(12);
    readonly Panel[] surfaces = { new Panel(), new Panel() };
    int front;            // the surface on top
    int streamSurface;    // the surface the current stream's mpv draws in
    bool seamless = true; // setting "seamless"
    volatile bool muteNext;   // PipeLoop: the next stream starts muted while the old one still plays

    sealed class Outgoing { public IntPtr Job; public Process Proc; public int Gen; public DateTime Since; }
    Outgoing outgoing;
    DateTime openedAt = DateTime.Now;
    int firstFrameGen = -1;
    double lastOpenSeconds = -1;   // the last stream's time to its first frame (the stats overlay shows it)

    // the Streamlink log of the current stream: one file per surface, so two streams never share one
    string curLog { get { return streamSurface == 0 ? slLog : Path.Combine(Path.GetDirectoryName(slLog), Path.GetFileNameWithoutExtension(slLog) + "-2.log"); } }

    void InitSwitch()
    {
        foreach (var s in surfaces) { s.Dock = DockStyle.Fill; s.BackColor = Color.Black; video.Controls.Add(s); }
        surfaces[0].BringToFront();
        pausedNote.BringToFront();
    }

    // Watch: instead of StopStream. Keeps a playing stream playing while the next one opens (or stops it as before).
    void HandOverOrStop()
    {
        openedAt = DateTime.Now;
        comparing = false;   // B (the original picture) lasts until the next stream
        bool alive = false;
        try { alive = slProc != null && !slProc.HasExited; } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        if (!seamless || paused || gamingActive || !played || mpvGen != gen || job == IntPtr.Zero || !alive || !watching)
        {
            StopStream();
            streamSurface = front;
            return;
        }
        KillOutgoing();   // a switch while the last one was still opening: the oldest goes
        outgoing = new Outgoing { Job = job, Proc = slProc, Gen = gen, Since = DateTime.Now };
        job = IntPtr.Zero; slProc = null; slPid = 0;
        muteNext = true;
        // PipeLoop leaves the old player's pipe (it waits for the next stream's, StartStreamProcess sets streamOn again)
        streamOn.Reset();
        lock (pipeLock) { if (mpvPipe != null) try { mpvPipe.Dispose(); } catch (Exception) { } }
        streamSurface = 1 - front;
        Log.Info("stream " + gen + ": keeps playing while the next one opens");
    }

    void KillOutgoing()
    {
        var o = outgoing;
        if (o == null) return;
        outgoing = null; muteNext = false;
        if (o.Job != IntPtr.Zero) { TerminateJobObject(o.Job, 0); Native.CloseHandle(o.Job); }
        if (o.Proc != null) o.Proc.Dispose();
    }

    // PipeLoop (UI thread, this stream's generation): mpv showed its first frame
    void FirstFrame()
    {
        if (firstFrameGen == gen) return;
        firstFrameGen = gen;
        lastOpenSeconds = (DateTime.Now - openedAt).TotalSeconds;
        string took = lastOpenSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        if (front != streamSurface)
        {
            surfaces[streamSurface].BringToFront();
            front = streamSurface;
            if (pausedNote.Visible) pausedNote.BringToFront();
        }
        if (outgoing != null)
        {
            KillOutgoing();
            SendMpv("{\"command\":[\"set_property\",\"mute\"," + (muted ? "true" : "false") + "]}");
            Log.Info("stream " + gen + ": first frame after " + took + " s; the last stream played until then");
        }
        else Log.Info("stream " + gen + ": first frame after " + took + " s");
    }

    // mpv's IPC: the event at the start of playback, or the answer to FirstFrameQuery when it was already playing when
    // LiteView connected (mpv answers "property unavailable" before the first frame)
    const string FirstFrameQuery = "{\"command\":[\"get_property\",\"playback-time\"],\"request_id\":71}";
    static bool IsFirstFrame(string line)
    {
        if (line.IndexOf("\"event\":\"playback-restart\"", StringComparison.Ordinal) >= 0) return true;
        return line.IndexOf("\"request_id\":71", StringComparison.Ordinal) >= 0 && line.IndexOf("\"error\":\"success\"", StringComparison.Ordinal) >= 0;
    }

    // OnTick: the old stream ends when the next one cannot take over soon
    void CheckHandOver()
    {
        if (outgoing == null) return;
        if (DateTime.Now - outgoing.Since > HandOverLimit) { Log.Warn("stream " + gen + ": no picture after " + (int)HandOverLimit.TotalSeconds + " s; the last stream was stopped"); EndHandOver(); }
    }

    // the next stream will not show a picture soon (offline, failed, stopped): the old one ends, the next one's
    // surface comes to the front (it shows the status), and the next stream gets the sound when it plays
    void EndHandOver()
    {
        if (outgoing == null) return;
        KillOutgoing();
        if (front != streamSurface) { surfaces[streamSurface].BringToFront(); front = streamSurface; if (pausedNote.Visible) pausedNote.BringToFront(); }
        SendMpv("{\"command\":[\"set_property\",\"mute\"," + (muted ? "true" : "false") + "]}");
    }
}
