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

    /// <summary>
    /// 門檻是設定值（預設 1080）：調成 720p 之後，大於 720p 的畫布才提示，代理也縮到 720p。
    /// 靜態狀態，改完一定要還原（同一個 class 裡的測試是循序跑的）。
    /// </summary>
    [Fact]
    public void 代理級別可以調_門檻與代理尺寸都跟著走()
    {
        try
        {
            FastMode.ProxyHeight = 720;

            Assert.Equal(1280, FastMode.ProxyWidth);
            Assert.False(FastMode.ShouldOffer(1280, 720));   // 剛好 720p：不必問
            Assert.True(FastMode.ShouldOffer(1920, 1080));   // 以前不問的，現在要問
            Assert.Equal((1280, 720), FastMode.ProxySize(1920, 1080));
            Assert.Equal((1280, 720), FastMode.ProxySize(3840, 2160));
        }
        finally
        {
            FastMode.ProxyHeight = FastMode.DefaultProxyHeight;
        }

        Assert.Equal(1920, FastMode.ProxyWidth);
        Assert.False(FastMode.ShouldOffer(1920, 1080));
    }

    [Theory]
    [InlineData(360, 640)]
    [InlineData(480, 854)]
    [InlineData(720, 1280)]
    [InlineData(1080, 1920)]
    [InlineData(1440, 2560)]
    [InlineData(2160, 3840)]
    public void 代理級別的寬度照16比9算(int height, int width)
        => Assert.Equal(width, FastMode.WidthFor(height));

    /// <summary>手改壞的 settings.json 不該讓代理畫布變成 0 或大得離譜。</summary>
    [Theory]
    [InlineData(0, 120)]
    [InlineData(-5, 120)]
    [InlineData(99999, 4320)]
    public void 亂填的代理級別會被夾回合理範圍(int set, int expected)
    {
        try
        {
            FastMode.ProxyHeight = set;
            Assert.Equal(expected, FastMode.ProxyHeight);
        }
        finally
        {
            FastMode.ProxyHeight = FastMode.DefaultProxyHeight;
        }
    }

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

    /// <summary>
    /// 既有的大專案改用快速模式：畫布縮成代理、輸出解析度記成原本的尺寸，而且可以復原。
    /// </summary>
    [Fact]
    public void 一般專案可以轉成快速模式_也能復原()
    {
        using var session = new Tools.EditorSession(ImageCodec.CreateBlankDocument(3840, 2160, SKColors.White));
        var doc = session.Document;
        Assert.False(doc.IsFastMode);

        var (proxyW, proxyH) = FastMode.ProxySize(doc.Width, doc.Height);
        History.ImageCommands.ResizeImage(session, proxyW, proxyH, "轉成快速模式",
            outputWidth: 3840, outputHeight: 2160);

        Assert.Equal(1920, doc.Width);
        Assert.True(doc.IsFastMode);
        Assert.Equal(3840, doc.OutputWidth);

        session.History.Undo();
        Assert.Equal(3840, doc.Width);
        Assert.False(doc.IsFastMode);

        session.History.Redo();
        Assert.Equal(1920, doc.Width);
        Assert.True(doc.IsFastMode);
        Assert.Equal(3840, doc.OutputWidth);
    }

    /// <summary>
    /// 放進來的大圖：輸出時要從「原始高清那份」重畫，不能拿代理解析度的再放大一次。
    /// （原始那份是變形工具留下的 LayerPixelSource，.mpp 也會存。）
    /// </summary>
    [Fact]
    public void 有原始高清來源時_輸出是從原圖重畫的()
    {
        const int originalSide = 512;
        const int proxySide = 128; // 代理上縮成 1/4

        // 細格紋：縮小之後細節一定糊掉，從原圖重畫則會回來
        using var original = new SKBitmap(new SKImageInfo(originalSide, originalSide,
            SKColorType.Bgra8888, SKAlphaType.Premul));
        for (var y = 0; y < originalSide; y++)
        for (var x = 0; x < originalSide; x++)
        {
            original.SetPixel(x, y, ((x / 4) + (y / 4)) % 2 == 0 ? SKColors.Black : SKColors.White);
        }

        using var doc = ImageCodec.CreateBlankDocument(proxySide, proxySide, SKColors.White);
        var layer = new RasterLayer { Name = "圖" };
        lock (doc.SyncRoot)
        {
            doc.Root.Add(layer);
            // 代理畫布上的樣子：把原圖縮成 128×128 蓋進去
            using var small = original.Resize(new SKImageInfo(proxySide, proxySide,
                SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.High);
            using var pixmap = small.PeekPixels();
            layer.Surface.CopyFrom(pixmap, SKPointI.Empty);

            // 變形工具留下的原始高清來源：原圖 → 目前呈現（縮 1/4）
            var scale = proxySide / (float)originalSide;
            layer.SetPixelSource(new Layers.LayerPixelSource(
                SKImage.FromBitmap(original),
                new SKRectI(0, 0, originalSide, originalSide),
                SKMatrix.CreateScale(scale, scale),
                SKPointI.Empty,
                SKRect.Create(0, 0, proxySide, proxySide),
                0f,
                new SKSize(originalSide, originalSide),
                layer.Surface.Revision));
        }
        doc.SetOutputSize(originalSide, originalSide);

        using var output = OutputRender.Render(doc);
        Assert.Equal(originalSide, output.Width);

        // 從原圖重畫 → 格紋邊緣銳利（純黑純白多）；從代理放大 → 幾乎都是灰的
        using var bitmap = SKBitmap.FromImage(output);
        var crisp = 0;
        for (var y = 0; y < bitmap.Height; y += 2)
        for (var x = 0; x < bitmap.Width; x += 2)
        {
            var p = bitmap.GetPixel(x, y);
            if (p.Red < 30 || p.Red > 225) crisp++;
        }
        var sampled = (bitmap.Width / 2) * (bitmap.Height / 2);
        Assert.True(crisp > sampled * 0.8,
            $"只有 {crisp}/{sampled} 個像素是銳利的 —— 看起來是拿代理放大的，不是從原圖重畫");
    }

    /// <summary>
    /// 端到端：一般的 4K 專案轉成快速模式（畫布縮到 1080p），輸出回 4K 時
    /// 要從轉換前保留的原始像素重畫，而不是把縮過的再放大。
    /// </summary>
    [Fact]
    public void 轉成快速模式之後_輸出仍然拿得回原本的細節()
    {
        const int side = 512;
        using var session = new Tools.EditorSession(ImageCodec.CreateBlankDocument(side, side, SKColors.White));
        var doc = session.Document;
        var layer = Assert.IsType<RasterLayer>(doc.ActiveLayer);

        // 細格紋：縮小之後糊掉，從原始重畫才會回來
        lock (doc.SyncRoot)
        {
            for (var y = 0; y < side; y += 4)
            for (var x = 0; x < side; x += 4)
            {
                var colour = ((x / 4) + (y / 4)) % 2 == 0 ? SKColors.Black : SKColors.White;
                layer.Surface.Fill(new SKRectI(x, y, x + 4, y + 4), colour);
            }
        }

        History.ImageCommands.ResizeImage(session, 128, 128, "轉成快速模式",
            outputWidth: side, outputHeight: side);
        Assert.True(doc.IsFastMode);
        Assert.NotNull(layer.ValidPixelSource); // 原始像素留著了

        using var output = OutputRender.Render(doc);
        using var bitmap = SKBitmap.FromImage(output);
        var crisp = 0;
        for (var y = 0; y < bitmap.Height; y += 2)
        for (var x = 0; x < bitmap.Width; x += 2)
        {
            var p = bitmap.GetPixel(x, y);
            if (p.Red < 30 || p.Red > 225) crisp++;
        }
        var sampled = (bitmap.Width / 2) * (bitmap.Height / 2);
        Assert.True(crisp > sampled * 0.8, $"只有 {crisp}/{sampled} 個像素銳利 —— 細節沒回來");
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
    /// <summary>效果的選取遮罩是 doc 座標的，放大時要跟著重取樣，不然遮罩只蓋到左上角。</summary>
    [Fact]
    public void 效果的遮罩會跟著放大()
    {
        using var doc = ImageCodec.CreateBlankDocument(480, 270, SKColors.Transparent);
        var layer = new RasterLayer { Name = "圖" };
        layer.Surface.Fill(new SKRectI(0, 0, 480, 270), SKColors.Red);

        // 遮罩：左半邊（0..240）
        var mask = new Tiles.MaskSurface();
        var rect = new SKRectI(0, 0, 240, 270);
        foreach (var idx in Tiles.TileIndex.CoveringRect(rect))
        {
            var tile = mask.GetForWrite(idx);
            var tileRect = idx.ToPixelRect();
            var inter = SKRectI.Intersect(tileRect, rect);
            for (var y = inter.Top; y < inter.Bottom; y++)
                tile.Alpha.AsSpan((y - tileRect.Top) * Tiles.MaskTile.Size + (inter.Left - tileRect.Left), inter.Width).Fill(255);
        }
        mask.ExtendBounds(rect);

        lock (doc.SyncRoot)
        {
            doc.Root.Add(layer);
            layer.SetEffects([LayerEffect.Create(new GaussianBlurEffect { Radius = 8 }) with { Mask = mask }]);
        }

        using var scaled = OutputRender.CloneScaled(doc, 1920, 1080);
        var copy = Assert.IsType<RasterLayer>(scaled.Root.Children.First(c => c.Name == "圖"));
        var scaledMask = copy.Effects[0].Mask;
        Assert.NotNull(scaledMask);
        Assert.Equal(new SKRectI(0, 0, 960, 1080), scaledMask.Bounds);

        static byte Alpha(Tiles.MaskSurface m, int x, int y)
        {
            var idx = Tiles.TileIndex.FromPixel(x, y);
            var tile = m.GetForRead(idx);
            if (tile == null) return 0;
            var r = idx.ToPixelRect();
            return tile.Alpha[(y - r.Top) * Tiles.MaskTile.Size + (x - r.Left)];
        }
        Assert.Equal(255, Alpha(scaledMask, 900, 1000)); // 原本 225,250 在遮罩內
        Assert.Equal(0, Alpha(scaledMask, 1200, 500));   // 右半邊沒遮罩
    }

    /// <summary>調整影像大小之後復原：原始高清來源要接回去（縮放時只是借用，不是釋放）。</summary>
    [Fact]
    public void 調整影像大小復原後_原始高清來源還在()
    {
        const int side = 256;
        using var session = new Tools.EditorSession(ImageCodec.CreateBlankDocument(side, side, SKColors.White));
        var doc = session.Document;
        var layer = Assert.IsType<RasterLayer>(doc.ActiveLayer);
        lock (doc.SyncRoot) layer.Surface.Fill(new SKRectI(0, 0, side, side), SKColors.Black);

        // 第一次縮小：留下原始來源
        History.ImageCommands.ResizeImage(session, 128, 128);
        var first = layer.ValidPixelSource;
        Assert.NotNull(first);

        // 再縮一次：新來源要串在同一張原圖上（不是拿 128 的再拍一次）
        History.ImageCommands.ResizeImage(session, 64, 64);
        var second = layer.ValidPixelSource;
        Assert.NotNull(second);
        Assert.Same(first.Pixels, second.Pixels);
        Assert.Equal(side, second.Bounds.Width);

        session.History.Undo();
        Assert.Equal(128, doc.Width);
        Assert.Same(first, layer.ValidPixelSource);
        Assert.Equal(side, first.Bounds.Width); // 原圖沒被釋放：還讀得到

        session.History.Undo();
        Assert.Equal(side, doc.Width);
        Assert.Null(layer.ValidPixelSource);
    }

    /// <summary>復原一次再放大回去（重做）：從原圖重畫，細節要回來。</summary>
    [Fact]
    public void 縮小再放大回去_是從原始像素重畫的()
    {
        const int side = 512;
        using var session = new Tools.EditorSession(ImageCodec.CreateBlankDocument(side, side, SKColors.White));
        var doc = session.Document;
        var layer = Assert.IsType<RasterLayer>(doc.ActiveLayer);
        lock (doc.SyncRoot)
        {
            for (var y = 0; y < side; y += 4)
            for (var x = 0; x < side; x += 4)
                layer.Surface.Fill(new SKRectI(x, y, x + 4, y + 4), ((x / 4) + (y / 4)) % 2 == 0 ? SKColors.Black : SKColors.White);
        }

        History.ImageCommands.ResizeImage(session, 128, 128);
        History.ImageCommands.ResizeImage(session, side, side);
        Assert.Equal(side, doc.Width);

        using var bitmap = History.ImageCommands.ReadRegion(layer.Surface, new SKRectI(0, 0, side, side));
        var crisp = 0;
        for (var y = 0; y < side; y += 2)
        for (var x = 0; x < side; x += 2)
        {
            var p = bitmap.GetPixel(x, y);
            if (p.Red < 30 || p.Red > 225) crisp++;
        }
        var sampled = (side / 2) * (side / 2);
        Assert.True(crisp > sampled * 0.8, $"只有 {crisp}/{sampled} 個像素銳利 —— 是拿 128 的放大，不是從原圖重畫");
    }
}
