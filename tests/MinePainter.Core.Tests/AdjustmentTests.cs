using MinePainter.Core.Adjustments;
using MinePainter.Core.Compositing;
using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

public class AdjustmentTests
{
    private static SKColor WaitPixel(Compositor compositor, int x, int y, Func<SKColor, bool> until, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        SKColor last = default;
        while (Environment.TickCount64 < deadline)
        {
            compositor.TryGetTile(TileIndex.FromPixel(x, y), out _);
            last = compositor.SamplePixel(x, y);
            if (until(last)) return last;
            Thread.Sleep(15);
        }
        throw new TimeoutException($"最後取樣 {last}");
    }

    private static RasterLayer SolidLayer(string name, SKRectI rect, SKColor color)
    {
        var layer = new RasterLayer { Name = name };
        layer.Surface.Fill(rect, color);
        return layer;
    }

    [Fact]
    public void BrightnessAdjustment_LightensBelow()
    {
        using var doc = ImageCodec.CreateBlankDocument(256, 256, new SKColor(100, 100, 100));
        var adj = new AdjustmentLayer(new BrightnessContrastAdjustment(Brightness: 0.2f)); // +51
        lock (doc.SyncRoot) doc.Root.Add(adj);

        using var compositor = new Compositor(doc);
        var px = WaitPixel(compositor, 128, 128, c => c.Red is > 145 and < 158);
        Assert.InRange(px.Red, 146, 157); // 100 + 51 ≈ 151
        Assert.Equal(px.Red, px.Green);
    }

    [Fact]
    public void SaturationMinusOne_MakesGray()
    {
        using var doc = ImageCodec.CreateBlankDocument(256, 256, new SKColor(200, 50, 50));
        var adj = new AdjustmentLayer(new HueSaturationAdjustment(Saturation: -1f));
        lock (doc.SyncRoot) doc.Root.Add(adj);

        using var compositor = new Compositor(doc);
        var px = WaitPixel(compositor, 128, 128,
            c => c.Alpha == 255 && Math.Abs(c.Red - c.Green) <= 2 && Math.Abs(c.Green - c.Blue) <= 2);
        // 去飽和 → 灰（亮度 ≈ 0.299*200+0.587*50+0.114*50 ≈ 95）
        Assert.InRange(px.Red, 90, 100);
    }

    [Fact]
    public void LightnessHalf_LightensTowardWhiteNotFullWhite()
    {
        using var doc = ImageCodec.CreateBlankDocument(256, 256, new SKColor(100, 100, 100));
        var adj = new AdjustmentLayer(new HueSaturationAdjustment(Lightness: 0.5f));
        lock (doc.SyncRoot) doc.Root.Add(adj);

        using var compositor = new Compositor(doc);
        // 100 * 0.5 + 255 * 0.5 ≈ 178；以前位移多乘了 255 會直接爆成 255
        var px = WaitPixel(compositor, 128, 128, c => c.Red is > 170 and < 186);
        Assert.InRange(px.Red, 171, 185);
        Assert.Equal(px.Red, px.Green);
    }

    [Fact]
    public void LightnessMinusHalf_DarkensTowardBlack()
    {
        using var doc = ImageCodec.CreateBlankDocument(256, 256, new SKColor(200, 200, 200));
        var adj = new AdjustmentLayer(new HueSaturationAdjustment(Lightness: -0.5f));
        lock (doc.SyncRoot) doc.Root.Add(adj);

        using var compositor = new Compositor(doc);
        var px = WaitPixel(compositor, 128, 128, c => c.Red is > 95 and < 105);
        Assert.InRange(px.Red, 96, 104); // 200 * 0.5 = 100
    }

    [Fact]
    public void AdjustmentInsideGroup_OnlyAffectsGroupContent()
    {
        // 群組外(下方)白色；群組內灰色方塊 + 亮度調整。
        // 調整只該影響群組內容，群組外的白色不變。
        using var doc = ImageCodec.CreateBlankDocument(512, 256, SKColors.White);
        var group = new GroupLayer { Name = "g" };
        group.Add(SolidLayer("grey", new SKRectI(0, 0, 256, 256), new SKColor(100, 100, 100)));
        group.Add(new AdjustmentLayer(new BrightnessContrastAdjustment(Brightness: 0.4f)));
        lock (doc.SyncRoot) doc.Root.Add(group);

        using var compositor = new Compositor(doc);
        var inside = WaitPixel(compositor, 128, 128, c => c.Red > 150);
        Assert.InRange(inside.Red, 195, 210); // 100+102 ≈ 202

        var outside = WaitPixel(compositor, 400, 128, c => c == SKColors.White);
        Assert.Equal(SKColors.White, outside); // 群組外不受影響
    }

    [Fact]
    public void AdjustmentOpacity_ScalesEffect()
    {
        using var doc = ImageCodec.CreateBlankDocument(256, 256, new SKColor(100, 100, 100));
        var adj = new AdjustmentLayer(new BrightnessContrastAdjustment(Brightness: 0.4f)) { Opacity = 0.5f };
        lock (doc.SyncRoot) doc.Root.Add(adj);

        using var compositor = new Compositor(doc);
        // 全強度 = 202；50% 強度 ≈ 151
        var px = WaitPixel(compositor, 128, 128, c => c.Red is > 143 and < 160);
        Assert.InRange(px.Red, 144, 159);
    }

    [Fact]
    public void ParameterChange_RecompositesAndUndoes()
    {
        using var doc = ImageCodec.CreateBlankDocument(256, 256, new SKColor(100, 100, 100));
        using var history = new HistoryManager(doc);
        var adj = new AdjustmentLayer(new BrightnessContrastAdjustment(0f, 0f));
        lock (doc.SyncRoot) doc.Root.Add(adj);

        using var compositor = new Compositor(doc);
        WaitPixel(compositor, 128, 128, c => c.Red is > 95 and < 105); // 無效果

        var old = adj.Adjustment;
        LayerCommands.SetAdjustment(doc, history, adj, old, new BrightnessContrastAdjustment(Brightness: 0.3f));
        WaitPixel(compositor, 128, 128, c => c.Red > 165); // ≈ 176

        history.Undo();
        WaitPixel(compositor, 128, 128, c => c.Red is > 95 and < 105);
        Assert.Same(old, adj.Adjustment);

        history.Redo();
        WaitPixel(compositor, 128, 128, c => c.Red > 165);
    }

    [Fact]
    public void HueRotation_Preserves_Gray()
    {
        // 灰色無色相，旋轉後應不變
        using var doc = ImageCodec.CreateBlankDocument(256, 256, new SKColor(128, 128, 128));
        var adj = new AdjustmentLayer(new HueSaturationAdjustment(Hue: 90f));
        lock (doc.SyncRoot) doc.Root.Add(adj);

        using var compositor = new Compositor(doc);
        var px = WaitPixel(compositor, 128, 128, c => c.Alpha == 255);
        Assert.InRange(px.Red, 125, 131);
        Assert.InRange(px.Green, 125, 131);
        Assert.InRange(px.Blue, 125, 131);
    }
}
