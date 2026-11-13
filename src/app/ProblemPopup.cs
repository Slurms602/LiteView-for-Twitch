// Details of a problem (UI-5): opened by clicking "⚠ ..." in the status line. The whole message (the status line
// shows one shortened line), and what can be done: try again, open the log folder, copy the details.
using System;
using System.Drawing;
using System.Windows.Forms;

sealed class ProblemPopup : Form
{
    static readonly Color Back = Color.FromArgb(31, 31, 35), Box = Color.FromArgb(24, 24, 27), Line = Color.FromArgb(53, 53, 59),
        Fg = Color.FromArgb(239, 239, 241), Accent = Color.FromArgb(145, 71, 255), Err = Color.FromArgb(255, 140, 140);

    public ProblemPopup(string title, string detail, Action retry, Action openLog)
    {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
        BackColor = Back; ForeColor = Fg; Font = new Font("Segoe UI", 9.5f); KeyPreview = true;
        Size = new Size(500, 250); Padding = new Padding(14, 12, 14, 12);

        var head = new Label { Text = title, Dock = DockStyle.Top, Height = 40, ForeColor = Err, Font = new Font("Segoe UI Semibold", 10.5f), AutoEllipsis = true };
        var text = new TextBox
        {
            Text = (detail ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n"), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.None, BackColor = Box, ForeColor = Fg, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f), TabStop = false,
        };
        var textBox = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8), BackColor = Box };
        textBox.Controls.Add(text);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
        buttons.Controls.Add(Btn("Close", false, (s, e) => Close()));
        buttons.Controls.Add(Btn("Copy details", false, (s, e) => { try { Clipboard.SetText(title + "\r\n\r\n" + text.Text); } catch (System.Runtime.InteropServices.ExternalException) { } }));
        if (openLog != null) buttons.Controls.Add(Btn("Open log folder", false, (s, e) => { openLog(); Close(); }));
        if (retry != null) buttons.Controls.Add(Btn("Try again", true, (s, e) => { Close(); retry(); }));
        Controls.Add(textBox); Controls.Add(buttons); Controls.Add(head);

        Deactivate += (s, e) => Close();
        KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        Paint += (s, e) => { using (var p = new Pen(Line)) e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1); };
    }

    static Button Btn(string text, bool primary, EventHandler click)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, Height = 30, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Margin = new Padding(8, 0, 0, 0),
            BackColor = primary ? Accent : Line, ForeColor = Color.White, Padding = new Padding(8, 0, 8, 0),
        };
        b.FlatAppearance.BorderSize = 0;
        b.Click += click;
        return b;
    }

    /// <summary>Shows it below <paramref name="anchor"/>, inside the screen's working area.</summary>
    public void ShowBelow(Control anchor, IWin32Window owner)
    {
        var p = anchor.PointToScreen(new Point(0, anchor.Height + 6));
        var wa = Screen.FromControl(anchor).WorkingArea;
        Location = new Point(Math.Max(wa.Left, Math.Min(p.X, wa.Right - Width)), Math.Max(wa.Top, Math.Min(p.Y, wa.Bottom - Height)));
        Show(owner);
    }
}
