// Playing a channel's video or clip (the Following page's Videos and clips view): through Watch like a live stream,
// so the chat (the channel's live chat), seamless switching, pause, retries and the stats work the same; Streamlink
// passes the video's playlist to mpv, so it can seek (arrow keys, its seek bar). Its position is read every second, so
// Pause / Play, F5, a quality change or a retry continue where it was. At its end the status says so (no retry).
using System;
using System.Globalization;
using LiteViewCore;

partial class LiteView
{
    VideoInfo currentVideo, videoNext;   // what plays instead of the live stream (null: live)
    bool replaying; int videoPos, videoTicks;
    const int VideoPosId = 83;

    void PlayVideo(VideoInfo v)
    {
        if (v == null || !ChannelName.IsValidLogin(v.Login)) return;
        Log.Info("video: " + (v.IsClip ? "clip" : VideoData.KindName(v.Kind).ToLowerInvariant()) + " " + v.Id);
        videoNext = v;
        Watch(v.Login);
    }

    // the same stream again (F5, Play, the quality box, Try again): a video stays a video
    void Replay()
    {
        if (channel == null) return;
        replaying = true;
        try { Watch(channel); } finally { replaying = false; }
    }

    // Watch: is it a video this time? (a click on a channel anywhere means its live stream)
    void TakeVideo(string name, bool isRetry)
    {
        if (videoNext != null) { currentVideo = videoNext; videoNext = null; videoPos = 0; return; }
        if (currentVideo != null && (isRetry || replaying) && currentVideo.Login == name) return;
        currentVideo = null; videoPos = 0;
    }

    // StartStreamProcess: the video's part of the command, and its name in the title
    void VideoRequest(StreamRequest r)
    {
        if (currentVideo == null) return;
        if (currentVideo.IsClip) r.Clip = currentVideo.Id; else { r.Video = currentVideo.Id; r.StartSeconds = videoPos; }
        title.Text = currentVideo.Name + "  ·  " + (currentVideo.IsClip ? "clip" : VideoData.KindName(currentVideo.Kind).ToLowerInvariant());
    }

    // ShowPlayingInfo: the video's line instead of the live stream's
    bool ShowVideoInfo()
    {
        if (currentVideo == null) return false;
        string t = VideoData.Line(currentVideo, DateTimeOffset.UtcNow);
        if (status.Text != t) { status.ForeColor = Dim; status.Text = t; }
        return true;
    }

    // OnTick: where the video is (every second)
    void TrackVideo()
    {
        if (currentVideo == null || currentVideo.IsClip || mpvGen != gen || ++videoTicks % 2 != 0) return;
        SendMpv("{\"command\":[\"get_property\",\"time-pos\"],\"request_id\":" + VideoPosId + "}");
    }

    // PipeLoop thread: the answer
    bool VideoLine(string line, int g)
    {
        if (line.IndexOf("\"request_id\":" + VideoPosId, StringComparison.Ordinal) < 0) return false;
        var v = Json.Parse(line);
        if (Json.S(v, "error") == "success")
        {
            var d = Json.V(v, "data");
            double pos = d is double dd ? dd : d is long dl ? dl : -1;
            if (pos >= 0 && pos < 1e7) Ui(g, () => { if (currentVideo != null) videoPos = (int)pos; });
        }
        return true;
    }

    // OnStreamExited: true = handled here (no automatic retry). A video ended when mpv said so (its end-file event with
    // reason "eof"); one Twitch will not give (subscribers only, deleted: Streamlink finds no stream) is not retried
    // every minute like an offline channel; any other exit is retried like a stream, from where it was.
    bool VideoExit(bool wasPlaying, StreamEnd end, string err)
    {
        if (currentVideo == null) return false;
        string what = currentVideo.IsClip ? "clip" : "video";
        if (wasPlaying && videoEofGen == gen)
        {
            Log.Info(what + " ended");
            StopStream();
            videoPos = 0; canRetry = true;
            status.ForeColor = Dim;
            status.Text = "The " + what + " ended  -  click here or press F5 to play it again";
            return true;
        }
        if (!wasPlaying && end == StreamEnd.Offline)
        {
            Log.Warn(what + " cannot be played");
            StopStream(); canRetry = true;
            ShowProblem("This " + what + " cannot be played", "Twitch did not give LiteView this " + what + ". It may be for subscribers only, deleted, or still being processed." + (err.Length > 0 ? "\n\n" + err : ""), Replay);
            return true;
        }
        return false;
    }

    int videoEofGen = -1, videoExitGen = -1, videoExitTicks;

    // OnStreamExited: a video's player ended but mpv's end-file event has not arrived on the UI thread yet: wait up to
    // two ticks (about a second) for it, so a video that played to its end is not taken for a failure
    bool WaitForVideoEnd()
    {
        if (currentVideo == null || !played || videoEofGen == gen) return false;
        if (videoExitGen != gen) { videoExitGen = gen; videoExitTicks = 0; }
        return ++videoExitTicks <= 2;
    }

    // PipeLoop thread: mpv's end-file event; "eof" means the video played to its end
    bool VideoEndLine(string line, int g)
    {
        if (line.IndexOf("\"event\":\"end-file\"", StringComparison.Ordinal) < 0) return false;
        if (line.IndexOf("\"reason\":\"eof\"", StringComparison.Ordinal) >= 0) Ui(g, () => videoEofGen = gen);
        return true;
    }

    static string Secs(int s) { return s.ToString(CultureInfo.InvariantCulture); }
}
