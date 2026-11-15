// While a stream plays, the status line shows its viewers, uptime, category and title, as under Twitch's player
// (Fmt.Playing). From Helix (streams?user_login) when the stream starts playing and once a minute while it plays and
// the window is not minimised; the whole title is the tooltip. Logged out, or before the answer: the line stays empty.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using LiteViewCore;

partial class LiteView
{
    readonly Following infoFeed = new Following(q => Tw.Helix(q));
    readonly Latest infoGuard = new Latest();
    readonly Timer infoTimer = new Timer { Interval = 60000 };
    LiveStream info; string infoFor; bool infoInit;

    // from the stream tick (every half second) while our player is playing
    void ShowPlayingInfo()
    {
        if (!infoInit)
        {
            infoInit = true;
            // only while our player plays this stream (not while Streamlink waits for an offline channel, paused or browsing)
            infoTimer.Tick += (s, e) => { if (watching && played && mpvGen == gen && slProc != null && WindowState != FormWindowState.Minimized) FetchInfo(); };
            infoTimer.Start();
            FormClosing += (s, e) => infoTimer.Stop();
        }
        if (infoFor != channel) { infoFor = channel; info = null; FetchInfo(); }
        var st = Tw.Token != null && info != null && info.Login == channel ? info : null;   // logged out: nothing
        string t = Fmt.Playing(st, DateTimeOffset.UtcNow);
        if (status.Text == t) return;
        status.ForeColor = Dim; status.Text = t;
        if (st != null && st.Title.Length > 0) statusTip.SetToolTip(status, st.Title + (st.Game.Length > 0 ? "\n" + st.Game : ""));
    }

    async void FetchInfo()
    {
        string ch = channel;
        if (ch == null || Tw.Token == null) { info = null; return; }
        int t = infoGuard.Next();
        List<LiveStream> r = null;
        try { r = await infoFeed.LiveNow(new[] { ch }); }
        catch (Exception ex) { Log.Warn("stream info: " + ex.GetType().Name); }
        if (!infoGuard.IsCurrent(t) || IsDisposed || ch != channel || r == null || Tw.Token == null) return;   // unreachable: keep what is shown
        info = r.FirstOrDefault(x => x.Login == ch);
    }
}
