using MinePainter.Core.Documents;
using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 出血與安全框。廠商的規格：名片裁切 90 × 54 mm、出血後 92 × 56 mm、圖文離裁切邊 3 mm 以上。
/// 「裁切線＝畫布內縮出血」的定義要能在換 dpi、改畫布之後仍然自洽。
/// </summary>
public class PrintSpecTests
{
    private const double Dpi = 300;

    [Fact]
    public void 名片含出血的畫布尺寸()
    {
        // 92 × 56 mm @ 300 dpi
        var size = PrintSpec.CanvasSizeFor(90, 54, bleedMm: 1, dpi: Dpi);
        Assert.Equal(1087, size.Width);
        Assert.Equal(661, size.Height);

        // 3 mm 出血版：96 × 60 mm
        var wide = PrintSpec.CanvasSizeFor(90, 54, bleedMm: 3, dpi: Dpi);
        Assert.Equal(1134, wide.Width);
        Assert.Equal(709, wide.Height);
    }

    [Fact]
    public void 裁切線與安全框的位置()
    {
        var spec = new PrintSpec(1, 3);
        var size = PrintSpec.CanvasSizeFor(90, 54, 1, Dpi);
        var trim = spec.TrimRect(size.Width, size.Height, Dpi);
        var safe = spec.SafeRect(size.Width, size.Height, Dpi);

        // 裁切後回到 90 × 54 mm（畫布是四捨五入到整數像素的，容 0.1 mm）
        Assert.Equal(90, PrintSpec.PixelsToMm(trim.Width, Dpi), 1);
        Assert.Equal(54, PrintSpec.PixelsToMm(trim.Height, Dpi), 1);

        // 安全框比裁切線再往內 3 mm（兩邊共 6 mm）
        Assert.Equal(84, PrintSpec.PixelsToMm(safe.Width, Dpi), 1);
        Assert.Equal(48, PrintSpec.PixelsToMm(safe.Height, Dpi), 1);
        Assert.Equal(PrintSpec.MmToPixels(4, Dpi), safe.Left, 1); // 距畫布邊 1 + 3 mm
    }

    [Fact]
    public void 出血零時裁切線就是畫布邊()
    {
        var spec = new PrintSpec(0, 3);
        var trim = spec.TrimRect(1000, 500, Dpi);
        Assert.Equal(SKRect.Create(1000, 500), trim);
    }

    [Fact]
    public void 誇張的內縮退成中線而不是反向矩形()
    {
        var spec = new PrintSpec(PrintSpec.MaxMm, PrintSpec.MaxMm); // 100 mm 出血套在很小的畫布上
        var trim = spec.TrimRect(100, 100, Dpi);
        Assert.True(trim.Width >= 0 && trim.Height >= 0, "矩形反向了：輔助線會畫成負的");
        Assert.Equal(50, trim.MidX, 1);
    }

    [Fact]
    public void 超出範圍的數值會被夾住()
    {
        Assert.Equal(0, new PrintSpec(-5, 3).BleedMm);
        Assert.Equal(PrintSpec.MaxMm, new PrintSpec(9999, 3).BleedMm);
        Assert.Equal(0, new PrintSpec(double.NaN, 3).BleedMm);
    }

    [Fact]
    public void 送印預設集帶著規格且畫布含出血()
    {
        var preset = Array.Find(PhysicalUnits.Presets, p => p.Spec is { BleedMm: 1 });
        Assert.NotNull(preset);
        Assert.Equal(1087, preset!.Width);
        Assert.Equal(661, preset.Height);
        Assert.Equal(3, preset.Spec!.SafeMm);
    }

    [Fact]
    public void 套用規格_改畫布與設定併成一步undo()
    {
        // 已經畫好的 90 × 54 mm 名片，廠商才說要出血
        var trim = PrintSpec.CanvasSizeFor(90, 54, bleedMm: 0, dpi: Dpi);
        using var doc = ImageCodec.CreateBlankDocument(trim.Width, trim.Height, SKColors.White, dpi: (float)Dpi);
        using var session = new EditorSession(doc);
        var before = doc.Width;

        var target = PrintSpec.CanvasSizeFor(90, 54, bleedMm: 1, dpi: Dpi);
        DocumentCommands.ApplyPrintSpec(session, new PrintSpec(1, 3), target.Width, target.Height, (float)Dpi);

        Assert.Equal(target.Width, doc.Width);
        Assert.Equal(1, doc.Print!.BleedMm);
        Assert.Single(session.History.UndoStack); // 使用者按了一次「套用」，就該只有一步

        session.Undo();
        Assert.Equal(before, doc.Width);
        Assert.Null(doc.Print);
    }

    [Fact]
    public void 套用規格_只改設定不改畫布()
    {
        using var doc = ImageCodec.CreateBlankDocument(1087, 661, SKColors.White, dpi: (float)Dpi);
        using var session = new EditorSession(doc);

        DocumentCommands.ApplyPrintSpec(session, new PrintSpec(1, 3), doc.Width, doc.Height, (float)Dpi);
        Assert.Equal(1087, doc.Width);
        Assert.Single(session.History.UndoStack);

        // 一模一樣再套一次不該多一步
        DocumentCommands.ApplyPrintSpec(session, new PrintSpec(1, 3), doc.Width, doc.Height, (float)Dpi);
        Assert.Single(session.History.UndoStack);
    }

    [Fact]
    public void 規格與dpi存進mpp再讀回來()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mp-print-{Guid.NewGuid():N}.mpp");
        try
        {
            using (var doc = ImageCodec.CreateBlankDocument(1087, 661, SKColors.White, dpi: 300f))
            {
                doc.Print = new PrintSpec(1, 3);
                MppFormat.Save(doc, path);
            }

            using var loaded = MppFormat.Load(path);
            Assert.Equal(1, loaded.Print!.BleedMm);
            Assert.Equal(3, loaded.Print.SafeMm);
            Assert.Equal(300f, loaded.Dpi);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void 沒有印刷規格的檔案讀回來還是沒有()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mp-print-{Guid.NewGuid():N}.mpp");
        try
        {
            using (var doc = ImageCodec.CreateBlankDocument(64, 64, SKColors.White))
            {
                MppFormat.Save(doc, path);
            }

            using var loaded = MppFormat.Load(path);
            Assert.Null(loaded.Print);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>輔助線是畫面上的東西：匯出的像素裡不能有它。</summary>
    [Fact]
    public void 匯出不含輔助線()
    {
        using var doc = ImageCodec.CreateBlankDocument(200, 120, SKColors.White, dpi: (float)Dpi);
        doc.Print = new PrintSpec(3, 3);

        using var image = OutputRender.Render(doc);
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        Assert.True(image.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0));

        for (var y = 0; y < info.Height; y++)
            for (var x = 0; x < info.Width; x++)
                Assert.Equal(SKColors.White, bitmap.GetPixel(x, y));
    }

    /// <summary>群組裡的圖層也要被逐層檢查（訊息要指得到真正的圖層）。</summary>
    [Fact]
    public void 群組裡超出安全框的圖層也會被指出來()
    {
        using var doc = ImageCodec.CreateBlankDocument(1000, 1000, SKColors.White, dpi: (float)Dpi);
        doc.Print = new PrintSpec(3, 3);
        lock (doc.SyncRoot)
        {
            var group = new GroupLayer { Name = "群組" };
            var inner = new RasterLayer { Name = "貼邊的圖" };
            inner.Surface.Fill(new SKRectI(0, 0, 20, 20), SKColors.Red); // 貼在畫布左上角
            group.Add(inner);
            doc.Root.Add(group);
        }

        var result = PrintCheck.Run(doc);
        Assert.NotNull(result);
        Assert.Contains("貼邊的圖", result!.LayersOutsideSafe);
    }
}
