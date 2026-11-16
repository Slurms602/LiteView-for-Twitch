// The chat's side of a past broadcast's chat replay (src/core/ChatReplay.cs, src/app/LiteView.Replay.cs): no chat
// connection and no recent messages; the channel's emotes and badges load by its user id as for a live chat, and the
// replayed comments take the same path as live lines (ChatLine: ignored users, highlights, emotes, display limits).
// The header says CHAT REPLAY and the chat box is hidden (Twitch's past broadcasts cannot be chatted in). Joining a
// live chat (or leaving) ends the replay.
using System;
using System.Collections.Generic;
using Named = System.Collections.Generic.KeyValuePair<string, Emote>;
using LiteViewCore;

partial class ChatPanel
{
    bool replay;

    public bool InReplay { get { return replay; } }

    public void ReplayJoin(string ch, string userId)
    {
        irc.Stop(); StopHistory(); CancelReply();
        channel = ch; roomId = ChannelName.IsNumericId(userId) ? userId : null;
        badges = new Dictionary<string, Emote>(); third = globals ?? new Dictionary<string, Emote>();
        channelSets = new List<KeyValuePair<string, List<Named>>>(); RefreshPicker();
        View.Clear(); Msg dummy; while (inbox.TryDequeue(out dummy)) { }
        string dummyS; while (pending.TryDequeue(out dummyS)) { }
        chatters.Clear(); me = null; joinedAs = Tw.Login;
        replay = true; foot.Visible = false; picker.Visible = false; head.Invalidate();
        inbox.Enqueue(Sys("Chat replay: the chat as it was during the broadcast"));
        drain.Start();
        bool hadGlobals = globals != null;
        LoadGlobals();                                  // loads the channel's emotes after the global ones, the first time
        if (hadGlobals && roomId != null) LoadChannel(roomId);
    }

    /// <summary>Comments the video has reached (UI thread).</summary>
    public void ReplayAdd(IEnumerable<IrcMsg> msgs)
    {
        if (!replay) return;
        foreach (var im in msgs)
        {
            Msg m;
            try { m = ChatLine(im); } catch (Exception ex) { Log.Warn("chat replay line: " + ex.GetType().Name); continue; }
            if (m != null) inbox.Enqueue(m);
        }
    }

    public void ReplayNote(string text) { if (replay) inbox.Enqueue(Sys(text)); }

    /// <summary>A seek: what was shown goes, the note says from where.</summary>
    public void ReplayRestart(string note)
    {
        if (!replay) return;
        Msg dummy; while (inbox.TryDequeue(out dummy)) { }
        View.Clear();
        inbox.Enqueue(Sys(note));
    }

    void EndReplay()
    {
        if (!replay) return;
        replay = false; foot.Visible = true; head.Invalidate();
        UpdateLogin();
    }
}
