// "Log in with Twitch": Twitch's activation page opens in the system browser (where you are probably
// already logged in), and this small window shows the code to check there and what LiteView may do.
// No password is ever typed into LiteView.
using System;
using System.Drawing;
using System.Windows.Forms;
using LiteViewCore;

sealed class LoginDialog : Form
{
    static readonly Color Bg = Color.FromArgb(24, 24, 27), Fg = Color.FromArgb(239, 239, 241), Dim = Color.FromArgb(173, 173, 184),
        Hover = Color.FromArgb(38, 38, 44), Accent = Color.FromArgb(145, 71, 255);

    public LoginDialog(string userCode, Uri page)
    {
        Text = "Log in with Twitch"; BackColor = Bg; ForeColor = Fg; Font = new Font("Segoe UI", 9.5f);
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual; ClientSize = new Size(460, 300);

        var intro = new Label
        {
            Text = "Twitch's activation page opened in your browser. Check that it shows this code, then click Activate and Authorize:",
            ForeColor = Fg, Location = new Point(20, 18), Size = new Size(420, 40),
        };
        var code = new Label
        {
            Text = string.IsNullOrEmpty(userCode) ? "(see the browser)" : userCode, ForeColor = Fg, Font = new Font("Consolas", 22f, FontStyle.Bold),
            Location = new Point(20, 62), Size = new Size(420, 46), TextAlign = ContentAlignment.MiddleCenter, BackColor = Hover,
        };
        var scopes = new Label
        {
            Text = "LiteView asks Twitch to let it:\n  • see which channels you follow (user:read:follows)\n  • list your Twitch emotes (user:read:emotes)\n  • read and send chat messages as you (chat:read, chat:edit)\nThe token stays on this PC, encrypted for your Windows account.",
            ForeColor = Dim, Location = new Point(20, 118), Size = new Size(420, 96),
        };
        var again = Btn("Open the page again", 20);
        again.Click += (s, e) => Launcher.Open(page);
        var copy = Btn("Copy code", 190);
        copy.Click += (s, e) => { try { if (!string.IsNullOrEmpty(userCode)) Clipboard.SetText(userCode); } catch (System.Runtime.InteropServices.ExternalException) { } };
        var cancel = Btn("Cancel", 320);
        cancel.Click += (s, e) => Close();
        CancelButton = cancel;
        var wait = new Label { Text = "Waiting for Twitch...", ForeColor = Dim, Location = new Point(20, 268), Size = new Size(420, 22) };
        Controls.AddRange(new Control[] { intro, code, scopes, again, copy, cancel, wait });
    }

    Button Btn(string text, int x)
    {
        var b = new Button { Text = text, Location = new Point(x, 224), Size = new Size(x == 20 ? 160 : 120, 34), FlatStyle = FlatStyle.Flat, BackColor = Hover, ForeColor = Fg, Cursor = Cursors.Hand };
        b.FlatAppearance.BorderSize = 0; b.FlatAppearance.MouseOverBackColor = Color.FromArgb(53, 53, 59); b.FlatAppearance.MouseDownBackColor = Accent;
        return b;
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, 4);   // dark title bar
    }
}
