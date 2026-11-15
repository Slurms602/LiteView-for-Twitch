// CPU scheduling modes and Gaming Mode decisions (P4). Nothing here guesses: Automatic only avoids the 3D V-Cache die
// on CPUs that really have one (CpuTopology), Windows leaves everything to Windows, Custom uses the user's own mask
// limited to processors that exist. Gaming Mode only reacts to what Windows reports about the window in front.
using System;
using System.Globalization;

namespace LiteViewCore
{
    public enum CpuPriority { Normal, BelowNormal }

    public sealed class CpuPlan
    {
        public ulong Mask;                // 0 = no pinning (Windows decides)
        public CpuPriority Priority;
        public string Why = "";
    }

    public static class Scheduling
    {
        /// <param name="mode">auto (default), windows or custom</param>
        /// <param name="customMask">hex, e.g. FFF000 (custom mode)</param>
        /// <param name="autoMask">CpuTopology's answer: the die without V-Cache, or 0</param>
        /// <param name="systemMask">the processors this process may use at all</param>
        public static CpuPlan Plan(string? mode, string? customMask, ulong autoMask, ulong systemMask)
        {
            switch ((mode ?? "").Trim().ToLowerInvariant())
            {
                case "windows":
                    return new CpuPlan { Mask = 0, Priority = CpuPriority.Normal, Why = "Windows decides (no pinning, normal priority)" };
                case "custom":
                    ulong m = ParseMask(customMask) & systemMask;
                    if (m != 0 && m != systemMask) return new CpuPlan { Mask = m, Priority = CpuPriority.BelowNormal, Why = "custom cores 0x" + m.ToString("X", CultureInfo.InvariantCulture) };
                    if (m == systemMask && m != 0) return new CpuPlan { Mask = 0, Priority = CpuPriority.BelowNormal, Why = "custom: every core (no pinning)" };
                    return Auto(autoMask, systemMask, "custom mask '" + (customMask ?? "") + "' has no usable core here; automatic: ");
                default:
                    return Auto(autoMask, systemMask, "automatic: ");
            }
        }

        static CpuPlan Auto(ulong autoMask, ulong systemMask, string prefix)
        {
            ulong m = autoMask & systemMask;
            return new CpuPlan
            {
                Mask = m == systemMask ? 0 : m, Priority = CpuPriority.BelowNormal,
                Why = prefix + (m != 0 && m != systemMask ? "off the 3D V-Cache die (0x" + m.ToString("X", CultureInfo.InvariantCulture) + "), below normal priority" : "no pinning (all cores alike), below normal priority"),
            };
        }

        /// <summary>"FFF000", "0xfff000" → the mask; 0 when not hex.</summary>
        public static ulong ParseMask(string? s)
        {
            string t = (s ?? "").Trim();
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t.Substring(2);
            ulong v;
            return t.Length > 0 && t.Length <= 16 && ulong.TryParse(t, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out v) ? v : 0;
        }
    }

    public static class GameDetect
    {
        // SHQueryUserNotificationState
        public const int QunsBusy = 2, QunsD3dFullScreen = 3;

        /// <summary>
        /// A game (or another fullscreen app) is in front: Windows reports exclusive fullscreen, or a fullscreen app,
        /// or the window in front covers its whole monitor. Never LiteView itself (its own fullscreen video) or the desktop.
        /// </summary>
        public static bool InFront(int quns, Box window, Box monitor, bool ours, bool desktop)
        {
            if (ours || desktop) return false;
            if (quns == QunsD3dFullScreen || quns == QunsBusy) return true;
            return monitor.W > 0 && window.X <= monitor.X && window.Y <= monitor.Y && window.Right >= monitor.Right && window.Bottom >= monitor.Bottom;
        }
    }

    /// <summary>Gaming Mode with patience: on after a game was in front for 2 looks in a row, off after 3 looks without.</summary>
    public sealed class GamingSwitch
    {
        public const int OnAfter = 2, OffAfter = 3;
        int seen, gone;
        public bool Active { get; private set; }

        /// <returns>true when Active changed</returns>
        public bool Update(bool gameInFront)
        {
            if (gameInFront) { seen++; gone = 0; } else { gone++; seen = 0; }
            bool was = Active;
            if (!Active && seen >= OnAfter) Active = true;
            else if (Active && gone >= OffAfter) Active = false;
            return Active != was;
        }

        public void Reset() { seen = gone = 0; Active = false; }
    }
}
