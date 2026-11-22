// A video's seek bar (src/app/VideoBar.cs) and a past broadcast's chat replay (src/core/ChatReplay.cs) follow the
// video's position (read every half second, src/app/LiteView.Videos.cs).
// Chat replay: when a past broadcast (or highlight, upload) plays with the native chat on, the chat shows the
// comments of that moment instead of the channel's live chat (chat gear > "Chat replay for videos" turns it off).
// Comments are fetched about 30 s ahead of the video, one request at a time and at most one a second; a jump (the
// bar, the arrow keys, Play after Pause) starts over from the new position, with the last 10 s shown at once. If
// Twitch does not answer, or refuses, the chat says so and the video plays on. Clips keep the live chat.
using System;
using System.Linq;
using System.Windows.Forms;
using LiteViewCore;

partial class LiteView
{
    readonly VideoBar videoBar = new VideoBar { Dock = DockStyle.Bottom, Visible = false };
    ReplayBuffer replayBuf;
    string replayVideo;
    int replayGen, replayErrors;
    bool replayFetching, replayStopped, replayLogNext, replaySeeked, chatReplay = true;   // setting "chatreplay"
    double fixturePos = -1;   // UI fixture (CI): a position that stands in for mpv's (the test video is 2 s long)
    double replayLastPos = -1;
    DateTime replayNextAt = DateTime.MinValue;
    bool barInit;

    void InitVideoBar()
    {
        if (barInit) return;
        barInit = true;
        video.Controls.Add(videoBar);
        video.Controls.SetChildIndex(videoBar, video.Controls.Count - 1);   // docked first: under the picture, the surfaces fill the rest
        videoBar.Seek += SeekVideo;
    }

    // Watch / the stream's start: the bar for a video, none for live
    void ShowVideoBar()
    {
        InitVideoBar();
        bool on = watching && currentVideo != null;
        if (on && currentVideo.Seconds > 0 && videoBar.Duration <= 0) videoBar.Set(videoPos, currentVideo.Seconds);
        if (videoBar.Visible == on) return;
        videoBar.Visible = on;
        if (!on) videoBar.Clear();
    }

    // arrow keys seek a video (not while typing; mpv's own arrow keys work too when its window has the keyboard)
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (watching && currentVideo != null && !Typing && !(ActiveControl is ComboBox) && (keyData == Keys.Left || keyData == Keys.Right) && Form.ActiveForm == this)
        {
            SeekVideoBy(keyData == Keys.Left ? -10 : 10);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    void SeekVideo(double t)
    {
        if (currentVideo == null || mpvGen != gen) return;
        t = Math.Max(0, t);
        SendMpv("{\"command\":[\"seek\"," + t.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + ",\"absolute\"]}");
        videoPos = (int)t; videoBar.Set(t, 0);
        RestartReplay(t);   // any jump LiteView makes starts the replay again, however small
        Log.Info("video: seek to " + VideoData.Length((int)t));
    }

    void SeekVideoBy(int seconds) { if (currentVideo != null) SeekVideo(Math.Max(0, videoBar.Position + seconds)); }

    // the video's position (UI thread)
    void VideoAt(double pos)
    {
        if (fixturePos >= 0) pos = fixturePos;
        videoBar.Set(pos, 0);
        ReplayAt(pos);
        RememberVideo(pos, false);   // src/app/LiteView.Watched.cs
    }

    // PipeLoop thread: mpv seeked (its own keys, or a loop): the next position decides whether it was a jump
    bool SeekLine(string line, int g)
    {
        if (line.IndexOf("\"event\":\"seek\"", StringComparison.Ordinal) < 0) return false;
        Ui(g, () => replaySeeked = true);
        return true;
    }

    // ---------- chat replay ----------

    // StartChat: a past broadcast's chat replay instead of the live chat (false: the live chat as usual)
    bool StartReplayChat()
    {
        if (!chatReplay || currentVideo == null || currentVideo.IsClip || !ChannelName.IsNumericId(currentVideo.Id) || channel == null) { replayVideo = null; return false; }
        if (replayVideo == currentVideo.Id && nchat.InReplay && nchat.Channel == channel) { replayLastPos = -1; return true; }   // the same video again (Play after Pause, F5): starts over at its position
        replayVideo = currentVideo.Id; replayGen++; replayErrors = 0; replayStopped = false;   // a request still on its way keeps the gate
        replayBuf = new ReplayBuffer(); replayLastPos = -1;
        nchat.ReplayJoin(channel, currentVideo.UserId);
        Log.Info("chat replay: video " + currentVideo.Id);
        return true;
    }

    void ReplayAt(double pos)
    {
        if (replayBuf == null || replayStopped || currentVideo == null || currentVideo.Id != replayVideo || !nchat.InReplay || webChat || !chatOn.Checked) return;
        bool seeked = replaySeeked && Math.Abs(pos - replayLastPos) > 1.5;   // mpv seeked, and it moved more than playing would
        replaySeeked = false;
        if (replayLastPos < 0 || seeked || pos < replayLastPos - 2 || pos > replayLastPos + 8) RestartReplay(pos);   // the start, or a jump: from here
        replayLastPos = pos;
        var due = replayBuf.Due(pos);
        if (due.Count > 0) nchat.ReplayAdd(due.Select(c => c.Msg));
        if (!replayFetching && replayBuf.NeedsFetch(pos) && DateTime.UtcNow >= replayNextAt) FetchReplay();   // else the next position (half a second) looks again
    }

    void RestartReplay(double pos)
    {
        if (replayBuf == null || currentVideo == null || currentVideo.Id != replayVideo || !nchat.InReplay) return;
        if (replayLastPos >= 0) nchat.ReplayRestart("Chat replay from " + (VideoData.Length((int)pos).Length > 0 ? VideoData.Length((int)pos) : "the start"));
        replayGen++; replayLogNext = true;   // an answer still on its way is for the old position: it is dropped
        replayBuf.Reset(pos);
        replayLastPos = pos;
    }

    // one request at a time and at most one a second, whatever jumps happen meanwhile (an answer to an old position
    // still holds the gate until it arrives)
    async void FetchReplay()
    {
        string video = replayVideo, ch = channel; int g = replayGen, offset = replayBuf.NextOffset();
        replayFetching = true; replayNextAt = DateTime.UtcNow.AddSeconds(1);
        object json;
        try { json = await Tw.Gql(ChatReplay.Query(video, offset)); }
        catch (ArgumentException) { json = null; }
        replayFetching = false;
        if (g != replayGen || video != replayVideo || replayBuf == null)
        {   // a jump or another video meanwhile: ask for the new position (when the gate allows)
            if (replayLastPos >= 0 && video == replayVideo) ReplayAt(replayLastPos);
            return;
        }
        var page = ChatReplay.Parse(json, ch);
        if (page == null)
        {
            replayErrors++;
            Log.Warn("chat replay: no answer at " + offset + " s (" + replayErrors + ")");
            if (replayErrors >= 3) { replayStopped = true; nchat.ReplayNote("Chat replay is not available right now (Twitch did not answer)."); return; }
            replayNextAt = DateTime.UtcNow.AddSeconds(5 * replayErrors);
            return;
        }
        if (page.Error != null)
        {
            replayStopped = true;
            Log.Warn("chat replay: Twitch refused (" + page.Error + ")");
            nchat.ReplayNote("Chat replay is not available for this video (Twitch: " + page.Error + ").");
            return;
        }
        replayErrors = 0;
        replayBuf.Add(page);
        if (replayLogNext || Fixture.On) { replayLogNext = false; Log.Info("chat replay: " + page.Comments.Count + " comments from " + offset + " s"); }   // the first answer after the start or a jump
        if (replayLastPos >= 0) ReplayAt(replayLastPos);   // what is due now shows at once
    }
}
