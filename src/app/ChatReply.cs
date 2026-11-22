// Replies and the message menu in the native chat. Right-click a message: Reply to <name>, Copy message, Copy name.
// Reply shows "Replying to @name: ..." above the chat box (Esc or ✕ cancels); the next message is sent with Twitch's
// reply tag, so it shows as a reply on Twitch, and our own copy of it gets the same "Replying to" line (carried in the
// same pending entry as the text, so a refused message takes its line with it). The message id comes from a received
// line and only goes into the tag when it is a real message id (IrcProtocol.ReplyTag). A reply ends when its message
// is deleted, its author ignored, or the channel changes; sub and raid notices cannot be replied to.
using System;
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
    string replyId, replyLine, replyChannel; Msg replyTo;
    const char LineMark = '\0';   // pending entry: text, then the reply line (sent text never has a NUL: CleanOutgoing)

    void InitReply()
    {
        replyBar.Dock = DockStyle.Bottom; replyBar.Height = 26; replyBar.BackColor = Theme.Side; replyBar.Padding = new Padding(10, 4, 4, 0); replyBar.Visible = false;
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
            bool canReply = input.Visible && channel != null && Tw.Token != null && !m.Deleted && m.Style != 2 && m.Style != 3 && IrcProtocol.IsMessageId(m.Id);
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
        var sb = new System.Text.StringBuilder();
        foreach (var p in m.P)
        {
            if (p.Kind != 0 && p.Kind != 1 && p.Kind != 4 && p.Kind != 5 && p.Kind != 8) continue;
            string t = p.Kind == 1 ? (p.E != null ? p.E.Name : "") : p.Text;
            if (string.IsNullOrEmpty(t)) continue;
            sb.Append(t);
            if (!p.Glue) sb.Append(' ');   // a word split around an emoji stays one word
        }
        return sb.ToString().Trim();
    }

    static void Copy(string text) { try { Clipboard.SetText(text); } catch (System.Runtime.InteropServices.ExternalException) { } }

    void StartReply(Msg m, string name, string body)
    {
        if (m.Deleted) return;   // deleted while the menu was open
        replyId = m.Id; replyChannel = channel; replyTo = m;
        replyLine = IrcProtocol.ForDisplay("Replying to @" + name + ": " + body);
        replyText.Text = replyLine;
        new ToolTip().SetToolTip(replyText, replyLine + "\n(Esc cancels)");
        replyBar.Visible = true;
        input.Focus();
    }

    void CancelReply() { replyId = replyLine = replyChannel = null; replyTo = null; replyBar.Visible = false; }

    // from Drain (UI thread, 10 times a second): the message was deleted or its author ignored since
    void CheckReply()
    {
        if (replyTo == null || !(replyTo.Deleted || ChatPrefs.Filter.IsIgnored(replyTo.Login))) return;
        CancelReply();
        View.Add(new System.Collections.Generic.List<Msg> { Sys("The message you were replying to was removed: the reply was cancelled.") });
    }

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
        if (replyId != null && (replyChannel != channel || replyTo == null || replyTo.Deleted || ChatPrefs.Filter.IsIgnored(replyTo.Login))) CancelReply();
        return IrcProtocol.ReplyTag(replyId);
    }

    // the message went out: its pending entry (the text, and the reply line when it was a reply); the reply ends
    string ReplySent(string text)
    {
        if (replyId == null || replyLine == null) return text;
        string entry = text + LineMark + replyLine.Replace(LineMark, ' ');
        CancelReply();
        return entry;
    }

    // a received reply starts with "@name " of the person it answers; Twitch's chat leaves that out (the "Replying to" line
    // above says it), unless it is the whole message
    static void DropReplyMention(Msg m, string display, string login)
    {
        int i = m.P.FindIndex(p => p.Kind != 6 && p.Kind != 2 && p.Kind != 3);
        if (i < 0 || i == m.P.Count - 1 || m.P[i].Kind != 5) return;
        string t = m.P[i].Text.Substring(1);
        if (t.Equals(display, StringComparison.OrdinalIgnoreCase) || (login != null && t.Equals(login, StringComparison.OrdinalIgnoreCase))) m.P.RemoveAt(i);
    }

    // IRC thread: our own message as Twitch confirmed it (USERSTATE), with its "Replying to" line when it was a reply
    Msg Echo(IrcMsg im, string entry)
    {
        int cut = entry.IndexOf(LineMark);
        var m = Build(im, cut < 0 ? entry : entry.Substring(0, cut), Tw.Login);
        if (m != null && cut >= 0) m.Reply = entry.Substring(cut + 1);
        return m;
    }
}

// the chat's own controls (GDI) in the chosen font (UiFont.ForControls; the window's font setting)
partial class ChatPanel
{
    /// <summary>UI fixture (CI): the keyboard in the chat box.</summary>
    public void FixtureFocusInput() { if (input.Visible) input.Focus(); else { input.Visible = true; input.Focus(); } }

    public void SetControlFonts()
    {
        input.Font = UiFont.ForControls(FontKind.Text, 10.5f);
        replyText.Font = UiFont.ForControls(FontKind.Text, 9f);
        loginLink.Font = UiFont.ForControls(FontKind.Text, 9.5f);
        head.Invalidate();
    }
}

// a chat that closes (an extra window's) stops hearing about images
partial class ChatPanel
{
    Action<Emote> imageLoaded;

    protected override void Dispose(bool disposing)
    {
        if (disposing && imageLoaded != null) { Images.Loaded -= imageLoaded; imageLoaded = null; }
        base.Dispose(disposing);
    }
}
