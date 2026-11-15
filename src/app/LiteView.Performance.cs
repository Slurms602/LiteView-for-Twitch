// CPU use modes and Gaming Mode (P4; decisions in src/core/Performance.cs).
// CPU use (menu > Performance): Automatic (upstream's behaviour: below normal priority, off the 3D V-Cache die only on
// CPUs that have one), Windows decides (no pinning, normal priority), or Custom cores. Applies from the next start.
// Gaming Mode (opt-in): while a fullscreen game is in front, the picture preset drops to Off (or Enhance), RTX is off
// and chat animations run slower; everything comes back when the game leaves. Your saved choices are not changed.
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using LiteViewCore;

partial class LiteView
{
    static CpuPlan cpuPlan;
    static ulong cpuUnpin;   // the full mask, when this process started pinned (Application.Restart passes affinity on)

    // read before the window exists (Main sets the process's affinity and priority; Streamlink and mpv inherit them)
    static CpuPlan CpuChoice()
    {
        if (cpuPlan != null) return cpuPlan;
        var d = new System.Collections.Generic.Dictionary<string, string>();
        try
        {
            string exe = typeof(LiteView).Assembly.Location;
            bool unused;
            d = IniStore.Read(Path.Combine(Path.GetDirectoryName(exe), Path.GetFileNameWithoutExtension(exe).ToLowerInvariant() + ".ini"), out unused);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        ulong system = ~0ul;
        using (var self = Process.GetCurrentProcess())
        {
            UIntPtr pm, sm;
            if (Native.GetProcessAffinityMask(self.Handle, out pm, out sm) && sm.ToUInt64() != 0)
            {
                system = sm.ToUInt64();   // every processor (pm is only what we were started with)
                if (pm.ToUInt64() != system && Environment.ProcessorCount <= 64) cpuUnpin = system;
            }
        }
        cpuPlan = Scheduling.Plan(Native.Get(d, "cpu", "auto"), Native.Get(d, "cpumask", ""), Native.NonCacheDieMask(), system);
        return cpuPlan;
    }

    /// <summary>Main: the affinity to set; with no pinning planned, undo pinning this process inherited (0 = leave it).</summary>
    static ulong CpuAffinity() { ulong m = CpuChoice().Mask; return m != 0 ? m : cpuUnpin; }

    static uint ChildPriorityFlag { get { return CpuChoice().Priority == CpuPriority.Normal ? 0u : Native.BELOW_NORMAL; } }

    string cpuMode = "auto", cpuMask = "";

    void SetCpuMode(string mode, string mask)
    {
        cpuMode = mode; cpuMask = mask ?? "";
        SaveSettings();
        if (MessageBox.Show(this, "CPU use: " + Scheduling.Plan(mode, mask, Native.NonCacheDieMask(), ~0ul).Why + ".\n\nThis applies when LiteView starts. Restart now?",
            "CPU use", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) Application.Restart();
    }

    void AskCpuMask()
    {
        int n = Environment.ProcessorCount;
        string t = TextListDialog.Ask(this, "Custom cores", "Logical processors as a hexadecimal mask (bit 0 = processor 0). This PC has " + n +
            " (0 to " + (n - 1) + "). Example: FFF000 = processors 12 to 23. Processors that do not exist are ignored.", cpuMask);
        if (t == null) return;
        t = t.Trim();
        if (Scheduling.ParseMask(t) == 0) { MessageBox.Show(this, "'" + t + "' is not a hexadecimal mask.", "Custom cores"); return; }
        SetCpuMode("custom", t);
    }

    // ---------- quality box ----------

    bool qualityRebuild;   // the variants list is being replaced: not the user choosing another quality

    // LoadSettings: the saved choice, also a variant such as 360p that is not in the fixed list
    void SelectQuality(string saved)
    {
        int q = quality.Items.IndexOf(saved);
        if (q < 0 && Quality.IsVariant(saved)) q = quality.Items.Add(saved);
        quality.SelectedIndex = q < 0 ? 0 : q;
    }

    // ---------- Gaming Mode ----------

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder name, int max);
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);

    string gamingSetting = "off";   // off, auto, on
    bool gamingEnhance, gamingActive;
    readonly GamingSwitch gamingSwitch = new GamingSwitch();
    readonly System.Windows.Forms.Timer gamingTimer = new System.Windows.Forms.Timer { Interval = 5000 };
    static readonly int OwnPid = Process.GetCurrentProcess().Id;

    void InitGaming()
    {
        gamingTimer.Tick += (s, e) => { if (gamingSwitch.Update(watching && GameInFront())) SetGaming(gamingSwitch.Active); };
        FormClosed += (s, e) => gamingTimer.Stop();
        ApplyGamingSetting();
    }

    void ApplyGamingSetting()
    {
        gamingTimer.Enabled = gamingSetting == "auto";
        if (gamingSetting != "auto") gamingSwitch.Reset();
        SetGaming(gamingSetting == "on");
    }

    // only what Windows says about the window in front: no process lists, no files
    bool GameInFront()
    {
        IntPtr h = GetForegroundWindow();
        if (h == IntPtr.Zero) return false;
        uint pid; GetWindowThreadProcessId(h, out pid);
        var cls = new StringBuilder(64); GetClassName(h, cls, cls.Capacity);
        string c = cls.ToString();
        bool desktop = c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd";
        int q; if (SHQueryUserNotificationState(out q) != 0) q = 0;
        RECT r;
        if (!GetWindowRect(h, out r)) return false;
        return GameDetect.InFront(q, new Box(r.L, r.T, r.R - r.L, r.B - r.T), ToBox(Screen.FromHandle(h).Bounds), pid == OwnPid, desktop);
    }

    void SetGaming(bool on)
    {
        if (on == gamingActive) return;
        gamingActive = on;
        Log.Info("gaming mode " + (on ? "on" : "off"));
        nchat.View.SetAnimInterval(on ? 100 : 33);
        if (watching) ApplyEnhance(true);
        if (on) { status.ForeColor = Dim; status.Text = "Gaming mode: lighter picture while the game is in front"; }
        else if (status.Text.StartsWith("Gaming mode", StringComparison.Ordinal)) status.Text = "";
    }

    // ApplyEnhance: the preset to use now (Gaming Mode lowers it; the saved choice stays)
    int GamingPicture(int pic) { return gamingActive ? Math.Min(pic, gamingEnhance ? 1 : 0) : pic; }

    ToolStripMenuItem PerformanceMenu()
    {
        var perf = new ToolStripMenuItem("Performance");
        var cpu = new ToolStripMenuItem("CPU use");
        foreach (var o in new[] { new[] { "auto", "Automatic (recommended)" }, new[] { "windows", "Windows decides" } })
        {
            string mode = o[0];
            var it = new ToolStripMenuItem(o[1]) { Checked = cpuMode == mode };
            it.Click += (s, e) => SetCpuMode(mode, cpuMask);
            cpu.DropDownItems.Add(it);
        }
        var custom = new ToolStripMenuItem("Custom cores..." + (cpuMode == "custom" ? "  (0x" + Scheduling.ParseMask(cpuMask).ToString("X") + ")" : "")) { Checked = cpuMode == "custom" };
        custom.Click += (s, e) => AskCpuMask();
        cpu.DropDownItems.Add(custom);
        cpu.DropDownItems.Add(new ToolStripSeparator());
        cpu.DropDownItems.Add(new ToolStripMenuItem("Now: " + CpuChoice().Why) { Enabled = false });
        DarkMenuColors.Apply(cpu.DropDown);

        var gaming = new ToolStripMenuItem("Gaming mode" + (gamingActive ? " (on now)" : ""));
        foreach (var o in new[] { new[] { "off", "Off" }, new[] { "auto", "Automatic: when a fullscreen game is in front" }, new[] { "on", "On" } })
        {
            string v = o[0];
            var it = new ToolStripMenuItem(o[1]) { Checked = gamingSetting == v };
            it.Click += (s, e) => { gamingSetting = v; ApplyGamingSetting(); SaveSettings(); };
            gaming.DropDownItems.Add(it);
        }
        gaming.DropDownItems.Add(new ToolStripSeparator());
        var pOff = new ToolStripMenuItem("Picture while gaming: Off") { Checked = !gamingEnhance };
        pOff.Click += (s, e) => { gamingEnhance = false; if (gamingActive) ApplyEnhance(true); SaveSettings(); };
        var pEnh = new ToolStripMenuItem("Picture while gaming: Enhance") { Checked = gamingEnhance };
        pEnh.Click += (s, e) => { gamingEnhance = true; if (gamingActive) ApplyEnhance(true); SaveSettings(); };
        gaming.DropDownItems.Add(pOff); gaming.DropDownItems.Add(pEnh);
        DarkMenuColors.Apply(gaming.DropDown);

        perf.DropDownItems.Add(cpu); perf.DropDownItems.Add(gaming);
        DarkMenuColors.Apply(perf.DropDown);
        return perf;
    }

    void LoadPerformanceSettings(System.Collections.Generic.Dictionary<string, string> ini)
    {
        string c = Native.Get(ini, "cpu", "auto").ToLowerInvariant();
        cpuMode = c == "windows" || c == "custom" ? c : "auto";
        cpuMask = Native.Get(ini, "cpumask", "");
        string g = Native.Get(ini, "gaming", "off").ToLowerInvariant();
        gamingSetting = g == "auto" || g == "on" ? g : "off";
        gamingEnhance = Native.Get(ini, "gamingpic", "off") == "enhance";
    }

    System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, string>> PerformanceSettings()
    {
        yield return Kv("cpu", cpuMode);
        yield return Kv("cpumask", cpuMask);
        yield return Kv("gaming", gamingSetting);
        yield return Kv("gamingpic", gamingEnhance ? "enhance" : "off");
    }
}
