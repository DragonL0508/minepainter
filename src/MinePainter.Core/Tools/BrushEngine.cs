using System.Buffers;
using MinePainter.Core.Tiles;
using SkiaSharp;

namespace MinePainter.Core.Tools;

public sealed record BrushSettings
{
    public float Radius { get; set; } = 4f;        // doc px（直徑 8）
    public float Hardness { get; set; } = 0.8f;    // 0..1
    public float Opacity { get; set; } = 1f;       // 0..1（整劃）
}

/// <summary>
/// 膠囊（capsule）筆刷：每一段輸入線段解析地蓋一個「兩端圓頭的線段」覆蓋度，
/// 相鄰段以 max 合成 = 圓角接合的折線（等同 paint.net 用幾何路徑描邊的結果）。
/// 沒有 dab 間距、沒有整數吸附，邊緣是連續的次像素面積覆蓋，不會抖動也不會出現扇貝邊。
/// 產出寫進 StrokeBuffer 的遮罩；顏色與不透明度在合成/commit 時才套用。
/// </summary>
public sealed class BrushEngine
{
    private SKPoint _last;

    /// <summary>
    /// 開始一劃；回傳首個點的 dirty 範圍（doc 座標）。
    /// clip = 選取遮罩（可 null）；bounds = 畫布範圍，超出的部分不落筆。
    /// </summary>
    public SKRectI BeginStroke(SKPoint p, StrokeBuffer buffer, BrushSettings settings,
        MaskSurface? clip = null, SKRectI? bounds = null)
    {
        _last = p;
        return StampSegment(p, p, buffer, settings, clip, bounds);
    }

    /// <summary>延續一劃到新採樣點；回傳本段的 dirty 範圍（可能為 Empty）。</summary>
    public SKRectI ContinueStroke(SKPoint p, StrokeBuffer buffer, BrushSettings settings,
        MaskSurface? clip = null, SKRectI? bounds = null)
    {
        var from = _last;
        if (from == p) return SKRectI.Empty;
        _last = p;
        return StampSegment(from, p, buffer, settings, clip, bounds);
    }

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
