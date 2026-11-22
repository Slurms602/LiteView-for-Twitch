// The mouse's side buttons (back and forward; src/core/SideButtons.cs). Owner's request 2026-11-22: show or hide the
// chat with them, as C does. A press reaches LiteView three ways, and each press takes exactly one of them:
// - on LiteView's own controls (top bar, chat, followed channels; also an extra window's): a message filter sees the
//   button going down and takes it (and its button-up, so Windows makes no "browser back" command of it either).
//   That includes the video: mpv (0.42) disables its window when embedded (--wid), so Windows gives the press to the
//   video panel behind it (CI, scene "sidebuttons": the press on the video arrives as "window");
// - should mpv's window take the mouse itself (another mpv build), mpv sends "liteview-mouse back|forward"
//   (config/mpv/input.conf: MBTN_BACK / MBTN_FORWARD, and GO_BACK / GO_FORWARD for the browser keys below);
// - as the keyboard keys Browser Back / Browser Forward, which some mouse software sends instead (src/app/LiteView.Extras.cs).
// A button set to "Nothing", or a press while nothing plays (the Following page), is left alone.
using System;
using System.Collections.Generic;
using System.Windows.Forms;
using LiteViewCore;

partial class LiteView
{
    SideAction mouseBack = SideButtons.Default, mouseForward = SideButtons.Default;   // settings "mouseback", "mouseforward"
    bool sideInit;

    SideAction SideActionFor(bool forward) { return forward ? mouseForward : mouseBack; }

    // would a press of this button do something in the main window now?
    bool SideActs(bool forward) { return watching && SideActionFor(forward) != SideAction.Nothing; }

    // from: where the press was (for the log)
    void SideButton(bool forward, string from)
    {
        var a = SideActionFor(forward);
        Log.Info("mouse " + (forward ? "forward" : "back") + " button (" + from + "): " + SideButtons.Save(a));
        if (!watching) return;
        switch (a)
        {
            case SideAction.Chat: ToggleChat(); break;
            case SideAction.Pause: TogglePause(); break;
            case SideAction.Mute: ToggleMute(); break;
            case SideAction.Fullscreen: ToggleFullscreen(); break;
            case SideAction.Back: Browse(); break;
        }
    }

    // the mute button's click (upstream's, src/liteview.cs); not PerformClick, which does nothing while fullscreen hides the bar
    void ToggleMute() { EndHandOver(); muted = !muted; SendMpv("{\"command\":[\"set_property\",\"mute\"," + (muted ? "true" : "false") + "]}"); UpdateMute(); SaveSettings(); }

    // C and the mouse buttons: fullscreen has no chat, so there "show the chat" leaves fullscreen with the chat shown
    void ToggleChat()
    {
        if (!watching) return;
        if (fullscreen) { ToggleFullscreen(); chatOn.Checked = true; return; }
        chatOn.Checked = !chatOn.Checked;
    }

    sealed class SideButtonFilter : IMessageFilter
    {
        readonly LiteView owner;
        readonly bool[] swallowUp = new bool[2];   // back, forward: their button-up goes too
        public SideButtonFilter(LiteView owner) { this.owner = owner; }

        public bool PreFilterMessage(ref Message m)
        {
            int msg = m.Msg;
            bool down = msg == 0x20B || msg == 0x20D || msg == 0xAB || msg == 0xAD;   // WM_(NC)XBUTTONDOWN, WM_(NC)XBUTTONDBLCLK
            bool up = msg == 0x20C || msg == 0xAC;                                    // WM_(NC)XBUTTONUP
            if (!down && !up) return false;
            bool? fwd = SideButtons.IsForward(m.WParam.ToInt64());
            if (!fwd.HasValue) return false;
            int i = fwd.Value ? 1 : 0;
            if (up) { bool s = swallowUp[i]; swallowUp[i] = false; return s; }
            swallowUp[i] = owner.SideButtonAt(m.HWnd, fwd.Value);
            return swallowUp[i];
        }
    }

    void InitSideButtons()
    {
        if (sideInit) return;
        sideInit = true;
        var filter = new SideButtonFilter(this);
        Application.AddMessageFilter(filter);
        FormClosed += (s, e) => Application.RemoveMessageFilter(filter);
    }

    // a side button went down on a window of this thread: true when the main window or an extra window acts on it
    // (it runs right after; the press goes no further)
    bool SideButtonAt(IntPtr hwnd, bool forward)
    {
        var c = Control.FromChildHandle(hwnd);
        var form = c == null || c.IsDisposed ? null : c.FindForm();
        if (form == this)
        {
            if (!SideActs(forward)) return false;
            try { BeginInvoke((Action)(() => SideButton(forward, "window"))); } catch (InvalidOperationException) { return false; }
            return true;
        }
        var x = form as ExtraPlayer;
        if (x == null || x.IsDisposed || !x.SideActs(forward)) return false;
        try { x.BeginInvoke((Action)(() => { if (!x.IsDisposed) x.SideButton(forward, "window"); })); } catch (InvalidOperationException) { return false; }
        return true;
    }

    // menu > Playback (and Settings > Playback): one choice list per button
    IEnumerable<ToolStripItem> SideButtonMenus()
    {
        foreach (bool forward in new[] { false, true })
        {
            bool fwd = forward;
            var m = new ToolStripMenuItem(fwd ? "Mouse forward button" : "Mouse back button")
            {
                ToolTipText = "The " + (fwd ? "forward" : "back") + " button on the side of the mouse, while a stream or video plays.\nIn an extra window it shows or hides that window's chat, or mutes it.",
            };
            foreach (var a in SideButtons.Choices)
            {
                var act = a;
                var it = new ToolStripMenuItem(SideButtons.Name(act) + (act == SideButtons.Default ? "  (default)" : "")) { Checked = SideActionFor(fwd) == act };
                it.Click += (s, e) =>
                {
                    if (fwd) mouseForward = act; else mouseBack = act;
                    SaveSettings();
                    Log.Info("mouse " + (fwd ? "forward" : "back") + " button: " + SideButtons.Save(act));
                };
                m.DropDownItems.Add(it);
            }
            DarkMenuColors.Apply(m.DropDown);
            yield return m;
        }
    }

    IEnumerable<KeyValuePair<string, string>> SideButtonSettings()
    {
        yield return Kv(SideButtons.BackKey, SideButtons.Save(mouseBack));
        yield return Kv(SideButtons.ForwardKey, SideButtons.Save(mouseForward));
    }

    void LoadSideButtonSettings(Dictionary<string, string> ini)
    {
        mouseBack = SideButtons.Parse(Native.Get(ini, SideButtons.BackKey, SideButtons.Save(SideButtons.Default)));
        mouseForward = SideButtons.Parse(Native.Get(ini, SideButtons.ForwardKey, SideButtons.Save(SideButtons.Default)));
    }

    // ---------- UI fixture (CI): scene "sidebuttons" ----------
    // a stream with the chat: the back button on the chat (a real X button press), the forward button on the top bar,
    // the back button on the video (mpv's own message), then the back button set to Nothing (on the top bar)
    void SideButtonScene()
    {
        Watch("nightowl_plays");
        int step = 0, waited = 0;
        var t = new System.Windows.Forms.Timer { Interval = 500 };
        t.Tick += (s, e) =>
        {
            waited += 500;
            if (step == 0)
            {
                if (firstFrameGen != gen) { if (waited > 20000) { t.Stop(); Log.Warn("side buttons: no picture"); } return; }
                if (waited < 2000) return;
                Log.Info("side buttons: chat shown: " + chatOn.Checked);
                FixtureXButton(nchat.PointToScreen(new System.Drawing.Point(nchat.Width / 2, nchat.Height / 2)), false);
                step = 1; waited = 0; return;
            }
            if (step == 1 && waited >= 1500)
            {
                Log.Info("side buttons: after the back button on the chat, chat shown: " + chatOn.Checked);
                FixtureXButton(bar.PointToScreen(new System.Drawing.Point(bar.Width / 2, bar.Height / 2)), true);
                step = 2; waited = 0; return;
            }
            if (step == 2 && waited >= 1500)
            {
                Log.Info("side buttons: after the forward button on the top bar, chat shown: " + chatOn.Checked);
                FixtureXButton(video.PointToScreen(new System.Drawing.Point(video.Width / 2, video.Height / 2)), false);   // the video panel (mpv's window is disabled)
                step = 3; waited = 0; return;
            }
            if (step == 3 && waited >= 2000)
            {
                Log.Info("side buttons: after the back button on the video, chat shown: " + chatOn.Checked);
                mouseBack = SideAction.Nothing;
                FixtureXButton(bar.PointToScreen(new System.Drawing.Point(bar.Width / 2, bar.Height / 2)), false);
                step = 4; waited = 0; return;
            }
            if (step == 4 && waited >= 1500)
            {
                mouseBack = SideButtons.Default;
                Log.Info("side buttons: back button set to Nothing, chat shown: " + chatOn.Checked);
                t.Stop(); t.Dispose();
            }
        };
        t.Start();
    }

    // a real side button press (the mouse moves there), as the owner's mouse makes it
    void FixtureXButton(System.Drawing.Point screen, bool forward)
    {
        SetForegroundWindow(Handle);
        SetCursorPos(screen.X, screen.Y);
        uint which = forward ? 2u : 1u;   // XBUTTON2 / XBUTTON1
        mouse_event(0x0080, 0, 0, which, UIntPtr.Zero);   // MOUSEEVENTF_XDOWN
        mouse_event(0x0100, 0, 0, which, UIntPtr.Zero);   // MOUSEEVENTF_XUP
    }
}
