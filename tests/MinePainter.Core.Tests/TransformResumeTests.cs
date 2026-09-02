using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 「縮小落地後再拉大不能糊」：變形框／浮動內容落地後留下續接點，
/// 只要 history 頂端還是那一步，對同一目標再變形就從最初的原始像素重取樣。
/// </summary>
public class TransformResumeTests
{
    private static SKColor Px(RasterLayer layer, int docX, int docY)
    {
        var lx = docX - layer.Offset.X;
        var ly = docY - layer.Offset.Y;
        var idx = TileIndex.FromPixel(lx, ly);
        var tile = layer.Surface.GetTileForRead(idx);
        if (tile == null) return SKColors.Transparent;
        var rect = idx.ToPixelRect();
        using var pixmap = tile.AsPixmap();
        return pixmap.GetPixelColor(lx - rect.Left, ly - rect.Top);
    }

    /// <summary>16×16 高頻棋盤：任何重取樣殘留都會現形。</summary>
    private static void FillChecker(RasterLayer layer, int left, int top)
    {
        for (var y = 0; y < 16; y++)
            for (var x = 0; x < 16; x++)
                if ((x + y) % 2 == 0)
                    layer.Surface.Fill(new SKRectI(left + x, top + y, left + x + 1, top + y + 1), SKColors.Black);
    }

    private static void AssertChecker(RasterLayer layer, int left, int top, SKColor background)
    {
        for (var y = 0; y < 16; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                var expected = (x + y) % 2 == 0 ? SKColors.Black : background;
                Assert.Equal(expected, Px(layer, left + x, top + y));
            }
        }
    }

    [Fact]
    public void Transform_ShrinkCommit_ThenEnlargeBack_IsLossless_AndUndoStepsBack()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(512, 512, SKColors.White));
        var doc = session.Document;
        var layer = (RasterLayer)doc.ActiveLayer!;
        lock (doc.SyncRoot) FillChecker(layer, 100, 100);
        session.ActiveTool = session.Move;

        // 第一輪：縮到 1/8 並落地
        var t1 = session.BeginTransform()!;
        Assert.False(t1.IsResumed);
        var source = t1.SourceRect;
        t1.TargetRect = new SKRect(source.Left, source.Top,
            source.Left + source.Width / 8, source.Top + source.Height / 8);
        t1.Apply(preview: false);
        session.CommitTransform();
        Assert.Single(session.History.UndoStack);
        Assert.NotEqual(SKColors.Black, Px(layer, 100 + 15, 100 + 15)); // 真的縮小了

        // 第二輪：對同一圖層再開變形 → 續接（像素仍是原始那份），拉回原尺寸原位
        var t2 = session.BeginTransform()!;
        Assert.True(t2.IsResumed);
        Assert.Equal(source.Size, t2.ResetSize);
        t2.TargetRect = source;
        t2.Apply(preview: true);   // 拖曳中
        t2.Apply(preview: false);  // 手勢結束
        session.CommitTransform();

        AssertChecker(layer, 100, 100, SKColors.White); // 逐位元回到原狀，不糊
        Assert.Equal(2, session.History.UndoStack.Count);

        // undo 只退回「縮小後」的狀態，不是一步跳回最初
        Assert.True(session.Undo());
        Assert.NotEqual(SKColors.Black, Px(layer, 100 + 15, 100 + 15));
        Assert.True(session.Undo());
        AssertChecker(layer, 100, 100, SKColors.White);
    }

    [Fact]
    public void Transform_ResumeDropped_WhenAnotherEditHappensInBetween()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(512, 512, SKColors.White));
        var doc = session.Document;
        var layer = (RasterLayer)doc.ActiveLayer!;
        lock (doc.SyncRoot) FillChecker(layer, 100, 100);
        session.ActiveTool = session.Move;

        var t1 = session.BeginTransform()!;
        t1.TargetRect = new SKRect(t1.SourceRect.Left, t1.SourceRect.Top,
            t1.SourceRect.Left + t1.SourceRect.Width / 2, t1.SourceRect.Top + t1.SourceRect.Height / 2);
        t1.Apply(preview: false);
        session.CommitTransform();

        // 中間多了別的步驟 → 續接點失效，下一輪從目前像素重新提起
        session.History.Push(new ActionHistoryEntry("別的編輯", SKRectI.Empty, _ => { }, _ => { }));
        var t2 = session.BeginTransform()!;
        Assert.False(t2.IsResumed);
        session.CancelTransform();
    }

    [Fact]
    public void Floating_ShrinkCommit_ThenLiftAgain_UsesOriginalPixels()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(512, 512, SKColors.White));
        var doc = session.Document;
        var layer = (RasterLayer)doc.ActiveLayer!;
        lock (doc.SyncRoot) FillChecker(layer, 100, 100);

        using var path = new SKPath();
        path.AddRect(SKRect.Create(100, 100, 16, 16));
        session.Selection = Selections.SelectionMask.FromPath(path, doc.Bounds);

        // 提起、縮到 4×4、落地
        var f1 = session.LiftSelection()!;
        f1.TargetRect = SKRect.Create(100, 100, 4, 4);
        session.CommitFloating();
        Assert.NotNull(session.Selection);
        Assert.Equal(new SKRectI(100, 100, 104, 104), session.Selection!.Bounds);

        // 再提起：像素是落地前的原始 16×16，而不是圖層上已縮成 4×4 的那份
        var f2 = session.LiftSelection()!;
        Assert.Equal(new SKSizeI(16, 16), f2.PixelSize);
        Assert.Equal(new SKRectI(100, 100, 104, 104), f2.SourceBounds);

        f2.TargetRect = SKRect.Create(100, 100, 16, 16);
        session.CommitFloating();
        AssertChecker(layer, 100, 100, SKColors.White); // 拉回原尺寸 = 逐位元還原
    }
}
