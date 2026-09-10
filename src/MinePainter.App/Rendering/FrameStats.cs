using System.Diagnostics;

namespace MinePainter.App.Rendering;

/// <summary>
/// 由 render thread 在每幀呼叫 OnFrame()，計算滑動平均 FPS。
/// 只有 render thread 寫入；其他執行緒讀 Fps 屬性（近似值即可）。
/// </summary>
public sealed class FrameStats
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly double[] _samples = new double[60];
    private int _cursor;
    private int _filled;
    private long _lastTicks;
    private double _fps;

    public double Fps => _fps;
    public long FrameIndex { get; private set; }

    /// <summary>上一幀尚未合成完成的 tile 數（狀態列顯示用）。</summary>
    public int PendingTiles { get; set; }

    private double _maxGap;

    /// <summary>自上次讀取以來最長的一次幀間隔（毫秒；讀了就歸零）。找「偶發停頓」用，平均 fps 看不出來。</summary>
    public double TakeMaxGapMs()
    {
        var v = _maxGap;
        _maxGap = 0;
        return v * 1000;
    }

    // ---- 幀內成本（MINEPAINTER_DEBUG_PERF_FRAMES 用；render thread 寫、UI 執行緒每秒讀一次）----
    private double _lockWaitMs, _drawMs, _maxLockWaitMs, _maxDrawMs;
    private int _gpuFrames, _tileFrames;

    /// <summary>畫布 draw op 記一幀：等文件鎖花的毫秒、整幀畫完的毫秒、走的是 GPU 圖層樹還是合成器 tile。</summary>
    public void RecordFrameCost(double lockWaitMs, double drawMs, bool gpu)
    {
        _lockWaitMs += lockWaitMs;
        _drawMs += drawMs;
        if (lockWaitMs > _maxLockWaitMs) _maxLockWaitMs = lockWaitMs;
        if (drawMs > _maxDrawMs) _maxDrawMs = drawMs;
        if (gpu) _gpuFrames++; else _tileFrames++;
    }

    /// <summary>取走自上次讀取以來的幀內成本統計並歸零。</summary>
    public (double LockWaitMs, double MaxLockWaitMs, double DrawMs, double MaxDrawMs, int GpuFrames, int TileFrames) TakeFrameCost()
    {
        var r = (_lockWaitMs, _maxLockWaitMs, _drawMs, _maxDrawMs, _gpuFrames, _tileFrames);
        _lockWaitMs = _drawMs = _maxLockWaitMs = _maxDrawMs = 0;
        _gpuFrames = _tileFrames = 0;
        return r;
    }

    public void OnFrame()
    {
        FrameIndex++;
        var now = _clock.ElapsedTicks;
        if (_lastTicks != 0)
        {
            var dt = (now - _lastTicks) / (double)Stopwatch.Frequency;
            if (dt > _maxGap) _maxGap = dt;
            _samples[_cursor] = dt;
            _cursor = (_cursor + 1) % _samples.Length;
            _filled = Math.Min(_filled + 1, _samples.Length);

            double sum = 0;
            for (var i = 0; i < _filled; i++) sum += _samples[i];
            if (sum > 0) _fps = _filled / sum;
        }
        _lastTicks = now;
    }
}
