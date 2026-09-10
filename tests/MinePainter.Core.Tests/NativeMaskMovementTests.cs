using MinePainter.Core.Adjustments;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

public class NativeMaskMovementTests
{
    private static LayerMask Mask() => new(new SKRectI(10, 10, 20, 30), Enumerable.Repeat((byte)255, 200).ToArray(), 0);

    private static EditorSession Session(out RasterLayer layer)
    {
        var session = new EditorSession(ImageCodec.CreateBlankDocument(128, 128, SKColors.Transparent));
        layer = (RasterLayer)session.Document.ActiveLayer!;
        layer.Surface.Fill(new SKRectI(10, 10, 30, 30), SKColors.Red);
        layer.Mask = Mask();
        session.ActiveTool = session.Move;
        session.RefreshSelectionHandles();
        return session;
    }

    [Fact]
    public void Drag_MovesRawMaskWithoutOverlay_UndoRedoKeepsReferences()
    {
        using var session = Session(out var layer);
        var original = layer.Mask!;
        session.Move.OnPointerDown(new ToolPointerEvent(new SKPoint(60, 60), 1), session);
        session.Move.OnPointerMove(new ToolPointerEvent(new SKPoint(69, 67), 1), session);
        session.Move.OnPointerUp(new ToolPointerEvent(new SKPoint(69, 67), 1), session);
        Assert.Equal(new SKPointI(9, 7), layer.Offset);
        Assert.Null(session.LayerOverlay);
        var moved = layer.Mask!;
        Assert.Equal(new SKRectI(19, 17, 29, 37), moved.Bounds);
        Assert.Same(original.Alpha, moved.Alpha);
        Assert.True(session.Undo());
        Assert.Same(original, layer.Mask);
        Assert.True(session.Redo());
        Assert.Same(moved, layer.Mask);
    }

    [Fact]
    public void GroupNudge_MovesOwnNestedAndAdjustmentMasks_OneUndo()
    {
        using var session = Session(out var layer);
        var doc = session.Document;
        doc.Root.Remove(layer);
        var group = new GroupLayer { Mask = Mask() };
        var nested = new GroupLayer { Mask = Mask() };
        var adjustment = new AdjustmentLayer(new CurvesAdjustment()) { Mask = Mask() };
        group.Add(nested); nested.Add(layer); nested.Add(adjustment); doc.Root.Add(group);
        doc.ActiveLayer = group;
        var nodes = new LayerNode[] { group, nested, layer, adjustment };
        var originals = nodes.Select(n => n.Mask!).ToArray();
        Assert.True(MoveTool.Nudge(session, 5, -3));
        Assert.All(nodes, n => Assert.Equal(new SKRectI(15, 7, 25, 27), n.Mask!.Bounds));
        Assert.True(session.Undo());
        for (var i = 0; i < nodes.Length; i++) Assert.Same(originals[i], nodes[i].Mask);
        Assert.True(session.Redo());
        Assert.All(nodes, n => Assert.Equal(new SKRectI(15, 7, 25, 27), n.Mask!.Bounds));
    }

    [Fact]
    public void ChildTransform_AncestorMaskStaysFixedAndPreventsUnmaskedOverlay()
    {
        using var session = Session(out var layer);
        layer.Mask = null;
        var doc = session.Document;
        doc.Root.Remove(layer);
        var original = Mask();
        var group = new GroupLayer { Mask = original }; group.Add(layer); doc.Root.Add(group);
        doc.ActiveLayer = layer;
        var transform = session.BeginTransform()!;
        transform.BeginGesturePreview(live: true);
        Assert.False(transform.GestureActive);
        var rect = transform.SourceRect; rect.Offset(4, 3); transform.TargetRect = rect;
        transform.Apply(false);
        Assert.Same(original, group.Mask);
        session.CancelTransform();
    }

    [Fact]
    public void Transform_TranslationIsLossless_CommitUndoRedoAndCancelPreserveMask()
    {
        using var session = Session(out var layer);
        var original = layer.Mask!;
        var transform = session.BeginTransform()!;
        transform.BeginGesturePreview(live: true);
        Assert.False(transform.GestureActive);
        var rect = transform.SourceRect; rect.Offset(7, 9); transform.TargetRect = rect;
        transform.Apply(false);
        Assert.Same(original.Alpha, layer.Mask!.Alpha);
        Assert.Equal(new SKRectI(17, 19, 27, 39), layer.Mask.Bounds);
        session.CommitTransform();
        var committed = layer.Mask;
        Assert.True(session.Undo()); Assert.Same(original, layer.Mask);
        Assert.True(session.Redo()); Assert.Same(committed, layer.Mask);
        transform = session.BeginTransform()!;
        rect = transform.SourceRect; rect.Offset(-3, 4); transform.TargetRect = rect;
        transform.Apply(false);
        session.CancelTransform();
        Assert.Same(committed, layer.Mask);
    }

    [Fact]
    public void GroupScale_TransformsGroupAndChildMasks_IdentityRestoresOriginals()
    {
        using var session = Session(out var layer);
        var doc = session.Document;
        doc.Root.Remove(layer);
        var group = new GroupLayer { Mask = Mask() }; group.Add(layer); doc.Root.Add(group); doc.ActiveLayer = group;
        var groupMask = group.Mask!; var layerMask = layer.Mask!;
        var transform = session.BeginTransform()!;
        var source = transform.SourceRect;
        transform.TargetRect = SKRect.Create(source.Left, source.Top, source.Width * 2, source.Height * 2);
        transform.Apply(false);
        Assert.Equal(new SKRectI(10, 10, 30, 50), group.Mask!.Bounds);
        Assert.Equal(group.Mask.Bounds, layer.Mask!.Bounds);
        Assert.Equal((byte)255, group.Mask.At(25, 35));
        Assert.Equal((byte)0, group.Mask.At(35, 35));
        transform.TargetRect = source; transform.Apply(false);
        Assert.Same(groupMask, group.Mask); Assert.Same(layerMask, layer.Mask);
        session.CancelTransform();
    }

    [Fact]
    public void Warp_TransformsMaskWithMesh_CommitUndoRestoresRawMask()
    {
        using var session = Session(out var layer);
        var original = layer.Mask!;
        var transform = session.EnterTransformMode(TransformMode.Warp)!;
        var warp = WarpMesh.Drag(transform.Warp!, 0, new SKPoint(-5, -5));
        transform.SetWarp(warp); transform.Apply(false);
        Assert.NotEqual(original.Bounds, layer.Mask!.Bounds);
        Assert.NotSame(original.Alpha, layer.Mask.Alpha);
        session.CommitTransform();
        var warped = layer.Mask;
        Assert.True(session.Undo()); Assert.Same(original, layer.Mask);
        Assert.True(session.Redo()); Assert.Same(warped, layer.Mask);
    }
}
