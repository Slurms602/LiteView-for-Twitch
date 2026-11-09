// Shared helpers: CPU die detection, process creation pinned off the game's die, priorities.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

static class Native
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO { public int cb; public string r, desk, title; public int x, y, w, h, xc, yc, fill, flags; public short show, r2; public IntPtr r3, hin, hout, herr; }
    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int pid, tid; }
    [StructLayout(LayoutKind.Sequential)]
    public struct UNICODE_STRING { public ushort Length, MaximumLength; public IntPtr Buffer; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CreateProcessW(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] public static extern bool SetProcessAffinityMask(IntPtr h, UIntPtr mask);
    [DllImport("kernel32.dll")] public static extern bool GetProcessAffinityMask(IntPtr h, out UIntPtr proc, out UIntPtr sys);
    [DllImport("kernel32.dll")] public static extern uint ResumeThread(IntPtr h);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] public static extern uint GetPriorityClass(IntPtr h);
    [DllImport("kernel32.dll")] public static extern bool SetPriorityClass(IntPtr h, uint cls);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetLogicalProcessorInformationEx(int rel, IntPtr buf, ref int len);
    [DllImport("ntdll.dll")] public static extern int NtQueryInformationProcess(IntPtr h, int cls, IntPtr buf, int len, out int ret);
    [DllImport("gdi32.dll")] public static extern int D3DKMTSetProcessSchedulingPriorityClass(IntPtr h, int prio);

    public const uint CREATE_SUSPENDED = 0x4, CREATE_NO_WINDOW = 0x08000000, BELOW_NORMAL = 0x4000, IDLE = 0x40;
    public const uint PROCESS_QUERY_INFORMATION = 0x400, PROCESS_SET_INFORMATION = 0x200, PROCESS_VM_READ = 0x10, PROCESS_QUERY_LIMITED = 0x1000;
    public const uint ABOVE_NORMAL = 0x8000;

    // Logical processors sharing the SMALLEST L3 = the die without 3D V-Cache (0 if all L3s are equal).
    public static ulong NonCacheDieMask()
    {
        int len = 0; GetLogicalProcessorInformationEx(2, IntPtr.Zero, ref len);
        IntPtr buf = Marshal.AllocHGlobal(len);
        try
        {
            if (!GetLogicalProcessorInformationEx(2, buf, ref len)) return 0;
            var l3 = new List<KeyValuePair<uint, ulong>>();
            for (int off = 0; off < len;)
            {
                IntPtr p = buf + off;
                if (Marshal.ReadInt32(p, 0) == 2 && Marshal.ReadByte(p, 8) == 3)
                    l3.Add(new KeyValuePair<uint, ulong>((uint)Marshal.ReadInt32(p, 12), (ulong)Marshal.ReadInt64(p, 40)));
                off += Marshal.ReadInt32(p, 4);
            }
            if (l3.Count < 2) return 0;
            l3.Sort((a, b) => a.Key.CompareTo(b.Key));
            if (l3[0].Key == l3[l3.Count - 1].Key) return 0;
            ulong m = 0; foreach (var c in l3) if (c.Key == l3[0].Key) m |= c.Value;
            return m;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public static string CommandLineOf(IntPtr h)
    {
        int ret; IntPtr b = Marshal.AllocHGlobal(65536);
        try
        {
            if (NtQueryInformationProcess(h, 60, b, 65536, out ret) != 0) return null;
            var us = (UNICODE_STRING)Marshal.PtrToStructure(b, typeof(UNICODE_STRING));
            return Marshal.PtrToStringUni(us.Buffer, us.Length / 2);
        }
        finally { Marshal.FreeHGlobal(b); }
    }

    // Start a program already pinned and below normal, so every child process inherits both.
    public static int StartPinned(string exe, string args, ulong mask, bool noWindow)
    {
        var si = new STARTUPINFO(); si.cb = Marshal.SizeOf(si);
        PROCESS_INFORMATION pi;
        var cmd = new StringBuilder("\"" + exe + "\" " + args);
        uint flags = CREATE_SUSPENDED | BELOW_NORMAL | (noWindow ? CREATE_NO_WINDOW : 0);
        if (!CreateProcessW(exe, cmd, IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero, Path.GetDirectoryName(exe), ref si, out pi)) return 0;
        if (mask != 0) SetProcessAffinityMask(pi.hProcess, new UIntPtr(mask));
        ResumeThread(pi.hThread); CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
        return pi.pid;
    }

    public static Dictionary<string, string> ReadIni(string path)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return d;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
            int eq = line.IndexOf('=');
            if (eq > 0) d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
        }
        return d;
    }

    public static string Get(Dictionary<string, string> d, string k, string def) { string v; return d.TryGetValue(k, out v) && v.Length > 0 ? v : def; }
}
