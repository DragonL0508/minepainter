using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Selections;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

public class SelectionTests
{
    private static SKColor GetLayerPixel(RasterLayer layer, int x, int y)
    {
        var idx = TileIndex.FromPixel(x, y);
        var tile = layer.Surface.GetTileForRead(idx);
        if (tile == null) return SKColors.Empty;
        var rect = idx.ToPixelRect();
        var offset = ((y - rect.Top) * Tile.Size + (x - rect.Left)) * 4;
        var s = tile.PixelSpan;
        return new SKColor(s[offset + 2], s[offset + 1], s[offset + 0], s[offset + 3]);
    }

    [Fact]
    public void FromPath_RectCoverage()
    {
        using var path = new SKPath();
        path.AddRect(SKRect.Create(10, 10, 100, 100));
        var mask = SelectionMask.FromPath(path, new SKRectI(0, 0, 512, 512));

        Assert.Equal(255, mask.CoverageAt(50, 50));
        Assert.Equal(0, mask.CoverageAt(200, 200));
        Assert.NotNull(mask.OutlinePath);
    }

    [Fact]
    public void SelectionOutsideCanvas_IsClipped()
    {
        // 框選超出畫布時，遮罩與輪廓都必須夾在畫布內 ——
        // 兩者不一致會讓浮動內容的變換基準錯位。
        var docBounds = new SKRectI(0, 0, 256, 256);
        using var path = new SKPath();
        path.AddRect(SKRect.Create(180, 180, 400, 400)); // 大半在畫布外

        var mask = SelectionMask.FromPath(path, docBounds);

        Assert.Equal(255, mask.CoverageAt(200, 200));
        Assert.NotNull(mask.OutlinePath);

        // 輪廓不得超出畫布
        var outline = SKRectI.Ceiling(mask.OutlinePath!.Bounds);
        Assert.True(docBounds.Contains(outline), $"輪廓 {outline} 超出畫布 {docBounds}");

        // 遮罩範圍與輪廓一致
        Assert.True(docBounds.Contains(mask.Bounds), $"遮罩範圍 {mask.Bounds} 超出畫布");
    }

    [Fact]
    public void SelectionFullyOutsideCanvas_IsEmpty()
    {
        using var path = new SKPath();
        path.AddRect(SKRect.Create(500, 500, 100, 100));
        var mask = SelectionMask.FromPath(path, new SKRectI(0, 0, 256, 256));
        Assert.True(mask.IsEmpty);
    }

    [Fact]
    public void MoveTool_HandlesSelectionThatOverflowsCanvas()
    {
        // 使用者回報：框選超出畫布後用移動工具會壞掉
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(256, 256, SKColors.White));
        var layer = (RasterLayer)session.Document.ActiveLayer!;
        layer.Surface.Fill(new SKRectI(150, 150, 256, 256), new SKColor(255, 0, 0));

        using var path = new SKPath();
        path.AddRect(SKRect.Create(150, 150, 400, 400)); // 超出畫布
        SelectionCommands.SetSelection(session, SelectionMask.FromPath(path, session.Document.Bounds), "選取");

        // 提起 → 移動 → 落地，全程不應該爆
        session.Move.OnPointerDown(new ToolPointerEvent(new SKPoint(200, 200), 1f), session);
        Assert.NotNull(session.Floating);

        session.Move.OnPointerMove(new ToolPointerEvent(new SKPoint(120, 120), 1f), session);
        session.Move.OnPointerUp(new ToolPointerEvent(new SKPoint(120, 120), 1f), session);
        session.CommitFloating();

        Assert.Null(session.Floating);
        Assert.NotNull(session.Selection);
        // 內容確實被搬到左上方
        Assert.Equal(new SKColor(255, 0, 0), GetLayerPixel(layer, 100, 100));
    }

    [Fact]
    public void Combine_AddAndSubtract()
    {
        using var p1 = new SKPath(); p1.AddRect(SKRect.Create(0, 0, 100, 100));
        using var p2 = new SKPath(); p2.AddRect(SKRect.Create(50, 0, 100, 100));
        var bounds = new SKRectI(0, 0, 512, 512);

        var a = SelectionMask.FromPath(p1, bounds);
        var b = SelectionMask.FromPath(p2, bounds);

        var added = SelectionMask.Combine(a, b, SelectionCombineMode.Add)!;
        Assert.Equal(255, added.CoverageAt(25, 50));
        Assert.Equal(255, added.CoverageAt(125, 50));

        var subtracted = SelectionMask.Combine(a, b, SelectionCombineMode.Subtract)!;
        Assert.Equal(255, subtracted.CoverageAt(25, 50));
        Assert.Equal(0, subtracted.CoverageAt(75, 50));

        var intersected = SelectionMask.Combine(a, b, SelectionCombineMode.Intersect)!;
        Assert.Equal(0, intersected.CoverageAt(25, 50));
        Assert.Equal(255, intersected.CoverageAt(75, 50));
        Assert.Equal(0, intersected.CoverageAt(125, 50));
    }

    [Fact]
    public void FloodFill_StopsAtColorBoundary()
    {
        using var doc = ImageCodec.CreateBlankDocument(512, 512, SKColors.White);
        var layer = (RasterLayer)doc.ActiveLayer!;
        // 中間畫一條黑柱把畫面分左右
        layer.Surface.Fill(new SKRectI(250, 0, 262, 512), SKColors.Black);

        var mask = FloodFiller.Fill(layer, new SKPointI(50, 256), 0, doc.Bounds);
        Assert.Equal(255, mask.CoverageAt(100, 256));  // 左側填到
        Assert.Equal(0, mask.CoverageAt(256, 256));    // 黑柱不填
        Assert.Equal(0, mask.CoverageAt(400, 256));    // 右側不填
        Assert.NotNull(mask.OutlinePath);
    }

    [Fact]
    public void FloodFill_ToleranceExpandsMatch()
    {
        using var doc = ImageCodec.CreateBlankDocument(256, 256, new SKColor(200, 200, 200));
        var layer = (RasterLayer)doc.ActiveLayer!;
        layer.Surface.Fill(new SKRectI(100, 0, 256, 256), new SKColor(180, 180, 180));

        var strict = FloodFiller.Fill(layer, new SKPointI(50, 128), 0, doc.Bounds);
        Assert.Equal(0, strict.CoverageAt(150, 128));

        var loose = FloodFiller.Fill(layer, new SKPointI(50, 128), 30, doc.Bounds);
        Assert.Equal(255, loose.CoverageAt(150, 128));
    }

    [Fact]
    public void BrushStroke_ClippedBySelection()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(512, 512, SKColors.White));
        // 選取左半
        using var path = new SKPath();
        path.AddRect(SKRect.Create(0, 0, 256, 512));
        session.Selection = SelectionMask.FromPath(path, session.Document.Bounds);

        session.Foreground = new SKColor(255, 0, 0);
        session.Brush.Settings.Radius = 30;
        session.Brush.Settings.Hardness = 1f;

        // 跨選取邊界畫一劃（x 200 → 300）
        var tool = session.Brush;
        tool.OnPointerDown(new ToolPointerEvent(new SKPoint(200, 256), 1f), session);
        tool.OnPointerMove(new ToolPointerEvent(new SKPoint(300, 256), 1f), session);
        tool.OnPointerUp(new ToolPointerEvent(new SKPoint(300, 256), 1f), session);

        var layer = (RasterLayer)session.Document.ActiveLayer!;
        Assert.Equal(new SKColor(255, 0, 0), GetLayerPixel(layer, 220, 256)); // 選取內有畫
        Assert.Equal(SKColors.White, GetLayerPixel(layer, 280, 256));          // 選取外被裁掉
    }

    [Fact]
    public void FillTool_FillsRegion_Undoable()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(256, 256, SKColors.White));
        var layer = (RasterLayer)session.Document.ActiveLayer!;
        layer.Surface.Fill(new SKRectI(100, 0, 112, 256), SKColors.Black); // 分隔柱

        session.Foreground = new SKColor(0, 128, 255);
        session.ActiveTool = session.Fill;
        session.Fill.OnPointerDown(new ToolPointerEvent(new SKPoint(50, 128), 1f), session);

        Assert.Equal(new SKColor(0, 128, 255), GetLayerPixel(layer, 50, 128));
        Assert.Equal(SKColors.White, GetLayerPixel(layer, 200, 128)); // 柱右側不受影響
        Assert.Equal(SKColors.Black, GetLayerPixel(layer, 105, 128)); // 柱本身不變

        Assert.True(session.History.Undo());
        Assert.Equal(SKColors.White, GetLayerPixel(layer, 50, 128));
    }

    [Fact]
    public void ReplaceMode_ClearsSelectionOnPointerDown()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(512, 512, SKColors.White));
        var tool = session.RectSelect;

        // 建立第一個選取 A
        tool.OnPointerDown(new ToolPointerEvent(new SKPoint(10, 10), 1f), session);
        tool.OnPointerUp(new ToolPointerEvent(new SKPoint(100, 100), 1f), session);
        var selectionA = session.Selection;
        Assert.NotNull(selectionA);

        // 第二次拖曳（無修飾鍵）：按下瞬間畫面上的舊選取就該消失（螞蟻線與把手一起）
        tool.OnPointerDown(new ToolPointerEvent(new SKPoint(200, 200), 1f), session);
        Assert.Null(session.Selection);
        Assert.Null(session.SelectionHandles);

        tool.OnPointerUp(new ToolPointerEvent(new SKPoint(300, 300), 1f), session);
        var selectionB = session.Selection;
        Assert.NotNull(selectionB);
        Assert.Equal(255, selectionB!.CoverageAt(250, 250));
        Assert.Equal(0, selectionB.CoverageAt(50, 50)); // Replace：A 不在了

        // undo 應還原成 A（不是 null）
        session.History.Undo();
        Assert.Same(selectionA, session.Selection);

        // Shift 加選：按下時不清
        tool.OnPointerDown(new ToolPointerEvent(new SKPoint(200, 200), 1f, ToolModifiers.Shift), session);
        Assert.Same(selectionA, session.Selection);
        tool.OnPointerUp(new ToolPointerEvent(new SKPoint(300, 300), 1f, ToolModifiers.Shift), session);
        Assert.Equal(255, session.Selection!.CoverageAt(50, 50));   // A 保留
        Assert.Equal(255, session.Selection.CoverageAt(250, 250));  // B 加入
    }

    [Fact]
    public void SelectionChange_Undoable()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(256, 256, SKColors.White));
        using var path = new SKPath();
        path.AddRect(SKRect.Create(0, 0, 100, 100));
        var mask = SelectionMask.FromPath(path, session.Document.Bounds);

        SelectionCommands.SetSelection(session, mask, "測試選取");
        Assert.Same(mask, session.Selection);

        session.History.Undo();
        Assert.Null(session.Selection);

        session.History.Redo();
        Assert.Same(mask, session.Selection);
    }
}
