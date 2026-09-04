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
    [InlineData(1.0f, 1.0f)]      // 100%：照實算
    [InlineData(0.8f, 1.0f)]      // 幾乎 1:1 也照實算
    [InlineData(0.5f, 0.25f)]     // 50% 檢視 → 算一半大
    [InlineData(0.35f, 0.177f)]
    [InlineData(0.25f, 0.125f)]   // 25% 檢視
    [InlineData(0.125f, 0.0625f)]
    [InlineData(0.01f, 0.0625f)]  // 再縮也不會低於下限
    public void 檢視比例對齊到每階根號二的階梯(float view, float expected)
        => Assert.Equal(expected, EffectPreviewScale.Quantize(view), 3);

    [Fact]
    public void 階梯是單調的_放越大算越細()
    {
        var last = 0f;
        for (var view = 0.05f; view <= 1.0f; view += 0.01f)
        {
            var scale = EffectPreviewScale.Quantize(view);
            Assert.True(scale >= last - 0.0001f, $"檢視 {view:P0} 的比例 {scale} 比更小的檢視還粗");
            last = scale;
        }
    }

    [Fact]
    public void 幾何參數不會被縮到滑桿下限以下()
    {
        // 外框寬度 5px：縮到 1/8 會變 0.6px、被夾成 1px，放大回來就是 8px 的框（粗 60%）
        var effects = new List<LayerEffect> { LayerEffect.Create(new ObjectOutlineEffect { Width = 5 }) };
        var safe = EffectPreviewScale.SafeScale(effects, 0.0625f);
        Assert.True(safe >= 0.2f - 0.001f, $"比例 {safe} 會讓 5px 的外框被夾寬");

        // 半徑 80 的模糊沒有這個問題（下限 0），可以縮到底
        var blur = new List<LayerEffect> { LayerEffect.Create(new GaussianBlurEffect { Radius = 80 }) };
        Assert.Equal(EffectPreviewScale.MinScale, EffectPreviewScale.SafeScale(blur, EffectPreviewScale.MinScale), 3);
    }

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
            Assert.True(layer.FxCache.PreviewScale < 1f, "縮著看就該用降解析度算");
            Assert.True(layer.FxCache.Rendered);

            // 使用者放大到 100%：畫面需要更細的東西，快取要重算
            doc.PreviewScale = 1f;
            LayerEffectRenderer.RenderLayerNow(doc, layer);
            Assert.Equal(1f, layer.FxCache.PreviewScale);
        }
    }

    /// <summary>
    /// 同一份快取只能有一種解析度：拖完之後只有一小塊被重算，那塊不能用全解析度算
    /// —— 不然物件會有一部分突然變清楚。
    /// </summary>
    [Fact]
    public void 局部重算沿用快取現在的比例()
    {
        // 半徑大的模糊：SafeScale 的下限管不到它，比例才會真的跟著檢視走
        var (doc, layer) = BigBlurredLayer(LayerEffect.Create(new GaussianBlurEffect { Radius = 80 }));
        using (doc)
        {
            doc.PreviewScale = 0.25f;
            LayerEffectRenderer.RenderLayerNow(doc, layer);
            var preview = layer.FxCache.PreviewScale;
            Assert.True(preview < 1f);

            // 使用者又縮更小（想要的比例變粗了），然後只改一小塊。
            // 縮小不會讓快取重算（現有的更細，夠用），所以這次局部更新必須沿用快取的比例，
            // 不然那一小塊會比周圍粗。
            doc.PreviewScale = 0.1f;
            Assert.True(EffectPreviewScale.Quantize(0.1f) < preview, "這條測試要的是『想要的比例比快取粗』");

            lock (doc.SyncRoot) layer.Surface.Fill(new SKRectI(400, 400, 500, 500), SKColors.Blue);
            layer.Invalidate(new SKRectI(400, 400, 500, 500));
            LayerEffectRenderer.RenderLayerNow(doc, layer);

            Assert.Equal(preview, layer.FxCache.PreviewScale);
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
            Assert.True(layer.FxCache.PreviewScale < 1f);

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
            Assert.True(layerB.FxCache.PreviewScale < 1f);

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
