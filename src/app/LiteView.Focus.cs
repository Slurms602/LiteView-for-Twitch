// Keys after typing (owner's report): a click in the chat's "Send a message" box kept the keyboard there even after
// clicking elsewhere (the video, the chat's messages, the followed channels, the top bar), so Space, M, I, B and the
// rest typed into the box instead of working. None of those places take the keyboard themselves, and the video is
// mpv's window (another process). Now a click anywhere else in the window takes the keyboard away from a text box:
// a message filter sees every mouse button going down in the window's own controls (WinForms' controls do not all
// notify their parents), and mpv sends "liteview-click" for a click on the video (config/mpv/input.conf). The chat's
// own row (the emote button, the emote picker) keeps the box typing, and so does a click on a control that takes the
// keyboard itself.
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LiteViewCore;

partial class LiteView
{
    // takes the keyboard when nothing else should have it (the window's keys still arrive: KeyPreview)
    sealed class KeySink : Control
    {
        public KeySink() { SetStyle(ControlStyles.Selectable, true); TabStop = false; SetBounds(-20, -20, 1, 1); AccessibleRole = AccessibleRole.None; }
        protected override bool IsInputKey(Keys k) { return true; }   // arrows and Space are the window's keys, not focus moves
    }
    readonly KeySink keySink = new KeySink();
    bool focusInit;

    [StructLayout(LayoutKind.Sequential)] struct PointStruct { public int X, Y; }
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(PointStruct p);

    sealed class ClickFilter : IMessageFilter
    {
        readonly LiteView owner;
        public ClickFilter(LiteView owner) { this.owner = owner; }
        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg == 0x201 || m.Msg == 0x204 || m.Msg == 0x207 || m.Msg == 0x20B) owner.ClickedInside(m.HWnd);   // left, right, middle, X button down
            return false;   // the click goes on as usual
        }
    }

    void InitFocus()
    {
        if (focusInit) return;
        focusInit = true;
        Controls.Add(keySink);
        var filter = new ClickFilter(this);
        Application.AddMessageFilter(filter);
        FormClosed += (s, e) => Application.RemoveMessageFilter(filter);
    }

    // a mouse button went down on a window of this thread: in this window and while a text box has the keyboard, look again
    // after the click (a control that takes the keyboard has it by then)
    void ClickedInside(IntPtr hwnd)
    {
        if (!(Control.FromChildHandle(GetFocus()) is TextBoxBase)) return;
        var target = Control.FromChildHandle(hwnd);
        if (target == null || target.FindForm() != this) return;
        var at = Cursor.Position;
        try { BeginInvoke((Action)(() => DropTextFocus(at))); } catch (InvalidOperationException) { }
    }

    // at: where the click was (null: on the video)
    void DropTextFocus(Point? at)
    {
        var focused = Control.FromChildHandle(GetFocus());
        if (!(focused is TextBoxBase) || focused.IsDisposed || !keySink.IsHandleCreated && !IsHandleCreated) return;
        if (at.HasValue)
        {
            if (focused.RectangleToScreen(focused.ClientRectangle).Contains(at.Value)) return;
            var under = Control.FromChildHandle(WindowFromPoint(new PointStruct { X = at.Value.X, Y = at.Value.Y }));
            for (var c = under; c != null; c = c.Parent)
                if (c is EmotePicker || c == focused.Parent) return;   // the chat's own row and its emote picker
            if (under is TextBoxBase || under is ComboBox) return;
        }
        InitFocus();
        keySink.Focus();
        if (Fixture.On) Log.Info("focus: the keyboard left the text box (" + (at.HasValue ? "click in the window" : "click on the video") + ")");
    }

    // ---------- UI fixture (CI): scene "focus" ----------
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);

    // a real click (the mouse moves there), as the owner's
    void FixtureClick(Point screen, bool right)
    {
        SetForegroundWindow(Handle);
        SetCursorPos(screen.X, screen.Y);
        mouse_event(right ? 0x0008u : 0x0002u, 0, 0, 0, UIntPtr.Zero);
        mouse_event(right ? 0x0010u : 0x0004u, 0, 0, 0, UIntPtr.Zero);
    }

    // a stream with the chat: type in the chat box, click the chat's header, type again, click the video
    void FocusScene()
    {
        Watch("nightowl_plays");
        int step = 0, waited = 0;
        var t = new System.Windows.Forms.Timer { Interval = 500 };
        t.Tick += (s, e) =>
        {
            waited += 500;
            if (step == 0)
            {
                if (firstFrameGen != gen) { if (waited > 20000) { t.Stop(); Log.Warn("focus: no picture"); } return; }
                if (waited < 2000) return;
                Activate(); nchat.FixtureFocusInput();
                Log.Info("focus: typing in the chat box: " + Typing);
                FixtureClick(nchat.PointToScreen(new Point(nchat.Width / 2, 12)), false);   // the "STREAM CHAT" header
                step = 1; waited = 0; return;
            }
            if (step == 1 && waited >= 1500)
            {
                Log.Info("focus: after a click on the chat's header, typing: " + Typing);
                nchat.FixtureFocusInput();
                Log.Info("focus: typing in the chat box again: " + Typing);
                FixtureClick(video.PointToScreen(new Point(video.Width / 2, video.Height / 2)), false);
                step = 2; waited = 0; return;
            }
            if (step == 2 && waited >= 2500) { t.Stop(); t.Dispose(); Log.Info("focus: after a click on the video, typing: " + Typing); }
        };
        t.Start();
    }
}
