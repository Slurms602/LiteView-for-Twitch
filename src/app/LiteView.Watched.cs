// Where you stopped in each video (src/core/WatchedVideos.cs): saved in liteview.videos while a video plays (every 30 s,
// and when it stops, ends or LiteView closes); the Following page's video cards show it as a red bar; a video plays on
// from there (unless it was barely started or nearly finished; its menu has "Play from the start").
using System;
using System.IO;
using LiteViewCore;

partial class LiteView
{
    WatchedVideos watched = new WatchedVideos();
    string watchedPath;
    bool watchedDirty, playFromStart;
    DateTime watchedSaved = DateTime.UtcNow;

    void InitWatched()
    {
        watchedPath = Path.Combine(root, "liteview.videos");
        try { if (File.Exists(watchedPath)) watched = WatchedVideos.Load(File.ReadAllText(watchedPath)); }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Log.Warn("watched videos: " + ex.GetType().Name); }
        browse.WatchedFraction = (id, len) => watched.Fraction(id, len);
        browse.VideoFromStart += v => { playFromStart = true; PlayVideo(v); };
        browse.VideoForget += v => { watched.Forget(v.Id); watchedDirty = true; SaveWatched(); };
        FormClosing += (s, e) => SaveWatched();
    }

    // TakeVideo: where a newly picked video starts
    int StartAt(VideoInfo v)
    {
        bool fromStart = playFromStart; playFromStart = false;
        if (v == null || v.IsClip || fromStart) return 0;
        int at = watched.ResumeAt(v.Id, v.Seconds);
        if (at > 0) Log.Info("video: continues at " + VideoData.Length(at));
        return at;
    }

    // the video's position (VideoAt) or its end
    void RememberVideo(double pos, bool ended)
    {
        if (currentVideo == null || currentVideo.IsClip) return;
        int len = videoBar.Duration > 0 ? (int)videoBar.Duration : currentVideo.Seconds;
        watched.Set(currentVideo.Id, ended && len > 0 ? len : (int)pos, len);
        watchedDirty = true;
        if (ended || DateTime.UtcNow - watchedSaved > TimeSpan.FromSeconds(30)) SaveWatched();
    }

    void SaveWatched()
    {
        if (!watchedDirty || watchedPath == null) return;
        watchedDirty = false; watchedSaved = DateTime.UtcNow;
        try { AtomicFile.WriteText(watchedPath, watched.Save(), false); }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Log.Warn("watched videos: " + ex.GetType().Name); }
    }
}
