using MinePainter.Core.Effects;
using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 效果快取的失效邏輯（殘留像素、外框跟不上的根本解）：
/// 快取在圖層座標、範圍＝內容＋效果外擴，寫回範圍＝髒區＋margin。
/// </summary>
public class EffectCacheInvalidationTests
{
    private static unsafe uint CachePixel(RasterLayer layer, int lx, int ly)
    {
        var tile = layer.FxCache.Surface.GetTileForRead(TileIndex.FromPixel(lx, ly));
        if (tile == null) return 0;
        return ((uint*)tile.Pixels)[(ly & 255) * Tile.Size + (lx & 255)];
    }

    private static byte Alpha(uint p) => (byte)(p >> 24);

    private static (EditorSession Session, RasterLayer Layer) NewTransparentSession(int size = 256)
    {
        var doc = ImageCodec.CreateBlankDocument(size, size, SKColors.Transparent);
        var session = new EditorSession(doc);
        return (session, (RasterLayer)doc.ActiveLayer!);
    }

    private static void FillSquare(RasterLayer layer, SKRectI rect, SKColor color)
    {
        lock (layer.Document!.SyncRoot)
        {
            layer.Surface.Fill(new SKRectI(rect.Left - layer.Offset.X, rect.Top - layer.Offset.Y,
                rect.Right - layer.Offset.X, rect.Bottom - layer.Offset.Y), color);
        }
        layer.Invalidate(rect);
    }

    [Fact]
    public void Outline_NoResidue_AfterContentMovesAway()
    {
        var (session, layer) = NewTransparentSession();
        var doc = session.Document;
        var square = new SKRectI(40, 40, 60, 60);
        FillSquare(layer, square, SKColors.Red);
        LayerEffectCommands.Add(doc, session.History, layer,
            LayerEffect.Create(new ObjectOutlineEffect { Width = 6, Color = SKColors.Black }));
        LayerEffectRenderer.RenderLayerNow(doc, layer);

        Assert.True(Alpha(CachePixel(layer, 36, 50)) > 200); // 外框在內容左邊 4px 處
        Assert.True(Alpha(CachePixel(layer, 50, 50)) > 200);

        // 只把方塊本身清掉、只標髒方塊範圍（外框在範圍外）——舊版會留下一圈外框殘影
        lock (doc.SyncRoot) layer.Surface.Fill(square, SKColors.Transparent);
        layer.Invalidate(square);
        LayerEffectRenderer.RenderLayerNow(doc, layer);

        Assert.Equal(0, Alpha(CachePixel(layer, 36, 50)));
        Assert.Equal(0, Alpha(CachePixel(layer, 50, 50)));
        Assert.Equal(0, Alpha(CachePixel(layer, 63, 63)));

        // 畫在別處：新位置有外框，舊位置什麼都沒有
        FillSquare(layer, new SKRectI(150, 150, 170, 170), SKColors.Red);
        LayerEffectRenderer.RenderLayerNow(doc, layer);
        Assert.True(Alpha(CachePixel(layer, 146, 160)) > 200);
        Assert.Equal(0, Alpha(CachePixel(layer, 36, 50)));
        session.Dispose();
    }

    [Fact]
    public void Cache_IsLayerLocal_OffsetChangeNeedsNoRecompute_AndGradientFollowsContent()
    {
        var (session, layer) = NewTransparentSession();
        var doc = session.Document;
        FillSquare(layer, new SKRectI(100, 100, 160, 130), SKColors.White);
        LayerEffectCommands.Add(doc, session.History, layer,
            LayerEffect.Create(new ObjectGradientEffect { Start = SKColors.Red, End = SKColors.Blue, Angle = 0 }));
        LayerEffectRenderer.RenderLayerNow(doc, layer);

        var leftBefore = CachePixel(layer, 101, 115);
        var rightBefore = CachePixel(layer, 158, 115);
        Assert.True(((leftBefore >> 16) & 0xFF) > 200);  // 左端偏紅
        Assert.True((rightBefore & 0xFF) > 200);          // 右端偏藍

        // 平移圖層（連帶把一半內容推出畫布外）：快取不該有待處理工作、內容也一個位元都不變
        lock (doc.SyncRoot) layer.Offset = new SKPointI(-130, 0);
        layer.InvalidateComposite(doc.Bounds);
        LayerEffectRenderer.RenderLayerNow(doc, layer);
        Assert.False(layer.FxCache.HasPending);
        Assert.Equal(leftBefore, CachePixel(layer, 101, 115));
        Assert.Equal(rightBefore, CachePixel(layer, 158, 115));

        // 合成結果：畫布 x=28（= 158-130）要是藍的那一端，不是被畫布裁切後重算的漸層
        using var composite = Compositing.Compositor.RenderComposite(doc);
        using var bmp = SKBitmap.FromImage(composite);
        var px = bmp.GetPixel(28, 115);
        Assert.True(px.Blue > 200 && px.Red < 60, $"got {px}");
        session.Dispose();
    }

    [Fact]
    public void Outline_ExtendsBeyondCanvas_AndBakeCoversIt()
    {
        var (session, layer) = NewTransparentSession(128);
        var doc = session.Document;
        FillSquare(layer, new SKRectI(0, 40, 20, 60), SKColors.Red);
        LayerEffectCommands.Add(doc, session.History, layer,
            LayerEffect.Create(new ObjectOutlineEffect { Width = 6, Color = SKColors.Black }));
        LayerEffectRenderer.RenderLayerNow(doc, layer);

        // 外框在畫布左邊界之外也算得出來（快取不裁到畫布）
        Assert.True(Alpha(CachePixel(layer, -3, 50)) > 200);

        Assert.True(LayerEffectCommands.Bake(session, layer));
        var tile = layer.Surface.GetTileForRead(TileIndex.FromPixel(-3, 50));
        Assert.NotNull(tile);
        session.Dispose();
    }

    [Fact]
    public void ElementPreview_IncludesEffects_AndBoundsGrowByMargin()
    {
        var (session, layer) = NewTransparentSession();
        var doc = session.Document;
        var text = new TextElement { Text = "Ab", Position = new SKPoint(60, 60), FontSize = 40, Color = SKColors.White };
        lock (doc.SyncRoot) layer.AddElement(text);
        LayerEffectCommands.Add(doc, session.History, layer,
            LayerEffect.Create(new ObjectOutlineEffect { Width = 8, Color = SKColors.Black }));

        var preview = LayerEffectRenderer.RenderElementPreview(layer, text, out var bounds);
        Assert.Equal(text.Bounds.Width + 2 * (10 + 1), bounds.Width);

        // 有純黑外框像素（不是白字也不是透明）
        var hasOutline = preview.Any(p => Alpha(p) > 200 && ((p >> 16) & 0xFF) < 30 && (p & 0xFF) < 30);
        Assert.True(hasOutline);
        session.Dispose();
    }

    [Fact]
    public void LayerDrag_OverlayCarriesEffects_ByDefault()
    {
        var (session, layer) = NewTransparentSession();
        var doc = session.Document;
        FillSquare(layer, new SKRectI(100, 100, 120, 120), SKColors.Red);
        LayerEffectCommands.Add(doc, session.History, layer,
            LayerEffect.Create(new ObjectOutlineEffect { Width = 6, Color = SKColors.Black }));

        Assert.True(EditorSession.RenderEffectsWhileDragging);
        Assert.True(session.BeginLayerDrag(layer));
        var overlay = session.LayerOverlay!;
        Assert.True(overlay.IncludesElements);
        Assert.True(overlay.Region.Contains(new SKRectI(94, 94, 126, 126))); // 含外框

        using var surface = SKSurface.Create(new SKImageInfo(256, 256));
        surface.Canvas.Clear(SKColors.Transparent);
        overlay.Draw(surface.Canvas, doc.Bounds, SKFilterQuality.None);
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        Assert.True(bmp.GetPixel(96, 110).Alpha > 200); // 外框跟著覆疊層
        session.EndLayerDrag();
        session.Dispose();
    }

    [Fact]
    public void OutlineGradient_ColorsChangeAlongAngle()
    {
        var (session, layer) = NewTransparentSession();
        var doc = session.Document;
        FillSquare(layer, new SKRectI(60, 100, 200, 120), SKColors.White);
        LayerEffectCommands.Add(doc, session.History, layer,
            LayerEffect.Create(new ObjectOutlineEffect
            {
                Width = 6, Gradient = true, Color = SKColors.Red, GradientEnd = SKColors.Blue, GradientAngle = 0,
            }));
        LayerEffectRenderer.RenderLayerNow(doc, layer);

        var left = CachePixel(layer, 57, 110);
        var right = CachePixel(layer, 202, 110);
        Assert.True(Alpha(left) > 200 && Alpha(right) > 200);
        Assert.True(((left >> 16) & 0xFF) > 180 && (left & 0xFF) < 80, $"left {left:X8}");
        Assert.True((right & 0xFF) > 180 && ((right >> 16) & 0xFF) < 80, $"right {right:X8}");

        // 序列化來回：漸層欄位要留住
        var effect = layer.Effects[0].Effect;
        var dict = EffectSerializer.Save(effect);
        var back = Assert.IsType<ObjectOutlineEffect>(EffectSerializer.Load(EffectSerializer.TypeIdOf(effect), dict));
        Assert.True(back.Gradient);
        Assert.Equal(SKColors.Blue, back.GradientEnd);
        session.Dispose();
    }

    [Fact]
    public void ShapeTool_Shift_ConstrainsToSquare_AndLineTo15Degrees()
    {
        var anchor = new SKPoint(10, 10);
        var p = ShapeTool.Constrain(anchor, new SKPoint(50, 25), shift: true, ShapeKind.Ellipse);
        Assert.Equal(50, p.X);
        Assert.Equal(50, p.Y); // 邊長取較長軸、方向跟指標

        p = ShapeTool.Constrain(anchor, new SKPoint(-30, 25), shift: true, ShapeKind.Rectangle);
        Assert.Equal(-30, p.X);
        Assert.Equal(50, p.Y);

        Assert.Equal(new SKPoint(50, 25), ShapeTool.Constrain(anchor, new SKPoint(50, 25), shift: false, ShapeKind.Ellipse));

        var line = ShapeTool.Constrain(anchor, new SKPoint(110, 14), shift: true, ShapeKind.Line);
        Assert.Equal(10, line.Y, 1); // 接近 0° 就吸到 0°
    }

    [Fact]
    public void Thumbnail_UsesEffectCache_NotOriginalElementColor()
    {
        var (session, layer) = NewTransparentSession();
        var doc = session.Document;
        var text = new TextElement { Text = "████", Position = new SKPoint(20, 100), FontSize = 80, Color = SKColors.Red };
        lock (doc.SyncRoot) layer.AddElement(text);
        LayerEffectCommands.Add(doc, session.History, layer,
            LayerEffect.Create(new ObjectGradientEffect { Start = SKColors.Blue, End = SKColors.Blue, Angle = 0 }));
        LayerEffectRenderer.RenderLayerNow(doc, layer);

        using var surface = SKSurface.Create(new SKImageInfo(256, 256));
        surface.Canvas.Clear(SKColors.Transparent);
        Compositing.LayerThumbnailRenderer.Draw(surface.Canvas, doc, layer, 256, 256);
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        var anyRed = false;
        var anyBlue = false;
        for (var y = 0; y < 256; y += 2)
        for (var x = 0; x < 256; x += 2)
        {
            var c = bmp.GetPixel(x, y);
            if (c.Alpha < 200) continue;
            if (c.Red > 200 && c.Blue < 60) anyRed = true;
            if (c.Blue > 200 && c.Red < 60) anyBlue = true;
        }
        Assert.True(anyBlue, "縮圖要看到效果後的顏色");
        Assert.False(anyRed, "原件不該再畫一次蓋掉效果");
        session.Dispose();
    }
}

public class OutlineGrowTests
{
    private static unsafe uint CachePixel(RasterLayer layer, int lx, int ly)
    {
        var tile = layer.FxCache.Surface.GetTileForRead(TileIndex.FromPixel(lx, ly));
        if (tile == null) return 0;
        return ((uint*)tile.Pixels)[(ly & 255) * Tile.Size + (lx & 255)];
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void Outline_GrowingWidth_IsNotClipped(int smooth)
    {
        var doc = ImageCodec.CreateBlankDocument(512, 512, SKColors.Transparent);
        using var session = new EditorSession(doc);
        var layer = (RasterLayer)doc.ActiveLayer!;
        lock (doc.SyncRoot) layer.Surface.Fill(new SKRectI(200, 200, 260, 260), SKColors.Red);
        layer.Invalidate(new SKRectI(200, 200, 260, 260));

        var fx = LayerEffect.Create(new ObjectOutlineEffect { Width = 6, Smooth = smooth, Color = SKColors.Black });
        LayerEffectCommands.Add(doc, session.History, layer, fx);
        LayerEffectRenderer.RenderLayerNow(doc, layer);
        Assert.True((CachePixel(layer, 196, 230) >> 24) > 200);
        Assert.Equal(0u, CachePixel(layer, 170, 230) >> 24);

        // 拉大到 40：四邊在 35px 外都要有外框，且外框外緣不能是被直線切掉的
        LayerEffectCommands.Replace(doc, session.History, layer,
            fx with { Effect = new ObjectOutlineEffect { Width = 40, Smooth = smooth, Color = SKColors.Black } });
        LayerEffectRenderer.RenderLayerNow(doc, layer);
        foreach (var (x, y) in new[] { (165, 230), (295, 230), (230, 165), (230, 295) })
            Assert.True((CachePixel(layer, x, y) >> 24) > 200, $"outline missing at ({x},{y}) smooth={smooth}");
        // 角落：距離 (200,200) 為 35 的斜方向也要有（圓角、且平滑會把物件角落磨圓幾格，不是直線切齊）
        Assert.True((CachePixel(layer, 200 - 25, 200 - 25) >> 24) > 200, $"corner missing smooth={smooth}");
        Assert.Equal(0u, CachePixel(layer, 200 - 45, 230) >> 24);
    }
}

public class TextOutlineOverhangTests
{
    private static unsafe uint CachePixel(RasterLayer layer, int lx, int ly)
    {
        var tile = layer.FxCache.Surface.GetTileForRead(TileIndex.FromPixel(lx, ly));
        if (tile == null) return 0;
        return ((uint*)tile.Pixels)[(ly & 255) * Tile.Size + (lx & 255)];
    }

    [Fact]
    public void Outline_AroundGlyphsExceedingEmBox_IsNotClipped()
    {
        // 「É」的重音、「|」的上下都超出 em box（行高算出來的框）：外框 40 在字面外 40px 處四邊都要有
        var doc = ImageCodec.CreateBlankDocument(1000, 700, SKColors.Transparent);
        using var session = new EditorSession(doc);
        var layer = new RasterLayer { Name = "T" };
        var el = new TextElement { Text = "Éjg|", FontFamily = "Arial", FontSize = 200, Position = new SKPoint(300, 250) };
        lock (doc.SyncRoot) { doc.Root.Add(layer); layer.AddElement(el); doc.ActiveLayer = layer; }

        using var bmp = new SKBitmap(new SKImageInfo(1000, 700, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var c = new SKCanvas(bmp)) { c.Clear(SKColors.Transparent); el.Render(c); }
        int il = int.MaxValue, it = int.MaxValue, ir = -1, ib = -1;
        for (var y = 0; y < 700; y++)
        for (var x = 0; x < 1000; x++)
            if (bmp.GetPixel(x, y).Alpha > 0) { il = Math.Min(il, x); it = Math.Min(it, y); ir = Math.Max(ir, x); ib = Math.Max(ib, y); }
        Assert.True(ir > il && ib > it);
        var b = el.Bounds;
        Assert.True(b.Top <= it && b.Bottom >= ib && b.Left <= il && b.Right >= ir, $"Bounds {b} must contain ink ({il},{it})-({ir},{ib})");

        LayerEffectCommands.Add(doc, session.History, layer,
            LayerEffect.Create(new ObjectOutlineEffect { Width = 40, Color = SKColors.Red }));
        LayerEffectRenderer.RenderLayerNow(doc, layer);

        int cl = int.MaxValue, ct = int.MaxValue, cr = -1, cb = -1;
        for (var y = 0; y < 700; y++)
        for (var x = 0; x < 1000; x++)
            if ((CachePixel(layer, x, y) >> 24) > 0) { cl = Math.Min(cl, x); ct = Math.Min(ct, y); cr = Math.Max(cr, x); cb = Math.Max(cb, y); }
        Assert.True(ct <= it - 39 && cb >= ib + 39 && cl <= il - 39 && cr >= ir + 39,
            $"outline cache ({cl},{ct})-({cr},{cb}) vs ink ({il},{it})-({ir},{ib})");
    }

    [Fact]
    public void Bounds_ContainsOwnEffects_WhenTextIsStretchedHorizontally()
    {
        // 文字被拉寬（ScaleX=2）時外框／陰影／光暈在 x 方向也跟著放大：Bounds 左右要蓋得住實際著墨
        var el = new TextElement
        {
            Text = "Wgj", FontFamily = "Arial", FontSize = 200, ScaleX = 2f, Position = new SKPoint(400, 300),
            Stroke = new TextStroke { Width = 30, Outer = new TextStroke { Width = 30, Color = SKColors.Red } },
            Shadow = new TextShadow { Distance = 30, Blur = 30, Spread = 10 },
            Glow = new TextGlow { Size = 40, Spread = 10 },
        };
        const int W = 2000, H = 1000;
        using var bmp = new SKBitmap(new SKImageInfo(W, H, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var c = new SKCanvas(bmp)) { c.Clear(SKColors.Transparent); el.Render(c); }
        int il = int.MaxValue, it = int.MaxValue, ir = -1, ib = -1;
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
            if (bmp.GetPixel(x, y).Alpha > 0) { il = Math.Min(il, x); it = Math.Min(it, y); ir = Math.Max(ir, x); ib = Math.Max(ib, y); }
        Assert.True(ir > il && ib > it);
        var b = el.Bounds;
        Assert.True(b.Left <= il && b.Right >= ir && b.Top <= it && b.Bottom >= ib,
            $"Bounds ({b.Left},{b.Top})-({b.Right},{b.Bottom}) must contain ink ({il},{it})-({ir},{ib})");
    }

    [Fact]
    public void GradientOutline_AroundLargeText_IsNotClippedLeftRight()
    {
        // 漸層外框的來源是整層（SourceMargin = WholeLayer）：快取範圍仍要留外框寬度的餘裕，
        // 否則左右只剩排版框的 2px、外框在那裡被直線切掉（上下有行高餘裕所以看不出來）
        var doc = ImageCodec.CreateBlankDocument(1600, 800, SKColors.Transparent);
        using var session = new EditorSession(doc);
        var layer = new RasterLayer { Name = "T" };
        var el = new TextElement { Text = "HIH", FontFamily = "Arial", FontSize = 288, Position = new SKPoint(300, 150) };
        lock (doc.SyncRoot) { doc.Root.Add(layer); layer.AddElement(el); doc.ActiveLayer = layer; }

        using var bmp = new SKBitmap(new SKImageInfo(1600, 800, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var c = new SKCanvas(bmp)) { c.Clear(SKColors.Transparent); el.Render(c); }
        int il = int.MaxValue, it = int.MaxValue, ir = -1, ib = -1;
        for (var y = 0; y < 800; y++)
        for (var x = 0; x < 1600; x++)
            if (bmp.GetPixel(x, y).Alpha > 0) { il = Math.Min(il, x); it = Math.Min(it, y); ir = Math.Max(ir, x); ib = Math.Max(ib, y); }

        LayerEffectCommands.Add(doc, session.History, layer, LayerEffect.Create(new ObjectOutlineEffect
        {
            Width = 60, Gradient = true, Color = SKColors.Red, GradientEnd = SKColors.Blue,
        }));
        LayerEffectRenderer.RenderLayerNow(doc, layer);

        int cl = int.MaxValue, ct = int.MaxValue, cr = -1, cb = -1;
        for (var y = 0; y < 800; y++)
        for (var x = 0; x < 1600; x++)
            if ((CachePixel(layer, x, y) >> 24) > 0) { cl = Math.Min(cl, x); ct = Math.Min(ct, y); cr = Math.Max(cr, x); cb = Math.Max(cb, y); }
        Assert.True(cl <= il - 59 && cr >= ir + 59 && ct <= it - 59 && cb >= ib + 59,
            $"gradient outline cache ({cl},{ct})-({cr},{cb}) vs ink ({il},{it})-({ir},{ib})");
    }
}
