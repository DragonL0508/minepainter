using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 整層內容提起（LiftLayerContent）與圖層內容框：
/// 圖層可持有畫布外像素，這組操作是把它們整批縮放回來的唯一入口。
/// </summary>
public class LayerContentLiftTests
{
    private static SKColor Px(RasterLayer layer, int x, int y)
    {
        var idx = TileIndex.FromPixel(x, y);
        var tile = layer.Surface.GetTileForRead(idx);
        if (tile == null) return SKColors.Empty;
        var rect = idx.ToPixelRect();
        var off = ((y - rect.Top) * Tile.Size + (x - rect.Left)) * 4;
        var s = tile.PixelSpan;
        return new SKColor(s[off + 2], s[off + 1], s[off + 0], s[off + 3]);
    }

    private static SKImage MakeImage(int w, int h, SKColor color)
    {
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        surface.Canvas.Clear(color);
        return surface.Snapshot();
    }

    private static EditorSession NewSession(int w = 512, int h = 512) =>
        new(ImageCodec.CreateBlankDocument(w, h, SKColors.White));

    /// <summary>貼一張 600×600 的綠圖到 512 畫布並落地（維持畫布大小的情境）。</summary>
    private static EditorSession SessionWithOversizedContent(out RasterLayer layer)
    {
        var session = NewSession();
        layer = (RasterLayer)session.Document.ActiveLayer!;
        session.PasteImage(MakeImage(600, 600, new SKColor(0, 200, 0)), new SKPointI(0, 0));
        session.CommitFloating();
        return session;
    }

    [Fact]
    public void LiftLayerContent_RecoversOutOfCanvasPixels_AndScalesBack()
    {
        // 使用者的核心情境：貼上超出畫布 → 落地 → 想把整張縮回畫布內
        using var session = SessionWithOversizedContent(out var layer);
        var undoBefore = session.History.UndoStack.Count;

        var floating = session.LiftLayerContent();
        Assert.NotNull(floating);
        Assert.True(floating!.IsWholeContent);
        Assert.Equal(SKRect.Create(0, 0, 600, 600), floating.TargetRect); // 含畫布外的完整內容

        floating.TargetRect = SKRect.Create(0, 0, 512, 512); // 縮回畫布
        session.CommitFloating();

        Assert.Equal(new SKColor(0, 200, 0), Px(layer, 300, 300)); // 內容還在
        Assert.Equal(SKColors.Empty, Px(layer, 550, 550));         // 已縮進畫布，外面清空
        Assert.Null(session.Selection);                             // 縮放圖層不是選取操作
        Assert.Equal(undoBefore + 1, session.History.UndoStack.Count);

        session.History.Undo();
        Assert.Equal(new SKColor(0, 200, 0), Px(layer, 550, 550)); // undo 連畫布外像素一起復原
    }

    [Fact]
    public void LiftLayerContent_UnmovedCommit_RestoresEverything()
    {
        // 提起後沒動就落地 → 等同取消：像素原樣放回（含畫布外）、不記歷史、不留選取
        using var session = SessionWithOversizedContent(out var layer);
        var undoBefore = session.History.UndoStack.Count;

        session.LiftLayerContent();
        session.CommitFloating();

        Assert.Null(session.Floating);
        Assert.Equal(new SKColor(0, 200, 0), Px(layer, 550, 550));
        Assert.Null(session.Selection);
        Assert.Equal(undoBefore, session.History.UndoStack.Count);
    }

    [Fact]
    public void LiftLayerContent_Cancel_RestoresEverything()
    {
        using var session = SessionWithOversizedContent(out var layer);

        var floating = session.LiftLayerContent()!;
        floating.TargetRect = SKRect.Create(0, 0, 100, 100);
        session.CancelFloating();

        Assert.Equal(new SKColor(0, 200, 0), Px(layer, 550, 550)); // Esc 完整還原
        Assert.Null(session.Selection);
    }

    [Fact]
    public void LayerContentFrame_ShownOnlyInMoveTool_AndYieldsToSelection()
    {
        using var session = SessionWithOversizedContent(out _);
        session.Selection = null; // 貼上落地後選取還在（會蓋過內容框），先取消 —— 與真實流程一致

        session.ActiveTool = session.Move;
        Assert.Equal(SKRect.Create(0, 0, 600, 600), session.SelectionHandles); // 內容框（含畫布外）

        session.ActiveTool = session.Brush;
        Assert.Null(session.SelectionHandles); // 繪畫工具下不顯示

        // 有選取時讓位給選取框（優先序）
        session.ActiveTool = session.Move;
        using var path = new SKPath();
        path.AddRect(SKRect.Create(10, 10, 50, 50));
        session.Selection = Selections.SelectionMask.FromPath(path, session.Document.Bounds);
        Assert.Equal(SKRect.Create(10, 10, 50, 50), session.SelectionHandles);
    }
}
