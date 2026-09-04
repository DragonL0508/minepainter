using MinePainter.Core.AI;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 推論期間的記憶體看門狗。這是最後一道保險：預檢是用實測值判斷，第一次跑的模型沒有實測值，
/// 只能邊跑邊盯，超標就中止——沒有它，一個裝不下的模型會把系統推進 swap 到只能重開機。
/// </summary>
public class MemoryWatchdogTests
{
    [Fact]
    public void TripsAndTerminatesTheRunWhenTheBudgetIsExceeded()
    {
        var terminated = 0;
        using var watchdog = new MemoryWatchdog(budgetBytes: 16L << 20, () => Interlocked.Increment(ref terminated));

        // 真的把記憶體碰出來（只配置不寫入不會進工作集）
        var hog = new byte[256L << 20];
        for (var i = 0; i < hog.Length; i += 4096) hog[i] = 1;

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!watchdog.Tripped && DateTime.UtcNow < deadline) Thread.Sleep(50);

        Assert.True(watchdog.Tripped);
        Assert.Equal(1, Volatile.Read(ref terminated));
        Assert.True(watchdog.PeakGrowthBytes > 0);
        GC.KeepAlive(hog);
    }

    [Fact]
    public void OnlyTerminatesOnceHoweverLongItStaysOverBudget()
    {
        var terminated = 0;
        // 預算 -1：任何用量（含「完全沒長」）都算超標，第一次取樣就一定會跳。
        // 用 0 的話得等工作集真的長出來，機器忙的時候 5 秒內未必會長 —— 這條驗的是
        // 「只中止一次」，不該被那個時機影響。
        using var watchdog = new MemoryWatchdog(budgetBytes: -1, () => Interlocked.Increment(ref terminated));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!watchdog.Tripped && DateTime.UtcNow < deadline) Thread.Sleep(50);
        Thread.Sleep(500); // 再過好幾個取樣週期

        Assert.True(watchdog.Tripped);
        Assert.Equal(1, Volatile.Read(ref terminated)); // 只中止一次，不重複呼叫
    }

    [Fact]
    public void StaysQuietWhenThereIsRoom()
    {
        // 前提是「這台機器現在真的有空間」—— 看門狗除了預算，也會在整台機器的可用實體記憶體
        // 低於安全水位時中止（那正是它該做的事）。跑測試時機器本來就很緊的話，這條沒得測。
        var available = (long)SystemMemory.AvailablePhysicalBytes;
        if (available > 0 && available < InferenceBudget.SafetyFloorBytes * 2) return;

        var terminated = 0;
        using var watchdog = new MemoryWatchdog(budgetBytes: long.MaxValue, () => Interlocked.Increment(ref terminated));
        Thread.Sleep(500);

        Assert.False(watchdog.Tripped);
        Assert.Equal(0, Volatile.Read(ref terminated));
    }

    [Fact]
    public void DisposeIsSafeToCallTwice()
    {
        var watchdog = new MemoryWatchdog(budgetBytes: long.MaxValue, () => { });
        watchdog.Dispose(); // 成功路徑會先停再讀峰值
        watchdog.Dispose(); // using 再收一次
    }
}
