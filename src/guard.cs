// GameFirst guard: runs in the background (starts at logon) and keeps the listed apps
// (Chrome and the Twitch Lite tools by default) off the game's CPU die and below the game:
//   - CPU: only the die WITHOUT 3D V-Cache (auto-detected), Below Normal priority
//     (Chrome's own Idle background tabs are left Idle)
//   - GPU: Below Normal GPU scheduling for Chrome's GPU process and mpv
// Settings: gamefirst.ini. Log: gamefirst.log. Costs about 1 ms of CPU every few seconds.
// Fork: "GameFirst.exe --stop" makes the running guard put back what it changed (affinity, priority, GPU
// priority of the processes still running) and exit (src/core/GuardLedger.cs).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using LiteViewCore;

static class Guard
{
    const string StopEvent = "Local\\GameFirstGuardStop";
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr h, out long created, out long exited, out long kernel, out long user);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern int D3DKMTGetProcessSchedulingPriorityClass(IntPtr h, out int cls);
    static long StartTime(IntPtr h) { long c, e, k, u; return GetProcessTimes(h, out c, out e, out k, out u) ? c : 0; }
    static string log;
    static void Log(string s) { try { File.AppendAllText(log, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + s + "\r\n"); } catch { } }

    [STAThread]
    static int Main(string[] args)
    {
        if (Array.Exists(args, a => a.Equals("--stop", StringComparison.OrdinalIgnoreCase)))
        {
            EventWaitHandle running;
            if (!EventWaitHandle.TryOpenExisting(StopEvent, out running)) return 1;   // not running
            using (running) running.Set();
            return 0;
        }
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
            var ledger = new GuardLedger();    // what to put back on --stop
            int pass = 0;
            using (var stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopEvent))
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
                            long started = StartTime(h);
                            UIntPtr pm, sm;
                            if (mask != 0 && Native.GetProcessAffinityMask(h, out pm, out sm) && pm.ToUInt64() != mask)
                            {
                                ledger.Affinity(pid, started, pm.ToUInt64());
                                Native.SetProcessAffinityMask(h, new UIntPtr(mask));
                            }
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
                                    int old;
                                    if (D3DKMTGetProcessSchedulingPriorityClass(h, out old) == 0) ledger.Gpu(pid, started, old);
                                    int r = Native.D3DKMTSetProcessSchedulingPriorityClass(h, 1);
                                    Log(string.Format("{0} {1}: GPU priority below normal (status 0x{2:X})", name, pid, r));
                                }
                            }
                            if (below && cls != 0 && cls != Native.IDLE && cls != Native.BELOW_NORMAL)
                            {
                                ledger.Priority(pid, started, cls);
                                Native.SetPriorityClass(h, Native.BELOW_NORMAL);
                            }
                        }
                        finally { Native.CloseHandle(h); }
                    }
                }
                if (++pass % 50 == 0) { seen.IntersectWith(alive); ledger.KeepOnly(alive); }
                if (stop.WaitOne(every)) { Restore(ledger); return 0; }
            }
        }
    }

    // --stop: put back the original affinity, priority and GPU priority of the processes we changed that still run
    static void Restore(GuardLedger ledger)
    {
        int n = 0;
        foreach (var e in ledger.Entries)
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED | Native.PROCESS_SET_INFORMATION, false, e.Pid);
            if (h == IntPtr.Zero) continue;
            try
            {
                if (ledger.ToRestore(e.Pid, StartTime(h)) == null) continue;   // that id belongs to another process now
                if (e.Affinity != null) Native.SetProcessAffinityMask(h, new UIntPtr(e.Affinity.Value));
                if (e.Priority != null) Native.SetPriorityClass(h, e.Priority.Value);
                if (e.GpuClass != null) Native.D3DKMTSetProcessSchedulingPriorityClass(h, e.GpuClass.Value);
                n++;
            }
            finally { Native.CloseHandle(h); }
        }
        Log("guard stopped (--stop): settings of " + n + " process(es) put back");
    }
}
