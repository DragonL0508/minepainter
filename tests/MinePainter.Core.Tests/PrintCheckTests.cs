using MinePainter.Core.Documents;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 送印檢查對應印刷廠的兩條規則：底圖要延伸到出血、圖文不要貼邊。
/// 判準要能分辨「滿版底圖」（本來就該蓋到出血）與「圖文」（要待在安全框內）。
/// </summary>
public class PrintCheckTests
{
    private const float Dpi = 300f;

    /// <summary>名片：畫布 1087 × 661（裁切 90 × 54 mm、出血 1 mm）。</summary>
    private static Document Card(bool backgroundCoversBleed)
    {
        var size = PrintSpec.CanvasSizeFor(90, 54, 1, Dpi);
        var doc = ImageCodec.CreateBlankDocument(size.Width, size.Height, SKColors.Transparent, "底圖", Dpi);
        doc.Print = new PrintSpec(1, 3);
        var background = (RasterLayer)doc.Root.Children[0];
        lock (doc.SyncRoot)
        {
            var fill = backgroundCoversBleed
                ? new SKRectI(0, 0, size.Width, size.Height)
                : SKRectI.Round(doc.Print.TrimRect(doc)); // 只填到裁切線＝忘了出血
            background.Surface.Fill(fill, SKColors.White);
        }
        return doc;
    }

    private static RasterLayer AddLayer(Document doc, string name, SKRectI rect)
    {
        var layer = new RasterLayer { Name = name };
        lock (doc.SyncRoot)
        {
            layer.Surface.Fill(rect, SKColors.Black);
            doc.Root.Add(layer);
        }
        return layer;
    }

    [Fact]
    public void 底圖鋪滿且圖文在安全框內_通過()
    {
        using var doc = Card(backgroundCoversBleed: true);
        AddLayer(doc, "文字", new SKRectI(200, 200, 500, 300)); // 遠離邊緣

        var result = PrintCheck.Run(doc)!;
        Assert.True(result.BleedCovered);
        Assert.Empty(result.LayersOutsideSafe);
        Assert.True(result.Ok);
    }

    [Fact]
    public void 底圖只填到裁切線_出血沒鋪滿()
    {
        using var doc = Card(backgroundCoversBleed: false);

        var result = PrintCheck.Run(doc)!;
        Assert.False(result.BleedCovered, "出血環整圈都是透明的，卻說鋪滿了");
        Assert.True(result.UncoveredBleedRatio > 0.9, $"出血環幾乎全空，卻只算出 {result.UncoveredBleedRatio:P0}");
        Assert.False(result.Ok);
    }

    [Fact]
    public void 滿版底圖不會被當成貼邊的圖文()
    {
        using var doc = Card(backgroundCoversBleed: true);

        var result = PrintCheck.Run(doc)!;
        Assert.Empty(result.LayersOutsideSafe); // 底圖蓋滿畫布＝它就是要延伸到出血的那一層
    }

    [Fact]
    public void 圖文超出安全框_指名圖層()
    {
        using var doc = Card(backgroundCoversBleed: true);
        // 安全框在距畫布邊 1 + 3 = 4 mm ≈ 47 px；這個框從 20 px 開始，明顯超出去
        AddLayer(doc, "logo", new SKRectI(20, 20, 300, 200));
        AddLayer(doc, "電話", new SKRectI(200, 200, 500, 300)); // 這個在安全框內

        var result = PrintCheck.Run(doc)!;
        Assert.Equal(["logo"], result.LayersOutsideSafe);
    }

    [Fact]
    public void 藏起來的圖層不算()
    {
        using var doc = Card(backgroundCoversBleed: true);
        var hidden = AddLayer(doc, "被藏起來的舊版", new SKRectI(0, 0, 100, 100));
        hidden.IsVisible = false;

        Assert.Empty(PrintCheck.Run(doc)!.LayersOutsideSafe);
    }

    [Fact]
    public void 圖層效果往外畫的部分也算進範圍()
    {
        using var doc = Card(backgroundCoversBleed: true);
        // 剛好貼齊安全框內緣：本身沒問題，但加上外框就伸出去了
        var safe = SKRectI.Round(doc.Print!.SafeRect(doc));
        var layer = AddLayer(doc, "有外框的字", new SKRectI(safe.Left, safe.Top, safe.Left + 200, safe.Top + 100));
        Assert.Empty(PrintCheck.Run(doc)!.LayersOutsideSafe);

        lock (doc.SyncRoot)
            layer.SetEffects([Effects.LayerEffect.Create(new Effects.ObjectOutlineEffect { Width = 12 })]);

        Assert.Contains("有外框的字", PrintCheck.Run(doc)!.LayersOutsideSafe);
    }

    [Fact]
    public void 沒有印刷規格就沒有檢查結果()
    {
        using var doc = ImageCodec.CreateBlankDocument(64, 64, SKColors.White);
        Assert.Null(PrintCheck.Run(doc));
    }

    [Fact]
    public void 出血零時不檢查出血()
    {
        using var doc = Card(backgroundCoversBleed: false);
        doc.Print = new PrintSpec(0, 3);
        Assert.True(PrintCheck.Run(doc)!.BleedCovered);
    }
}
