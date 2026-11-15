// Chat history when joining (P4): the channel's last messages, like on the Twitch website, from
// recent-messages.robotty.de (src/core/RecentMessages.cs). On by default; chat menu > "Recent messages when joining".
// The lines take the same path as live chat (ChatLine: ignored users, highlights, emotes, display limits). They are
// shown once the channel's emotes and badges have loaded (so emotes are pictures, not words), or after 4 s anyway,
// above "Welcome to the chat room!" and anything that arrived live meanwhile. A message is never shown twice (whether
// its live copy comes before or after the history), and what moderators delete or time out while the history waits
// is shown deleted, as in live chat; after "chat cleared" the history is dropped. A failed or empty answer shows nothing.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LiteViewCore;

partial class ChatPanel
{
    int historyGen;                 // a newer join (or Part) makes older answers worthless
    List<string> historyLines;      // fetched, not shown yet
    bool historyEmotes;             // the channel's emotes and badges are loaded
    string historyFor;
    readonly System.Windows.Forms.Timer historyWait = new System.Windows.Forms.Timer { Interval = 4000 };
    bool historyInit, historyPending, historyCleared;
    // UI thread only: ids the history showed (their live copies are dropped in Drain), and moderation seen while it waited
    readonly HashSet<string> historyIds = new HashSet<string>(), historyDeleted = new HashSet<string>(), historyTimedOut = new HashSet<string>();

    void StartHistory(string ch)
    {
        if (!historyInit) { historyInit = true; historyWait.Tick += (s, e) => { historyWait.Stop(); ShowHistory(true); }; }
        int g = ++historyGen; historyLines = null; historyEmotes = false; historyFor = ch;
        historyWait.Stop(); ResetHistoryState();
        if (!ChatPrefs.History) return;
        string url = RecentMessages.Url(ch);
        if (url == null) return;
        historyPending = true;
        historyWait.Start();
        Task.Run(async () =>
        {
            string json = null;
            try { json = await Tw.Get(url).ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn("chat history: " + ex.GetType().Name); }
            var lines = RecentMessages.Lines(json, ch);
            Post(() =>
            {
                if (g != historyGen) return;
                if (lines.Count == 0) { historyWait.Stop(); historyPending = false; return; }
                historyLines = lines; ShowHistory(false);
            });
        });
    }

    void StopHistory() { historyGen++; historyLines = null; historyWait.Stop(); ResetHistoryState(); }

    void ResetHistoryState()
    {
        historyPending = historyCleared = false;
        historyIds.Clear(); historyDeleted.Clear(); historyTimedOut.Clear();
    }

    // CLEARMSG (id) or CLEARCHAT (who; null = the whole chat), on the UI thread: remembered while the history waits
    void HistoryModerated(string id, string who)
    {
        if (!historyPending) return;
        if (id != null) historyDeleted.Add(id);
        else if (who != null) historyTimedOut.Add(who);
        else historyCleared = true;
    }

    // Drain (UI thread): the live copy of a message the history already showed
    void DropHistoryEchoes(List<Msg> list)
    {
        if (historyIds.Count > 0) list.RemoveAll(m => m.Id != null && historyIds.Contains(m.Id));
    }

    void HistoryReady(string ch)
    {
        if (ch != historyFor || ch != channel) return;
        historyEmotes = true; ShowHistory(false);
    }

    void ShowHistory(bool waited)
    {
        if (historyLines == null || historyFor != channel) return;
        if (!historyEmotes && !waited && historyWait.Enabled) return;   // wait a little for the emotes
        historyWait.Stop();
        var lines = historyLines; historyLines = null;   // once per join
        Drain();   // what arrived live goes into the view first, so a message in both is found
        historyPending = false;
        if (historyCleared) { Log.Info("chat: recent messages dropped (chat was cleared)"); return; }
        var have = new HashSet<string>(View.Msgs.Where(m => m.Id != null).Select(m => m.Id));
        var list = new List<Msg>();
        foreach (var l in lines)
        {
            var im = IrcMsg.Parse(l);
            if (im == null) continue;
            string id = im.Tag("id");
            if (id != null && !have.Add(id)) continue;
            Msg m;
            try { m = ChatLine(im); } catch (Exception ex) { Log.Warn("chat history line: " + ex.GetType().Name); continue; }
            if (m == null) continue;
            if ((m.Id != null && historyDeleted.Contains(m.Id)) || (m.Login != null && historyTimedOut.Contains(m.Login))) m.Deleted = true;
            if (m.Id != null) historyIds.Add(m.Id);
            list.Add(m);
        }
        historyDeleted.Clear(); historyTimedOut.Clear();
        View.Prepend(list);
        Log.Info("chat: " + list.Count + " recent messages");
    }
}
