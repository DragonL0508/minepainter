using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>對齊模式（按住 Tab）的吸附計算：畫布四邊＋兩條中線。</summary>
public class CanvasSnapTests
{
    private static readonly SKRectI Doc = new(0, 0, 1000, 800);

    [Fact]
    public void SnapsLeftEdge_ToCanvasLeft()
    {
        var rect = SKRect.Create(5, 300, 100, 60);
        var (dx, dy, guides) = CanvasSnap.Compute(rect, -2, 0, Doc, tolerance: 8, wholePixels: true);

        // 拖到 left=3，距畫布左邊 3px（容差內）→ 吸到 0
        Assert.Equal(-5, dx);
        Assert.Equal(0, dy);
        Assert.NotNull(guides);
        Assert.Equal(0f, guides!.X);
        Assert.Null(guides.Y);
    }

    [Fact]
    public void SnapsRightEdge_ToCanvasRight()
    {
        var rect = SKRect.Create(880, 100, 100, 60); // right = 980
        var (dx, _, guides) = CanvasSnap.Compute(rect, 16, 0, Doc, 8, true);

        // right 拖到 996，距 1000 差 4 → 吸過去
        Assert.Equal(20, dx);
        Assert.Equal(1000f, guides!.X);
    }

    [Fact]
    public void SnapsCenter_ToCanvasCenterlines_BothAxes()
    {
        var rect = SKRect.Create(446, 356, 100, 80); // mid = (496, 396)
        var (dx, dy, guides) = CanvasSnap.Compute(rect, 0, 0, Doc, 8, true);

        // 中心距 (500, 400) 各差 4 → 兩軸同時吸
        Assert.Equal(4, dx);
        Assert.Equal(4, dy);
        Assert.Equal(500f, guides!.X);
        Assert.Equal(400f, guides.Y);
    }

    [Fact]
    public void NoSnap_BeyondTolerance()
    {
        var rect = SKRect.Create(300, 300, 100, 60); // 離所有目標都遠
        var (dx, dy, guides) = CanvasSnap.Compute(rect, 7, -3, Doc, 8, true);

        Assert.Equal(7, dx);
        Assert.Equal(-3, dy);
        Assert.Null(guides);
    }

    [Fact]
    public void WholePixels_RoundsAdjustment_VectorPathIsExact()
    {
        // 框寬 101 → 中心在 x.5；吸到中線 500 需要半像素位移
        var rect = SKRect.Create(446, 100, 101, 60); // mid X = 496.5

        var (pixelDx, _, _) = CanvasSnap.Compute(rect, 0, 0, Doc, 8, wholePixels: true);
        Assert.Equal(MathF.Round(3.5f), pixelDx); // 像素路徑：整數位移

        var (vectorDx, _, guides) = CanvasSnap.Compute(rect, 0, 0, Doc, 8, wholePixels: false);
        Assert.Equal(3.5f, vectorDx); // 向量路徑：精確貼齊
        Assert.Equal(500f, guides!.X);
    }

    [Fact]
    public void PicksNearestTarget_WhenSeveralInRange()
    {
        // 小框：left 距 0 有 6px、mid 距 0 有 8px —— 兩個都在容差內，要吸最近的（left）
        var rect = SKRect.Create(6, 300, 4, 4);
        var (dx, _, guides) = CanvasSnap.Compute(rect, 0, 0, Doc, 10, true);

        Assert.Equal(-6, dx);
        Assert.Equal(0f, guides!.X);
    }

    [Fact]
    public void EmptyRect_AdjustIsNoOp_AndModeOffClearsGuides()
    {
        using var doc = MinePainter.Core.IO.ImageCodec.CreateBlankDocument(200, 100, SKColors.White);
        using var session = new EditorSession(doc);

        session.SnapToCanvas = true;
        session.SnapGuides = new SnapGuides(1, null);
        var (dx, dy) = CanvasSnap.Adjust(session, SKRect.Empty, 3, 4);
        Assert.Equal(3, dx);
        Assert.Equal(4, dy);
        Assert.Null(session.SnapGuides); // 空框不吸附，也把導線清掉

        session.SnapToCanvas = false;
        session.SnapGuides = new SnapGuides(1, null);
        (dx, dy) = CanvasSnap.Adjust(session, SKRect.Create(0, 0, 10, 10), 3, 4);
        Assert.Equal(3, dx);
        Assert.Null(session.SnapGuides); // 模式關閉：原樣返回並清導線
    }

    [Fact]
    public void TextFrameBounds_HugsInk_AndIgnoresEffects()
    {
        var text = new TextElement
        {
            Text = "ABgj",
            FontFamily = "Arial",
            FontSize = 48,
            Color = SKColors.Red,
            Position = new SKPoint(80, 80),
        };

        // 實際渲染出來的墨水範圍
        var info = new SKImageInfo(400, 300, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bmp = new SKBitmap(info);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.Transparent);
            text.Render(canvas);
        }
        int l = 999, t = 999, r = -1, b = -1;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y).Alpha > 32)
                {
                    l = Math.Min(l, x); t = Math.Min(t, y);
                    r = Math.Max(r, x); b = Math.Max(b, y);
                }
        Assert.True(r > l && b > t, "應該有畫出字");

        // FrameBounds 必須貼著墨水（各邊誤差 3px 內）——這就是使用者看到的框
        var frame = text.FrameBounds;
        Assert.InRange(frame.Left, l - 3, l + 3);
        Assert.InRange(frame.Top, t - 3, t + 3);
        Assert.InRange(frame.Right, r - 2, r + 4);
        Assert.InRange(frame.Bottom, b - 2, b + 4);

        // 加上陰影/光暈：Bounds（失效範圍）要長大，FrameBounds（使用者的框）不動
        var effected = text with
        {
            Shadow = new TextShadow { Distance = 20, Blur = 10 },
            Glow = new TextGlow { Size = 15, Spread = 4 },
        };
        Assert.True(effected.Bounds.Width > text.Bounds.Width);
        Assert.Equal(frame, effected.FrameBounds);
    }

    [Fact]
    public void MoveToolLayerDrag_SnapsExactContent_ToCanvasEdge()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(400, 300, SKColors.Transparent));
        var layer = (RasterLayer)session.Document.ActiveLayer!;
        layer.Surface.Fill(new SKRectI(50, 80, 90, 120), new SKColor(1, 2, 3)); // 內容左緣 50
        session.ActiveTool = session.Move;
        session.SnapToCanvas = true;
        session.SnapTolerance = 8f;

        // 從內容正中央按下（避開四角把手），往左拖 47px → 內容左緣到 3，
        // 容差內 → 吸到 0（吸的是實際內容框，不是 tile 邊界）
        session.Move.OnPointerDown(new ToolPointerEvent(new SKPoint(70, 100), 1f), session);
        session.Move.OnPointerMove(new ToolPointerEvent(new SKPoint(23, 100), 1f), session);
        Assert.NotNull(session.SnapGuides);
        Assert.Equal(0f, session.SnapGuides!.X);
        session.Move.OnPointerUp(new ToolPointerEvent(new SKPoint(23, 100), 1f), session);

        Assert.Equal(-50, layer.Offset.X); // 內容左緣正好落在畫布左邊
        Assert.Null(session.SnapGuides);   // 放開後導線收掉
    }

    [Fact]
    public void ElementDrag_SnapsTextFrame_ExactlyToEdge()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(600, 400, SKColors.White));
        var layer = (RasterLayer)session.Document.ActiveLayer!;
        var text = new TextElement
        {
            Text = "Snap", FontFamily = "Arial", FontSize = 40, Position = new SKPoint(200, 150),
        };
        lock (session.Document.SyncRoot) layer.AddElement(text);
        ElementDragHelper.SetSelected(session, layer, text);
        session.SnapToCanvas = true;
        session.SnapTolerance = 8f;

        var frame = text.FrameBounds;
        var drag = new ElementDragHelper();
        lock (session.Document.SyncRoot)
        {
            drag.BeginMoveLocked(session, layer, text, new SKPoint(frame.MidX, frame.MidY));
        }
        // 拖到框左緣離畫布左邊 5px 的位置 → 精確吸到 0
        var dx = -(frame.Left - 5);
        drag.Continue(session, new SKPoint(frame.MidX + dx, frame.MidY));
        drag.End(session);

        var moved = (TextElement)layer.FindElement(text.Id)!;
        Assert.Equal(0f, moved.FrameBounds.Left, 2); // 吸附對的是使用者看到的框
    }
}
