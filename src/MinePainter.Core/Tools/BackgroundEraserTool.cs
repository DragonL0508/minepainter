using System.Buffers;
using MinePainter.Core.History;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using SkiaSharp;

namespace MinePainter.Core.Tools;

/// <summary>去背筆的取樣方式（對應 Photoshop 背景橡皮擦的「取樣」）。</summary>
public enum BackgroundSampling
{
    /// <summary>連續：每個 dab 都以筆刷中心（熱點）當時的顏色為背景色。</summary>
    Continuous,
    /// <summary>一次：整劃只用落筆瞬間熱點的顏色。</summary>
    Once,
}

public sealed record BackgroundEraserSettings
{
    public float Radius { get; set; } = 20f;        // doc px
    public float Hardness { get; set; } = 1f;       // 筆刷邊緣硬度 0..1
    public byte Tolerance { get; set; } = 32;       // 與背景色的最大色差（0..255）
    /// <summary>柔邊 0..1：色差超過容許度後再多 Softness×容許度 的漸層帶（0 = 硬切）。</summary>
    public float Softness { get; set; } = 0.5f;
    public BackgroundSampling Sampling { get; set; } = BackgroundSampling.Continuous;
    /// <summary>限制「連續」：只擦與熱點相連的那一片相近色，不擦圈內孤立的相近色區。</summary>
    public bool Contiguous { get; set; } = true;
    /// <summary>保護前景色：與前景色相近的像素不擦；並用「背景色→前景色」的色差軸估計半透明邊緣。</summary>
    public bool ProtectForeground { get; set; }
}

/// <summary>
/// 去背筆（Photoshop「背景橡皮擦」的演算法）：
/// 筆刷中心是一個「熱點」，每個 dab 先取熱點顏色當背景色，圈內與背景色相近的像素才被擦掉、
/// 差很多的（= 前景物件）留著。所以只要沿著物件邊緣塗，背景會被吃掉而物件不受傷，
/// 這是它與普通橡皮擦最大的差別。
///
/// 每個像素的擦除量 = 筆刷覆蓋度 × 色差權重：
/// * 色差 ≤ 容許度 → 全擦；容許度～容許度×(1+柔邊) → smoothstep 漸弱；再遠 → 不擦。
///   漸層帶讓髮絲、毛邊這種「半是背景半是物件」的像素得到部分 alpha，而不是鋸齒硬邊。
/// * 保護前景色開啟時，額外沿「背景色→前景色」這條色軸投影（單軸色差鍵，
///   與去綠幕的 color-difference key 同型）：越靠近前景色越保留，等於一個輕量的 alpha 估計；
///   與前景色本身相近的像素完全不擦。
/// * 限制「連續」時只擦 dab 內與熱點四連通相連的像素（小範圍 flood）。
///
/// 擦除量寫進 StrokeBuffer（取 max，wash 語意），合成器即時預覽、PointerUp 才以 DstOut 烙進圖層，
/// 與橡皮擦共用同一條 commit / undo 路徑。
/// </summary>
public sealed class BackgroundEraserTool : ITool, IBrushCursorTool
{
    public string Name => "去背筆";

    public BackgroundEraserSettings Settings { get; } = new();

    public float CursorRadius => Settings.Radius;

    private readonly RestoreStroke _restore = new();
    private TileSnapshot? _beforeSnapshot;
    private RasterLayer? _targetLayer;
    private bool _strokeActive;
    private SKPoint _last;
    private float _carry; // 上一段沒用完的 dab 間距
    private SKColor? _sampled; // 取樣模式「一次」時整劃固定的背景色（null = 落筆在透明處，整劃不擦）

    public void OnPointerDown(ToolPointerEvent e, EditorSession session)
    {
        if (session.Document.ActiveLayer is not RasterLayer layer) return;
        if (layer.IsTextLayer)
        {
            session.Notify("文字圖層不能直接繪製；要畫請先「圖層文字平面化」");
            return;
        }

        // Alt＝反向：把這一輪擦掉的擦回來（與橡皮擦同一套，見 RestoreStroke）
        if (e.Modifiers.HasFlag(ToolModifiers.Alt))
        {
            if (!_restore.Begin(session, layer, e.DocPosition, Settings.Radius, Settings.Hardness, 1f))
                session.Notify("這一輪還沒擦過東西，沒有可以還原的內容");
            return;
        }

        var doc = session.Document;
        SKRectI dirty;
        lock (doc.SyncRoot)
        {
            _beforeSnapshot = layer.Surface.Snapshot();
            session.EraseBaseline.BeginErase(layer, session.History);
            session.StrokeBuffer.Begin(layer.Id, session.Foreground, 1f, isEraser: true);
            _sampled = Settings.Sampling == BackgroundSampling.Once
                ? SampleAt(layer, e.DocPosition)
                : null;
            _last = e.DocPosition;
            _carry = 0f;
            dirty = Dab(layer, e.DocPosition, session);
        }
        _targetLayer = layer;
        _strokeActive = true;
        if (!dirty.IsEmpty) layer.Invalidate(dirty);
    }

    public void OnPointerMove(ToolPointerEvent e, EditorSession session)
    {
        if (_restore.IsActive)
        {
            _restore.Continue(session, e.DocPosition);
            return;
        }
        if (!_strokeActive || _targetLayer == null) return;
        var doc = session.Document;
        SKRectI dirty;
        lock (doc.SyncRoot)
        {
            dirty = Advance(_targetLayer, e.DocPosition, session);
        }
        if (!dirty.IsEmpty) _targetLayer.Invalidate(dirty);
    }

    public void OnPointerUp(ToolPointerEvent e, EditorSession session)
    {
        if (_restore.IsActive)
        {
            _restore.End(session, e.DocPosition);
            return;
        }
        if (!_strokeActive) return;
        _strokeActive = false;

        var doc = session.Document;
        var buffer = session.StrokeBuffer;
        var target = _targetLayer;
        _targetLayer = null;

        TileDeltaEntry? entry = null;
        SKRectI dirtyDoc;
        lock (doc.SyncRoot)
        {
            if (target != null) Advance(target, e.DocPosition, session);
            dirtyDoc = buffer.DirtyBounds;
            if (target != null && target.Document == doc && !dirtyDoc.IsEmpty)
            {
                BrushTool.CommitStroke(target, buffer);
                var affected = new SKRectI(
                    dirtyDoc.Left - target.Offset.X, dirtyDoc.Top - target.Offset.Y,
                    dirtyDoc.Right - target.Offset.X, dirtyDoc.Bottom - target.Offset.Y);
                entry = TileDeltaEntry.Capture(Name, target, _beforeSnapshot!, affected);
            }
            buffer.End();
            _beforeSnapshot?.Dispose();
            _beforeSnapshot = null;
        }

        if (entry != null)
        {
            session.History.Push(entry);
            session.EraseBaseline.AfterStroke(session.History);
        }
        if (!dirtyDoc.IsEmpty) target?.Invalidate(dirtyDoc);
    }

    /// <summary>沿 _last→p 以固定間距落 dab（間距 = 半徑/4，最少 1px）；回傳 dirty（doc 座標）。</summary>
    private SKRectI Advance(RasterLayer layer, SKPoint p, EditorSession session)
    {
        var spacing = Math.Max(1f, Math.Max(0.5f, Settings.Radius) * 0.25f);
        var dx = p.X - _last.X;
        var dy = p.Y - _last.Y;
        var len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1e-3f) return SKRectI.Empty;

        var dirty = SKRectI.Empty;
        var t = spacing - _carry;
        while (t <= len)
        {
            var q = new SKPoint(_last.X + dx * (t / len), _last.Y + dy * (t / len));
            dirty = Union(dirty, Dab(layer, q, session));
            t += spacing;
        }
        _carry = len - (t - spacing);
        _last = p;
        return dirty;
    }

    private static SKRectI Union(SKRectI a, SKRectI b) =>
        a.IsEmpty ? b : b.IsEmpty ? a : SKRectI.Union(a, b);

    /// <summary>讀 doc 座標處的圖層顏色（反 premul）；透明回傳 null。</summary>
    private static unsafe SKColor? SampleAt(RasterLayer layer, SKPoint doc)
    {
        var x = (int)MathF.Floor(doc.X) - layer.Offset.X;
        var y = (int)MathF.Floor(doc.Y) - layer.Offset.Y;
        var tile = layer.Surface.GetTileForRead(new TileIndex(x >> 8, y >> 8));
        if (tile == null) return null;
        var px = ((uint*)tile.Pixels)[((y & 255) << 8) | (x & 255)];
        return Unpremul(px);
    }

    /// <summary>premul BGRA → 直接色（alpha 0 → null）。</summary>
    private static SKColor? Unpremul(uint px)
    {
        var a = (int)(px >> 24);
        if (a == 0) return null;
        var b = (int)(px & 0xFF) * 255 / a;
        var g = (int)((px >> 8) & 0xFF) * 255 / a;
        var r = (int)((px >> 16) & 0xFF) * 255 / a;
        return new SKColor((byte)Math.Min(r, 255), (byte)Math.Min(g, 255), (byte)Math.Min(b, 255), (byte)a);
    }

    /// <summary>單個 dab：以中心熱點顏色為背景色，計算圈內每個像素的擦除量並蓋進 StrokeBuffer。</summary>
    private unsafe SKRectI Dab(RasterLayer layer, SKPoint center, EditorSession session)
    {
        var bg = Settings.Sampling == BackgroundSampling.Once ? _sampled : SampleAt(layer, center);
        if (bg is not { } background) return SKRectI.Empty; // 熱點在透明處：沒有背景色可取，不擦

        var doc = session.Document;
        var radius = Math.Max(0.5f, Settings.Radius);
        var hardness = Math.Clamp(Settings.Hardness, 0f, 1f);
        var tol = Math.Max(1f, Settings.Tolerance);
        var soft = Math.Clamp(Settings.Softness, 0f, 1f);
        var fadeEnd = tol * (1f + soft * 2f); // 柔邊 100% = 漸層帶延伸到 3 倍容許度

        var left = (int)MathF.Floor(center.X - radius - 1f);
        var top = (int)MathF.Floor(center.Y - radius - 1f);
        var right = (int)MathF.Ceiling(center.X + radius + 1f);
        var bottom = (int)MathF.Ceiling(center.Y + radius + 1f);
        var rect = SKRectI.Intersect(new SKRectI(left, top, right, bottom), doc.Bounds);
        if (rect.Width <= 0 || rect.Height <= 0) return SKRectI.Empty;

        var w = rect.Width;
        var h = rect.Height;
        var pool = ArrayPool<byte>.Shared;
        var mask = pool.Rent(w * h);
        var weight = pool.Rent(w * h); // 色差權重（連續限制的 flood 用）
        try
        {
            var protect = Settings.ProtectForeground;
            var fg = session.Foreground;
            // 背景→前景色軸（保護前景色時的單軸 alpha 估計）
            float axR = fg.Red - background.Red, axG = fg.Green - background.Green, axB = fg.Blue - background.Blue;
            var axLen2 = axR * axR + axG * axG + axB * axB;
            var useAxis = protect && axLen2 > tol * tol; // 前景色與背景色太接近時退化成純容許度

            var reader = new PixelReader(layer.Surface);
            var off = layer.Offset;
            var any = false;

            for (var y = 0; y < h; y++)
            {
                var row = y * w;
                for (var x = 0; x < w; x++)
                {
                    var px = reader.Get(rect.Left + x - off.X, rect.Top + y - off.Y);
                    var a = (int)(px >> 24);
                    if (a == 0) { weight[row + x] = 0; continue; }

                    // 反 premul 後的 RGB
                    var b = (int)(px & 0xFF) * 255 / a;
                    var g = (int)((px >> 8) & 0xFF) * 255 / a;
                    var r = (int)((px >> 16) & 0xFF) * 255 / a;

                    float dr = r - background.Red, dg = g - background.Green, db = b - background.Blue;
                    var dist = MathF.Sqrt((dr * dr + dg * dg + db * db) / 3f); // 0..255

                    float erase;
                    if (dist <= tol) erase = 1f;
                    else if (dist < fadeEnd)
                    {
                        var t = (dist - tol) / (fadeEnd - tol);
                        erase = 1f - t * t * (3f - 2f * t);
                    }
                    else erase = 0f;

                    if (protect)
                    {
                        float fr = r - fg.Red, fgd = g - fg.Green, fb = b - fg.Blue;
                        var distF = MathF.Sqrt((fr * fr + fgd * fgd + fb * fb) / 3f);
                        // 與前景色相近（且比背景色更近）→ 保護；容許度開很大時背景本身仍要擦得掉
                        if (distF <= tol && distF < dist) erase = 0f;
                        else if (useAxis)
                        {
                            // 投影到背景→前景軸：0 = 純背景、1 = 純前景；擦除量取兩者較嚴格的
                            var proj = Math.Clamp((dr * axR + dg * axG + db * axB) / axLen2, 0f, 1f);
                            erase = Math.Min(erase, 1f - proj);
                        }
                    }
                    weight[row + x] = (byte)(erase * 255f + 0.5f);
                }
            }

            if (Settings.Contiguous)
                KeepConnected(weight, w, h,
                    (int)MathF.Floor(center.X) - rect.Left, (int)MathF.Floor(center.Y) - rect.Top);

            for (var y = 0; y < h; y++)
            {
                var py = rect.Top + y + 0.5f;
                var row = y * w;
                for (var x = 0; x < w; x++)
                {
                    var wgt = weight[row + x];
                    if (wgt == 0) { mask[row + x] = 0; continue; }
                    var px = rect.Left + x + 0.5f;
                    var ddx = px - center.X;
                    var ddy = py - center.Y;
                    var c = BrushEngine.Coverage(MathF.Sqrt(ddx * ddx + ddy * ddy), radius, hardness);
                    var value = (byte)(c * wgt + 0.5f);
                    mask[row + x] = value;
                    any |= value != 0;
                }
            }

            if (!any) return SKRectI.Empty;
            session.StrokeBuffer.Mask.StampMax(new ReadOnlySpan<byte>(mask, 0, w * h), w, h,
                new SKPointI(rect.Left, rect.Top), session.Selection?.Mask, doc.Bounds);
            return rect;
        }
        finally
        {
            pool.Return(mask);
            pool.Return(weight);
        }
    }

    /// <summary>只保留與 (sx, sy) 四連通相連的非零權重（dab 範圍內的小 flood）。</summary>
    private static void KeepConnected(byte[] weight, int w, int h, int sx, int sy)
    {
        if (sx < 0 || sy < 0 || sx >= w || sy >= h || weight[sy * w + sx] == 0)
        {
            Array.Clear(weight, 0, w * h);
            return;
        }
        var pool = ArrayPool<bool>.Shared;
        var seen = pool.Rent(w * h);
        var stack = ArrayPool<int>.Shared.Rent(w * h);
        try
        {
            Array.Clear(seen, 0, w * h);
            var sp = 0;
            stack[sp++] = sy * w + sx;
            seen[sy * w + sx] = true;
            while (sp > 0)
            {
                var i = stack[--sp];
                var x = i % w;
                var y = i / w;
                if (x > 0) Push(i - 1);
                if (x < w - 1) Push(i + 1);
                if (y > 0) Push(i - w);
                if (y < h - 1) Push(i + w);
            }
            for (var i = 0; i < w * h; i++)
                if (!seen[i]) weight[i] = 0;

            void Push(int j)
            {
                if (seen[j] || weight[j] == 0) return;
                seen[j] = true;
                stack[sp++] = j;
            }
        }
        finally
        {
            pool.Return(seen);
            ArrayPool<int>.Shared.Return(stack);
        }
    }

    /// <summary>圖層像素讀取器（圖層座標），快取最後命中的 tile。</summary>
    private unsafe struct PixelReader(TileSurface surface)
    {
        private int _tx = int.MinValue, _ty = int.MinValue;
        private uint* _pixels;

        public uint Get(int x, int y)
        {
            var tx = x >> 8;
            var ty = y >> 8;
            if (tx != _tx || ty != _ty)
            {
                _tx = tx; _ty = ty;
                var tile = surface.GetTileForRead(new TileIndex(tx, ty));
                _pixels = tile == null ? null : (uint*)tile.Pixels;
            }
            return _pixels == null ? 0 : _pixels[((y & 255) << 8) | (x & 255)];
        }
    }
}
