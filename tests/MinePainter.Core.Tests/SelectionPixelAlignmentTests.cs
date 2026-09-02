using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Selections;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 選取框的像素對齊。
///
/// 螞蟻線畫的是 OutlinePath、把手框用的是 Bounds；兩者只要差半個像素，
/// 放大檢視時就會看到兩個對不齊的框。所以不變量是
/// 「OutlinePath 沿像素邊界，且 OutlinePath.Bounds == Bounds」。
/// </summary>
public class SelectionPixelAlignmentTests
{
    private static EditorSession NewSession() =>
        new(ImageCodec.CreateBlankDocument(512, 512, SKColors.White));

    private static void AssertOutlineMatchesBounds(SelectionMask mask)
    {
        Assert.NotNull(mask.OutlinePath);
        var outline = mask.OutlinePath!.Bounds;
        var bounds = mask.Bounds;

        Assert.Equal(bounds.Left, outline.Left, 3);
        Assert.Equal(bounds.Top, outline.Top, 3);
        Assert.Equal(bounds.Right, outline.Right, 3);
        Assert.Equal(bounds.Bottom, outline.Bottom, 3);

        // 而且落在整數像素上
        Assert.Equal(MathF.Round(outline.Left), outline.Left, 3);
        Assert.Equal(MathF.Round(outline.Top), outline.Top, 3);
        Assert.Equal(MathF.Round(outline.Right), outline.Right, 3);
        Assert.Equal(MathF.Round(outline.Bottom), outline.Bottom, 3);
    }

    [Fact]
    public void FromPath_FractionalRect_OutlineIsPixelAligned()
    {
        using var doc = ImageCodec.CreateBlankDocument(512, 512, SKColors.White);
        using var path = new SKPath();
        path.AddRect(SKRect.Create(100.4f, 100.6f, 100.3f, 100.7f));

        var mask = SelectionMask.FromPath(path, doc.Bounds);
        AssertOutlineMatchesBounds(mask);
    }

    [Fact]
    public void FromPath_BoundsUseFloorForTopLeft()
    {
        // 舊版四個邊都用 SKRectI.Ceiling，左上角會整整偏一格
        using var doc = ImageCodec.CreateBlankDocument(512, 512, SKColors.White);
        using var path = new SKPath();
        path.AddRect(SKRect.Create(10.5f, 20.5f, 100, 100));

        var mask = SelectionMask.FromPath(path, doc.Bounds);
        Assert.True(mask.Bounds.Left <= 11, $"左邊界不該被往上取整，實得 {mask.Bounds.Left}");
        Assert.True(mask.Bounds.Top <= 21, $"上邊界不該被往上取整，實得 {mask.Bounds.Top}");
    }

    [Fact]
    public void RectangleSelectTool_SnapsToWholePixels()
    {
        using var session = NewSession();
        var tool = session.RectSelect;

        tool.OnPointerDown(new ToolPointerEvent(new SKPoint(100.4f, 50.6f), 1f), session);
        tool.OnPointerUp(new ToolPointerEvent(new SKPoint(300.7f, 250.2f), 1f), session);

        AssertOutlineMatchesBounds(session.Selection!);
        var frame = session.SelectionHandles!.Value;
        Assert.Equal(100, frame.Left, 3);
        Assert.Equal(51, frame.Top, 3);
        Assert.Equal(301, frame.Right, 3);
        Assert.Equal(250, frame.Bottom, 3);
    }

    [Fact]
    public void LassoSelection_OutlineFollowsPixelEdges()
    {
        using var session = NewSession();
        var tool = session.Lasso;

        tool.OnPointerDown(new ToolPointerEvent(new SKPoint(100.3f, 100.7f), 1f), session);
        foreach (var p in new[]
                 {
                     new SKPoint(200.5f, 110.2f), new SKPoint(210.8f, 200.4f),
                     new SKPoint(110.1f, 190.9f),
                 })
        {
            tool.OnPointerMove(new ToolPointerEvent(p, 1f), session);
        }
        tool.OnPointerUp(new ToolPointerEvent(new SKPoint(110.1f, 190.9f), 1f), session);

        AssertOutlineMatchesBounds(session.Selection!);
    }

    [Fact]
    public void FloatingSelection_OutlineAndFrameStayAligned_WhileScaling()
    {
        using var session = NewSession();
        var layer = (RasterLayer)session.Document.ActiveLayer!;
        layer.Surface.Fill(new SKRectI(100, 100, 200, 200), SKColors.Red);
        SelectionCommands.SetSelection(session,
            SelectionMask.FromPath(RectPath(100, 100, 100, 100), session.Document.Bounds), "選取");

        var floating = session.LiftSelection()!;
        var handles = new HandleDragController();
        Assert.True(handles.TryBegin(session, new SKPoint(200, 200), tolerance: 10f));
        handles.Continue(session, new SKPoint(317.6f, 288.3f), ToolModifiers.None);

        // 浮動框落在整數像素上
        var rect = floating.TargetRect;
        Assert.Equal(MathF.Round(rect.Right), rect.Right, 3);
        Assert.Equal(MathF.Round(rect.Bottom), rect.Bottom, 3);

        // 螞蟻線（變換後的輪廓）與把手框重合
        using var outline = floating.GetTransformedOutline();
        var frame = session.SelectionHandles!.Value;
        Assert.Equal(frame.Left, outline!.Bounds.Left, 2);
        Assert.Equal(frame.Top, outline.Bounds.Top, 2);
        Assert.Equal(frame.Right, outline.Bounds.Right, 2);
        Assert.Equal(frame.Bottom, outline.Bounds.Bottom, 2);
    }

    [Fact]
    public void ShiftResizeText_KeepsCurrentAspect()
    {
        // 先把文字拉寬（ScaleX = 2），再按 Shift 拖角 →
        // 等比縮放「目前看到的框」：ScaleX 維持 2、字級放大（使用者 2026-09-02 明示）
        using var session = NewSession();
        var layer = (RasterLayer)session.Document.ActiveLayer!;
        var text = new TextElement
        {
            Text = "MinePainter",
            Position = new SKPoint(50, 50),
            FontSize = 40,
            ScaleX = 2f, // 已經被拉寬
        };
        lock (session.Document.SyncRoot) layer.AddElement(text);
        session.SelectedElement = (layer.Id, text.Id);

        var b = text.FrameBounds; // 把手在使用者看到的緊框上
        var drag = new ElementDragHelper();
        Assert.True(drag.TryBegin(session, new SKPoint(b.Right, b.Bottom), 10f, allowInsideMove: false));
        drag.Continue(session, new SKPoint(b.Right + 40, b.Bottom + 40), ToolModifiers.Shift);
        drag.End(session);

        TextElement result;
        lock (session.Document.SyncRoot)
        {
            result = (TextElement)layer.FindElement(text.Id)!;
        }
        Assert.Equal(2f, result.ScaleX, 3);
        Assert.True(result.FontSize > 40, $"字級應該跟著放大，實得 {result.FontSize}");
    }

    [Fact]
    public void ResizeTextWithoutShift_StillAllowsFreeStretch()
    {
        using var session = NewSession();
        var layer = (RasterLayer)session.Document.ActiveLayer!;
        var text = new TextElement
        {
            Text = "MinePainter",
            Position = new SKPoint(50, 50),
            FontSize = 40,
        };
        lock (session.Document.SyncRoot) layer.AddElement(text);
        session.SelectedElement = (layer.Id, text.Id);

        var b = text.FrameBounds; // 把手在使用者看到的緊框上
        var drag = new ElementDragHelper();
        Assert.True(drag.TryBegin(session, new SKPoint(b.Right, b.Bottom), 10f, allowInsideMove: false));
        drag.Continue(session, new SKPoint(b.Right + 200, b.Bottom), ToolModifiers.None);
        drag.End(session);

        TextElement result;
        lock (session.Document.SyncRoot)
        {
            result = (TextElement)layer.FindElement(text.Id)!;
        }
        Assert.True(result.ScaleX > 1.1f, $"沒按 Shift 應該可以自由拉寬，實得 ScaleX={result.ScaleX}");
    }

    [Fact]
    public void RebuildOutline_IsFastEnoughForInteractiveDragging()
    {
        // 拖把手時每次滑鼠移動都會重新柵格化 + 重建輪廓
        using var doc = ImageCodec.CreateBlankDocument(1600, 1200, SKColors.White);
        using var path = new SKPath();
        path.AddRect(SKRect.Create(0, 0, 1600, 1200));

        SelectionMask.FromPath(path, doc.Bounds); // 暖機

        var sw = System.Diagnostics.Stopwatch.StartNew();
        const int iterations = 10;
        for (var i = 0; i < iterations; i++) SelectionMask.FromPath(path, doc.Bounds);
        sw.Stop();

        var per = sw.Elapsed.TotalMilliseconds / iterations;
        Assert.True(per < 60, $"全畫布選取重建一次要 {per:0.#} ms，拖曳會卡");
    }

    private static SKPath RectPath(float x, float y, float w, float h)
    {
        var path = new SKPath();
        path.AddRect(SKRect.Create(x, y, w, h));
        return path;
    }
}
