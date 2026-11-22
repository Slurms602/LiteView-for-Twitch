// Chat preferences (P4): text size, timestamps, highlight words, ignored users, recent messages when joining
// (src/app/ChatHistory.cs), the chat's width (drag its inner edge; UI-3) and side: right of the video (default) or
// left of it, after the followed channels column. The native chat (src/chat.cs) reads ChatPrefs while it builds and
// draws messages.
using System;
using System.Drawing;
using System.Windows.Forms;
using LiteViewCore;

static class ChatPrefs
{
    public static readonly ChatFilter Filter = new ChatFilter();
    public static bool Timestamps, History = true;
    // Normal was 9.75 pt (13 px) at first: one step bigger on the owner's 1440p screen (a saved choice keeps its name)
    public static readonly float[] Sizes = { 9.75f, 11f, 12f, 13.5f };
    public static readonly string[] SizeNames = { "Small", "Normal", "Large", "Larger" };
}

partial class LiteView
{
    const int MinChatW = 260, MaxChatW = 720;
    int chatWidth = 340, chatSize = 1;
    int ChatW { get { return chatWidth; } }   // upstream: const 340
    readonly Panel chatGrip = new Panel();
    bool gripDrag, chatLeft; int gripX, gripW;

    void InitChatPrefs()
    {
        // a 5 px handle on the chat's edge next to the video: drag to change the chat's width
        chatGrip.Dock = chatLeft ? DockStyle.Right : DockStyle.Left; chatGrip.Width = 5; chatGrip.Cursor = Cursors.SizeWE; chatGrip.BackColor = Theme.Side;
        chatBox.Controls.Add(chatGrip);
        new ToolTip().SetToolTip(chatGrip, "Drag to change the chat's width");
        chatGrip.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { gripDrag = true; gripX = Cursor.Position.X; gripW = chatWidth; } };
        chatGrip.MouseMove += (s, e) =>
        {
            if (!gripDrag) return;
            int dx = Cursor.Position.X - gripX;   // the chat grows away from the video
            int w = Math.Max(MinChatW, Math.Min(MaxChatW, gripW + (chatLeft ? dx : -dx)));
            if (w == chatWidth) return;
            chatWidth = w; Relayout();
        };
        chatGrip.MouseUp += (s, e) => { if (!gripDrag) return; gripDrag = false; FitWindow(); SaveSettings(); };
        nchat.View.SetTextSize(ChatPrefs.Sizes[chatSize]);
        AddChatMenuItems(nchat.Menu);
    }

    // the chat's own settings at the top of its gear menu, made fresh each time it opens (so they show the settings as
    // they are), and the same items on the Settings window's Chat page
    readonly System.Collections.Generic.List<ToolStripItem> chatMenuItems = new System.Collections.Generic.List<ToolStripItem>();

    void AddChatMenuItems(ContextMenuStrip menu)
    {
        menu.ShowItemToolTips = true;
        menu.Opening += (s, e) =>
        {
            foreach (var it in chatMenuItems) { menu.Items.Remove(it); it.Dispose(); }   // the last opening's items (and their submenus)
            chatMenuItems.Clear();
            chatMenuItems.AddRange(ChatMenuItems());
            chatMenuItems.Add(new ToolStripSeparator());
            for (int i = chatMenuItems.Count - 1; i >= 0; i--) menu.Items.Insert(0, chatMenuItems[i]);
        };
    }

    System.Collections.Generic.List<ToolStripItem> ChatMenuItems()
    {
        var size = new ToolStripMenuItem("Text size");
        for (int i = 0; i < ChatPrefs.Sizes.Length; i++)
        {
            int k = i;
            var it = new ToolStripMenuItem(ChatPrefs.SizeNames[i]) { Checked = chatSize == i };
            it.Click += (s, e) => { chatSize = k; nchat.View.SetTextSize(ChatPrefs.Sizes[k]); SaveSettings(); };
            size.DropDownItems.Add(it);
        }
        DarkMenuColors.Apply(size.DropDown);
        var stamps = Toggle("Timestamps", ChatPrefs.Timestamps, v => ChatPrefs.Timestamps = v);
        var history = Toggle("Recent messages when joining", ChatPrefs.History, v => ChatPrefs.History = v);
        history.ToolTipText = "The last messages of the channel, like on Twitch, from recent-messages.robotty.de\n(the service Chatterino uses; it learns which channels you open)";
        var replayItem = Toggle("Chat replay for videos", chatReplay, v => { chatReplay = v; if (watching && currentVideo != null && chatOn.Checked && !webChat) { StopChat(); StartChat(); } });
        replayItem.ToolTipText = "A past broadcast shows its chat as it was, from Twitch (asked anonymously).\nOff: the channel's live chat, as for clips.";
        var words = new ToolStripMenuItem("Highlight words...");
        words.Click += (s, e) =>
        {
            string t = TextListDialog.Ask(this, "Highlight words", "Messages with these words are highlighted like a mention of your name. One word or phrase per line.", string.Join("\r\n", ChatPrefs.Filter.Words));
            if (t != null) { ChatPrefs.Filter.SetWords(t); SaveSettings(); }
        };
        var ignore = new ToolStripMenuItem("Ignored users...");
        ignore.Click += (s, e) =>
        {
            string t = TextListDialog.Ask(this, "Ignored users", "Messages from these users are not shown. One Twitch user name per line.", string.Join("\r\n", ChatPrefs.Filter.Ignored));
            if (t != null) { ChatPrefs.Filter.SetIgnored(t); SaveSettings(); }
        };
        var left = Toggle("Chat on the left", chatLeft, v => { chatLeft = v; chatGrip.Dock = chatLeft ? DockStyle.Right : DockStyle.Left; Relayout(); });
        var width = new ToolStripMenuItem("Reset chat width") { Enabled = chatWidth != 340 };
        width.Click += (s, e) => { chatWidth = 340; Relayout(); FitWindow(); SaveSettings(); };
        return new System.Collections.Generic.List<ToolStripItem> { size, stamps, history, replayItem, words, ignore, left, width };
    }

    void LoadChatSettings(System.Collections.Generic.Dictionary<string, string> ini)
    {
        int v;
        if (int.TryParse(Native.Get(ini, "chatwidth", "340"), out v)) chatWidth = Math.Max(MinChatW, Math.Min(MaxChatW, v));
        if (int.TryParse(Native.Get(ini, "chatsize", "1"), out v) && v >= 0 && v < ChatPrefs.Sizes.Length) chatSize = v;
        UiFont.UseInter = Native.Get(ini, "font", "inter") != "segoe";
        UiFont.SetSmoothing(Native.Get(ini, "smoothing", "cleartype"));
        ApplyFont();
        chatLeft = Native.Get(ini, "chatside", "right") == "left";
        chatGrip.Dock = chatLeft ? DockStyle.Right : DockStyle.Left;
        ChatPrefs.Timestamps = Native.Get(ini, "timestamps", "0") == "1";
        ChatPrefs.History = Native.Get(ini, "chathistory", "1") != "0";
        chatReplay = Native.Get(ini, "chatreplay", "1") != "0";
        ChatPrefs.Filter.SetWords(Native.Get(ini, "highlights", ""));
        ChatPrefs.Filter.SetIgnored(Native.Get(ini, "ignored", ""));
    }

    // the font setting (UiFont) changed or was loaded: the chat, the Following page and the followed column draw with it
    void ApplyFont()
    {
        nchat.View.SetTextSize(ChatPrefs.Sizes[chatSize]);
        nchat.View.ReLayout();   // also when only the smoothing changed (SetTextSize keeps the layout when the font is the same)
        if (browse != null) browse.ReFont();
        side.Invalidate();
        // the window's controls (GDI): the whole window's font (buttons, boxes, the status line), the title, notes
        Font = UiFont.ForControls(FontKind.Text, 9.5f);
        title.Font = UiFont.ForControls(FontKind.Semi, 10.5f);
        pausedNote.Font = UiFont.ForControls(FontKind.Text, 12f);
        nchat.SetControlFonts();
        foreach (var w in extras) if (!w.IsDisposed) w.SetFonts();
        if (watching) { status.Location = new Point(title.Right + 14, 12); LayoutBar(); }
    }

    System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, string>> ChatSettings()
    {
        yield return Kv("chatwidth", chatWidth.ToString(System.Globalization.CultureInfo.InvariantCulture));
        yield return Kv("chatsize", chatSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        yield return Kv("chatside", chatLeft ? "left" : "right");
        yield return Kv("font", UiFont.UseInter ? "inter" : "segoe");
        yield return Kv("timestamps", ChatPrefs.Timestamps ? "1" : "0");
        yield return Kv("chathistory", ChatPrefs.History ? "1" : "0");
        yield return Kv("chatreplay", chatReplay ? "1" : "0");
        yield return Kv("highlights", ChatPrefs.Filter.SaveWords());
        yield return Kv("ignored", ChatPrefs.Filter.SaveIgnored());
    }
}

// A small dark dialog for a list of words or names (one per line).
sealed class TextListDialog : Form
{
    readonly TextBox box;

    TextListDialog(string title, string help, string text)
    {
        Text = title; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent; Size = new Size(420, 360); BackColor = Theme.Side; ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9.5f); Padding = new Padding(12);
        HandleCreated += (s, e) => Theme.Frame(Handle);
        var label = new Label { Text = help, Dock = DockStyle.Top, Height = 44 };
        box = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Text = text, AcceptsReturn = true,
            BackColor = Theme.Panel, ForeColor = ForeColor, BorderStyle = BorderStyle.FixedSingle };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 90, Height = 30, FlatStyle = FlatStyle.Flat, BackColor = Theme.Accent, ForeColor = Color.White };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90, Height = 30, FlatStyle = FlatStyle.Flat, BackColor = Theme.Line, ForeColor = Theme.Text };
        ok.FlatAppearance.BorderSize = cancel.FlatAppearance.BorderSize = 0;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
        buttons.Controls.Add(cancel); buttons.Controls.Add(ok);
        Controls.Add(box); Controls.Add(buttons); Controls.Add(label);
        AcceptButton = null; CancelButton = cancel;   // Enter makes new lines in the box
    }

    /// <summary>The edited text, or null when cancelled.</summary>
    public static string Ask(IWin32Window owner, string title, string help, string text)
    {
        using (var d = new TextListDialog(title, help, text)) return d.ShowDialog(owner) == DialogResult.OK ? d.box.Text : null;
    }
}
