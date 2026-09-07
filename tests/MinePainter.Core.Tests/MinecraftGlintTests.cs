using MinePainter.Core.Documents;
using MinePainter.Core.Effects;
using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;
using static MinePainter.Core.Effects.EffectMath;

namespace MinePainter.Core.Tests;

public class MinecraftGlintTests
{
    private static uint[] Render(MinecraftGlintEffect effect, uint[] source, int width, int height)
    {
        var ctx = EffectContext.FromPixels(source, width, height);
        effect.Render(ctx);
        return ctx.Dst;
    }

    [Fact]
    public void 零強度不改原圖()
    {
        var source = Enumerable.Range(0, 256).Select(a => Premul(70, 110, 180, a)).ToArray();
        Assert.Equal(source, Render(new MinecraftGlintEffect { Strength = 0 }, source, 16, 16));
    }

    [Fact]
    public void 所有透明度都保留_邊緣不產生色暈()
    {
        var source = Enumerable.Range(0, 256).Select(a => Premul(70, 110, 180, a)).ToArray();
        var output = Render(new MinecraftGlintEffect { Strength = 100 }, source, 16, 16);
        for (var i = 0; i < output.Length; i++)
        {
            Assert.Equal(A(source[i]), A(output[i]));
            Assert.InRange(B(output[i]), 0, A(output[i]));
            Assert.InRange(G(output[i]), 0, A(output[i]));
            Assert.InRange(R(output[i]), 0, A(output[i]));
        }
        Assert.Equal(0u, output[0]);
    }

    [Fact]
    public void 預設是有亮暗紋理的紫光_保留原有明暗細節()
    {
        var source = Enumerable.Repeat(Pack(90, 90, 90, 255), 128 * 128).ToArray();
        var output = Render(new MinecraftGlintEffect(), source, 128, 128);
        Assert.True(output.Average(p => B(p) - G(p)) > 10, "光澤應偏紫，不是灰色提亮");
        Assert.True(output.Max(B) - output.Min(B) > 40, "亮帶應有明暗紋理，不是整片塗紫");
        var dark = Render(new MinecraftGlintEffect(), Enumerable.Repeat(Pack(20, 20, 20, 255), source.Length).ToArray(), 128, 128);
        Assert.True(output.Zip(dark, (a, b) => R(a) - R(b)).Average() > 40, "附魔不能蓋掉物件原有的明暗細節");
    }

    [Fact]
    public void 附魔是增亮_不能把鑽石的青綠與高光染暗()
    {
        // 使用者提供的原圖色票：附魔圖只增加光，綠色高光會到 255，不會往紫色混合而變暗。
        uint[] colors = [Pack(203, 235, 51, 255), Pack(240, 253, 164, 255), Pack(32, 37, 8, 255), Pack(255, 255, 255, 255)];
        var source = Enumerable.Range(0, 64 * 64).Select(i => colors[i % colors.Length]).ToArray();
        var output = Render(new MinecraftGlintEffect(), source, 64, 64);
        for (var i = 0; i < output.Length; i++)
        {
            Assert.True(R(output[i]) >= R(source[i]), "紅色不能被染暗");
            Assert.True(G(output[i]) >= G(source[i]), "鑽石的綠色細節不能被染暗");
            Assert.True(B(output[i]) >= B(source[i]), "藍色光澤只能增亮");
        }
    }

    [Fact]
    public void 預設光色符合參考圖的增亮比例()
    {
        // 參考圖未飽和像素的增量中位數：R/B 約 0.376、G/B 約 0.165。
        var output = Render(new MinecraftGlintEffect(), Enumerable.Repeat(Pack(20, 20, 20, 255), 96 * 96).ToArray(), 96, 96);
        var blue = output.Average(p => B(p) - 20);
        Assert.True(blue > 45, "暗色物件上應有明顯藍紫增亮");
        Assert.InRange(output.Average(p => R(p) - 20) / blue, 0.35, 0.40);
        Assert.InRange(output.Average(p => G(p) - 20) / blue, 0.15, 0.18);
    }

    [Fact]
    public void 相同位置的光量不隨底色改變()
    {
        var effect = new MinecraftGlintEffect { Strength = 50 };
        var dark = Render(effect, Enumerable.Repeat(Pack(10, 10, 10, 255), 64 * 64).ToArray(), 64, 64);
        var light = Render(effect, Enumerable.Repeat(Pack(90, 90, 90, 255), 64 * 64).ToArray(), 64, 64);
        Assert.InRange(dark.Zip(light, (a, b) => Math.Abs(R(b) - R(a) - 80)).Max(), 0, 1);
    }

    [Fact]
    public void 光澤位置會改變亮帶_同參數重畫保持一致()
    {
        var source = Enumerable.Repeat(Pack(90, 90, 90, 255), 64 * 64).ToArray();
        var original = Render(new MinecraftGlintEffect(), source, 64, 64);
        var shifted = Render(new MinecraftGlintEffect { Phase = 35 }, source, 64, 64);
        Assert.True(original.Zip(shifted, (a, b) => Math.Abs(B(a) - B(b))).Average() > 10);
        Assert.Equal(original, Render(new MinecraftGlintEffect(), source, 64, 64));
    }

    [Fact]
    public void 分區重算沿用圖層座標_不會出現拼接縫()
    {
        var effect = new MinecraftGlintEffect();
        var source = Enumerable.Repeat(Pack(90, 90, 90, 255), 96 * 64).ToArray();
        var full = new EffectContext(new SKRectI(-32, -16, 64, 48), new SKRectI(-32, -16, 64, 48), source, new SKSizeI(96, 64));
        effect.Render(full);
        var part = new EffectContext(new SKRectI(-10, 5, 24, 30), full.SrcRect, source, full.DocSize);
        effect.Render(part);
        for (var y = 0; y < part.Height; y++)
        for (var x = 0; x < part.Width; x++)
        {
            var expected = full.Dst[(y + 21) * full.Width + x + 22];
            Assert.InRange(Math.Abs(B(expected) - B(part.Dst[y * part.Width + x])), 0, 1);
        }
    }

    [Fact]
    public void 快速模式輸出會放大紋理尺寸_不改強度位置與顏色()
    {
        var effect = new MinecraftGlintEffect { Scale = 40, Strength = 63, Phase = 28 };
        var entry = ScaleRules.ScaleEffect(LayerEffect.Create(effect), 3, 3, clampToSlider: false);
        var scaled = Assert.IsType<MinecraftGlintEffect>(entry.Effect);
        Assert.Equal(effect with { Scale = 120 }, scaled);
    }

    [Fact]
    public void 移動圖層後快速模式匯出不改變物件上的亮帶位置()
    {
        using var original = MakeFastDocument(SKPointI.Empty);
        using var moved = MakeFastDocument(new SKPointI(17, 5));
        using var first = OutputRender.Render(original);
        using var second = OutputRender.Render(moved);
        using var a = SKBitmap.FromImage(first);
        using var b = SKBitmap.FromImage(second);
        var differences = new List<int>();
        for (var y = 12; y < 48; y++)
        for (var x = 12; x < 48; x++)
            differences.Add(Math.Abs(a.GetPixel(x, y).Blue - b.GetPixel(x + 34, y + 10).Blue));
        Assert.InRange(differences.Average(), 0, 1);

        static Document MakeFastDocument(SKPointI offset)
        {
            var doc = ImageCodec.CreateBlankDocument(64, 64, SKColors.Transparent);
            var layer = (RasterLayer)doc.ActiveLayer!;
            lock (doc.SyncRoot)
            {
                layer.Surface.Fill(new SKRectI(4, 4, 28, 28), SKColors.Gray);
                layer.Offset = offset;
                layer.SetEffects([LayerEffect.Create(new MinecraftGlintEffect { Scale = 16 })]);
                doc.SetOutputSize(128, 128);
            }
            return doc;
        }
    }

    [Fact]
    public void 預覽取消不留效果_確定後可一步復原重做()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(48, 48, SKColors.Gray));
        var layer = Assert.IsType<RasterLayer>(session.Document.ActiveLayer);
        var effect = new MinecraftGlintEffect();
        using (var preview = new LayerEffectPreview(session, layer, LayerEffect.Create(effect), isNew: true))
        {
            preview.Preview(effect with { Phase = 35 }, CancellationToken.None);
            preview.Cancel();
        }
        Assert.Empty(layer.Effects);
        Assert.Empty(session.History.UndoStack);
        using (var preview = new LayerEffectPreview(session, layer, LayerEffect.Create(effect), isNew: true))
            preview.Commit(effect);
        Assert.Single(session.History.UndoStack);
        Assert.True(session.Undo());
        Assert.Empty(layer.Effects);
        Assert.True(session.Redo());
        Assert.IsType<MinecraftGlintEffect>(Assert.Single(layer.Effects).Effect);
        using var original = ImageCommands.ReadRegion(layer.Surface, session.Document.Bounds);
        Assert.Equal(SKColors.Gray, original.GetPixel(24, 24));
    }

    [Fact]
    public void Mpp保留所有參數_重新開啟後輸出一致()
    {
        using var doc = ImageCodec.CreateBlankDocument(64, 64, SKColors.Gray);
        var layer = Assert.IsType<RasterLayer>(doc.ActiveLayer);
        var effect = new MinecraftGlintEffect { Strength = 83, Scale = 29, Phase = 42, Angle = 23, RelativeToObject = false, Color = new SKColor(180, 70, 240) };
        lock (doc.SyncRoot) layer.SetEffects([LayerEffect.Create(effect)]);
        var path = Path.Combine(Path.GetTempPath(), $"glint-{Guid.NewGuid():N}.mpp");
        try
        {
            MppFormat.Save(doc, path);
            using var restored = MppFormat.Load(path);
            var savedLayer = Assert.IsType<RasterLayer>(restored.Root.Children[0]);
            Assert.Equal(effect, Assert.IsType<MinecraftGlintEffect>(Assert.Single(savedLayer.Effects).Effect));
            using var first = OutputRender.Render(doc);
            using var second = OutputRender.Render(restored);
            using var a = SKBitmap.FromImage(first);
            using var b = SKBitmap.FromImage(second);
            Assert.True(a.Pixels.Average(p => p.Blue - p.Green) > 10, "匯出應包含附魔效果");
            Assert.InRange(a.Pixels.Zip(b.Pixels, (p, q) => Math.Abs(p.Blue - q.Blue)).Average(), 0, 1);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Psd無對應樣式時保留可見附魔結果並回報烙印()
    {
        using var doc = ImageCodec.CreateBlankDocument(48, 48, SKColors.Gray);
        var layer = Assert.IsType<RasterLayer>(doc.ActiveLayer);
        lock (doc.SyncRoot) layer.SetEffects([LayerEffect.Create(new MinecraftGlintEffect())]);
        using var stream = new MemoryStream();
        PsdFormat.Save(doc, stream, null, out var warnings);
        Assert.NotEmpty(warnings);
        stream.Position = 0;
        using var restored = PsdFormat.Load(stream, out _);
        using var output = OutputRender.Render(restored);
        using var pixels = SKBitmap.FromImage(output);
        Assert.True(pixels.Pixels.Average(p => p.Blue - p.Green) > 10, "PSD 不能默默遺失附魔光澤");
    }
}
