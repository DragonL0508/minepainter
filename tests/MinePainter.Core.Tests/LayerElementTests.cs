using MinePainter.Core.Compositing;
using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

public class LayerElementTests
{
    private static SKColor WaitPixel(Compositor compositor, int x, int y, Func<SKColor, bool> until, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        SKColor last = default;
        while (Environment.TickCount64 < deadline)
        {
            compositor.TryGetTile(TileIndex.FromPixel(x, y), out _);
            last = compositor.SamplePixel(x, y);
            if (until(last)) return last;
            Thread.Sleep(15);
        }
        throw new TimeoutException($"最後取樣 {last}");
    }

    [Fact]
    public void ShapeElement_RendersAndStaysEditable()
    {
        using var doc = ImageCodec.CreateBlankDocument(512, 512, SKColors.White);
        var layer = new RasterLayer { Name = "v" };
        var shape = new ShapeElement
        {
            Kind = ShapeKind.Rectangle,
            Rect = SKRect.Create(100, 100, 200, 100),
            FillColor = new SKColor(255, 0, 0),
            StrokeWidth = 0,
        };
        lock (doc.SyncRoot)
        {
            doc.Root.Add(layer);
            layer.AddElement(shape);
        }

        using var compositor = new Compositor(doc);
        WaitPixel(compositor, 200, 150, c => c == new SKColor(255, 0, 0));

        // 「事後」改外形：這正是 paint.net 做不到的
        lock (doc.SyncRoot)
        {
            layer.ReplaceElement(shape with { Rect = SKRect.Create(300, 300, 100, 100), FillColor = new SKColor(0, 0, 255) });
        }
        WaitPixel(compositor, 350, 350, c => c == new SKColor(0, 0, 255));
        WaitPixel(compositor, 200, 150, c => c == SKColors.White); // 舊位置恢復
    }

    [Fact]
    public void TextElement_EditAfterOtherOperations()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(512, 512, SKColors.White));
        var doc = session.Document;

        // 用文字工具單擊建立（paint.net 式），輸入內容後落地
        session.Foreground = SKColors.Black;
        session.ActiveTool = session.Text;
        ClickText(session, new SKPoint(100, 100));

        var layer = Assert.IsType<RasterLayer>(doc.ActiveLayer);
        var created = Assert.IsType<TextElement>(layer.Elements[0]);
        VectorCommands.CommitNewElement(doc, session.History, layer,
            created with { Text = "文字" }, "新增文字");
        var text = Assert.IsType<TextElement>(layer.Elements[0]);
        Assert.Equal("文字", text.Text);

        // 切去用別的工具（paint.net 在這裡就會 rasterize）
        session.ActiveTool = session.Brush;
        session.ActiveTool = session.Text;

        // 回來仍可改字
        var updated = text with { Text = "還是可以編輯", FontSize = 72 };
        VectorCommands.ReplaceElement(doc, session.History, layer, text, updated, "編輯文字");
        Assert.Equal("還是可以編輯", ((TextElement)layer.Elements[0]).Text);

        // undo 鏈路
        session.History.Undo();
        Assert.Equal("文字", ((TextElement)layer.Elements[0]).Text);
        session.History.Redo();
        Assert.Equal("還是可以編輯", ((TextElement)layer.Elements[0]).Text);
    }

    [Fact]
    public void ShapeTool_RasterizesDirectlyToLayer_Undoable()
    {
        // 使用者定案：形狀直接柵格化進點陣圖層，不是向量物件
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(512, 512, SKColors.White));
        var doc = session.Document;
        var layer = (RasterLayer)doc.ActiveLayer!;
        session.Shape.Kind = ShapeKind.Rectangle;
        session.Shape.Filled = true;
        session.Foreground = new SKColor(0, 128, 0);

        session.Shape.OnPointerDown(new ToolPointerEvent(new SKPoint(50, 50), 1f), session);
        session.Shape.OnPointerMove(new ToolPointerEvent(new SKPoint(150, 120), 1f), session);
        session.Shape.OnPointerUp(new ToolPointerEvent(new SKPoint(150, 120), 1f), session);

        // 像素直接落在原圖層，沒有新圖層、沒有向量物件
        Assert.Same(layer, doc.ActiveLayer);
        Assert.Single(doc.Root.Children);

        SKColor GetPx(int x, int y)
        {
            var idx = TileIndex.FromPixel(x, y);
            var tile = layer.Surface.GetTileForRead(idx)!;
            var rect = idx.ToPixelRect();
            var off = ((y - rect.Top) * Tile.Size + (x - rect.Left)) * 4;
            var s = tile.PixelSpan;
            return new SKColor(s[off + 2], s[off + 1], s[off + 0], s[off + 3]);
        }

        Assert.Equal(new SKColor(0, 128, 0), GetPx(100, 85));  // 形狀內
        Assert.Equal(SKColors.White, GetPx(300, 300));          // 形狀外

        // 單一 undo 步驟還原
        Assert.True(session.History.Undo());
        Assert.Equal(SKColors.White, GetPx(100, 85));
    }

    /// <summary>模擬使用者單擊（paint.net 式：點下去就開始輸入）。</summary>
    private static void ClickText(EditorSession session, SKPoint p)
    {
        var tool = session.Text;
        tool.OnPointerDown(new ToolPointerEvent(p, 1f), session);
        tool.OnPointerUp(new ToolPointerEvent(p, 1f), session);
    }

    [Fact]
    public void TextTool_SingleClick_CreatesOwnLayer_EmptyDiscardLeavesNoTrace()
    {
        // paint.net 式：單擊即開始輸入。文字一定自己一層（使用者 2026-09-02 明示）：
        // 建立當下靜默生一個文字圖層、不進 history —— 內容為空落地就連圖層一起靜默收掉；
        // 有內容才補單一步「新增文字」（undo 一次收掉整個文字圖層）。
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(512, 512, SKColors.White));
        var doc = session.Document;
        var background = (RasterLayer)doc.ActiveLayer!;
        session.ActiveTool = session.Text;

        ClickText(session, new SKPoint(100, 100));

        var layer = Assert.IsType<RasterLayer>(doc.ActiveLayer);
        Assert.NotSame(background, layer);
        Assert.Equal(2, doc.Root.Children.Count);
        Assert.Empty(background.Elements);
        var element = Assert.IsType<TextElement>(Assert.Single(layer.Elements));
        Assert.Equal("", element.Text);     // 空內容，等使用者輸入
        Assert.NotNull(session.PendingTextEdit); // UI 應立即開啟畫布內編輯
        Assert.False(session.History.CanUndo);   // 建立當下不進 history

        // 內容為空落地 → 元素與圖層都靜默收掉，history 依舊乾淨
        VectorCommands.DiscardElement(doc, layer, element.Id);
        VectorCommands.DiscardNewTextLayer(doc, layer);
        Assert.Single(doc.Root.Children);
        Assert.Same(background, doc.ActiveLayer);
        Assert.False(session.History.CanUndo);

        // 再點一次並輸入內容 → 單一步「新增文字」，undo 一次收掉整個文字圖層
        session.PendingTextEdit = null;
        ClickText(session, new SKPoint(120, 120));
        var layer2 = Assert.IsType<RasterLayer>(doc.ActiveLayer);
        var second = Assert.IsType<TextElement>(Assert.Single(layer2.Elements));
        VectorCommands.CommitNewTextLayer(doc, session.History, layer2, second with { Text = "哈囉" }, "新增文字");
        Assert.Equal("哈囉", ((TextElement)layer2.Elements[0]).Text);
        Assert.Equal("哈囉", layer2.Name);
        Assert.True(session.History.CanUndo);
        Assert.True(session.Undo());
        Assert.Single(doc.Root.Children);
    }

    [Fact]
    public void TextTool_ClickEmpty_DeselectsFirst_ThenCreates()
    {
        // 使用者明示：輸入完（元素還選著）點空白處，第一下是取消選取，不是直接開新文字
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(512, 512, SKColors.White));
        var doc = session.Document;
        session.ActiveTool = session.Text;

        ClickText(session, new SKPoint(100, 100));
        var layer = (RasterLayer)doc.ActiveLayer!;
        var created = Assert.IsType<TextElement>(Assert.Single(layer.Elements));
        VectorCommands.CommitNewTextLayer(doc, session.History, layer, created with { Text = "完成" }, "新增文字");
        session.PendingTextEdit = null;
        Assert.NotNull(session.SelectedElement); // 建立後元素是選著的

        // 第一下：取消選取，不建立
        ClickText(session, new SKPoint(300, 300));
        Assert.Equal(2, doc.Root.Children.Count);
        Assert.Null(session.SelectedElement);
        Assert.Null(session.PendingTextEdit);

        // 第二下：沒有選取了，才開始新文字 —— 又是自己一層
        ClickText(session, new SKPoint(300, 300));
        Assert.Equal(3, doc.Root.Children.Count);
        Assert.Single(layer.Elements);
        Assert.NotNull(session.PendingTextEdit);
    }

    [Fact]
    public void TextHitTest_OnlyOnActiveLayer()
    {
        // 使用者定案（paint.net 邏輯）：物件屬於它所在的圖層，
        // 沒選到那個圖層就選不到、編輯不到。
        using var doc = ImageCodec.CreateBlankDocument(512, 512, SKColors.White);
        var background = doc.Root.Children[0];
        var textLayer = new RasterLayer { Name = "文字層" };
        var text = new TextElement { Text = "哈囉", Position = new SKPoint(100, 100), FontSize = 48 };
        lock (doc.SyncRoot)
        {
            doc.Root.Add(textLayer);
            textLayer.AddElement(text);
            doc.ActiveLayer = background; // 作用中是背景層
        }

        // 作用中不是文字所在的圖層 → 點不到
        Assert.Null(VectorHitTest.FindTextAt(doc, new SKPoint(110, 120)));

        // 切到文字所在的圖層才點得到
        lock (doc.SyncRoot) doc.ActiveLayer = textLayer;
        var hit = VectorHitTest.FindTextAt(doc, new SKPoint(110, 120));
        Assert.NotNull(hit);
        Assert.Equal(text.Id, hit!.Value.Element.Id);
        Assert.Same(textLayer, hit.Value.Layer);

        Assert.Null(VectorHitTest.FindTextAt(doc, new SKPoint(400, 400))); // 空白處
    }

    [Fact]
    public void Text_GetsOwnLayer_NotMixedWithPixels()
    {
        // 文字一定自己一層：像素留在原圖層，文字進新圖層（且文字圖層不能被筆刷畫）
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(512, 512, SKColors.White));
        var doc = session.Document;
        var background = (RasterLayer)doc.ActiveLayer!;

        session.Foreground = new SKColor(255, 0, 0);
        session.Brush.Settings.Radius = 20;
        session.Brush.Settings.Hardness = 1f;
        session.Brush.OnPointerDown(new ToolPointerEvent(new SKPoint(400, 400), 1f), session);
        session.Brush.OnPointerUp(new ToolPointerEvent(new SKPoint(400, 400), 1f), session);

        session.ActiveTool = session.Text;
        ClickText(session, new SKPoint(100, 100));

        Assert.Equal(2, doc.Root.Children.Count);
        var textLayer = Assert.IsType<RasterLayer>(doc.ActiveLayer);
        Assert.NotSame(background, textLayer);
        Assert.Empty(background.Elements);
        Assert.True(background.Surface.TileCount > 0);
        Assert.Single(textLayer.Elements);
        Assert.Equal(0, textLayer.Surface.TileCount);
        Assert.True(textLayer.IsTextLayer);
    }

    [Fact]
    public void HandleDrag_ResizesTextFontSize_Undoable()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(512, 512, SKColors.White));
        var doc = session.Document;
        var layer = (RasterLayer)doc.ActiveLayer!; // 物件屬於作用中圖層
        var text = new TextElement { Text = "ABC", Position = new SKPoint(100, 100), FontSize = 40 };
        lock (doc.SyncRoot) layer.AddElement(text);
        ElementDragHelper.SetSelected(session, layer, text);

        var bounds = text.FrameBounds; // 把手在使用者看到的緊框上
        var helper = new ElementDragHelper();

        // 抓右下角把手往外拉 → 字級變大
        var corner = new SKPoint(bounds.Right, bounds.Bottom);
        Assert.True(helper.TryBegin(session, corner, handleTolerance: 10f, allowInsideMove: false));
        helper.Continue(session, new SKPoint(corner.X + 100, bounds.Top + bounds.Height * 2));
        helper.End(session);

        var resized = Assert.IsType<TextElement>(layer.Elements[0]);
        Assert.True(resized.FontSize > 40, $"字級應變大，實際 {resized.FontSize}");
        Assert.Equal("ABC", resized.Text); // 內容不變

        // 單一 undo 還原字級
        Assert.True(session.History.Undo());
        Assert.Equal(40, ((TextElement)layer.Elements[0]).FontSize, 1);
    }

    [Fact]
    public void MoveTool_PrefersTextElement_OverLayerOffset()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(512, 512, SKColors.White));
        var doc = session.Document;
        var layer = (RasterLayer)doc.ActiveLayer!;
        var text = new TextElement { Text = "拖我", Position = new SKPoint(100, 100), FontSize = 48 };
        lock (doc.SyncRoot) layer.AddElement(text);

        // 點在自己圖層的文字上 → 移動該文字，而不是整個圖層
        session.Move.OnPointerDown(new ToolPointerEvent(new SKPoint(110, 120), 1f), session);
        session.Move.OnPointerMove(new ToolPointerEvent(new SKPoint(160, 170), 1f), session);
        session.Move.OnPointerUp(new ToolPointerEvent(new SKPoint(160, 170), 1f), session);

        var moved = Assert.IsType<TextElement>(layer.Elements[0]);
        Assert.Equal(150, moved.Position.X, 1);
        Assert.Equal(SKPointI.Empty, layer.Offset); // 圖層本身沒被平移

        // 點空白處 → 改成移動整個圖層：單層只動像素、本層文字留在原地
        //（2026-09-02 使用者明示：文字跟著走會與覆疊快路徑步調不同、一直閃；群組才是全部一起動）
        session.Move.OnPointerDown(new ToolPointerEvent(new SKPoint(400, 400), 1f), session);
        session.Move.OnPointerMove(new ToolPointerEvent(new SKPoint(420, 400), 1f), session);
        session.Move.OnPointerUp(new ToolPointerEvent(new SKPoint(420, 400), 1f), session);
        Assert.Equal(20, layer.Offset.X);
        Assert.Equal(150, ((TextElement)layer.Elements[0]).Position.X, 1);

        // undo 只還原 Offset，文字本來就沒動
        Assert.True(session.History.Undo());
        Assert.Equal(SKPointI.Empty, layer.Offset);
        Assert.Equal(150, ((TextElement)layer.Elements[0]).Position.X, 1);
    }

    [Fact]
    public void MoveTool_MovesWholeGroup_IncludingText()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(512, 512, SKColors.White));
        var doc = session.Document;

        var group = new GroupLayer { Name = "G" };
        var a = new RasterLayer { Name = "A" };
        var sub = new GroupLayer { Name = "子群組" };
        var b = new RasterLayer { Name = "B", Offset = new SKPointI(5, 5) };
        var text = new TextElement { Text = "字", Position = new SKPoint(100, 100), FontSize = 32 };
        lock (doc.SyncRoot)
        {
            doc.Root.Add(group);
            group.Add(a);
            group.Add(sub);
            sub.Add(b);
            a.AddElement(text);
            doc.ActiveLayer = group;
        }

        session.ActiveTool = session.Move;
        session.Move.OnPointerDown(new ToolPointerEvent(new SKPoint(300, 300), 1f), session);
        session.Move.OnPointerMove(new ToolPointerEvent(new SKPoint(330, 320), 1f), session);
        session.Move.OnPointerUp(new ToolPointerEvent(new SKPoint(330, 320), 1f), session);

        // 所有子孫點陣圖層一起動（含巢狀群組內的），文字物件跟著走
        Assert.Equal(new SKPointI(30, 20), a.Offset);
        Assert.Equal(new SKPointI(35, 25), b.Offset);
        var movedText = Assert.IsType<TextElement>(a.Elements[0]);
        Assert.Equal(130, movedText.Position.X, 1);
        Assert.Equal(120, movedText.Position.Y, 1);

        // 整趟一步 undo：位移與文字一起還原；redo 一起回來
        Assert.True(session.Undo());
        Assert.Equal(SKPointI.Empty, a.Offset);
        Assert.Equal(new SKPointI(5, 5), b.Offset);
        Assert.Equal(100, ((TextElement)a.Elements[0]).Position.X, 1);
        Assert.True(session.Redo());
        Assert.Equal(new SKPointI(30, 20), a.Offset);
        Assert.Equal(130, ((TextElement)a.Elements[0]).Position.X, 1);
    }

    [Fact]
    public void HiddenElement_NotComposited()
    {
        // 畫布內編輯期間原元素隱藏（由 overlay 顯示），避免重影
        using var doc = ImageCodec.CreateBlankDocument(256, 256, SKColors.White);
        var layer = new RasterLayer();
        var shape = new ShapeElement
        {
            Kind = ShapeKind.Rectangle,
            Rect = new SKRect(50, 50, 200, 200),
            FillColor = new SKColor(200, 0, 0),
            StrokeWidth = 0,
        };
        lock (doc.SyncRoot)
        {
            doc.Root.Add(layer);
            layer.AddElement(shape);
        }

        using var compositor = new Compositor(doc);
        WaitPixel(compositor, 128, 128, c => c == new SKColor(200, 0, 0));

        lock (doc.SyncRoot) layer.HiddenElementId = shape.Id;
        WaitPixel(compositor, 128, 128, c => c == SKColors.White);

        lock (doc.SyncRoot) layer.HiddenElementId = null;
        WaitPixel(compositor, 128, 128, c => c == new SKColor(200, 0, 0));
    }

    [Fact]
    public void VectorLayer_CacheInvalidation_OnReplace()
    {
        using var doc = ImageCodec.CreateBlankDocument(256, 256, SKColors.White);
        var layer = new RasterLayer();
        var line = new ShapeElement
        {
            Kind = ShapeKind.Line,
            Rect = new SKRect(20, 128, 236, 128),
            StrokeColor = new SKColor(200, 0, 200),
            StrokeWidth = 10,
        };
        lock (doc.SyncRoot)
        {
            doc.Root.Add(layer);
            layer.AddElement(line);
        }

        using var compositor = new Compositor(doc);
        WaitPixel(compositor, 128, 128, c => c == new SKColor(200, 0, 200));

        lock (doc.SyncRoot)
        {
            layer.RemoveElement(line.Id);
        }
        WaitPixel(compositor, 128, 128, c => c == SKColors.White);
    }

    [Fact]
    public void RightDragRotate_RotatesTextAroundFrameCenter_Undoable()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(600, 400, SKColors.White));
        var layer = (RasterLayer)session.Document.ActiveLayer!;
        var text = new TextElement
        {
            Text = "Rot", FontFamily = "Arial", FontSize = 40, Position = new SKPoint(200, 150),
        };
        lock (session.Document.SyncRoot) layer.AddElement(text);
        ElementDragHelper.SetSelected(session, layer, text);

        var frame = text.FrameBounds;
        var center = new SKPoint(frame.MidX, frame.MidY);

        var drag = new ElementDragHelper();
        Assert.True(drag.TryBeginRotate(session, new SKPoint(center.X + 100, center.Y)));
        drag.ContinueRotate(session, new SKPoint(center.X, center.Y + 100)); // 右 → 下 = +90°
        drag.End(session);

        var rotated = (TextElement)layer.FindElement(text.Id)!;
        Assert.Equal(90f, rotated.Rotation, 1);
        // 以框中心為軸：旋轉後框中心不動
        Assert.True(Math.Abs(rotated.FrameBounds.MidX - center.X) < 1.5f,
            $"中心 X 不該漂移（{center.X} → {rotated.FrameBounds.MidX}）");
        Assert.True(Math.Abs(rotated.FrameBounds.MidY - center.Y) < 1.5f,
            $"中心 Y 不該漂移（{center.Y} → {rotated.FrameBounds.MidY}）");

        // 單一步 undo 還原
        Assert.True(session.History.Undo());
        var restored = (TextElement)layer.FindElement(text.Id)!;
        Assert.Equal(0f, restored.Rotation, 2);
        Assert.Equal(text.Position, restored.Position);
        Assert.False(session.History.Undo()); // 整趟只記一步
    }

    [Fact]
    public void RightDragRotate_ShiftSnapsTo15Degrees()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(600, 400, SKColors.White));
        var layer = (RasterLayer)session.Document.ActiveLayer!;
        var text = new TextElement
        {
            Text = "Rot", FontFamily = "Arial", FontSize = 40, Position = new SKPoint(200, 150),
        };
        lock (session.Document.SyncRoot) layer.AddElement(text);
        ElementDragHelper.SetSelected(session, layer, text);

        var frame = text.FrameBounds;
        var center = new SKPoint(frame.MidX, frame.MidY);

        var drag = new ElementDragHelper();
        Assert.True(drag.TryBeginRotate(session, new SKPoint(center.X + 100, center.Y)));
        // 拖到約 38° 的位置，Shift → 吸附 45°
        var rad = 38f * MathF.PI / 180f;
        drag.ContinueRotate(session,
            new SKPoint(center.X + 100 * MathF.Cos(rad), center.Y + 100 * MathF.Sin(rad)),
            ToolModifiers.Shift);
        drag.End(session);

        var rotated = (TextElement)layer.FindElement(text.Id)!;
        Assert.Equal(45f, rotated.Rotation, 2);
    }
}
