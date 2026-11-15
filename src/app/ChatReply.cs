// Replies and the message menu in the native chat. Right-click a message: Reply to <name>, Copy message, Copy name.
// Reply shows "Replying to @name: ..." above the chat box (Esc or ✕ cancels); the next message is sent with Twitch's
// reply tag, so it shows as a reply on Twitch, and our own copy of it gets the same "Replying to" line. The message id
// comes from a received line and only goes into the tag when it is a real message id (IrcProtocol.ReplyTag).
using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LiteViewCore;

partial class ChatPanel
{
    readonly Panel replyBar = new Panel();
    readonly Label replyText = new Label();
    readonly Button replyClose = new Button();
    readonly ContextMenuStrip msgMenu = new ContextMenuStrip();
    string replyId, replyLine, replyChannel;
    // what we sent as a reply -> its "Replying to" line, for our own copy when Twitch confirms it (IRC thread reads it)
    readonly ConcurrentDictionary<string, string> sentReplies = new ConcurrentDictionary<string, string>();

    void InitReply()
    {
        replyBar.Dock = DockStyle.Bottom; replyBar.Height = 26; replyBar.BackColor = Color.FromArgb(31, 31, 35); replyBar.Padding = new Padding(10, 4, 4, 0); replyBar.Visible = false;
        replyText.Dock = DockStyle.Fill; replyText.ForeColor = ChatView.Dim; replyText.AutoEllipsis = true; replyText.Font = new Font("Segoe UI", 9f); replyText.TextAlign = ContentAlignment.MiddleLeft;
        replyClose.Dock = DockStyle.Right; replyClose.Width = 26; replyClose.FlatStyle = FlatStyle.Flat; replyClose.FlatAppearance.BorderSize = 0; replyClose.ForeColor = ChatView.Fg;
        replyClose.Text = ((char)0xE711).ToString(); replyClose.Font = new Font("Segoe MDL2 Assets", 8f); replyClose.AccessibleName = "Cancel the reply"; replyClose.TabStop = false;
        replyClose.Click += (s, e) => { CancelReply(); input.Focus(); };
        replyBar.Controls.Add(replyText); replyBar.Controls.Add(replyClose);
        Controls.Add(replyBar);
        Controls.SetChildIndex(replyBar, Controls.GetChildIndex(foot));   // docked just above the chat box (and below the emote picker)
        input.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape && replyId != null) { e.SuppressKeyPress = true; e.Handled = true; CancelReply(); } };

        msgMenu.Renderer = new ToolStripProfessionalRenderer(new DarkColors()); msgMenu.ForeColor = ChatView.Fg; msgMenu.ShowImageMargin = false;
        View.MessageMenu += ShowMessageMenu;
    }

    void ShowMessageMenu(Msg m, Point at)
    {
        msgMenu.Items.Clear();
        string name = string.IsNullOrEmpty(m.Display) ? m.Login : m.Display;
        string body = Body(m);
        if (m.Login != null)
        {
            bool canReply = input.Visible && channel != null && Tw.Token != null && !m.Deleted && IrcProtocol.IsMessageId(m.Id);
            var reply = new ToolStripMenuItem("Reply to " + name) { Enabled = canReply };
            reply.Click += (s, e) => StartReply(m, name, body);
            msgMenu.Items.Add(reply);
        }
        if (body.Length > 0) msgMenu.Items.Add("Copy message", null, (s, e) => Copy(body));
        if (m.Login != null) msgMenu.Items.Add("Copy name", null, (s, e) => Copy(m.Login));
        if (msgMenu.Items.Count > 0) msgMenu.Show(View, at);
    }

    // the words of a message as typed: names, badges and the timestamp left out, emotes by name
    static string Body(Msg m)
    {
        return string.Join(" ", m.P.Where(p => p.Kind == 0 || p.Kind == 1 || p.Kind == 4 || p.Kind == 5).Select(p => p.Kind == 1 ? (p.E != null ? p.E.Name : "") : p.Text)
            .Where(t => !string.IsNullOrEmpty(t))).Trim();
    }

    static void Copy(string text) { try { Clipboard.SetText(text); } catch (System.Runtime.InteropServices.ExternalException) { } }

    void StartReply(Msg m, string name, string body)
    {
        replyId = m.Id; replyChannel = channel;
        replyLine = IrcProtocol.ForDisplay("Replying to @" + name + ": " + body);
        replyText.Text = replyLine;
        new ToolTip().SetToolTip(replyText, replyLine + "\n(Esc cancels)");
        replyBar.Visible = true;
        input.Focus();
    }

    void CancelReply() { replyId = replyLine = replyChannel = null; replyBar.Visible = false; }

    /// <summary>For the UI fixture: a reply to the newest message that can have one.</summary>
    public void ReplyToLast()
    {
        var m = View.Msgs.LastOrDefault(x => x.Login != null && IrcProtocol.IsMessageId(x.Id));
        if (m == null) { Log.Warn("reply: no message to reply to"); return; }
        string name = string.IsNullOrEmpty(m.Display) ? m.Login : m.Display;
        StartReply(m, name, Body(m));
        Log.Info("chat: replying");
    }

    // the start of the PRIVMSG line: the reply tag while replying in this channel, else nothing
    string ReplyPrefix()
    {
        if (replyId != null && replyChannel != channel) CancelReply();   // another channel since: not a reply any more
        return IrcProtocol.ReplyTag(replyId);
    }

    // the message went out: remember its reply line for our own copy, and end the reply
    void ReplySent(string text)
    {
        if (replyId == null) return;
        if (sentReplies.Count > 20) sentReplies.Clear();
        sentReplies[text] = replyLine;
        CancelReply();
    }

    // IRC thread: our own message as Twitch confirmed it gets its "Replying to" line
    Msg WithReply(Msg m, string text)
    {
        string line;
        if (m != null && sentReplies.TryRemove(text, out line)) m.Reply = line;
        return m;
    }
}
