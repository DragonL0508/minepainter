using SkiaSharp;

namespace MinePainter.Core.Tools;

/// <summary>
/// 彎曲變形（Photoshop「彎曲」）的網格：一張 4×4 控制點的雙三次貝茲曲面，
/// 把 <see cref="Frame"/>（平的矩形）映射到曲面上。控制點列主序：索引 = row*4 + col，
/// col 沿水平（u）、row 沿垂直（v）；四角 = 0,3,12,15，其餘 8 個邊點是切線把手、4 個內點控制內部。
/// immutable：每次改動換新實例（render thread 直接讀）。
/// </summary>
public sealed record WarpMesh(SKPoint[] Points, SKRect Frame)
{
    public const int Subdivisions = 32;

    /// <summary>平的網格（＝identity）：控制點均勻落在矩形上。</summary>
    public static WarpMesh Flat(SKRect frame)
    {
        var pts = new SKPoint[16];
        for (var r = 0; r < 4; r++)
        for (var c = 0; c < 4; c++)
            pts[r * 4 + c] = new SKPoint(frame.Left + frame.Width * c / 3f, frame.Top + frame.Height * r / 3f);
        return new WarpMesh(pts, frame);
    }

    public static bool IsCorner(int index) => index is 0 or 3 or 12 or 15;

    /// <summary>四角相鄰的兩個切線把手索引。</summary>
    public static (int A, int B) CornerHandles(int corner) => corner switch
    {
        0 => (1, 4),
        3 => (2, 7),
        12 => (13, 8),
        _ => (14, 11),
    };

    public SKRect Bounds => QuadGeometry.Bounds(Points);

    public bool IsFlat
    {
        get
        {
            var flat = Flat(Frame);
            return QuadGeometry.NearlyEqual(Points, flat.Points, 0.01f);
        }
    }

    public WarpMesh WithPoint(int index, SKPoint p)
    {
        var next = (SKPoint[])Points.Clone();
        next[index] = p;
        return this with { Points = next };
    }

    /// <summary>
    /// 拖控制點：角點帶著它的兩個切線把手一起走（PS 行為），其他點各自獨立。
    /// 一律從起始網格換算（不累積）。
    /// </summary>
    public static WarpMesh Drag(WarpMesh start, int index, SKPoint delta)
    {
        var next = (SKPoint[])start.Points.Clone();
        void Move(int i) => next[i] = new SKPoint(start.Points[i].X + delta.X, start.Points[i].Y + delta.Y);
        Move(index);
        if (IsCorner(index))
        {
            var (a, b) = CornerHandles(index);
            Move(a);
            Move(b);
        }
        return start with { Points = next };
    }

    public WarpMesh Translated(float dx, float dy) => this with { Points = QuadGeometry.Translated(Points, dx, dy) };

    public WarpMesh Rotated(SKPoint center, float deg) => this with { Points = QuadGeometry.Rotated(Points, center, deg) };

    /// <summary>控制點命中（角優先）；-1 = 無。</summary>
    public int HitPoint(SKPoint p, float tolerance)
    {
        foreach (var i in new[] { 0, 3, 12, 15 })
        {
            if (Math.Abs(p.X - Points[i].X) <= tolerance && Math.Abs(p.Y - Points[i].Y) <= tolerance) return i;
        }
        for (var i = 0; i < 16; i++)
        {
            if (IsCorner(i)) continue;
            if (Math.Abs(p.X - Points[i].X) <= tolerance && Math.Abs(p.Y - Points[i].Y) <= tolerance) return i;
        }
        return -1;
    }

    /// <summary>曲面上 (u,v)∈[0,1]² 的點。</summary>
    public SKPoint Evaluate(float u, float v)
    {
        Span<float> bu = stackalloc float[4];
        Span<float> bv = stackalloc float[4];
        Bernstein(u, bu);
        Bernstein(v, bv);
        float x = 0, y = 0;
        for (var r = 0; r < 4; r++)
        for (var c = 0; c < 4; c++)
        {
            var w = bu[c] * bv[r];
            var p = Points[r * 4 + c];
            x += w * p.X;
            y += w * p.Y;
        }
        return new SKPoint(x, y);
    }

    private static void Bernstein(float t, Span<float> b)
    {
        var s = 1 - t;
        b[0] = s * s * s;
        b[1] = 3 * t * s * s;
        b[2] = 3 * t * t * s;
        b[3] = t * t * t;
    }

    /// <summary>畫在畫布上的 3×3 網格線（曲面上 u,v = 0,1/3,2/3,1 的曲線）。</summary>
    public SKPath GridPath(int segments = 16)
    {
        var path = new SKPath();
        for (var k = 0; k <= 3; k++)
        {
            var t = k / 3f;
            path.MoveTo(Evaluate(0, t));
            for (var i = 1; i <= segments; i++) path.LineTo(Evaluate(i / (float)segments, t));
            path.MoveTo(Evaluate(t, 0));
            for (var i = 1; i <= segments; i++) path.LineTo(Evaluate(t, i / (float)segments));
        }
        return path;
    }

    /// <summary>
    /// 把一張原始像素（位於 <paramref name="srcBounds"/>，經 <paramref name="pixelMatrix"/> 落在 Frame 內）
    /// 沿曲面畫出來：曲面細分成三角形網格，貼圖座標對回原始像素（Decal：影像外透明，
    /// 群組裡比框小的圖層不會被邊緣像素拉成一片）。canvas 已在 doc 座標。
    /// </summary>
    public void Draw(SKCanvas canvas, SKImage image, SKRectI srcBounds, SKMatrix pixelMatrix, SKFilterQuality quality)
    {
        if (!pixelMatrix.TryInvert(out var inverse)) return;
        const int n = Subdivisions;
        var count = (n + 1) * (n + 1);
        var positions = new SKPoint[count];
        var texs = new SKPoint[count];
        for (var j = 0; j <= n; j++)
        for (var i = 0; i <= n; i++)
        {
            var u = i / (float)n;
            var v = j / (float)n;
            var idx = j * (n + 1) + i;
            positions[idx] = Evaluate(u, v);
            var flat = new SKPoint(Frame.Left + Frame.Width * u, Frame.Top + Frame.Height * v);
            var src = inverse.MapPoint(flat);
            texs[idx] = new SKPoint(src.X - srcBounds.Left, src.Y - srcBounds.Top);
        }

        var indices = new ushort[n * n * 6];
        var k = 0;
        for (var j = 0; j < n; j++)
        for (var i = 0; i < n; i++)
        {
            var a = (ushort)(j * (n + 1) + i);
            var b = (ushort)(a + 1);
            var c = (ushort)(a + n + 1);
            var d = (ushort)(c + 1);
            indices[k++] = a; indices[k++] = b; indices[k++] = c;
            indices[k++] = b; indices[k++] = d; indices[k++] = c;
        }

        using var vertices = SKVertices.CreateCopy(SKVertexMode.Triangles, positions, texs, null, indices);
        using var shader = image.ToShader(SKShaderTileMode.Decal, SKShaderTileMode.Decal);
        using var paint = new SKPaint { Shader = shader, FilterQuality = quality, IsAntialias = false };
        canvas.DrawVertices(vertices, SKBlendMode.Modulate, paint);
    }
}
