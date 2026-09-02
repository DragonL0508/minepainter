using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Selections;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 「選取框把手」是統一概念：選取範圍、浮動內容、文字物件共用同一套互動。
/// </summary>
public class HandleDragTests
{
    private static EditorSession NewSession() =>
        new(ImageCodec.CreateBlankDocument(512, 512, SKColors.White));

    private static SelectionMask RectSelection(EditorSession session, SKRect rect)
    {
        using var path = new SKPath();
        path.AddRect(rect);
        return SelectionMask.FromPath(path, session.Document.Bounds);
    }

    [Fact]
    public void Selection_ShowsHandlesImmediately()
    {
        using var session = NewSession();
        var tool = session.RectSelect;

        tool.OnPointerDown(new ToolPointerEvent(new SKPoint(100, 100), 1f), session);
        tool.OnPointerUp(new ToolPointerEvent(new SKPoint(300, 250), 1f), session);

        // 一有選取範圍就有把手框 —— 兩者是同一個東西
        Assert.NotNull(session.Selection);
        Assert.NotNull(session.SelectionHandles);
        var frame = session.SelectionHandles!.Value;
        Assert.Equal(100, frame.Left, 1);
        Assert.Equal(300, frame.Right, 1);

        // 取消選取後把手一併消失
        SelectionCommands.SetSelection(session, null, "取消選取");
        Assert.Null(session.SelectionHandles);
    }

    [Fact]
    public void DragHandle_ResizesSelectionItself()
    {
        using var session = NewSession();
        SelectionCommands.SetSelection(session, RectSelection(session, SKRect.Create(100, 100, 100, 100)), "選取");
        Assert.Equal(255, session.Selection!.CoverageAt(150, 150));

        var handles = new HandleDragController();
        // 抓右下角往外拉到 (300,300)
        Assert.True(handles.TryBegin(session, new SKPoint(200, 200), tolerance: 10f));
        handles.Continue(session, new SKPoint(300, 300), ToolModifiers.None);
        handles.End(session);

        // 選取範圍本身變大了
        Assert.Equal(255, session.Selection!.CoverageAt(280, 280));
        var frame = session.SelectionHandles!.Value;
        Assert.Equal(300, frame.Right, 2);

        // 可 undo 回原本的選取
        session.History.Undo();
        Assert.Equal(0, session.Selection!.CoverageAt(280, 280));
        Assert.Equal(255, session.Selection.CoverageAt(150, 150));
    }

    [Fact]
    public void GetFrame_PrioritisesFloatingThenElementThenSelection()
    {
        using var session = NewSession();
        var layer = (RasterLayer)session.Document.ActiveLayer!;

        // 只有選取範圍
        SelectionCommands.SetSelection(session, RectSelection(session, SKRect.Create(10, 10, 50, 50)), "選取");
        var frame = HandleDragController.GetFrame(session);
        Assert.NotNull(frame);
        Assert.Equal(10, frame!.Value.Left, 1);

        // 加上文字物件並選中它 → 改回報物件的框
        var text = new TextElement { Text = "字", Position = new SKPoint(200, 200), FontSize = 48 };
        lock (session.Document.SyncRoot) layer.AddElement(text);
        ElementDragHelper.SetSelected(session, layer, text);
        frame = HandleDragController.GetFrame(session);
        // 物件框＝使用者看到的緊框（FrameBounds），不是失效用的保守 Bounds
        Assert.Equal(text.FrameBounds.Left, frame!.Value.Left, 1);

        // 提起浮動內容 → 浮動內容優先
        session.SelectedElement = null;
        SelectionCommands.SetSelection(session, RectSelection(session, SKRect.Create(10, 10, 50, 50)), "選取");
        layer.Surface.Fill(new SKRectI(10, 10, 60, 60), new SKColor(1, 2, 3));
        var floating = session.LiftSelection();
        Assert.NotNull(floating);
        floating!.TargetRect = SKRect.Create(400, 400, 50, 50);
        frame = HandleDragController.GetFrame(session);
        Assert.Equal(400, frame!.Value.Left, 1);
    }

    [Fact]
    public void DragHandle_ResizesFloatingContent()
    {
        using var session = NewSession();
        var layer = (RasterLayer)session.Document.ActiveLayer!;
        layer.Surface.Fill(new SKRectI(100, 100, 200, 200), new SKColor(255, 0, 0));
        SelectionCommands.SetSelection(session, RectSelection(session, SKRect.Create(100, 100, 100, 100)), "選取");

        var floating = session.LiftSelection()!;
        var handles = new HandleDragController();

        Assert.True(handles.TryBegin(session, new SKPoint(200, 200), tolerance: 10f));
        handles.Continue(session, new SKPoint(300, 300), ToolModifiers.None);
        handles.End(session);

        Assert.Equal(200, floating.TargetRect.Width, 1);
        Assert.Equal(200, floating.TargetRect.Height, 1);
    }

    [Fact]
    public void ShiftDragHandle_KeepsSelectionAspect()
    {
        using var session = NewSession();
        SelectionCommands.SetSelection(session, RectSelection(session, SKRect.Create(0, 0, 200, 100)), "選取");

        var handles = new HandleDragController();
        Assert.True(handles.TryBegin(session, new SKPoint(200, 100), tolerance: 10f));
        // 拖到畫布內（400×200）—— 超出畫布時選取會被裁掉，那是另一回事
        handles.Continue(session, new SKPoint(400, 150), ToolModifiers.Shift);
        handles.End(session);

        var frame = session.SelectionHandles!.Value;
        Assert.Equal(2.0, frame.Width / frame.Height, 1); // 維持 2:1
    }

    /// <summary>
    /// 把手框與選取範圍是同一個概念，任何時候都不該分家。
    /// （曾經是兩份各自維護的狀態，畫面上會出現兩個對不起來的框。）
    /// </summary>
    [Fact]
    public void HandleFrame_AlwaysMatchesSelectionBounds_EvenWhenClipped()
    {
        using var session = NewSession();
        SelectionCommands.SetSelection(session, RectSelection(session, SKRect.Create(0, 0, 200, 100)), "選取");

        var handles = new HandleDragController();
        Assert.True(handles.TryBegin(session, new SKPoint(200, 100), tolerance: 10f));
        handles.Continue(session, new SKPoint(600, 600), ToolModifiers.Shift); // 拖出畫布外
        handles.End(session);

        AssertFrameMatchesSelection(session);
    }

    [Fact]
    public void HandleFrame_AlwaysMatchesSelectionBounds_AfterUndo()
    {
        using var session = NewSession();
        SelectionCommands.SetSelection(session, RectSelection(session, SKRect.Create(20, 30, 200, 100)), "選取");

        var handles = new HandleDragController();
        Assert.True(handles.TryBegin(session, new SKPoint(220, 130), tolerance: 10f));
        handles.Continue(session, new SKPoint(400, 300), ToolModifiers.None);
        handles.End(session);
        AssertFrameMatchesSelection(session);

        session.Undo();
        AssertFrameMatchesSelection(session);

        session.Undo();
        Assert.Null(session.Selection);
        Assert.Null(session.SelectionHandles);
    }

    private static void AssertFrameMatchesSelection(EditorSession session)
    {
        if (session.Selection is not { IsEmpty: false } selection)
        {
            Assert.Null(session.SelectionHandles);
            return;
        }

        var b = selection.Bounds;
        var frame = session.SelectionHandles!.Value;
        Assert.Equal(b.Left, frame.Left, 1);
        Assert.Equal(b.Top, frame.Top, 1);
        Assert.Equal(b.Right, frame.Right, 1);
        Assert.Equal(b.Bottom, frame.Bottom, 1);
    }
}
