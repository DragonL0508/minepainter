using SkiaSharp;
using MinePainter.Core.Layers;
using static MinePainter.Core.Effects.EffectMath;

namespace MinePainter.Core.Effects;

/// <summary>兩色漸層取樣：給定漸層框、角度（或放射狀），回傳某像素的顏色（未預乘 SKColor）。</summary>
internal sealed class GradientRamp
{
    private readonly float _cx, _cy, _dx, _dy, _half, _maxR;
    private readonly bool _radial;
    private readonly SKColor[] _lut = new SKColor[257];

    public GradientRamp(SKRectI box, float angleDeg, bool radial, SKColor start, SKColor end)
        : this(box, angleDeg, radial, GradientStops.Two(start, end)) { }

    public GradientRamp(SKRectI box, float angleDeg, bool radial, GradientStops stops)
    {
        var bw = Math.Max(1, box.Width);
        var bh = Math.Max(1, box.Height);
        _cx = box.Left + bw / 2f;
        _cy = box.Top + bh / 2f;
        var rad = angleDeg * MathF.PI / 180f;
        _dx = MathF.Cos(rad);
        _dy = MathF.Sin(rad);
        _half = Math.Abs(_dx) * bw / 2f + Math.Abs(_dy) * bh / 2f;
        _maxR = MathF.Sqrt(bw * bw + bh * bh) / 2f;
        _radial = radial;
        for (var i = 0; i <= 256; i++) _lut[i] = stops.ColorAt(i / 256f);
    }

    public SKColor At(int x, int y)
    {
        var px = x + 0.5f - _cx;
        var py = y + 0.5f - _cy;
        float t;
        if (_radial) t = MathF.Sqrt(px * px + py * py) / Math.Max(1f, _maxR);
        else t = _half <= 0 ? 0.5f : (px * _dx + py * _dy) / (2 * _half) + 0.5f;
        return _lut[(int)(Math.Clamp(t, 0f, 1f) * 256)];
    }
}
