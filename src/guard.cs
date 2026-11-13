// GameFirst guard: runs in the background (starts at logon) and keeps the listed apps
// (Chrome and the Twitch Lite tools by default) off the game's CPU die and below the game:
//   - CPU: only the die WITHOUT 3D V-Cache (auto-detected), Below Normal priority
//     (Chrome's own Idle background tabs are left Idle)
//   - GPU: Below Normal GPU scheduling for Chrome's GPU process and mpv
// Settings: gamefirst.ini. Log: gamefirst.log. Costs about 1 ms of CPU every few seconds.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using LiteViewCore;

static class Guard
{
    static string log;
    static void Log(string s) { try { File.AppendAllText(log, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + s + "\r\n"); } catch { } }

    [STAThread]
    static int Main()
    {
        bool created;
        using (var mutex = new Mutex(true, "Local\\GameFirstGuard", out created))
        {
            if (!created) return 0;   // already running
            string root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            log = Path.Combine(root, "gamefirst.log");
            try { if (File.Exists(log) && new FileInfo(log).Length > 256 * 1024) File.Delete(log); } catch { }

            var ini = Native.ReadIni(Path.Combine(root, "gamefirst.ini"));
            string aff = Native.Get(ini, "affinity", "auto").ToLowerInvariant();
            ulong mask = 0;
            if (aff == "auto") mask = Native.NonCacheDieMask();
            else if (aff != "all" && !ulong.TryParse(aff.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out mask))
            { Log("affinity=" + aff + " is not auto, all or a hex mask: no pinning"); mask = 0; }   // upstream crashed here
            bool below = Native.Get(ini, "priority", "belownormal").ToLowerInvariant() != "normal";
            bool gpuLow = Native.Get(ini, "gpupriority", "belownormal").ToLowerInvariant() != "normal";
            int every = IniStore.GetInt(ini, "interval", 4, 1, 3600) * 1000;   // upstream: int.Parse, crashed on a typo
            var apps = new List<string>();
            // Default: only LiteView's own player processes. Chrome, Discord etc. are changed only when listed in gamefirst.ini.
            foreach (var a in Native.Get(ini, "apps", "mpv,streamlink,streamlinkw").Split(','))
                if (a.Trim().Length > 0) apps.Add(a.Trim().Replace(".exe", ""));

            using (var self = Process.GetCurrentProcess())
            {
                self.PriorityClass = ProcessPriorityClass.Idle;
                if (mask != 0) self.ProcessorAffinity = new IntPtr((long)mask);
            }
            Log(string.Format("guard started: mask 0x{0:X} below={1} gpuLow={2} apps={3}", mask, below, gpuLow, string.Join(",", apps)));

            var seen = new HashSet<int>();     // pids whose GPU priority step is done
            int pass = 0;
            while (true)
            {
                var alive = new HashSet<int>();
                foreach (var name in apps)
                {
                    foreach (var proc in Process.GetProcessesByName(name))
                    {
                        int pid = proc.Id; proc.Dispose();
                        alive.Add(pid);
                        // Only the rights we need: Chrome denies memory-read access to other processes.
                        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED | Native.PROCESS_SET_INFORMATION, false, pid);
                        if (h == IntPtr.Zero) continue;
                        try
                        {
                            UIntPtr pm, sm;
                            if (mask != 0 && Native.GetProcessAffinityMask(h, out pm, out sm) && pm.ToUInt64() != mask)
                                Native.SetProcessAffinityMask(h, new UIntPtr(mask));
                            uint cls = Native.GetPriorityClass(h);
                            if (gpuLow && !seen.Contains(pid))
                            {
                                seen.Add(pid);
                                bool gpu = name.Equals("mpv", StringComparison.OrdinalIgnoreCase);
                                if (!gpu)
                                {
                                    // Chrome's GPU process: read its command line, or (if Chrome refuses) it is
                                    // the one Chrome itself raised to Above Normal.
                                    string cl = null;
                                    IntPtr hr = Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION | Native.PROCESS_VM_READ, false, pid);
                                    if (hr != IntPtr.Zero) { cl = Native.CommandLineOf(hr); Native.CloseHandle(hr); }
                                    gpu = cl != null ? cl.Contains("--type=gpu-process") : cls == Native.ABOVE_NORMAL;
                                }
                                if (gpu)
                                {
                                    int r = Native.D3DKMTSetProcessSchedulingPriorityClass(h, 1);
                                    Log(string.Format("{0} {1}: GPU priority below normal (status 0x{2:X})", name, pid, r));
                                }
                            }
                            if (below && cls != 0 && cls != Native.IDLE && cls != Native.BELOW_NORMAL)
                                Native.SetPriorityClass(h, Native.BELOW_NORMAL);
                        }
                        finally { Native.CloseHandle(h); }
                    }
                }
                if (++pass % 50 == 0) seen.IntersectWith(alive);
                Thread.Sleep(every);
            }
        }
    }
}
