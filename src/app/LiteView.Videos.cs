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

    // OnStreamExited: a video that played to (near) its end ended; true = handled (no retry)
    bool VideoEnded(bool wasPlaying)
    {
        if (currentVideo == null || !wasPlaying) return false;
        bool atEnd = currentVideo.IsClip || currentVideo.Seconds <= 0 || videoPos >= currentVideo.Seconds - 30;
        if (!atEnd) return false;   // stopped in the middle: retried like a stream, from where it was
        Log.Info("video ended");
        StopStream();
        videoPos = 0; canRetry = true;
        status.ForeColor = Dim;
        status.Text = (currentVideo.IsClip ? "The clip ended" : "The video ended") + "  -  click here or press F5 to play it again";
        return true;
    }

    static string Secs(int s) { return s.ToString(CultureInfo.InvariantCulture); }
}
