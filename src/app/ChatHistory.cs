// Chat history when joining (P4): the channel's last messages, like on the Twitch website, from
// recent-messages.robotty.de (src/core/RecentMessages.cs). On by default; chat menu > "Recent messages when joining".
// The lines take the same path as live chat (ChatLine: ignored users, highlights, emotes, display limits). They are
// shown once the channel's emotes and badges have loaded (so emotes are pictures, not words), or after 4 s anyway,
// above "Welcome to the chat room!" and anything that arrived live meanwhile; a message that already came in live is
// not shown twice. A failed or empty answer simply shows nothing.
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
    bool historyInit;

    void StartHistory(string ch)
    {
        if (!historyInit) { historyInit = true; historyWait.Tick += (s, e) => { historyWait.Stop(); ShowHistory(true); }; }
        int g = ++historyGen; historyLines = null; historyEmotes = false; historyFor = ch;
        historyWait.Stop();
        if (!ChatPrefs.History) return;
        string url = RecentMessages.Url(ch);
        if (url == null) return;
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
                if (lines.Count == 0) { historyWait.Stop(); return; }
                historyLines = lines; ShowHistory(false);
            });
        });
    }

    void StopHistory() { historyGen++; historyLines = null; historyWait.Stop(); }

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
            if (m != null) list.Add(m);
        }
        View.Prepend(list);
        Log.Info("chat: " + list.Count + " recent messages");
    }
}
