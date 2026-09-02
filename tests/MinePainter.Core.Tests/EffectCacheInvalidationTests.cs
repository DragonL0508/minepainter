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
