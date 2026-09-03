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
