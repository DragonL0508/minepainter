using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

public class TransformSessionTests
{
    private static SKColor LayerPx(RasterLayer layer, int docX, int docY)
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

    [Fact]
    public void GroupTransform_ScalesPixelsAndText_SingleUndo()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(512, 512, SKColors.White));
        var doc = session.Document;
        var group = new GroupLayer { Name = "G" };
        var a = new RasterLayer { Name = "A" };
        lock (doc.SyncRoot)
        {
            doc.Root.Add(group);
            group.Add(a);
            a.Surface.Fill(new SKRectI(100, 100, 200, 200), new SKColor(255, 0, 0));
            a.AddElement(new TextElement { Text = "T", Position = new SKPoint(100, 220), FontSize = 40 });
            doc.ActiveLayer = group;
        }
        session.ActiveTool = session.Move;
        session.RefreshSelectionHandles();
        Assert.NotNull(session.SelectionHandles); // 群組內容框（像素 ∪ 文字）

        var t = session.BeginTransform();
        Assert.NotNull(t);
        Assert.True(t!.IsGroup);
        Assert.True(t.SourceRect.Left <= 100 && t.SourceRect.Top <= 100);

        // 以左上角為錨放大兩倍
        t.TargetRect = new SKRect(
            t.SourceRect.Left, t.SourceRect.Top,
            t.SourceRect.Left + t.SourceRect.Width * 2,
            t.SourceRect.Top + t.SourceRect.Height * 2);
        var mapped = t.Matrix.MapPoint(new SKPoint(150, 150)); // 紅色方塊中心的新位置
        t.Apply(preview: false);
        session.CommitTransform();

        var text = Assert.IsType<TextElement>(a.Elements[0]);
        Assert.Equal(80, text.FontSize, 1); // 40 × 2
        var c = LayerPx(a, (int)mapped.X, (int)mapped.Y);
        Assert.True(c.Red > 200 && c.Alpha > 200, $"放大後 {mapped} 應為紅色，實際 {c}");
        var expanded = LayerPx(a, 280, 280); // 原本沒內容、放大後才覆蓋到的位置
        Assert.True(expanded.Red > 200, $"(280,280) 應為放大後的紅色，實際 {expanded}");

        // 單一 undo 還原像素與文字
        Assert.True(session.History.CanUndo);
        Assert.True(session.Undo());
        Assert.Equal(40, ((TextElement)a.Elements[0]).FontSize, 1);
        Assert.True(LayerPx(a, 150, 150).Red > 200);
        Assert.Equal(SKColors.Transparent, LayerPx(a, 280, 280)); // 放大出去的部分收回來
        Assert.True(session.Redo());
        Assert.Equal(80, ((TextElement)a.Elements[0]).FontSize, 1);
    }

    [Fact]
    public void Transform_ShrinkThenRestore_IsLossless()
    {
        // 核心不變量：session 期間永遠從原始像素重取樣 —— 縮小再拉回原尺寸 = 逐位元還原
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(512, 512, SKColors.White));
        var doc = session.Document;
        var layer = (RasterLayer)doc.ActiveLayer!;
        lock (doc.SyncRoot)
        {
            // 高頻棋盤圖樣：任何重取樣殘留都會現形
            for (var y = 0; y < 16; y++)
                for (var x = 0; x < 16; x++)
                    if ((x + y) % 2 == 0)
                        layer.Surface.Fill(new SKRectI(100 + x, 100 + y, 101 + x, 101 + y), SKColors.Black);
        }
        session.ActiveTool = session.Move;

        var t = session.BeginTransform();
        Assert.NotNull(t);

        // 縮到 1/8 再拉回原尺寸
        t!.TargetRect = new SKRect(
            t.SourceRect.Left, t.SourceRect.Top,
            t.SourceRect.Left + t.SourceRect.Width / 8,
            t.SourceRect.Top + t.SourceRect.Height / 8);
        t.Apply(preview: true);
        t.TargetRect = t.SourceRect;
        t.Apply(preview: true);

        for (var y = 0; y < 16; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                var expected = (x + y) % 2 == 0 ? SKColors.Black : SKColors.White;
                Assert.Equal(expected, LayerPx(layer, 100 + x, 100 + y));
            }
        }

        // 恰好回到原狀落地：不記任何 history 步驟
        session.CommitTransform();
        Assert.False(session.History.CanUndo);
    }

    [Fact]
    public void RotateGesture_RotatesTextElement_Undoable()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(512, 512, SKColors.White));
        var doc = session.Document;
        var layer = (RasterLayer)doc.ActiveLayer!;
        lock (doc.SyncRoot)
        {
            layer.AddElement(new TextElement { Text = "轉我", Position = new SKPoint(200, 200), FontSize = 40 });
        }
        session.ActiveTool = session.Move;

        var t = session.BeginTransform();
        Assert.NotNull(t);
        var center = new SKPoint(t!.TargetRect.MidX, t.TargetRect.MidY);

        // 右鍵從中心右側拖到中心下方 = 順時針 90°
        Assert.True(session.Move.BeginRotate(session, new SKPoint(center.X + 100, center.Y)));
        session.Move.ContinueRotate(session, new SKPoint(center.X, center.Y + 100), ToolModifiers.None);
        session.Move.EndRotate(session);
        Assert.Equal(90, t.RotationDeg, 1);
        Assert.Equal(90, session.SelectionHandlesRotation, 1); // 框跟著轉

        session.CommitTransform();
        var text = Assert.IsType<TextElement>(layer.Elements[0]);
        Assert.Equal(90, text.Rotation, 1);

        Assert.True(session.Undo());
        Assert.Equal(0, ((TextElement)layer.Elements[0]).Rotation, 1);
        Assert.True(session.Redo());
        Assert.Equal(90, ((TextElement)layer.Elements[0]).Rotation, 1);
    }

    [Fact]
    public void TransformMove_PureTranslation_UsesOffsets_NotRestamp()
    {
        // 純平移（含放大後的平移）不重蓋章：位移走圖層 Offset —— 大圖拖曳才跟得上滑鼠
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(512, 512, SKColors.White));
        var doc = session.Document;
        var layer = (RasterLayer)doc.ActiveLayer!;
        lock (doc.SyncRoot)
        {
            layer.Surface.Fill(new SKRectI(50, 50, 70, 70), SKColors.Blue);
        }
        session.ActiveTool = session.Move;

        var t = session.BeginTransform()!;
        t.TargetRect = new SKRect(t.SourceRect.Left + 30, t.SourceRect.Top + 20,
            t.SourceRect.Right + 30, t.SourceRect.Bottom + 20);
        t.Apply(preview: true);

        Assert.Equal(new SKPointI(30, 20), layer.Offset);       // 位移在 Offset 上
        Assert.Equal(new SKPointI(30, 20), t.OffsetDelta);
        Assert.Equal(SKColors.Blue, LayerPx(layer, 85, 75));    // 呈現位置跟著位移
        Assert.Equal(SKColors.White, LayerPx(layer, 55, 55));   // 舊位置讓出來（背景白）

        // 落地：位移記進 history，undo 還原
        session.CommitTransform();
        Assert.Equal(new SKPointI(30, 20), layer.Offset);
        Assert.True(session.Undo());
        Assert.Equal(SKPointI.Empty, layer.Offset);
        Assert.True(session.Redo());
        Assert.Equal(new SKPointI(30, 20), layer.Offset);
    }

    [Fact]
    public void ScaledThenMoved_KeepsHighQualityStamp_AndOffsets()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(512, 512, SKColors.White));
        var doc = session.Document;
        var layer = (RasterLayer)doc.ActiveLayer!;
        lock (doc.SyncRoot)
        {
            layer.Surface.Fill(new SKRectI(100, 100, 200, 200), SKColors.Red);
        }
        session.ActiveTool = session.Move;

        var t = session.BeginTransform()!;
        // 放大 1.5 倍（手勢結束 = High 蓋章）
        t.TargetRect = new SKRect(t.SourceRect.Left, t.SourceRect.Top,
            t.SourceRect.Left + t.SourceRect.Width * 1.5f,
            t.SourceRect.Top + t.SourceRect.Height * 1.5f);
        t.Apply(preview: false);

        // 再平移：走 Offset，不重蓋章
        t.TargetRect = new SKRect(t.TargetRect.Left + 40, t.TargetRect.Top + 10,
            t.TargetRect.Right + 40, t.TargetRect.Bottom + 10);
        t.Apply(preview: true);
        Assert.Equal(new SKPointI(40, 10), t.OffsetDelta);

        // 放大後 (150,150) 映到 1.5 倍 + 平移的位置應為紅色
        var mapped = t.Matrix.MapPoint(new SKPoint(150, 150));
        Assert.True(LayerPx(layer, (int)mapped.X, (int)mapped.Y).Red > 200);

        // 落地 + undo：像素與位移一起還原
        session.CommitTransform();
        Assert.True(session.Undo());
        Assert.Equal(SKPointI.Empty, layer.Offset);
        Assert.Equal(SKColors.Red, LayerPx(layer, 150, 150));
        Assert.True(LayerPx(layer, 350, 250).Alpha == 0 || LayerPx(layer, 350, 250) == SKColors.White);
    }

    [Fact]
    public void GesturePreview_NoTileWritesDuringDrag_StampsOnEnd()
    {
        // 縮放/旋轉手勢期間走 render thread 覆疊：拖曳中一格 tile 都不寫、
        // 手勢結束才蓋章一次（大圖「移動後旋轉很卡」的解法）
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(512, 512, SKColors.White));
        var doc = session.Document;
        var layer = (RasterLayer)doc.ActiveLayer!;
        lock (doc.SyncRoot)
        {
            layer.Surface.Fill(new SKRectI(100, 100, 200, 200), SKColors.Red);
        }
        session.ActiveTool = session.Move;

        var t = session.BeginTransform()!;
        t.BeginGesturePreview();
        Assert.NotNull(t.Overlay);                 // render thread 有東西可畫
        Assert.Equal(0, layer.Surface.TileCount);  // 像素已提走

        // 拖曳中改尺寸與角度：只發布新矩陣，不寫任何 tile
        t.TargetRect = new SKRect(t.SourceRect.Left, t.SourceRect.Top,
            t.SourceRect.Left + t.SourceRect.Width * 1.5f,
            t.SourceRect.Top + t.SourceRect.Height * 1.5f);
        t.RotationDeg = 25f;
        t.Apply(preview: true);
        Assert.Equal(0, layer.Surface.TileCount);
        var mapped = t.Overlay!.Matrix.MapPoint(new SKPoint(150, 150));

        // 手勢結束：High 蓋章寫回 tile，殘影標記 HandingOver 等合成器追上
        t.EndGesture();
        Assert.True(layer.Surface.TileCount > 0);
        Assert.True(t.Overlay is { HandingOver: true });
        Assert.True(LayerPx(layer, (int)mapped.X, (int)mapped.Y).Red > 150,
            $"蓋章位置 {mapped} 應為紅色");

        // 取消：無損還原
        session.CancelTransform();
        Assert.Equal(SKColors.Red, LayerPx(layer, 150, 150));
        Assert.False(session.History.CanUndo);
    }

    [Fact]
    public void CancelTransform_RestoresExactly()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(512, 512, SKColors.White));
        var doc = session.Document;
        var layer = (RasterLayer)doc.ActiveLayer!;
        lock (doc.SyncRoot)
        {
            layer.Surface.Fill(new SKRectI(50, 50, 60, 60), SKColors.Blue);
        }
        session.ActiveTool = session.Move;

        var t = session.BeginTransform()!;
        t.TargetRect = new SKRect(t.SourceRect.Left + 40, t.SourceRect.Top + 40,
            t.SourceRect.Right + 40, t.SourceRect.Bottom + 40);
        t.RotationDeg = 30;
        t.Apply(preview: true);

        session.CancelTransform();
        Assert.Null(session.Transform);
        Assert.Equal(SKColors.Blue, LayerPx(layer, 55, 55));
        Assert.False(session.History.CanUndo);
    }
}
