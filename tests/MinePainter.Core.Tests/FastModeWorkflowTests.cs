using MinePainter.Core.Documents;
using MinePainter.Core.Effects;
using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 使用者實際的快速模式流程（2026-09-05 回報「去背後輸出還是糊」）：
/// 開快速模式專案 → 放進一張圖 → 變形縮小 → AI 去背 → 套效果 → 輸出。
/// 每一步之後原始高清來源都要還在，輸出的邊緣要銳利。
/// </summary>
public class FastModeWorkflowTests
{
    private const int Output = 512, Proxy = 128;

    private static SKBitmap Disc(int side)
    {
        var bitmap = new SKBitmap(new SKImageInfo(side, side, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        using var paint = new SKPaint { Color = SKColors.Red, IsAntialias = true };
        canvas.DrawCircle(side / 2f, side / 2f, side * 0.3f, paint);
        canvas.Flush();
        return bitmap;
    }

    /// <summary>輸出中某一列上半透明像素的數量：從原圖重畫只有幾個，放大來的會有一大段軟邊。</summary>
    private static int SoftEdgePixels(Document doc, int row)
    {
        using var output = OutputRender.Render(doc);
        using var bitmap = SKBitmap.FromImage(output);
        var soft = 0;
        for (var x = 0; x < bitmap.Width; x++)
        {
            var a = bitmap.GetPixel(x, row).Alpha;
            if (a is > 20 and < 235) soft++;
        }
        return soft;
    }

    private static EditorSession FastModeSession()
    {
        var session = new EditorSession(ImageCodec.CreateBlankDocument(Proxy, Proxy, SKColors.White));
        session.Document.SetOutputSize(Output, Output);
        Assert.True(session.Document.IsFastMode);
        return session;
    }

    [Fact]
    public void 匯入_變形縮小_去背_套效果_輸出仍銳利()
    {
        using var session = FastModeSession();
        var doc = session.Document;

        // 1. 放進一張與輸出同大的圖（快速模式：像素縮到代理、原圖留成來源）
        using var original = Disc(Output);
        var layer = ImageCommands.ImportImageLayer(session, original, "圖");
        Assert.NotNull(layer.ValidPixelSource);

        // 2. 變形工具縮到畫布的 3/4
        var t = session.BeginTransform();
        Assert.NotNull(t);
        t.TargetRect = SKRect.Create(16, 16, 96, 96);
        session.CommitTransform();
        Assert.NotNull(layer.ValidPixelSource);

        // 3. 去背（本機演算）
        Assert.True(BackgroundRemovalCommand.Run(session, layer, new BackgroundRemovalOptions()));
        Assert.NotNull(layer.ValidPixelSource);
        var soft = SoftEdgePixels(doc, Output / 2);
        Assert.True(soft <= 8, $"去背後輸出的圓邊有 {soft} 個半透明像素 —— 是拿代理放大的");

        // 4. 非破壞性效果：外框
        LayerEffectCommands.Add(doc, session.History, layer, LayerEffect.Create(new ObjectOutlineEffect { Width = 3 }));
        Assert.NotNull(layer.ValidPixelSource);

        // 5. 輸出：圓的邊仍銳利（外框本身會多一段實心，但不該出現大片軟邊）
        soft = SoftEdgePixels(doc, Output / 2);
        Assert.True(soft <= 24, $"套效果後輸出的圓邊有 {soft} 個半透明像素");
    }

    [Fact]
    public void 匯入_去背_烙印效果_輸出仍銳利()
    {
        using var session = FastModeSession();
        var doc = session.Document;
        using var original = Disc(Output);
        var layer = ImageCommands.ImportImageLayer(session, original, "圖");

        Assert.True(BackgroundRemovalCommand.Run(session, layer, new BackgroundRemovalOptions()));
        LayerEffectCommands.Add(doc, session.History, layer, LayerEffect.Create(new GaussianBlurEffect { Radius = 0 }));
        Assert.True(LayerEffectCommands.Bake(session, layer));

        Assert.NotNull(layer.ValidPixelSource);
        var soft = SoftEdgePixels(doc, Output / 2);
        Assert.True(soft <= 8, $"烙印後輸出的圓邊有 {soft} 個半透明像素 —— 是拿代理放大的");
    }

    [Fact]
    public void 匯入_套效果_再去背_輸出仍銳利()
    {
        using var session = FastModeSession();
        var doc = session.Document;
        using var original = Disc(Output);
        var layer = ImageCommands.ImportImageLayer(session, original, "圖");

        // 先套效果再去背：去背前會先平面化效果，原始來源也要跟著平面化而不是丟掉
        LayerEffectCommands.Add(doc, session.History, layer, LayerEffect.Create(new GaussianBlurEffect { Radius = 0 }));
        Assert.True(BackgroundRemovalCommand.Run(session, layer, new BackgroundRemovalOptions()));

        Assert.NotNull(layer.ValidPixelSource);
        var soft = SoftEdgePixels(doc, Output / 2);
        Assert.True(soft <= 8, $"輸出的圓邊有 {soft} 個半透明像素 —— 是拿代理放大的");
    }

    [Fact]
    public void 貼上大圖_去背_輸出仍銳利()
    {
        using var session = FastModeSession();
        var doc = session.Document;
        var background = Assert.IsType<RasterLayer>(doc.ActiveLayer);

        // 剪貼簿一張與輸出同大的圖：快速模式照代理比例縮，且因為背景層有內容，貼到新圖層
        using var original = Disc(Output);
        Assert.Equal((Proxy, Proxy), session.PastedSize(Output, Output));
        Assert.True(session.PasteImage(SKImage.FromBitmap(original), SKPointI.Empty));
        Assert.Equal(new SKSizeI(Proxy, Proxy), session.Floating!.PixelSize);
        session.CommitFloating();

        var layer = Assert.IsType<RasterLayer>(doc.ActiveLayer);
        Assert.NotSame(background, layer);
        var source = layer.ValidPixelSource;
        Assert.NotNull(source);
        Assert.Equal(Output, source.Bounds.Width);

        Assert.True(BackgroundRemovalCommand.Run(session, layer, new BackgroundRemovalOptions()));
        Assert.NotNull(layer.ValidPixelSource);
        var soft = SoftEdgePixels(doc, Output / 2);
        Assert.True(soft <= 8, $"輸出的圓邊有 {soft} 個半透明像素 —— 是拿代理放大的");

        // undo 兩步（去背、貼上）：新圖層一起消失
        session.History.Undo();
        session.History.Undo();
        Assert.Same(background, doc.ActiveLayer);
        Assert.Single(doc.Descendants());
    }
}
