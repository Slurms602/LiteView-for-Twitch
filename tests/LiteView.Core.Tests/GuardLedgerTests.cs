using LiteViewCore;
using Xunit;

public class GuardLedgerTests
{
    [Fact]
    public void KeepsTheFirstOriginalOfEachSetting()
    {
        var l = new GuardLedger();
        l.Priority(10, 1000, 0x20);        // NORMAL
        l.Priority(10, 1000, 0x4000);      // a later pass sees our BELOW_NORMAL: ignored
        l.Affinity(10, 1000, 0xFFFF);
        l.Gpu(10, 1000, 2);
        var e = l.ToRestore(10, 1000)!;
        Assert.Equal(0x20u, e.Priority);
        Assert.Equal(0xFFFFul, e.Affinity);
        Assert.Equal(2, e.GpuClass);
    }

    [Fact]
    public void AReusedProcessIdIsNotRestored()
    {
        var l = new GuardLedger();
        l.Priority(10, 1000, 0x20);
        Assert.Null(l.ToRestore(10, 2000));       // same id, another process
        l.Priority(10, 2000, 0x8000);             // the new process replaces the old entry
        Assert.Equal(0x8000u, l.ToRestore(10, 2000)!.Priority);
        Assert.Null(l.ToRestore(10, 1000));
        Assert.Null(l.ToRestore(11, 1000));
    }

    [Fact]
    public void EndedProcessesAreForgotten()
    {
        var l = new GuardLedger();
        l.Priority(10, 1, 0x20); l.Priority(11, 1, 0x20); l.Affinity(12, 1, 3);
        l.KeepOnly(new[] { 11 });
        Assert.Single(l.Entries);
        Assert.NotNull(l.ToRestore(11, 1));
    }
}
