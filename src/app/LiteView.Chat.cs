// Chat preferences (P4): text size, timestamps, highlight words, ignored users, and the chat's width (drag its left
// edge; UI-3). The native chat (src/chat.cs) reads ChatPrefs while it builds and draws messages.
using System;
using System.Drawing;
using System.Windows.Forms;
using LiteViewCore;

static class ChatPrefs
{
    public static readonly ChatFilter Filter = new ChatFilter();
    public static bool Timestamps;
    public static readonly float[] Sizes = { 9f, 9.75f, 11f, 12.5f };
    public static readonly string[] SizeNames = { "Small", "Normal", "Large", "Larger" };
}

partial class LiteView
{
    const int MinChatW = 260, MaxChatW = 720;
    int chatWidth = 340, chatSize = 1;
    int ChatW { get { return chatWidth; } }   // upstream: const 340
    readonly Panel chatGrip = new Panel();
    bool gripDrag; int gripX, gripW;

    void InitChatPrefs()
    {
        // a 5 px handle on the chat's left edge: drag to change the chat's width
        chatGrip.Dock = DockStyle.Left; chatGrip.Width = 5; chatGrip.Cursor = Cursors.SizeWE; chatGrip.BackColor = Color.FromArgb(31, 31, 35);
        chatBox.Controls.Add(chatGrip);
        new ToolTip().SetToolTip(chatGrip, "Drag to change the chat's width");
        chatGrip.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { gripDrag = true; gripX = Cursor.Position.X; gripW = chatWidth; } };
        chatGrip.MouseMove += (s, e) =>
        {
            if (!gripDrag) return;
            int w = Math.Max(MinChatW, Math.Min(MaxChatW, gripW + gripX - Cursor.Position.X));
            if (w == chatWidth) return;
            chatWidth = w; Relayout();
        };
        chatGrip.MouseUp += (s, e) => { if (!gripDrag) return; gripDrag = false; FitWindow(); SaveSettings(); };
        nchat.View.SetTextSize(ChatPrefs.Sizes[chatSize]);
        AddChatMenuItems(nchat.Menu);
    }

    void AddChatMenuItems(ContextMenuStrip menu)
    {
        var size = new ToolStripMenuItem("Text size");
        for (int i = 0; i < ChatPrefs.Sizes.Length; i++)
        {
            int k = i;
            var it = new ToolStripMenuItem(ChatPrefs.SizeNames[i]);
            it.Click += (s, e) => { chatSize = k; nchat.View.SetTextSize(ChatPrefs.Sizes[k]); SaveSettings(); };
            size.DropDownItems.Add(it);
        }
        size.DropDownOpening += (s, e) => { for (int i = 0; i < size.DropDownItems.Count; i++) ((ToolStripMenuItem)size.DropDownItems[i]).Checked = i == chatSize; };
        DarkMenuColors.Apply(size.DropDown);
        var stamps = new ToolStripMenuItem("Timestamps");
        stamps.Click += (s, e) => { ChatPrefs.Timestamps = !ChatPrefs.Timestamps; SaveSettings(); };
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
        var width = new ToolStripMenuItem("Reset chat width");
        width.Click += (s, e) => { chatWidth = 340; Relayout(); FitWindow(); SaveSettings(); };
        menu.Opening += (s, e) => { stamps.Checked = ChatPrefs.Timestamps; width.Enabled = chatWidth != 340; };
        menu.Items.Insert(0, new ToolStripSeparator());
        foreach (var it in new ToolStripItem[] { width, ignore, words, stamps, size }) menu.Items.Insert(0, it);
    }

    void LoadChatSettings(System.Collections.Generic.Dictionary<string, string> ini)
    {
        int v;
        if (int.TryParse(Native.Get(ini, "chatwidth", "340"), out v)) chatWidth = Math.Max(MinChatW, Math.Min(MaxChatW, v));
        if (int.TryParse(Native.Get(ini, "chatsize", "1"), out v) && v >= 0 && v < ChatPrefs.Sizes.Length) chatSize = v;
        ChatPrefs.Timestamps = Native.Get(ini, "timestamps", "0") == "1";
        ChatPrefs.Filter.SetWords(Native.Get(ini, "highlights", ""));
        ChatPrefs.Filter.SetIgnored(Native.Get(ini, "ignored", ""));
    }

    System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, string>> ChatSettings()
    {
        yield return Kv("chatwidth", chatWidth.ToString(System.Globalization.CultureInfo.InvariantCulture));
        yield return Kv("chatsize", chatSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        yield return Kv("timestamps", ChatPrefs.Timestamps ? "1" : "0");
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
        StartPosition = FormStartPosition.CenterParent; Size = new Size(420, 360); BackColor = Color.FromArgb(31, 31, 35); ForeColor = Color.FromArgb(239, 239, 241);
        Font = new Font("Segoe UI", 9.5f); Padding = new Padding(12);
        var label = new Label { Text = help, Dock = DockStyle.Top, Height = 44 };
        box = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Text = text, AcceptsReturn = true,
            BackColor = Color.FromArgb(24, 24, 27), ForeColor = ForeColor, BorderStyle = BorderStyle.FixedSingle };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 90, Height = 30, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(145, 71, 255), ForeColor = Color.White };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90, Height = 30, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(53, 53, 59), ForeColor = Color.White };
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
