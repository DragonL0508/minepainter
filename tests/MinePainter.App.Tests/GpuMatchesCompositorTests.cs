using MinePainter.Core.Adjustments;
using MinePainter.App.Rendering;
using MinePainter.Core.Compositing;
using MinePainter.Core.Documents;
using MinePainter.Core.Effects;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.App.Tests;

/// <summary>
/// GPU 圖層渲染路徑畫出來的，必須跟合成器（＝匯出得到的那份）一致。
///
/// 這條線曾經被跨過一次：效果堆疊被翻成 Skia 濾鏡交給 GPU 算，但 Skia 的 dilate 是**方形**核心，
/// 而外框走的是精確歐氏距離場 —— 15px 的外框把中文筆畫糊成一塊塊方塊，畫面與匯出結果不一樣
/// （使用者拿實際專案回報）。效果一律以 CPU 算好的那份為準，這裡守的就是那個「一律」。
/// </summary>
public class GpuMatchesCompositorTests
{
    private const int Size = 384;

    public static TheoryData<string> Cases() =>
    [
        "外框", "光暈", "陰影", "漸層", "塗色", "羽化", "多層外框加陰影",
        "調整效果", "帶遮罩的效果", "群組效果", "調整圖層", "圖層不透明度與混合模式", "文字物件加效果",
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public void 與合成器逐像素一致(string what)
    {
        var doc = ImageCodec.CreateBlankDocument(Size, Size, SKColors.White);
        var bottom = (RasterLayer)doc.ActiveLayer!;
        bottom.Surface.Fill(new SKRectI(0, 0, Size, Size), new SKColor(0x30, 0x60, 0x90));
        var session = new EditorSession(doc);
        Build(doc, what);
        LayerEffectRenderer.RenderAllNow(doc);

        var info = new SKImageInfo(doc.Width, doc.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        using (var renderer = new GpuLayerRenderer())
        {
            surface.Canvas.Clear(SKColors.Transparent);
            lock (doc.SyncRoot)
            {
                Assert.True(renderer.TryDraw(surface.Canvas, session, new SKRectI(0, 0, doc.Width, doc.Height)));
            }
        }
        surface.Canvas.Flush();

        using var gpu = surface.Snapshot();
        using var cpu = Compositor.RenderComposite(doc);
        var (differing, worst) = Diff(gpu, cpu, info);
        Assert.True(differing == 0, $"{what}：有 {differing} 個像素與合成器不同（最大差 {worst}/255）");
    }

    private static LayerEffect Fx(IEffect effect) => LayerEffect.Create(effect, color: SKColors.Black);

    private static void Build(Document doc, string what)
    {
        switch (what)
        {
            case "外框":
                Shape(doc, [Fx(new ObjectOutlineEffect { Width = 15, Color = SKColors.White })]);
                break;
            case "光暈":
                Shape(doc, [Fx(new ObjectGlowEffect { Size = 22, Spread = 12, Opacity = 95 })]);
                break;
            case "陰影":
                Shape(doc, [Fx(new ObjectShadowEffect { OffsetX = 8, OffsetY = 10, Blur = 12 })]);
                break;
            case "漸層":
                Shape(doc, [Fx(new ObjectGradientEffect { Angle = 35f })]);
                break;
            case "塗色":
                Shape(doc, [Fx(new ObjectFillEffect { Color = SKColors.Orange, Opacity = 80 })]);
                break;
            case "羽化":
                Shape(doc, [Fx(new ObjectFeatherEffect { Radius = 12 })]);
                break;
            case "多層外框加陰影":
                Shape(doc, [
                    Fx(new ObjectOutlineEffect { Width = 6, Color = SKColors.Crimson }),
                    Fx(new ObjectOutlineEffect { Width = 9, Color = SKColors.White }),
                    Fx(new ObjectShadowEffect { OffsetX = 5, OffsetY = 7, Blur = 8, Opacity = 60 }),
                ]);
                break;
            case "調整效果":
                Shape(doc, [Fx(new AdjustmentEffect(new HueSaturationAdjustment(Hue: 0.3f)))]);
                break;
            case "帶遮罩的效果":
            {
                var layer = Shape(doc, []);
                lock (doc.SyncRoot)
                {
                    layer.SetEffects([
                        LayerEffect.Create(new ObjectGlowEffect { Size = 18 }, color: SKColors.Black) with
                        {
                            Mask = HalfMask(),
                        },
                    ]);
                }
                break;
            }
            case "群組效果":
            {
                var group = new GroupLayer { Name = "組" };
                var inner = new RasterLayer { Name = "組內" };
                lock (doc.SyncRoot)
                {
                    doc.Root.Add(group);
                    group.Add(inner);
                    inner.Surface.Fill(new SKRectI(90, 90, 260, 240), SKColors.Yellow);
                    group.SetEffects([Fx(new ObjectOutlineEffect { Width = 10, Color = SKColors.Black })]);
                }
                break;
            }
            case "調整圖層":
            {
                Shape(doc, [Fx(new ObjectShadowEffect { OffsetX = 6, OffsetY = 6, Blur = 8 })]);
                lock (doc.SyncRoot)
                {
                    doc.Root.Add(new AdjustmentLayer(new BrightnessContrastAdjustment(Brightness: 0.25f)));
                    doc.Root.Add(new AdjustmentLayer(new HueSaturationAdjustment(Saturation: -0.4f))
                    {
                        Opacity = 0.5f,
                    });
                }
                break;
            }
            case "圖層不透明度與混合模式":
            {
                var layer = Shape(doc, [Fx(new ObjectOutlineEffect { Width = 8, Color = SKColors.White })]);
                lock (doc.SyncRoot)
                {
                    layer.Opacity = 0.55f;
                    layer.BlendMode = BlendMode.Multiply;
                }
                break;
            }
            case "文字物件加效果":
            {
                var layer = new RasterLayer { Name = "文字" };
                lock (doc.SyncRoot)
                {
                    doc.Root.Add(layer);
                    layer.AddElement(new TextElement
                    {
                        Text = "尋找",
                        FontSize = 110,
                        Position = new SKPoint(40, 220),
                        Color = SKColors.Red,
                        Rotation = -12f,
                    });
                    layer.SetEffects([
                        Fx(new ObjectGradientEffect { Angle = 94f }),
                        Fx(new ObjectOutlineEffect { Width = 15, Color = SKColors.White }),
                        Fx(new ObjectGlowEffect { Size = 22, Spread = 12, Opacity = 95 }),
                    ]);
                }
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(what), what, "沒有這個案例");
        }
    }

    /// <summary>細一點的形狀：方形核心的膨脹在細節上最容易露餡。</summary>
    private static RasterLayer Shape(Document doc, IReadOnlyList<LayerEffect> effects)
    {
        var layer = new RasterLayer { Name = "內容" };
        lock (doc.SyncRoot)
        {
            doc.Root.Add(layer);
            layer.Surface.Fill(new SKRectI(80, 100, 300, 118), SKColors.Yellow);
            layer.Surface.Fill(new SKRectI(140, 100, 158, 280), SKColors.Yellow);
            layer.Surface.Fill(new SKRectI(200, 200, 260, 214), SKColors.Yellow);
            if (effects.Count > 0) layer.SetEffects(effects);
        }
        return layer;
    }

    /// <summary>左半邊全開、右半邊全關的遮罩。</summary>
    private static MaskSurface HalfMask()
    {
        var mask = new MaskSurface();
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size / 2; x++)
        {
            var idx = TileIndex.FromPixel(x, y);
            var tile = mask.GetForWrite(idx);
            var r = idx.ToPixelRect();
            tile.Alpha[(y - r.Top) * MaskTile.Size + (x - r.Left)] = 255;
        }
        mask.ExtendBounds(new SKRectI(0, 0, Size / 2, Size));
        return mask;
    }

    private static (long Differing, int Worst) Diff(SKImage a, SKImage b, SKImageInfo info)
    {
        using var ba = new SKBitmap(info);
        using var bb = new SKBitmap(info);
        Assert.True(a.ReadPixels(info, ba.GetPixels(), info.RowBytes, 0, 0));
        Assert.True(b.ReadPixels(info, bb.GetPixels(), info.RowBytes, 0, 0));
        var pa = ba.Bytes;
        var pb = bb.Bytes;
        long differing = 0;
        var worst = 0;
        for (var i = 0; i < pa.Length; i += 4)
        {
            var d = 0;
            for (var c = 0; c < 4; c++) d = Math.Max(d, Math.Abs(pa[i + c] - pb[i + c]));
            if (d > worst) worst = d;
            if (d > 1) differing++; // 允許 1/255 的四捨五入
        }
        return (differing, worst);
    }
}
