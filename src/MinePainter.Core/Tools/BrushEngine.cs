using System.Buffers;
using MinePainter.Core.Tiles;
using SkiaSharp;

namespace MinePainter.Core.Tools;

public sealed record BrushSettings
{
    public float Radius { get; set; } = 4f;        // doc px（直徑 8）
    public float Hardness { get; set; } = 0.8f;    // 0..1
    public float Opacity { get; set; } = 1f;       // 0..1（整劃）
    public float Smoothing { get; set; } = 50f;    // 0..100 手抖穩定強度（螢幕空間，見 BrushEngine）
}

/// <summary>
/// 膠囊（capsule）筆刷：每一段輸入線段解析地蓋一個「兩端圓頭的線段」覆蓋度，
/// 相鄰段以 max 合成 = 圓角接合的折線（等同 paint.net 用幾何路徑描邊的結果）。
/// 沒有 dab 間距、沒有整數吸附，邊緣是連續的次像素面積覆蓋，不會抖動也不會出現扇貝邊。
/// 產出寫進 StrokeBuffer 的遮罩；顏色與不透明度在合成/commit 時才套用。
///
/// 輸入端另做兩層平滑，解決「畫布縮小時畫的線放大看是樓梯」的問題
/// （滑鼠是整數螢幕像素，縮到 25% 時每一步就是 4 個文件像素）：
/// 1. 手抖穩定（<see cref="Stabilize"/>，可調強度）：兩段式濾波，
///    先拉繩（lazy brush：筆尖被長度 L 的繩子拉著走，繩長內的晃動不落筆），
///    再距離域指數平滑（每前進一步以 1−e^(−step/L) 的比例追上）。
///    兩段各自只有 L 的固定滯後、與速度無關；對垂直行進方向的手抖衰減遠強於單段或方框平均
///    （方框平均會共振：窗長剛好等於手抖週期才有效）。
/// 2. 路徑窗移動平均（<see cref="SmoothingWindow"/>）：控制點 = 最近一小段路徑內採樣的平均，
///    專門吃掉整數螢幕座標造成的樓梯；窗只有三個螢幕像素，快速揮筆時自然退化成不平滑。
/// 3. 向心 Catmull-Rom 曲線：筆劃沿通過控制點的平滑曲線蓋章，而不是直線折線。
///    曲線段要等下一個點進來才能定形，所以落筆比游標晚一個採樣；PointerUp 用 EndStroke 補完。
/// </summary>
public sealed class BrushEngine
{
    private readonly List<SKPoint> _points = new(8); // 平滑後的控制點（只留最後幾個）
    private readonly List<SKPoint> _raw = new(16);   // 路徑窗內的（穩定後）採樣
    private SKPoint _rope;   // 拉繩筆尖
    private SKPoint _ema;    // 指數平滑輸出
    private SKPoint _lastInput;
    private bool _active;

    /// <summary>
    /// 手抖穩定長度 L（doc px）：拉繩繩長 = 指數平滑距離常數。0 = 關閉。
    /// </summary>
    public float Stabilize { get; set; }

    /// <summary>
    /// 樓梯平滑的路徑窗長度（doc px）。0 = 關閉（每個採樣直接當控制點）。
    /// </summary>
    public float SmoothingWindow { get; set; }

    /// <summary>
    /// 開始一劃；回傳首個點的 dirty 範圍（doc 座標）。
    /// clip = 選取遮罩（可 null）；bounds = 畫布範圍，超出的部分不落筆。
    /// </summary>
    public SKRectI BeginStroke(SKPoint p, StrokeBuffer buffer, BrushSettings settings,
        MaskSurface? clip = null, SKRectI? bounds = null)
    {
        _points.Clear();
        _raw.Clear();
        _points.Add(p);
        _raw.Add(p);
        _rope = _ema = _lastInput = p;
        _active = true;
        return StampSegment(p, p, buffer, settings, clip, bounds);
    }

    /// <summary>延續一劃到新採樣點；回傳本次實際蓋章的 dirty 範圍（可能為 Empty）。</summary>
    public SKRectI ContinueStroke(SKPoint p, StrokeBuffer buffer, BrushSettings settings,
        MaskSurface? clip = null, SKRectI? bounds = null)
    {
        if (!_active) return SKRectI.Empty;
        if (_lastInput == p) return SKRectI.Empty;
        var stabilized = StabilizeInput(p);
        if (_raw[^1] == stabilized) return SKRectI.Empty;
        return AddControlPoint(Smooth(stabilized), buffer, settings, clip, bounds);
    }

    /// <summary>兩段式手抖穩定：拉繩 → 距離域指數平滑。</summary>
    private SKPoint StabilizeInput(SKPoint p)
    {
        var step = SKPoint.Distance(p, _lastInput);
        _lastInput = p;
        var len = Stabilize;
        if (len <= 0f)
        {
            _rope = _ema = p;
            return p;
        }

        var dx = p.X - _rope.X;
        var dy = p.Y - _rope.Y;
        var dist = MathF.Sqrt(dx * dx + dy * dy);
        if (dist > len)
        {
            var k = (dist - len) / dist;
            _rope = new SKPoint(_rope.X + dx * k, _rope.Y + dy * k);
        }

        var a = 1f - MathF.Exp(-step / len);
        _ema = new SKPoint(_ema.X + (_rope.X - _ema.X) * a, _ema.Y + (_rope.Y - _ema.Y) * a);
        return _ema;
    }

    /// <summary>把新採樣放進路徑窗，回傳窗內所有採樣的平均。</summary>
    private SKPoint Smooth(SKPoint p)
    {
        _raw.Add(p);
        var window = SmoothingWindow;
        if (window <= 0f)
        {
            _raw.RemoveRange(0, _raw.Count - 1);
            return p;
        }

        // 從尾端往回累積路徑長度，超出窗的舊點丟掉
        var keepFrom = _raw.Count - 1;
        var acc = 0f;
        for (var i = _raw.Count - 1; i > 0; i--)
        {
            acc += SKPoint.Distance(_raw[i], _raw[i - 1]);
            if (acc > window) break;
            keepFrom = i - 1;
        }
        if (keepFrom > 0) _raw.RemoveRange(0, keepFrom);

        float sx = 0f, sy = 0f;
        foreach (var q in _raw) { sx += q.X; sy += q.Y; }
        return new SKPoint(sx / _raw.Count, sy / _raw.Count);
    }

    /// <summary>
    /// 結束一劃：把游標最後位置補進曲線，蓋完尚未定形的尾段。回傳 dirty 範圍。
    /// </summary>
    public SKRectI EndStroke(SKPoint p, StrokeBuffer buffer, BrushSettings settings,
        MaskSurface? clip = null, SKRectI? bounds = null)
    {
        if (!_active) return SKRectI.Empty;
        _active = false;

        var dirty = AddControlPoint(p, buffer, settings, clip, bounds);

        // 尾段 P[n-2] → P[n-1]：P[n] 用 P[n-1] 往前反射
        var n = _points.Count;
        if (n >= 2)
        {
            var p0 = n >= 3 ? _points[n - 3] : Reflect(_points[n - 2], _points[n - 1]);
            var p1 = _points[n - 2];
            var p2 = _points[n - 1];
            var p3 = Reflect(p2, p1);
            dirty = Union(dirty, StampCurve(p0, p1, p2, p3, buffer, settings, clip, bounds));
        }
        _points.Clear();
        _raw.Clear();
        return dirty;
    }

    private SKRectI AddControlPoint(SKPoint p, StrokeBuffer buffer, BrushSettings settings,
        MaskSurface? clip, SKRectI? bounds)
    {
        if (_points.Count > 0 && _points[^1] == p) return SKRectI.Empty;
        _points.Add(p);
        if (_points.Count > 4) _points.RemoveAt(0);

        // 有 P[n-3], P[n-2], P[n-1] 三點後才能蓋 P[n-3] → P[n-2]（前一點若不存在用反射）
        var n = _points.Count;
        if (n < 3) return SKRectI.Empty;
        var p1 = _points[n - 3];
        var p2 = _points[n - 2];
        var p0 = n >= 4 ? _points[n - 4] : Reflect(p1, p2);
        var p3 = _points[n - 1];
        return StampCurve(p0, p1, p2, p3, buffer, settings, clip, bounds);
    }

    /// <summary>a 相對 b 的反射點（a − (b − a)），當作曲線端點的虛擬鄰居，讓端點切線順著線段方向。</summary>
    private static SKPoint Reflect(SKPoint a, SKPoint b) => new(a.X * 2f - b.X, a.Y * 2f - b.Y);

    private static SKRectI Union(SKRectI a, SKRectI b) =>
        a.IsEmpty ? b : b.IsEmpty ? a : SKRectI.Union(a, b);

    /// <summary>
    /// 沿向心 Catmull-Rom 曲線 P1→P2 蓋章：曲線切成短直段（每段約 radius/4，最少 1px），
    /// 相鄰短段以 max 合成，接合處不留痕。
    /// </summary>
    private static SKRectI StampCurve(SKPoint p0, SKPoint p1, SKPoint p2, SKPoint p3,
        StrokeBuffer buffer, BrushSettings settings, MaskSurface? clip, SKRectI? bounds)
    {
        var chord = SKPoint.Distance(p1, p2);
        var step = Math.Max(1f, Math.Max(0.5f, settings.Radius) * 0.25f);
        var pieces = (int)Math.Clamp(MathF.Ceiling(chord / step), 1, 64);
        if (pieces == 1)
            return StampSegment(p1, p2, buffer, settings, clip, bounds);

        // 向心參數化（alpha = 0.5）：採樣不等距也不會打結或超調
        var t0 = 0f;
        var t1 = t0 + Knot(p0, p1);
        var t2 = t1 + Knot(p1, p2);
        var t3 = t2 + Knot(p2, p3);

        var dirty = SKRectI.Empty;
        var prev = p1;
        for (var i = 1; i <= pieces; i++)
        {
            var t = t1 + (t2 - t1) * i / pieces;
            var next = i == pieces ? p2 : CatmullRom(p0, p1, p2, p3, t0, t1, t2, t3, t);
            dirty = Union(dirty, StampSegment(prev, next, buffer, settings, clip, bounds));
            prev = next;
        }
        return dirty;
    }

    private static float Knot(SKPoint a, SKPoint b) =>
        Math.Max(MathF.Sqrt(SKPoint.Distance(a, b)), 1e-3f);

    /// <summary>Barry–Goldman 金字塔式求值。</summary>
    private static SKPoint CatmullRom(SKPoint p0, SKPoint p1, SKPoint p2, SKPoint p3,
        float t0, float t1, float t2, float t3, float t)
    {
        var a1 = Lerp(p0, p1, (t - t0) / (t1 - t0));
        var a2 = Lerp(p1, p2, (t - t1) / (t2 - t1));
        var a3 = Lerp(p2, p3, (t - t2) / (t3 - t2));
        var b1 = Lerp(a1, a2, (t - t0) / (t2 - t0));
        var b2 = Lerp(a2, a3, (t - t1) / (t3 - t1));
        return Lerp(b1, b2, (t - t1) / (t2 - t1));
    }

    private static SKPoint Lerp(SKPoint a, SKPoint b, float t) =>
        new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    /// <summary>
    /// 單一像素在半徑 r、距筆劃中心線 d 時的覆蓋度（0..1）。
    /// 外緣：1px 寬的面積覆蓋率近似（半徑 − 距離 + 0.5，與 Skia 的 AA 同型）；
    /// 硬度 h：距離 ≤ h·r 全實，之後 smoothstep 衰減到 r。
    /// </summary>
    public static float Coverage(float d, float radius, float hardness)
    {
        var edge = Math.Clamp(radius - d + 0.5f, 0f, 1f);
        if (edge <= 0f) return 0f;
        if (hardness >= 1f) return edge;

        var hardRadius = radius * hardness;
        if (d <= hardRadius) return edge;
        var falloff = Math.Max(radius - hardRadius, 0.01f);
        var t = Math.Clamp((d - hardRadius) / falloff, 0f, 1f);
        var soft = 1f - t * t * (3f - 2f * t);
        return soft * edge;
    }

    private static SKRectI StampSegment(SKPoint a, SKPoint b, StrokeBuffer buffer, BrushSettings settings,
        MaskSurface? clip, SKRectI? bounds)
    {
        var radius = Math.Max(0.5f, settings.Radius);
        var hardness = Math.Clamp(settings.Hardness, 0f, 1f);

        var left = (int)MathF.Floor(Math.Min(a.X, b.X) - radius - 1f);
        var top = (int)MathF.Floor(Math.Min(a.Y, b.Y) - radius - 1f);
        var right = (int)MathF.Ceiling(Math.Max(a.X, b.X) + radius + 1f);
        var bottom = (int)MathF.Ceiling(Math.Max(a.Y, b.Y) + radius + 1f);
        var rect = new SKRectI(left, top, right, bottom);
        if (bounds is { } limit) rect = SKRectI.Intersect(rect, limit);
        if (rect.Width <= 0 || rect.Height <= 0) return SKRectI.Empty;

        var w = rect.Width;
        var h = rect.Height;
        var pool = ArrayPool<byte>.Shared;
        var mask = pool.Rent(w * h);
        try
        {
            var abx = b.X - a.X;
            var aby = b.Y - a.Y;
            var len2 = abx * abx + aby * aby;
            var any = false;

            for (var y = 0; y < h; y++)
            {
                var py = rect.Top + y + 0.5f;
                var row = y * w;
                for (var x = 0; x < w; x++)
                {
                    var px = rect.Left + x + 0.5f;
                    // 點到線段的距離
                    float dx, dy;
                    if (len2 <= 1e-6f)
                    {
                        dx = px - a.X;
                        dy = py - a.Y;
                    }
                    else
                    {
                        var t = ((px - a.X) * abx + (py - a.Y) * aby) / len2;
                        t = Math.Clamp(t, 0f, 1f);
                        dx = px - (a.X + abx * t);
                        dy = py - (a.Y + aby * t);
                    }
                    var d = MathF.Sqrt(dx * dx + dy * dy);
                    var c = Coverage(d, radius, hardness);
                    var value = (byte)(c * 255f + 0.5f);
                    mask[row + x] = value;
                    any |= value != 0;
                }
            }

            if (!any) return SKRectI.Empty;
            buffer.Mask.StampMax(new ReadOnlySpan<byte>(mask, 0, w * h), w, h, new SKPointI(rect.Left, rect.Top), clip, bounds);
            return rect;
        }
        finally
        {
            pool.Return(mask);
        }
    }
}
