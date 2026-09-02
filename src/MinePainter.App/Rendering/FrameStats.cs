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

    public void OnFrame()
    {
        FrameIndex++;
        var now = _clock.ElapsedTicks;
        if (_lastTicks != 0)
        {
            var dt = (now - _lastTicks) / (double)Stopwatch.Frequency;
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
