using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Selections;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

public class FloatingSelectionTests
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

    private static EditorSession SessionWithRedSquare()
    {
        var session = new EditorSession(ImageCodec.CreateBlankDocument(512, 512, SKColors.White));
        var layer = (RasterLayer)session.Document.ActiveLayer!;
        layer.Surface.Fill(new SKRectI(100, 100, 200, 200), new SKColor(255, 0, 0));

        using var path = new SKPath();
        path.AddRect(SKRect.Create(100, 100, 100, 100));
        session.Selection = SelectionMask.FromPath(path, session.Document.Bounds);
        return session;
    }

    [Fact]
    public void MoveTool_LiftsSelectionAndMovesIt()
    {
        using var session = SessionWithRedSquare();
        var layer = (RasterLayer)session.Document.ActiveLayer!;

        // 按在選取範圍內 → 提起
        session.Move.OnPointerDown(new ToolPointerEvent(new SKPoint(150, 150), 1f), session);
        Assert.NotNull(session.Floating);

        // 原處已被挖空（露出下方的白）
        Assert.NotEqual(new SKColor(255, 0, 0), Px(layer, 150, 150));

        // 移動 +200,+100
        session.Move.OnPointerMove(new ToolPointerEvent(new SKPoint(350, 250), 1f), session);
        session.Move.OnPointerUp(new ToolPointerEvent(new SKPoint(350, 250), 1f), session);

        var floating = session.Floating!;
        Assert.Equal(300, floating.TargetRect.Left, 1);
        Assert.Equal(200, floating.TargetRect.Top, 1);

        // 提交後像素落在新位置
        session.CommitFloating();
        Assert.Null(session.Floating);
        Assert.Equal(new SKColor(255, 0, 0), Px(layer, 350, 250));
        // 原處留下透明 —— 選取範圍內的像素（含同層的白底）整塊被搬走了
        Assert.Equal(0, Px(layer, 150, 150).Alpha);
    }

    [Fact]
    public void FloatingMove_IsSingleUndoStep()
    {
        using var session = SessionWithRedSquare();
        var layer = (RasterLayer)session.Document.ActiveLayer!;
        var undoBefore = session.History.UndoStack.Count;

        session.Move.OnPointerDown(new ToolPointerEvent(new SKPoint(150, 150), 1f), session);
        session.Move.OnPointerMove(new ToolPointerEvent(new SKPoint(250, 150), 1f), session);
        session.Move.OnPointerMove(new ToolPointerEvent(new SKPoint(350, 250), 1f), session);
        session.Move.OnPointerUp(new ToolPointerEvent(new SKPoint(350, 250), 1f), session);
        session.CommitFloating();

        // 一整趟提起+移動+提交 = 一步 undo
        Assert.Equal(undoBefore + 1, session.History.UndoStack.Count);

        session.History.Undo();
        Assert.Equal(new SKColor(255, 0, 0), Px(layer, 150, 150)); // 回到原位
        Assert.Equal(SKColors.White, Px(layer, 350, 250));
    }

    [Fact]
    public void UndoWhileStillFloating_UndoesTheMove()
    {
        // 框選 → 移動 → Ctrl+Z：此時內容還浮動著（像素已從圖層挖走但還沒進 history）。
        // 走 session.Undo() 會先落地再復原，結果就是「這次移動被取消」，而不是去動到上一步。
        using var session = SessionWithRedSquare();
        var layer = (RasterLayer)session.Document.ActiveLayer!;

        session.Move.OnPointerDown(new ToolPointerEvent(new SKPoint(150, 150), 1f), session);
        session.Move.OnPointerMove(new ToolPointerEvent(new SKPoint(350, 250), 1f), session);
        session.Move.OnPointerUp(new ToolPointerEvent(new SKPoint(350, 250), 1f), session);
        Assert.NotNull(session.Floating);

        session.Undo();

        Assert.Null(session.Floating);
        Assert.Equal(new SKColor(255, 0, 0), Px(layer, 150, 150)); // 像素回到原位
        Assert.Equal(SKColors.White, Px(layer, 350, 250));         // 新位置沒有殘留
        Assert.Equal(255, session.Selection!.CoverageAt(150, 150)); // 選取框也回到原位
        Assert.Equal(0, session.Selection.CoverageAt(350, 250));
    }

    [Fact]
    public void UndoWhileStillFloating_IsRedoable()
    {
        // 落地後再 undo，所以這一步是可以 redo 回來的
        using var session = SessionWithRedSquare();
        var layer = (RasterLayer)session.Document.ActiveLayer!;

        session.Move.OnPointerDown(new ToolPointerEvent(new SKPoint(150, 150), 1f), session);
        session.Move.OnPointerMove(new ToolPointerEvent(new SKPoint(350, 250), 1f), session);
        session.Move.OnPointerUp(new ToolPointerEvent(new SKPoint(350, 250), 1f), session);
        session.Undo();

        Assert.True(session.History.CanRedo);
        session.History.Redo();
        Assert.Equal(new SKColor(255, 0, 0), Px(layer, 350, 250));
        Assert.Equal(0, Px(layer, 150, 150).Alpha);
    }

    [Fact]
    public void UndoWhileStillFloating_DoesNotSkipToPreviousStep()
    {
        // 修掉的 bug：直接呼叫 History.Undo() 會略過還沒落地的移動、
        // 去復原「上一步」（這裡是填色），留下「像素被挖走卻沒有對應歷史」的狀態。
        using var session = SessionWithRedSquare();
        var layer = (RasterLayer)session.Document.ActiveLayer!;

        // 先做一步有記錄的編輯當作「上一步」
        using (var before = layer.Surface.Snapshot())
        {
            var rect = new SKRectI(300, 300, 400, 400);
            layer.Surface.Fill(rect, new SKColor(0, 0, 255));
            var entry = History.TileDeltaEntry.Capture("填色", layer, before, rect);
            Assert.NotNull(entry);
            session.History.Push(entry!);
        }
        Assert.Equal(new SKColor(0, 0, 255), Px(layer, 350, 350));

        session.Move.OnPointerDown(new ToolPointerEvent(new SKPoint(150, 150), 1f), session);
        session.Move.OnPointerMove(new ToolPointerEvent(new SKPoint(150, 400), 1f), session);
        session.Move.OnPointerUp(new ToolPointerEvent(new SKPoint(150, 400), 1f), session);

        session.Undo();

        // 復原的是移動，不是填色
        Assert.Equal(new SKColor(0, 0, 255), Px(layer, 350, 350));
        Assert.Equal(new SKColor(255, 0, 0), Px(layer, 150, 150));
    }

    [Fact]
    public void SelectionFollowsFloatingContent()
    {
        // Pinta 的 MoveSelectedTool 對選取套用同一個變換 —— 框要跟著內容走
        using var session = SessionWithRedSquare();

        session.Move.OnPointerDown(new ToolPointerEvent(new SKPoint(150, 150), 1f), session);
        session.Move.OnPointerMove(new ToolPointerEvent(new SKPoint(350, 250), 1f), session);

        // 拖曳期間：輪廓已經在新位置（不重新柵格化，只變換路徑）
        using (var outline = session.Floating!.GetTransformedOutline())
        {
            Assert.NotNull(outline);
            Assert.Equal(300, outline!.Bounds.Left, 2);
            Assert.Equal(200, outline.Bounds.Top, 2);
        }

        session.Move.OnPointerUp(new ToolPointerEvent(new SKPoint(350, 250), 1f), session);
        session.CommitFloating();

        // 落地後選取遮罩本身也在新位置
        Assert.Equal(255, session.Selection!.CoverageAt(350, 250));
        Assert.Equal(0, session.Selection.CoverageAt(150, 150));

        // 一步 undo 同時還原像素與選取框
        session.History.Undo();
        Assert.Equal(255, session.Selection!.CoverageAt(150, 150));
        Assert.Equal(0, session.Selection.CoverageAt(350, 250));
    }

    [Fact]
    public void ClickOutsideSelection_Deselects()
    {
        using var session = SessionWithRedSquare();
        Assert.NotNull(session.Selection);

        // 在選取範圍外點一下（沒有拖曳）
        session.Move.OnPointerDown(new ToolPointerEvent(new SKPoint(400, 400), 1f), session);
        session.Move.OnPointerUp(new ToolPointerEvent(new SKPoint(400, 400), 1f), session);

        Assert.Null(session.Selection);
        Assert.Null(session.SelectionHandles);
    }

    [Fact]
    public void DragOutsideSelection_MovesSelectionNotLayer()
    {
        using var session = SessionWithRedSquare();
        var layer = (RasterLayer)session.Document.ActiveLayer!;

        // 選取範圍外「拖曳」→ 一樣是移動選取的內容（paint.net / Pinta 行為），
        // 圖層本身不能被平移，選取也不該被取消
        session.Move.OnPointerDown(new ToolPointerEvent(new SKPoint(400, 400), 1f), session);
        session.Move.OnPointerMove(new ToolPointerEvent(new SKPoint(430, 400), 1f), session);
        session.Move.OnPointerUp(new ToolPointerEvent(new SKPoint(430, 400), 1f), session);

        Assert.NotNull(session.Selection);
        Assert.Equal(SKPointI.Empty, layer.Offset);

        var floating = Assert.IsType<FloatingSelection>(session.Floating);
        Assert.Equal(130, floating.TargetRect.Left, 1); // 原本 100，往右 30
        Assert.Equal(100, floating.TargetRect.Top, 1);

        session.CommitFloating();
        Assert.Equal(new SKColor(255, 0, 0), Px(layer, 180, 150)); // 紅方塊搬到新位置
        Assert.Equal(0, Px(layer, 110, 150).Alpha);                // 原處被挖空
    }

    [Fact]
    public void DragOutsideFloating_KeepsMovingTheFloatingContent()
    {
        using var session = SessionWithRedSquare();

        // 先提起並移動一次
        session.Move.OnPointerDown(new ToolPointerEvent(new SKPoint(150, 150), 1f), session);
        session.Move.OnPointerMove(new ToolPointerEvent(new SKPoint(250, 150), 1f), session);
        session.Move.OnPointerUp(new ToolPointerEvent(new SKPoint(250, 150), 1f), session);
        Assert.NotNull(session.Floating);

        // 再從浮動內容外面拖 → 繼續帶著它跑，不會落地也不會動圖層
        session.Move.OnPointerDown(new ToolPointerEvent(new SKPoint(450, 450), 1f), session);
        session.Move.OnPointerMove(new ToolPointerEvent(new SKPoint(460, 450), 1f), session);
        session.Move.OnPointerUp(new ToolPointerEvent(new SKPoint(460, 450), 1f), session);

        var floating = Assert.IsType<FloatingSelection>(session.Floating);
        Assert.Equal(210, floating.TargetRect.Left, 1); // 100 + 100 + 10
        Assert.Equal(SKPointI.Empty, ((RasterLayer)session.Document.ActiveLayer!).Offset);
    }

    [Fact]
    public void ClickOutsideFloating_CommitsAndDeselects()
    {
        using var session = SessionWithRedSquare();
        var layer = (RasterLayer)session.Document.ActiveLayer!;

        session.Move.OnPointerDown(new ToolPointerEvent(new SKPoint(150, 150), 1f), session);
        session.Move.OnPointerMove(new ToolPointerEvent(new SKPoint(350, 250), 1f), session);
        session.Move.OnPointerUp(new ToolPointerEvent(new SKPoint(350, 250), 1f), session);

        // 浮動內容外點一下（沒拖曳）→ 落地 + 取消選取
        session.Move.OnPointerDown(new ToolPointerEvent(new SKPoint(60, 60), 1f), session);
        session.Move.OnPointerUp(new ToolPointerEvent(new SKPoint(60, 60), 1f), session);

        Assert.Null(session.Floating);
        Assert.Null(session.Selection);
        Assert.Equal(new SKColor(255, 0, 0), Px(layer, 350, 250));
    }

    [Fact]
    public void CancelFloating_RestoresSelectionToo()
    {
        using var session = SessionWithRedSquare();

        session.Move.OnPointerDown(new ToolPointerEvent(new SKPoint(150, 150), 1f), session);
        session.Move.OnPointerMove(new ToolPointerEvent(new SKPoint(350, 350), 1f), session);
        session.CancelFloating();

        // 選取框也回到原位
        Assert.Equal(255, session.Selection!.CoverageAt(150, 150));
        Assert.Equal(0, session.Selection.CoverageAt(350, 350));
    }

    [Fact]
    public void CancelFloating_RestoresOriginalPixels()
    {
        using var session = SessionWithRedSquare();
        var layer = (RasterLayer)session.Document.ActiveLayer!;

        session.Move.OnPointerDown(new ToolPointerEvent(new SKPoint(150, 150), 1f), session);
        session.Move.OnPointerMove(new ToolPointerEvent(new SKPoint(350, 350), 1f), session);
        session.CancelFloating();

        Assert.Null(session.Floating);
        Assert.Equal(new SKColor(255, 0, 0), Px(layer, 150, 150)); // 原樣還原
        Assert.Equal(SKColors.White, Px(layer, 350, 350));
    }

    [Fact]
    public void FloatingResize_ScalesContent()
    {
        using var session = SessionWithRedSquare();
        var layer = (RasterLayer)session.Document.ActiveLayer!;

        session.Move.OnPointerDown(new ToolPointerEvent(new SKPoint(150, 150), 1f), session);
        session.Move.OnPointerUp(new ToolPointerEvent(new SKPoint(150, 150), 1f), session);
        var floating = session.Floating!;

        // 拖右下角把手放大到 2 倍（原 100×100 → 200×200）
        var corner = new SKPoint(floating.TargetRect.Right, floating.TargetRect.Bottom);
        session.Move.OnPointerDown(new ToolPointerEvent(corner, 1f), session);
        session.Move.OnPointerMove(new ToolPointerEvent(new SKPoint(300, 300), 1f), session);
        session.Move.OnPointerUp(new ToolPointerEvent(new SKPoint(300, 300), 1f), session);

        Assert.Equal(200, floating.TargetRect.Width, 1);
        Assert.Equal(200, floating.TargetRect.Height, 1);

        session.CommitFloating();
        Assert.Equal(new SKColor(255, 0, 0), Px(layer, 280, 280)); // 放大後涵蓋更遠處
    }

    [Fact]
    public void ShiftResize_KeepsAspectRatio()
    {
        // 原始 200×100 的比例，Shift 拖角應維持 2:1
        var start = SKRect.Create(0, 0, 200, 100);
        var resized = MoveTool.ResizeRect(start, corner: 2, p: new SKPoint(400, 400), keepAspect: true);
        Assert.Equal(2.0, resized.Width / resized.Height, 2);

        var free = MoveTool.ResizeRect(start, corner: 2, p: new SKPoint(400, 400), keepAspect: false);
        Assert.Equal(400, free.Width, 1);
        Assert.Equal(400, free.Height, 1);
    }

    [Fact]
    public void NoSelection_MoveToolMovesWholeLayer()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(512, 512, SKColors.White));
        var layer = (RasterLayer)session.Document.ActiveLayer!;

        session.Move.OnPointerDown(new ToolPointerEvent(new SKPoint(100, 100), 1f), session);
        session.Move.OnPointerMove(new ToolPointerEvent(new SKPoint(140, 130), 1f), session);
        session.Move.OnPointerUp(new ToolPointerEvent(new SKPoint(140, 130), 1f), session);

        Assert.Null(session.Floating);
        Assert.Equal(new SKPointI(40, 30), layer.Offset);
    }

    [Fact]
    public void LiftOnlyAffectsActiveLayer()
    {
        // 選取範圍的像素只從作用中圖層提起，其他圖層不動
        using var session = SessionWithRedSquare();
        var doc = session.Document;
        var active = (RasterLayer)doc.ActiveLayer!;

        var other = new RasterLayer { Name = "另一層" };
        other.Surface.Fill(new SKRectI(100, 100, 200, 200), new SKColor(0, 0, 255));
        lock (doc.SyncRoot) doc.Root.Add(other);

        session.Move.OnPointerDown(new ToolPointerEvent(new SKPoint(150, 150), 1f), session);

        Assert.NotNull(session.Floating);
        Assert.Equal(active.Id, session.Floating!.LayerId);
        Assert.Equal(new SKColor(0, 0, 255), Px(other, 150, 150)); // 另一層完全沒被動到
    }
}
