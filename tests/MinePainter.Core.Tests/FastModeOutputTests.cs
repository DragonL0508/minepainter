using MinePainter.Core.Compositing;
using MinePainter.Core.Documents;
using MinePainter.Core.Effects;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 快速模式：畫布是 1080p 級的代理，輸出時整份放大重算成專案真正的解析度。
/// 這裡守的是「重算」真的有發生 —— 文字是以新尺寸重畫的，不是把 1080p 拉大。
/// </summary>
public class FastModeOutputTests
{
    private static (Document Doc, RasterLayer Layer, TextElement Text) ProxyDocWithText(int w, int h, float fontSize)
    {
        var doc = ImageCodec.CreateBlankDocument(w, h, SKColors.White);
        var layer = new RasterLayer { Name = "文字" };
        var text = new TextElement
        {
            Text = "MinePainter",
            Position = new SKPoint(w * 0.1f, h * 0.4f),
            FontSize = fontSize,
            Color = SKColors.Black,
        };
        lock (doc.SyncRoot)
        {
            doc.Root.Add(layer);
            layer.AddElement(text);
            doc.ActiveLayer = layer;
        }
        return (doc, layer, text);
    }

    /// <summary>邊緣柔化的像素數：把 1080p 拉大會多出一堆灰邊，重畫則不會。</summary>
    private static int SoftPixels(SKImage image)
    {
        using var bitmap = SKBitmap.FromImage(image);
        var soft = 0;
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var p = bitmap.GetPixel(x, y);
            var lum = (p.Red * 299 + p.Green * 587 + p.Blue * 114) / 1000;
            if (lum > 40 && lum < 215) soft++;
        }
        return soft;
    }

    [Theory]
    [InlineData(1920, 1080, false)]  // 剛好 Full HD：不必問
    [InlineData(1920, 1081, true)]
    [InlineData(3840, 2160, true)]
    [InlineData(1280, 720, false)]
    public void 只有比FullHD大才提議快速模式(int w, int h, bool offer)
        => Assert.Equal(offer, FastMode.ShouldOffer(w, h));

    [Theory]
    [InlineData(3840, 2160, 1920, 1080)]   // 4K → 一半
    [InlineData(2560, 1440, 1920, 1080)]
    [InlineData(4000, 1000, 1920, 480)]    // 超寬：以寬度為準
    [InlineData(1000, 4000, 270, 1080)]    // 直向：以高度為準
    [InlineData(800, 600, 800, 600)]       // 本來就小：不縮
    public void 代理畫布等比裝進FullHD(int w, int h, int pw, int ph)
    {
        var (proxyW, proxyH) = FastMode.ProxySize(w, h);
        Assert.Equal(pw, proxyW);
        Assert.Equal(ph, proxyH);
    }

    [Fact]
    public void 輸出解析度預設跟著畫布_設定之後才是快速模式()
    {
        using var doc = ImageCodec.CreateBlankDocument(960, 540, SKColors.White);
        Assert.False(doc.IsFastMode);
        Assert.Equal(960, doc.OutputWidth);

        doc.SetOutputSize(3840, 2160);
        Assert.True(doc.IsFastMode);
        Assert.Equal(3840, doc.OutputWidth);
        Assert.Equal(4f, doc.OutputScale, 3);

        doc.SetOutputSize(960, 540); // 與畫布相同＝回到一般模式
        Assert.False(doc.IsFastMode);
    }

    [Fact]
    public void 輸出時文字是以新尺寸重畫的_不是把小圖拉大()
    {
        var (doc, _, _) = ProxyDocWithText(480, 270, 48f);
        using (doc)
        {
            doc.SetOutputSize(1920, 1080);

            using var output = OutputRender.Render(doc);
            Assert.Equal(1920, output.Width);
            Assert.Equal(1080, output.Height);

            // 對照組：把代理解析度的合成結果直接拉大 4 倍
            using var proxy = Compositor.RenderComposite(doc);
            using var surface = SKSurface.Create(new SKImageInfo(1920, 1080, SKColorType.Bgra8888, SKAlphaType.Premul));
            surface.Canvas.Clear(SKColors.White);
            using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High })
            {
                surface.Canvas.DrawImage(proxy, SKRect.Create(1920, 1080), paint);
            }
            using var upscaled = surface.Snapshot();

            var redrawn = SoftPixels(output);
            var stretched = SoftPixels(upscaled);
            Assert.True(redrawn * 2 < stretched,
                $"重畫的灰邊 {redrawn} 應該遠少於拉大的 {stretched}（多了代表其實只是被拉大）");
        }
    }

    [Fact]
    public void 效果的像素長度會跟著放大()
    {
        using var doc = ImageCodec.CreateBlankDocument(480, 270, SKColors.Transparent);
        var layer = new RasterLayer { Name = "圖" };
        layer.Surface.Fill(new SKRectI(100, 60, 300, 200), SKColors.Red);
        lock (doc.SyncRoot)
        {
            doc.Root.Add(layer);
            layer.SetEffects([
                LayerEffect.Create(new ObjectOutlineEffect { Width = 5 }),
                LayerEffect.Create(new GaussianBlurEffect { Radius = 8 }),
            ]);
        }

        using var scaled = OutputRender.CloneScaled(doc, 1920, 1080);
        var copy = Assert.IsType<RasterLayer>(scaled.Root.Children.First(c => c.Name == "圖"));
        var outline = Assert.IsType<ObjectOutlineEffect>(copy.Effects[0].Effect);
        var blur = Assert.IsType<GaussianBlurEffect>(copy.Effects[1].Effect);
        Assert.Equal(20, outline.Width);      // 5px × 4
        Assert.Equal(32f, blur.Radius);       // 8px × 4
    }

    [Fact]
    public void 存檔讀檔會記得輸出解析度()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mp_fast_{Guid.NewGuid():N}.mpp");
        try
        {
            var (doc, _, _) = ProxyDocWithText(480, 270, 32f);
            using (doc)
            {
                doc.SetOutputSize(1920, 1080);
                MppFormat.Save(doc, path);
            }

            using var loaded = MppFormat.Load(path);
            Assert.True(loaded.IsFastMode);
            Assert.Equal(1920, loaded.OutputWidth);
            Assert.Equal(1080, loaded.OutputHeight);
            Assert.Equal(480, loaded.Width); // 畫布仍是代理尺寸
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void 匯出時的尺寸就是專案的輸出解析度()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mp_fast_{Guid.NewGuid():N}.png");
        try
        {
            var (doc, _, _) = ProxyDocWithText(480, 270, 32f);
            using (doc)
            {
                doc.SetOutputSize(1920, 1080);
                MppFormat.Export(doc, path);
            }

            using var stream = File.OpenRead(path);
            using var image = SKBitmap.Decode(stream);
            Assert.Equal(1920, image.Width);
            Assert.Equal(1080, image.Height);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// 「轉成完整解析度」＝一般的調整影像大小，所以效果與文字外觀也要跟著放大，而且可以復原。
    /// </summary>
    [Fact]
    public void 轉成完整解析度會連效果一起放大_而且可復原()
    {
        using var session = new Tools.EditorSession(ImageCodec.CreateBlankDocument(480, 270, SKColors.Transparent));
        var doc = session.Document;
        var layer = new RasterLayer { Name = "圖" };
        layer.Surface.Fill(new SKRectI(100, 60, 300, 200), SKColors.Red);
        lock (doc.SyncRoot)
        {
            doc.Root.Add(layer);
            layer.SetEffects([LayerEffect.Create(new ObjectOutlineEffect { Width = 6 })]);
        }
        doc.SetOutputSize(1920, 1080);

        History.ImageCommands.ResizeImage(session, doc.OutputWidth, doc.OutputHeight, "轉成完整解析度");
        lock (doc.SyncRoot) doc.SetOutputSize(0, 0);

        Assert.Equal(1920, doc.Width);
        Assert.False(doc.IsFastMode);
        Assert.Equal(24, ((ObjectOutlineEffect)layer.Effects[0].Effect).Width); // 6px × 4

        session.History.Undo();
        Assert.Equal(480, doc.Width);
        Assert.Equal(6, ((ObjectOutlineEffect)layer.Effects[0].Effect).Width);
    }

    [Fact]
    public void 複製出來的文件與原文件互不影響()
    {
        var (doc, layer, _) = ProxyDocWithText(480, 270, 48f);
        using (doc)
        {
            using var scaled = OutputRender.CloneScaled(doc, 960, 540);
            var copy = Assert.IsType<RasterLayer>(scaled.Root.Children.First(c => c.Name == "文字"));

            lock (scaled.SyncRoot) copy.Name = "改過的";
            Assert.Equal("文字", layer.Name);
            Assert.Equal(480, doc.Width);
            Assert.Equal(960, scaled.Width);
        }
    }
}
