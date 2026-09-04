using MinePainter.Core.Documents;
using MinePainter.Core.Compositing;
using MinePainter.Core.Effects;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 效果的降解析度預覽：畫面縮著看的時候就算粗一點的，輸出時一律重算全解析度。
/// 這裡守的是「省得對」與「該精準的地方一定精準」。
/// </summary>
public class EffectPreviewScaleTests
{
    private static (Document Doc, RasterLayer Layer) BigBlurredLayer(params LayerEffect[] effects)
    {
        var doc = ImageCodec.CreateBlankDocument(2048, 1024, SKColors.Transparent);
        var layer = new RasterLayer { Name = "大圖" };
        layer.Surface.Fill(new SKRectI(100, 100, 1900, 900), SKColors.Red);
        lock (doc.SyncRoot)
        {
            doc.Root.Add(layer);
            layer.SetEffects(effects.Length > 0
                ? effects
                : [LayerEffect.Create(new GaussianBlurEffect())]);
        }
        return (doc, layer);
    }

    [Theory]
    [InlineData(1.0f, 1.0f)]
    [InlineData(0.8f, 1.0f)]
    [InlineData(0.5f, 0.5f)]
    [InlineData(0.4f, 0.5f)]
    [InlineData(0.25f, 0.25f)]
    [InlineData(0.2f, 0.25f)]
    [InlineData(0.1f, 0.125f)]
    [InlineData(0.01f, 0.125f)]
    public void 檢視比例對齊到二的冪次(float view, float expected)
        => Assert.Equal(expected, EffectPreviewScale.Quantize(view));

    [Fact]
    public void 幾何參數會跟著比例縮()
    {
        var outline = new ObjectOutlineEffect { Width = 20, Softness = 40 };
        var half = (ObjectOutlineEffect)EffectPreviewScale.Scale(outline, 0.5f);
        Assert.Equal(10, half.Width);      // 像素長度：跟著縮
        Assert.Equal(40, half.Softness);   // 百分比：不動
    }

    [Fact]
    public void 縮著看的時候用降解析度算_放大回去要重算()
    {
        var (doc, layer) = BigBlurredLayer();
        using (doc)
        {
            doc.PreviewScale = 0.25f;
            LayerEffectRenderer.RenderLayerNow(doc, layer);
            Assert.Equal(0.25f, layer.FxCache.PreviewScale);
            Assert.True(layer.FxCache.Rendered);

            // 使用者放大到 100%：畫面需要更細的東西，快取要重算
            doc.PreviewScale = 1f;
            LayerEffectRenderer.RenderLayerNow(doc, layer);
            Assert.Equal(1f, layer.FxCache.PreviewScale);
        }
    }

    [Fact]
    public void 輸出一律重算全解析度()
    {
        var (doc, layer) = BigBlurredLayer();
        using (doc)
        {
            doc.PreviewScale = 0.25f;
            LayerEffectRenderer.RenderLayerNow(doc, layer);
            Assert.Equal(0.25f, layer.FxCache.PreviewScale);

            using var composite = Compositor.RenderComposite(doc); // 匯出／拼合走這條
            Assert.Equal(1f, layer.FxCache.PreviewScale);
        }
    }

    [Fact]
    public void 不支援縮放的效果照舊全解析度()
    {
        // 像素化看的是絕對格線，縮了會對不上（IsPositionIndependent = false）
        var (doc, layer) = BigBlurredLayer(LayerEffect.Create(new PixelateEffect()));
        using (doc)
        {
            doc.PreviewScale = 0.25f;
            LayerEffectRenderer.RenderLayerNow(doc, layer);
            Assert.Equal(1f, layer.FxCache.PreviewScale);
        }
    }

    /// <summary>
    /// 最重要的一條：畫面上是降解析度的預覽，匯出得到的必須與「從頭全解析度算」逐像素相同。
    /// 這條垮掉就是「使用者以為在做 4K，實際輸出是放大的預覽」。
    /// </summary>
    [Fact]
    public void 匯出結果與全程全解析度算出來的一模一樣()
    {
        static SKBitmap Render(float previewScale)
        {
            var (doc, layer) = BigBlurredLayer(
                LayerEffect.Create(new ObjectOutlineEffect { Width = 16 }),
                LayerEffect.Create(new GaussianBlurEffect()));
            using (doc)
            {
                doc.PreviewScale = previewScale;
                LayerEffectRenderer.RenderLayerNow(doc, layer);
                using var composite = Compositor.RenderComposite(doc);
                var bitmap = new SKBitmap(new SKImageInfo(doc.Width, doc.Height,
                    SKColorType.Bgra8888, SKAlphaType.Premul));
                Assert.True(composite.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0));
                return bitmap;
            }
        }

        using var exact = Render(1f);
        using var viaPreview = Render(0.25f);

        for (var y = 0; y < exact.Height; y += 7)
        for (var x = 0; x < exact.Width; x += 7)
        {
            Assert.Equal(exact.GetPixel(x, y), viaPreview.GetPixel(x, y));
        }
    }

    [Fact]
    public void 降解析度的結果與全解析度大致相同()
    {
        var (docA, layerA) = BigBlurredLayer(LayerEffect.Create(new ObjectOutlineEffect { Width = 24 }));
        var (docB, layerB) = BigBlurredLayer(LayerEffect.Create(new ObjectOutlineEffect { Width = 24 }));
        using (docA)
        using (docB)
        {
            docA.PreviewScale = 1f;
            LayerEffectRenderer.RenderLayerNow(docA, layerA);
            docB.PreviewScale = 0.25f;
            LayerEffectRenderer.RenderLayerNow(docB, layerB);
            Assert.Equal(0.25f, layerB.FxCache.PreviewScale);

            // 外框往內容外長出來的範圍要差不多（差一格 tile 以內）
            var a = layerA.FxCache.Surface.ContentBounds;
            var b = layerB.FxCache.Surface.ContentBounds;
            Assert.True(Math.Abs(a.Left - b.Left) <= Tile.Size, $"左 {a.Left} vs {b.Left}");
            Assert.True(Math.Abs(a.Right - b.Right) <= Tile.Size, $"右 {a.Right} vs {b.Right}");
            Assert.True(Math.Abs(a.Top - b.Top) <= Tile.Size, $"上 {a.Top} vs {b.Top}");
            Assert.True(Math.Abs(a.Bottom - b.Bottom) <= Tile.Size, $"下 {a.Bottom} vs {b.Bottom}");
        }
    }
}
