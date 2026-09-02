using System.Diagnostics;

namespace MinePainter.Core.AI;

/// <summary>
/// 推論期間盯著記憶體：本行程用量超過預算、或整台機器的可用實體記憶體低於安全水位，
/// 就中止推論。沒有這道保險，一個裝不下的模型會把系統推進 swap，整台電腦當住到只能重開機。
///
/// 盯的是「本行程的工作集」而不是 VRAM：DirectML 在 VRAM 不夠時會把差額配到共享的系統記憶體，
/// 那部分會算進本行程的工作集，所以工作集抓得到 GPU 溢流，也抓得到純 CPU 推論。
/// </summary>
internal sealed class MemoryWatchdog : IDisposable
{
    private readonly long _budgetBytes;
    private readonly long _baselineBytes;
    private readonly Action _onTrip;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private long _peakGrowth;
    private int _tripped;
    private int _disposed;

    /// <summary>推論期間本行程用量比開始前多出的峰值。</summary>
    public long PeakGrowthBytes => Interlocked.Read(ref _peakGrowth);

    /// <summary>是否因為記憶體不足而中止。</summary>
    public bool Tripped => Volatile.Read(ref _tripped) != 0;

    /// <param name="budgetBytes">允許增加的用量上限。</param>
    /// <param name="onTrip">超標時呼叫（設 RunOptions.Terminate）。只會呼叫一次。</param>
    public MemoryWatchdog(long budgetBytes, Action onTrip)
    {
        _budgetBytes = budgetBytes;
        _onTrip = onTrip;
        _baselineBytes = CurrentUsage();
        _loop = Task.Run(Watch);
    }

    private async Task Watch()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                Sample();
                await Task.Delay(150, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Sample()
    {
        var growth = CurrentUsage() - _baselineBytes;
        if (growth > Interlocked.Read(ref _peakGrowth)) Interlocked.Exchange(ref _peakGrowth, growth);

        var available = (long)SystemMemory.AvailablePhysicalBytes;
        var overBudget = growth > _budgetBytes;
        var machineStarving = available > 0 && available < InferenceBudget.SafetyFloorBytes;
        if (!overBudget && !machineStarving) return;

        if (Interlocked.Exchange(ref _tripped, 1) == 0) _onTrip();
    }

    private static long CurrentUsage()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            return self.WorkingSet64;
        }
        catch (InvalidOperationException) { return 0; }
    }

    /// <summary>停止監看並抓最後一次峰值。可重複呼叫（成功路徑會先停再讀峰值，之後 using 再 Dispose 一次）。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Sample(); // 收工前再抓一次峰值：短推論可能整段都在兩次取樣之間
        _stop.Cancel();
        try { _loop.Wait(1000); } catch (AggregateException) { }
        _stop.Dispose();
    }
}
