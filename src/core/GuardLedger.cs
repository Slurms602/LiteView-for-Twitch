// GameFirst's memory of what it changed (P4): the original affinity, priority class and GPU scheduling class of each
// process, kept from the first change, so "GameFirst.exe --stop" can put them back. A process is identified by its
// id AND start time: a new process that got a reused id is never "restored" with someone else's settings.
using System.Collections.Generic;
using System.Linq;

namespace LiteViewCore
{
    public sealed class GuardLedger
    {
        public sealed class Entry
        {
            public int Pid; public long Started;
            public ulong? Affinity; public uint? Priority; public int? GpuClass;
        }

        readonly Dictionary<int, Entry> d = new Dictionary<int, Entry>();

        public IReadOnlyCollection<Entry> Entries { get { return d.Values; } }

        Entry For(int pid, long started)
        {
            Entry? e;
            if (!d.TryGetValue(pid, out e) || e.Started != started) { e = new Entry { Pid = pid, Started = started }; d[pid] = e; }
            return e;
        }

        // the first original wins: later passes see GameFirst's own value, not the app's
        public void Affinity(int pid, long started, ulong original) { var e = For(pid, started); if (e.Affinity == null) e.Affinity = original; }
        public void Priority(int pid, long started, uint original) { var e = For(pid, started); if (e.Priority == null) e.Priority = original; }
        public void Gpu(int pid, long started, int original) { var e = For(pid, started); if (e.GpuClass == null) e.GpuClass = original; }

        /// <summary>Forget processes that ended.</summary>
        public void KeepOnly(ICollection<int> alive)
        {
            foreach (var pid in d.Keys.Where(p => !alive.Contains(p)).ToList()) d.Remove(pid);
        }

        /// <summary>What to put back for this process now, or null when it is not one we changed (or the id was reused).</summary>
        public Entry? ToRestore(int pid, long started)
        {
            Entry? e;
            return d.TryGetValue(pid, out e) && e.Started == started ? e : null;
        }
    }
}
