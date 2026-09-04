using MinePainter.Core.Adjustments;
using MinePainter.Core.Effects;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 效果堆疊翻成 Skia image filter（GPU 路徑）。這裡驗的是「翻得出來 / 翻不出來」的判斷，
/// 以及翻出來的濾鏡畫出來的形狀對不對 —— 不是要跟 CPU 版逐像素相同（GPU 版是互動當下的近似）。
/// </summary>
public class GpuEffectFilterTests
{
    private static LayerEffect Fx(IEffect effect, SKColor? color = null) =>
        LayerEffect.Create(effect, color: color);

    /// <summary>畫一個紅方塊，套上濾鏡，回傳結果點陣圖。</summary>
    private static SKBitmap RenderWithFilter(SKImageFilter? filter, int size = 200)
    {
        var bmp = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { Color = SKColors.Red, IsAntialias = false, ImageFilter = filter };
        canvas.DrawRect(SKRect.Create(70, 70, 60, 60), paint);
        canvas.Flush();
        return bmp;
    }

    [Fact]
    public void ObjectEffects_AreTranslatable()
    {
        Assert.True(GpuEffectFilters.CanTranslate([Fx(new ObjectShadowEffect())]));
        Assert.True(GpuEffectFilters.CanTranslate([Fx(new ObjectGlowEffect())]));
        Assert.True(GpuEffectFilters.CanTranslate([Fx(new ObjectOutlineEffect { Width = 6 })]));
        Assert.True(GpuEffectFilters.CanTranslate([Fx(new ObjectFillEffect())]));
        // 一整串也可以
        Assert.True(GpuEffectFilters.CanTranslate(
        [
            Fx(new ObjectOutlineEffect { Width = 4 }),
            Fx(new ObjectShadowEffect()),
        ]));
    }

    [Fact]
    public void ThingsSkiaCannotDo_FallBackToCpu()
    {
        // 沒有 Skia 對應的效果
        Assert.False(GpuEffectFilters.CanTranslate([Fx(new AdjustmentEffect(new InvertAdjustment()))]));
        Assert.False(GpuEffectFilters.CanTranslate([Fx(new GaussianBlurEffect())]));
        // 外框的漸層／平滑／柔邊走的是距離場，翻不出來
        Assert.False(GpuEffectFilters.CanTranslate([Fx(new ObjectOutlineEffect { Gradient = true })]));
        Assert.False(GpuEffectFilters.CanTranslate([Fx(new ObjectOutlineEffect { Smooth = 3 })]));
        // 一串裡只要有一個翻不出來就整串放棄（順序不能拆）
        Assert.False(GpuEffectFilters.CanTranslate(
        [
            Fx(new ObjectShadowEffect()),
            Fx(new AdjustmentEffect(new InvertAdjustment())),
        ]));
        // 停用的不算數；整串都停用＝沒有東西要畫
        Assert.False(GpuEffectFilters.CanTranslate([Fx(new ObjectShadowEffect()) with { Enabled = false }]));
        // 帶遮罩的要逐像素混，濾鏡表達不了
        Assert.False(GpuEffectFilters.CanTranslate(
            [LayerEffect.Create(new ObjectShadowEffect(), new Tiles.MaskSurface())]));
    }

    [Fact]
    public void Outline_PaintsARingAroundTheContent()
    {
        using var filter = GpuEffectFilters.Build([Fx(new ObjectOutlineEffect { Width = 8, Color = SKColors.Black })]);
        Assert.NotNull(filter);
        using var bmp = RenderWithFilter(filter);

        Assert.Equal(SKColors.Red, bmp.GetPixel(100, 100));      // 中心仍是內容
        var edge = bmp.GetPixel(100, 66);                        // 方塊上緣外 4px
        Assert.True(edge.Alpha > 0, "外框沒有畫出來");
        Assert.True(edge.Red < 80 && edge.Green < 80, $"外框應該是黑色，拿到 {edge}");
        Assert.Equal(0, bmp.GetPixel(100, 40).Alpha);            // 再往外就沒有了
    }

    [Fact]
    public void Shadow_LandsOnTheOffsetSide()
    {
        using var filter = GpuEffectFilters.Build(
            [Fx(new ObjectShadowEffect { OffsetX = 20, OffsetY = 20, Blur = 4, Opacity = 100 })]);
        Assert.NotNull(filter);
        using var bmp = RenderWithFilter(filter);

        Assert.Equal(SKColors.Red, bmp.GetPixel(100, 100));               // 內容還在
        Assert.True(bmp.GetPixel(145, 145).Alpha > 0, "右下角沒有陰影");   // 位移方向
        Assert.Equal(0, bmp.GetPixel(45, 45).Alpha);                      // 反方向沒有
    }

    [Fact]
    public void Fill_ReplacesTheColour()
    {
        using var filter = GpuEffectFilters.Build(
            [Fx(new ObjectFillEffect { Color = new SKColor(0, 200, 0), Opacity = 100 })]);
        Assert.NotNull(filter);
        using var bmp = RenderWithFilter(filter);

        var c = bmp.GetPixel(100, 100);
        Assert.True(c.Green > 150 && c.Red < 80, $"沒有被塗成綠色，拿到 {c}");
        Assert.Equal(0, bmp.GetPixel(20, 20).Alpha); // 形狀不變
    }

    [Fact]
    public void Build_ReturnsNull_ForUntranslatableStacks()
    {
        Assert.Null(GpuEffectFilters.Build([Fx(new AdjustmentEffect(new InvertAdjustment()))]));
        Assert.Null(GpuEffectFilters.Build([]));
    }
}
