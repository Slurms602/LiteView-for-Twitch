// The mouse's side buttons (back and forward, Windows' "X buttons"). Owner's request 2026-11-22: show or hide the chat
// with them, as C does. Each button has its own action (menu > Playback > Mouse back button / Mouse forward button, also
// Settings > Playback), kept in liteview.ini as "mouseback" and "mouseforward"; both show or hide the chat by default.
// A press arrives three ways (src/app/LiteView.Mouse.cs): as a window message on LiteView's own controls (the video's
// too: mpv's embedded window is disabled), as a line from mpv should its window take the mouse (config/mpv/input.conf:
// MBTN_BACK / MBTN_FORWARD), or as the keyboard keys Browser Back / Browser Forward, which some mouse software sends.
using System;
using System.Globalization;

namespace LiteViewCore
{
    public enum SideAction { Nothing, Chat, Pause, Mute, Fullscreen, Back }

    public static class SideButtons
    {
        /// <summary>The choices, in the menus' order.</summary>
        public static readonly SideAction[] Choices = { SideAction.Chat, SideAction.Pause, SideAction.Mute, SideAction.Fullscreen, SideAction.Back, SideAction.Nothing };
        public const SideAction Default = SideAction.Chat;
        public const string BackKey = "mouseback", ForwardKey = "mouseforward";

        public static string Name(SideAction a)
        {
            switch (a)
            {
                case SideAction.Chat: return "Show or hide the chat  (C)";
                case SideAction.Pause: return "Pause or play  (Space)";
                case SideAction.Mute: return "Mute or unmute  (M)";
                case SideAction.Fullscreen: return "Fullscreen on or off  (F11)";
                case SideAction.Back: return "Back to the Following page  (Backspace)";
                default: return "Nothing";
            }
        }

        public static string Save(SideAction a) { return a.ToString().ToLowerInvariant(); }

        /// <summary>A saved value; anything unknown (an older or edited file) is the default.</summary>
        public static SideAction Parse(string? s)
        {
            if (s != null)
                foreach (var a in Choices)
                    if (string.Equals(Save(a), s.Trim(), StringComparison.OrdinalIgnoreCase)) return a;
            return Default;
        }

        /// <summary>A mouse message's wParam (WM_XBUTTONDOWN and its kin): true for the forward button (XBUTTON2), false for
        /// back (XBUTTON1), null for neither.</summary>
        public static bool? IsForward(long wParam)
        {
            int b = (int)((wParam >> 16) & 0xFFFF);
            return b == 1 ? false : b == 2 ? true : (bool?)null;
        }

        /// <summary>mpv's line for "script-message liteview-mouse back|forward": true for forward, false for back, null for any
        /// other line.</summary>
        public static bool? FromMpvLine(string? line)
        {
            if (line == null || line.IndexOf("\"liteview-mouse\"", StringComparison.Ordinal) < 0) return null;
            var j = Json.Parse(line);
            if (Json.S(j, "event") != "client-message") return null;
            var args = Json.A(j, "args");
            if (args.Length != 2 || Convert.ToString(args[0], CultureInfo.InvariantCulture) != "liteview-mouse") return null;
            string which = Convert.ToString(args[1], CultureInfo.InvariantCulture) ?? "";
            return which == "forward" ? true : which == "back" ? false : (bool?)null;
        }

        /// <summary>mpv's line for "script-message liteview-chat" (C pressed while mpv has the keyboard).</summary>
        public static bool IsChatKey(string? line)
        {
            if (line == null || line.IndexOf("\"liteview-chat\"", StringComparison.Ordinal) < 0) return false;
            var j = Json.Parse(line);
            var args = Json.A(j, "args");
            return Json.S(j, "event") == "client-message" && args.Length == 1 && Convert.ToString(args[0], CultureInfo.InvariantCulture) == "liteview-chat";
        }
    }
}
