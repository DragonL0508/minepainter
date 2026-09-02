using SkiaSharp;

namespace MinePainter.Core.Tools;

/// <summary>
/// 四角變形（透視／扭曲）的純幾何：四角順序固定為 0=左上 1=右上 2=右下 3=左下
/// （與 <see cref="MoveTool.HandlePoints"/> 的角把手同序，邊把手 4..7 = 上右下左）。
/// 映射用 3×3 單應矩陣（SKMatrix 含 Persp0/1/2），Skia 的 DrawImage／Concat／MapRect 都直接吃。
/// </summary>
public static class QuadGeometry
{
    public static SKPoint[] Corners(SKRect r) =>
    [
        new(r.Left, r.Top), new(r.Right, r.Top),
        new(r.Right, r.Bottom), new(r.Left, r.Bottom),
    ];

    public static SKRect Bounds(ReadOnlySpan<SKPoint> q)
    {
        float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, b = float.MinValue;
        foreach (var p in q)
        {
            l = Math.Min(l, p.X); t = Math.Min(t, p.Y);
            r = Math.Max(r, p.X); b = Math.Max(b, p.Y);
        }
        return q.Length == 0 ? SKRect.Empty : new SKRect(l, t, r, b);
    }

    public static SKPoint Center(ReadOnlySpan<SKPoint> q)
    {
        var b = Bounds(q);
        return new SKPoint(b.MidX, b.MidY);
    }

    /// <summary>邊把手位置（4=上 5=右 6=下 7=左 的中點）。</summary>
    public static SKPoint[] EdgeMidpoints(ReadOnlySpan<SKPoint> q) =>
    [
        Mid(q[0], q[1]), Mid(q[1], q[2]), Mid(q[2], q[3]), Mid(q[3], q[0]),
    ];

    /// <summary>四角＋四邊中點（索引與 <see cref="MoveTool.HandlePoints"/> 同序）。</summary>
    public static SKPoint[] HandlePoints(ReadOnlySpan<SKPoint> q)
    {
        var e = EdgeMidpoints(q);
        return [q[0], q[1], q[2], q[3], e[0], e[1], e[2], e[3]];
    }

    private static SKPoint Mid(SKPoint a, SKPoint b) => new((a.X + b.X) / 2f, (a.Y + b.Y) / 2f);

    /// <summary>把手命中測試（角優先）；未命中 -1。</summary>
    public static int HitHandle(ReadOnlySpan<SKPoint> q, SKPoint p, float tolerance, bool includeEdges)
    {
        var handles = HandlePoints(q);
        var count = includeEdges ? 8 : 4;
        for (var i = 0; i < count; i++)
        {
            if (Math.Abs(p.X - handles[i].X) <= tolerance && Math.Abs(p.Y - handles[i].Y) <= tolerance)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// 嚴格凸四邊形（同向、不自交、面積夠大）—— 單應矩陣對凹／翻面的四邊形會把影像折疊起來，
    /// 拖曳時不接受這種目標，停在上一個合法狀態。
    /// </summary>
    public static bool IsConvex(ReadOnlySpan<SKPoint> q, float minEdge = 1f)
    {
        if (q.Length != 4) return false;
        var sign = 0;
        for (var i = 0; i < 4; i++)
        {
            var a = q[i];
            var b = q[(i + 1) % 4];
            var c = q[(i + 2) % 4];
            var ex = b.X - a.X;
            var ey = b.Y - a.Y;
            if (ex * ex + ey * ey < minEdge * minEdge) return false;
            var cross = ex * (c.Y - b.Y) - ey * (c.X - b.X);
            if (Math.Abs(cross) < 1e-3f) return false;
            var s = Math.Sign(cross);
            if (sign == 0) sign = s;
            else if (s != sign) return false;
        }
        return true;
    }

    /// <summary>凸四邊形的點包含測試。</summary>
    public static bool Contains(ReadOnlySpan<SKPoint> q, SKPoint p)
    {
        if (q.Length != 4) return false;
        var sign = 0;
        for (var i = 0; i < 4; i++)
        {
            var a = q[i];
            var b = q[(i + 1) % 4];
            var cross = (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
            var s = Math.Sign(cross);
            if (s == 0) continue;
            if (sign == 0) sign = s;
            else if (s != sign) return false;
        }
        return true;
    }

    public static SKPoint[] Translated(ReadOnlySpan<SKPoint> q, float dx, float dy)
    {
        var r = new SKPoint[q.Length];
        for (var i = 0; i < q.Length; i++) r[i] = new SKPoint(q[i].X + dx, q[i].Y + dy);
        return r;
    }

    public static SKPoint[] Rotated(ReadOnlySpan<SKPoint> q, SKPoint center, float deg)
    {
        var r = new SKPoint[q.Length];
        if (Math.Abs(deg) < 0.001f)
        {
            q.CopyTo(r);
            return r;
        }
        var m = SKMatrix.CreateRotationDegrees(deg, center.X, center.Y);
        for (var i = 0; i < q.Length; i++) r[i] = m.MapPoint(q[i]);
        return r;
    }

    public static bool NearlyEqual(ReadOnlySpan<SKPoint> a, ReadOnlySpan<SKPoint> b, float eps = 0.01f)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
        {
            if (Math.Abs(a[i].X - b[i].X) > eps || Math.Abs(a[i].Y - b[i].Y) > eps) return false;
        }
        return true;
    }

    /// <summary>q 是不是 start 整體平移了一個整數向量（四角位移一致）。</summary>
    public static bool IsIntegerTranslationOf(ReadOnlySpan<SKPoint> q, ReadOnlySpan<SKPoint> start, out SKPointI delta)
    {
        delta = SKPointI.Empty;
        if (q.Length != start.Length || q.Length == 0) return false;
        var dx = q[0].X - start[0].X;
        var dy = q[0].Y - start[0].Y;
        for (var i = 1; i < q.Length; i++)
        {
            if (Math.Abs(q[i].X - start[i].X - dx) > 0.01f || Math.Abs(q[i].Y - start[i].Y - dy) > 0.01f)
                return false;
        }
        var rx = MathF.Round(dx);
        var ry = MathF.Round(dy);
        if (Math.Abs(dx - rx) > 0.001f || Math.Abs(dy - ry) > 0.001f) return false;
        delta = new SKPointI((int)rx, (int)ry);
        return true;
    }

    /// <summary>
    /// 單位正方形 (0,0)(1,0)(1,1)(0,1) → 四邊形 的單應矩陣（Heckbert 的閉式解）。
    /// </summary>
    public static SKMatrix SquareToQuad(ReadOnlySpan<SKPoint> q)
    {
        float x0 = q[0].X, y0 = q[0].Y, x1 = q[1].X, y1 = q[1].Y;
        float x2 = q[2].X, y2 = q[2].Y, x3 = q[3].X, y3 = q[3].Y;

        var dx1 = x1 - x2; var dx2 = x3 - x2; var dx3 = x0 - x1 + x2 - x3;
        var dy1 = y1 - y2; var dy2 = y3 - y2; var dy3 = y0 - y1 + y2 - y3;

        float a, b, c, d, e, f, g, h;
        if (Math.Abs(dx3) < 1e-6f && Math.Abs(dy3) < 1e-6f)
        {
            // 平行四邊形：純仿射
            a = x1 - x0; b = x2 - x1; c = x0;
            d = y1 - y0; e = y2 - y1; f = y0;
            g = 0f; h = 0f;
        }
        else
        {
            var det = dx1 * dy2 - dx2 * dy1;
            if (Math.Abs(det) < 1e-9f) det = det < 0 ? -1e-9f : 1e-9f;
            g = (dx3 * dy2 - dx2 * dy3) / det;
            h = (dx1 * dy3 - dx3 * dy1) / det;
            a = x1 - x0 + g * x1; b = x3 - x0 + h * x3; c = x0;
            d = y1 - y0 + g * y1; e = y3 - y0 + h * y3; f = y0;
        }

        // SKMatrix 欄位順序：ScaleX SkewX TransX / SkewY ScaleY TransY / Persp0 Persp1 Persp2
        return new SKMatrix(a, b, c, d, e, f, g, h, 1f);
    }

    /// <summary>四邊形 a → 四邊形 b 的單應矩陣（a 退化時回 identity）。</summary>
    public static SKMatrix QuadToQuad(ReadOnlySpan<SKPoint> a, ReadOnlySpan<SKPoint> b)
    {
        var toA = SquareToQuad(a);
        if (!toA.TryInvert(out var fromA)) return SKMatrix.Identity;
        // Concat(first, second)：先套 second 再套 first —— 先 a→正方形，再 正方形→b
        return SKMatrix.Concat(SquareToQuad(b), fromA);
    }

    /// <summary>矩形 → 四邊形。</summary>
    public static SKMatrix RectToQuad(SKRect src, ReadOnlySpan<SKPoint> quad) => QuadToQuad(Corners(src), quad);

    /// <summary>
    /// Photoshop「透視」：拖一個角時，同一條水平邊上的鄰角沿邊反向動同樣的量、
    /// 同一條垂直邊上的鄰角沿邊反向動同樣的量 —— 框永遠保持左右／上下對稱的梯形。
    /// 以起始四邊形自己的邊向量為軸（框旋轉過也對）。
    /// </summary>
    public static SKPoint[] PerspectiveDrag(ReadOnlySpan<SKPoint> start, int corner, SKPoint delta)
    {
        var q = start.ToArray();
        if (corner is < 0 or > 3) return q;

        // 局部軸：u = 上邊方向、v = 左邊方向（單位向量）
        var u = Unit(new SKPoint(start[1].X - start[0].X, start[1].Y - start[0].Y));
        var v = Unit(new SKPoint(start[3].X - start[0].X, start[3].Y - start[0].Y));
        var du = delta.X * u.X + delta.Y * u.Y;
        var dv = delta.X * v.X + delta.Y * v.Y;

        var h = corner switch { 0 => 1, 1 => 0, 2 => 3, _ => 2 }; // 水平鄰角
        var vv = corner switch { 0 => 3, 1 => 2, 2 => 1, _ => 0 }; // 垂直鄰角

        q[corner] = new SKPoint(start[corner].X + delta.X, start[corner].Y + delta.Y);
        q[h] = new SKPoint(start[h].X - du * u.X, start[h].Y - du * u.Y);
        q[vv] = new SKPoint(start[vv].X - dv * v.X, start[vv].Y - dv * v.Y);
        return q;
    }

    /// <summary>
    /// Photoshop「扭曲」：角把手各自自由拖曳；邊把手把整條邊（兩端點）一起平移。
    /// constrain（Shift）＝只沿起始框的水平或垂直軸動（取較大的分量）。
    /// </summary>
    public static SKPoint[] DistortDrag(ReadOnlySpan<SKPoint> start, int handle, SKPoint delta, bool constrain)
    {
        var q = start.ToArray();
        if (handle is < 0 or > 7) return q;

        if (constrain)
        {
            var u = Unit(new SKPoint(start[1].X - start[0].X, start[1].Y - start[0].Y));
            var v = Unit(new SKPoint(start[3].X - start[0].X, start[3].Y - start[0].Y));
            var du = delta.X * u.X + delta.Y * u.Y;
            var dv = delta.X * v.X + delta.Y * v.Y;
            delta = Math.Abs(du) >= Math.Abs(dv)
                ? new SKPoint(du * u.X, du * u.Y)
                : new SKPoint(dv * v.X, dv * v.Y);
        }

        if (handle < 4)
        {
            q[handle] = new SKPoint(start[handle].X + delta.X, start[handle].Y + delta.Y);
            return q;
        }

        var (i, j) = (handle - 4) switch { 0 => (0, 1), 1 => (1, 2), 2 => (2, 3), _ => (3, 0) };
        q[i] = new SKPoint(start[i].X + delta.X, start[i].Y + delta.Y);
        q[j] = new SKPoint(start[j].X + delta.X, start[j].Y + delta.Y);
        return q;
    }

    private static SKPoint Unit(SKPoint v)
    {
        var len = MathF.Sqrt(v.X * v.X + v.Y * v.Y);
        return len < 1e-6f ? new SKPoint(1, 0) : new SKPoint(v.X / len, v.Y / len);
    }
}
