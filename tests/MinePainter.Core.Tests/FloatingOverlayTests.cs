using MinePainter.Core.Documents;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Selections;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 浮動內容的「覆疊路徑」：拖曳大片像素時，浮動內容不進合成器，
/// 改由 render thread 直接畫在合成結果上（成本與選取面積無關）。
/// 只在結果完全相同時才走 —— 這裡守住的就是那個「完全相同」。
/// </summary>
public class FloatingOverlayTests
{
    private const int Size = 512;

    private static EditorSession NewSession(out RasterLayer bottom)
    {
        var doc = ImageCodec.CreateBlankDocument(Size, Size, SKColors.White);
        bottom = (RasterLayer)doc.ActiveLayer!;
        bottom.Surface.Fill(new SKRectI(0, 0, Size, Size), SKColors.White);
        return new EditorSession(doc);
    }

    private static RasterLayer AddLayer(Document doc, string name, Action<RasterLayer>? setup = null)
    {
        var layer = new RasterLayer { Name = name };
        setup?.Invoke(layer);
        lock (doc.SyncRoot) doc.Root.Add(layer);
        return layer;
    }

    private static SelectionMask RectMask(Document doc, SKRectI r)
    {
        using var path = new SKPath();
        path.AddRect(new SKRect(r.Left, r.Top, r.Right, r.Bottom));
        return SelectionMask.FromPath(path, doc.Bounds);
    }

    // ---- 判準 ----

    [Fact]
    public void TopmostLayer_UsesOverlay()
    {
        using var session = NewSession(out _);
        var top = AddLayer(session.Document, "top",
            l => l.Surface.Fill(new SKRectI(50, 50, 150, 150), SKColors.Red));
        session.Document.ActiveLayer = top;
        session.Selection = RectMask(session.Document, new SKRectI(50, 50, 150, 150));

        Assert.NotNull(session.LiftSelection());
        Assert.True(session.IsFloatingOverlaid);
    }

    [Fact]
    public void VisibleLayerAbove_FallsBackToCompositor()
    {
        using var session = NewSession(out var bottom);
        AddLayer(session.Document, "above");
        session.Document.ActiveLayer = bottom;
        bottom.Surface.Fill(new SKRectI(50, 50, 150, 150), SKColors.Red);
        session.Selection = RectMask(session.Document, new SKRectI(50, 50, 150, 150));

        Assert.NotNull(session.LiftSelection());
        Assert.False(session.IsFloatingOverlaid); // 上面還有東西會蓋住它，層序必須由合成器負責
    }

    [Fact]
    public void HiddenLayerAbove_StillUsesOverlay()
    {
        using var session = NewSession(out var bottom);
        AddLayer(session.Document, "above").IsVisible = false;
        session.Document.ActiveLayer = bottom;
        bottom.Surface.Fill(new SKRectI(50, 50, 150, 150), SKColors.Red);
        session.Selection = RectMask(session.Document, new SKRectI(50, 50, 150, 150));

        Assert.NotNull(session.LiftSelection());
        Assert.True(session.IsFloatingOverlaid);
    }

    [Theory]
    [InlineData(0.5f, BlendMode.Normal)]
    [InlineData(1f, BlendMode.Multiply)]
    public void LayerOpacityOrBlend_FallsBackToCompositor(float opacity, BlendMode blend)
    {
        using var session = NewSession(out _);
        var top = AddLayer(session.Document, "top", l =>
        {
            l.Opacity = opacity;
            l.BlendMode = blend;
            l.Surface.Fill(new SKRectI(50, 50, 150, 150), SKColors.Red);
        });
        session.Document.ActiveLayer = top;
        session.Selection = RectMask(session.Document, new SKRectI(50, 50, 150, 150));

        Assert.NotNull(session.LiftSelection());
        Assert.False(session.IsFloatingOverlaid); // 不透明度/混合模式要整層套一次，拆不開
    }

    [Fact]
    public void GroupWithOpacity_FallsBackToCompositor()
    {
        using var session = NewSession(out _);
        var group = new GroupLayer { Name = "g", Opacity = 0.5f };
        var inner = new RasterLayer { Name = "inner" };
        inner.Surface.Fill(new SKRectI(50, 50, 150, 150), SKColors.Red);
        lock (session.Document.SyncRoot)
        {
            session.Document.Root.Add(group);
            group.Add(inner);
        }
        session.Document.ActiveLayer = inner;
        session.Selection = RectMask(session.Document, new SKRectI(50, 50, 150, 150));

        Assert.NotNull(session.LiftSelection());
        Assert.False(session.IsFloatingOverlaid); // 群組的不透明度也套在浮動內容上
    }

    // ---- 效能不變式：覆疊路徑拖曳時不得驚動合成器 ----

    [Fact]
    public void OverlayDrag_DoesNotInvalidateAnything()
    {
        using var session = NewSession(out _);
        var top = AddLayer(session.Document, "top",
            l => l.Surface.Fill(new SKRectI(50, 50, 350, 350), SKColors.Red));
        session.Document.ActiveLayer = top;
        session.Selection = RectMask(session.Document, new SKRectI(50, 50, 350, 350));
        session.ActiveTool = session.Move;

        session.Move.OnPointerDown(new ToolPointerEvent(new SKPoint(200, 200), 1f), session);
        Assert.True(session.IsFloatingOverlaid);

        var changes = 0;
        session.Document.Changed += _ => Interlocked.Increment(ref changes);
        for (var i = 1; i <= 20; i++)
            session.Move.OnPointerMove(new ToolPointerEvent(new SKPoint(200 + i * 3, 200 + i * 2), 1f), session);

        // 一格都不用重新合成 —— 這就是大片像素能跟手的原因
        Assert.Equal(0, changes);
        Assert.Equal(new SKRect(110, 90, 410, 390), session.Floating!.TargetRect);
    }

    [Fact]
    public void CompositorDrag_StillInvalidates()
    {
        using var session = NewSession(out var bottom);
        AddLayer(session.Document, "above");
        session.Document.ActiveLayer = bottom;
        bottom.Surface.Fill(new SKRectI(50, 50, 350, 350), SKColors.Red);
        session.Selection = RectMask(session.Document, new SKRectI(50, 50, 350, 350));
        session.ActiveTool = session.Move;

        session.Move.OnPointerDown(new ToolPointerEvent(new SKPoint(200, 200), 1f), session);
        Assert.False(session.IsFloatingOverlaid);

        var changes = 0;
        session.Document.Changed += _ => Interlocked.Increment(ref changes);
        for (var i = 1; i <= 20; i++)
            session.Move.OnPointerMove(new ToolPointerEvent(new SKPoint(200 + i * 3, 200 + i * 2), 1f), session);

        Assert.Equal(20, changes);
    }

    // ---- 兩條路徑必須畫出一模一樣的畫面 ----

    /// <summary>
    /// 同一份文件、同一個移動，一份走覆疊、一份走合成器，逐像素比對。
    /// 用「一張全透明但看得見的圖層」逼出合成器路徑 —— 它不影響畫面，只影響判準。
    /// </summary>
    [Fact]
    public void OverlayAndCompositor_ProduceIdenticalPixels()
    {
        using var overlaySession = BuildAndDrag(withEmptyLayerOnTop: false);
        using var compositorSession = BuildAndDrag(withEmptyLayerOnTop: true);

        Assert.True(overlaySession.IsFloatingOverlaid);
        Assert.False(compositorSession.IsFloatingOverlaid);

        using var a = RenderScreen(overlaySession);
        using var b = RenderScreen(compositorSession);

        for (var y = 0; y < Size; y += 3)
        for (var x = 0; x < Size; x += 3)
        {
            if (a.GetPixel(x, y) != b.GetPixel(x, y))
                Assert.Fail($"({x},{y}) 覆疊={a.GetPixel(x, y)} 合成器={b.GetPixel(x, y)}");
        }
    }

    private static EditorSession BuildAndDrag(bool withEmptyLayerOnTop)
    {
        var doc = ImageCodec.CreateBlankDocument(Size, Size, new SKColor(30, 60, 90));
        var art = new RasterLayer { Name = "art" };
        art.Surface.Fill(new SKRectI(40, 40, 300, 300), new SKColor(220, 40, 40, 180));
        art.Surface.Fill(new SKRectI(100, 100, 200, 200), new SKColor(40, 220, 40));
        lock (doc.SyncRoot) doc.Root.Add(art);
        doc.ActiveLayer = art;

        // 全透明但可見：畫面上什麼都不加，卻讓 CanOverlay 失效 → 逼出合成器路徑
        if (withEmptyLayerOnTop) lock (doc.SyncRoot) doc.Root.Add(new RasterLayer { Name = "empty" });

        var session = new EditorSession(doc);
        session.ActiveTool = session.Move;
        session.Selection = RectMask(doc, new SKRectI(60, 60, 260, 260));
        session.Move.OnPointerDown(new ToolPointerEvent(new SKPoint(150, 150), 1f), session);
        session.Move.OnPointerMove(new ToolPointerEvent(new SKPoint(230, 200), 1f), session);
        session.Move.OnPointerUp(new ToolPointerEvent(new SKPoint(230, 200), 1f), session);
        return session;
    }

    /// <summary>重現畫面：合成器的 tile ＋（走覆疊時）疊上浮動內容與殘影。</summary>
    private static SKBitmap RenderScreen(EditorSession session)
    {
        WaitUntilComposited(session);

        var bitmap = new SKBitmap(new SKImageInfo(Size, Size, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        foreach (var idx in TileIndex.CoveringRect(session.Document.Bounds))
        {
            if (session.Compositor.TryGetTile(idx, out var img) && img != null)
                canvas.DrawImage(img, idx.X * Tile.Size, idx.Y * Tile.Size);
        }

        // 拆下來的整個圖層（此時所有格子都已合成完 → 整片由覆疊層畫）
        if (session.LayerOverlay is { } layerOverlay && layerOverlay.ShouldDraw(tileIsClean: true))
            layerOverlay.Draw(canvas, session.Document.Bounds, SKFilterQuality.None);

        if (session.Ghost is { } ghost) canvas.DrawImage(ghost.Image, ghost.Rect);
        if (session.FloatingOverlay is { } floating) floating.DrawInto(canvas, preview: true);
        canvas.Flush();
        return bitmap;
    }

    private static void WaitUntilComposited(EditorSession session)
    {
        var deadline = Environment.TickCount64 + 5000;
        var settled = 0;
        while (Environment.TickCount64 < deadline)
        {
            // 先問一輪把沒合成過的格排進去，再等佇列清空
            foreach (var idx in TileIndex.CoveringRect(session.Document.Bounds))
                session.Compositor.TryGetTile(idx, out _);

            settled = session.Compositor.DirtyCount == 0 ? settled + 1 : 0;
            if (settled >= 3) return;
            Thread.Sleep(15);
        }
        throw new TimeoutException("合成逾時");
    }

    // ---- 落地後的殘影 ----

    [Fact]
    public void CommitInOverlayMode_LeavesGhostUntilCompositorCatchesUp()
    {
        using var session = NewSession(out _);
        var top = AddLayer(session.Document, "top",
            l => l.Surface.Fill(new SKRectI(50, 50, 250, 250), SKColors.Red));
        session.Document.ActiveLayer = top;
        session.Selection = RectMask(session.Document, new SKRectI(50, 50, 250, 250));
        session.ActiveTool = session.Move;

        session.Move.OnPointerDown(new ToolPointerEvent(new SKPoint(150, 150), 1f), session);
        session.Move.OnPointerMove(new ToolPointerEvent(new SKPoint(250, 250), 1f), session);
        session.Move.OnPointerUp(new ToolPointerEvent(new SKPoint(250, 250), 1f), session);
        var target = session.Floating!.TargetRect;

        session.CommitFloating();

        // 合成器還沒重畫那塊，殘影頂著（不然畫面會閃一下「東西不見了」）
        var ghost = session.Ghost;
        Assert.NotNull(ghost);
        Assert.Equal(target, ghost!.Rect);

        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline && session.Ghost != null)
        {
            foreach (var idx in TileIndex.CoveringRect(session.Document.Bounds))
                session.Compositor.TryGetTile(idx, out _);
            session.CollectOverlayGhost();
            Thread.Sleep(10);
        }
        Assert.Null(session.Ghost); // 合成器追上就收掉
    }

    // ---- 拖曳整個圖層（layer.Offset）也走覆疊路徑 ----

    [Fact]
    public void LayerDrag_DoesNotInvalidatePerMove()
    {
        using var session = NewSession(out _);
        var top = AddLayer(session.Document, "top",
            l => l.Surface.Fill(new SKRectI(0, 0, Size, Size), SKColors.Red));
        session.Document.ActiveLayer = top;
        session.Selection = null;
        session.ActiveTool = session.Move;

        session.Move.OnPointerDown(new ToolPointerEvent(new SKPoint(200, 200), 1f), session);

        var changes = 0;
        session.Document.Changed += _ => Interlocked.Increment(ref changes);
        for (var i = 1; i <= 20; i++)
            session.Move.OnPointerMove(new ToolPointerEvent(new SKPoint(200 + i * 3, 200 + i * 2), 1f), session);

        Assert.NotNull(session.LayerOverlay);
        Assert.Equal(1, changes); // 只有「把圖層拆下來」那一次，之後每一步都是零成本
        Assert.Equal(new SKPointI(60, 40), top.Offset);

        session.Move.OnPointerUp(new ToolPointerEvent(new SKPoint(260, 240), 1f), session);
        Assert.True(session.LayerOverlay!.HandingOver); // 交還中，等合成器追上才收
    }

    [Fact]
    public void LayerDrag_HandoverSwapsResponsibilityPerTile()
    {
        using var session = NewSession(out _);
        var top = AddLayer(session.Document, "top",
            l => l.Surface.Fill(new SKRectI(0, 0, Size, Size), SKColors.Red));
        session.Document.ActiveLayer = top;
        session.ActiveTool = session.Move;

        session.Move.OnPointerDown(new ToolPointerEvent(new SKPoint(200, 200), 1f), session);
        session.Move.OnPointerMove(new ToolPointerEvent(new SKPoint(260, 240), 1f), session);
        var overlay = session.LayerOverlay!;

        // 拆下來的當下：髒格子裡還有這一層，等它重畫成「不含本層」才輪到覆疊
        Assert.False(overlay.HandingOver);
        Assert.True(overlay.ShouldDraw(tileIsClean: true));
        Assert.False(overlay.ShouldDraw(tileIsClean: false));

        session.Move.OnPointerUp(new ToolPointerEvent(new SKPoint(260, 240), 1f), session);

        // 交還時反過來：重畫完的格子已經含本層，覆疊要讓位
        Assert.True(overlay.HandingOver);
        Assert.False(overlay.ShouldDraw(tileIsClean: true));
        Assert.True(overlay.ShouldDraw(tileIsClean: false));

        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline && session.LayerOverlay != null)
        {
            foreach (var idx in TileIndex.CoveringRect(session.Document.Bounds))
                session.Compositor.TryGetTile(idx, out _);
            session.CollectOverlayGhost();
            Thread.Sleep(10);
        }
        Assert.Null(session.LayerOverlay);
    }

    [Fact]
    public void LayerDrag_FallsBackWhenLayerIsNotOnTop()
    {
        using var session = NewSession(out var bottom);
        AddLayer(session.Document, "above");
        session.Document.ActiveLayer = bottom;
        session.ActiveTool = session.Move;

        session.Move.OnPointerDown(new ToolPointerEvent(new SKPoint(200, 200), 1f), session);

        var changes = 0;
        session.Document.Changed += _ => Interlocked.Increment(ref changes);
        for (var i = 1; i <= 10; i++)
            session.Move.OnPointerMove(new ToolPointerEvent(new SKPoint(200 + i * 3, 200), 1f), session);

        Assert.Null(session.LayerOverlay); // 上面有東西會蓋住 → 層序仍交給合成器
        Assert.Equal(10, changes);
    }

    /// <summary>
    /// 拆下來的是「像素」不是整層 —— 文字物件不跟著 Offset 走，拖曳期間必須留在畫面上。
    /// </summary>
    [Fact]
    public void LayerDrag_KeepsTextElementsVisible()
    {
        var doc = ImageCodec.CreateBlankDocument(Size, Size, SKColors.White);
        var art = (RasterLayer)doc.ActiveLayer!;
        art.Surface.Fill(new SKRectI(0, 0, Size, Size), SKColors.White);
        lock (doc.SyncRoot)
        {
            art.AddElement(new Vectors.TextElement
            {
                Text = "測試",
                Position = new SKPoint(60, 200),
                FontSize = 96,
                Color = SKColors.Black,
            });
        }

        using var session = new EditorSession(doc);
        session.ActiveTool = session.Move;
        session.Move.OnPointerDown(new ToolPointerEvent(new SKPoint(400, 400), 1f), session);
        session.Move.OnPointerMove(new ToolPointerEvent(new SKPoint(430, 420), 1f), session);
        Assert.NotNull(session.LayerOverlay);

        using var screen = RenderScreen(session);
        var dark = 0;
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            if (screen.GetPixel(x, y).Red < 100) dark++; // 文字的黑
        }
        Assert.True(dark > 200, $"拖曳圖層時文字物件不見了（只找到 {dark} 個深色像素）");
    }

    /// <summary>
    /// 覆疊路徑與合成器路徑拖完圖層後，畫面必須一模一樣。
    /// </summary>
    [Fact]
    public void LayerDrag_OverlayAndCompositorProduceIdenticalPixels()
    {
        using var overlaySession = BuildAndDragLayer(withEmptyLayerOnTop: false);
        using var compositorSession = BuildAndDragLayer(withEmptyLayerOnTop: true);

        Assert.NotNull(overlaySession.LayerOverlay);
        Assert.Null(compositorSession.LayerOverlay);

        using var a = RenderScreen(overlaySession);
        using var b = RenderScreen(compositorSession);

        for (var y = 0; y < Size; y += 3)
        for (var x = 0; x < Size; x += 3)
        {
            if (a.GetPixel(x, y) != b.GetPixel(x, y))
                Assert.Fail($"({x},{y}) 覆疊={a.GetPixel(x, y)} 合成器={b.GetPixel(x, y)}");
        }
    }

    private static EditorSession BuildAndDragLayer(bool withEmptyLayerOnTop)
    {
        var doc = ImageCodec.CreateBlankDocument(Size, Size, new SKColor(30, 60, 90));
        var art = new RasterLayer { Name = "art" };
        art.Surface.Fill(new SKRectI(40, 40, 300, 300), new SKColor(220, 40, 40, 180));
        art.Surface.Fill(new SKRectI(100, 100, 200, 200), new SKColor(40, 220, 40));
        lock (doc.SyncRoot) doc.Root.Add(art);
        doc.ActiveLayer = art;
        if (withEmptyLayerOnTop) lock (doc.SyncRoot) doc.Root.Add(new RasterLayer { Name = "empty" });

        var session = new EditorSession(doc);
        session.ActiveTool = session.Move;
        session.Move.OnPointerDown(new ToolPointerEvent(new SKPoint(150, 150), 1f), session);
        session.Move.OnPointerMove(new ToolPointerEvent(new SKPoint(230, 200), 1f), session);
        return session; // 刻意停在拖曳中：要比的就是拖曳當下的畫面
    }

    [Fact]
    public void CancelInOverlayMode_GhostSitsAtSourcePosition()
    {
        using var session = NewSession(out _);
        var top = AddLayer(session.Document, "top",
            l => l.Surface.Fill(new SKRectI(50, 50, 250, 250), SKColors.Red));
        session.Document.ActiveLayer = top;
        session.Selection = RectMask(session.Document, new SKRectI(50, 50, 250, 250));
        session.ActiveTool = session.Move;

        session.Move.OnPointerDown(new ToolPointerEvent(new SKPoint(150, 150), 1f), session);
        session.Move.OnPointerMove(new ToolPointerEvent(new SKPoint(250, 250), 1f), session);
        session.CancelFloating();

        Assert.NotNull(session.Ghost);
        Assert.Equal(new SKRect(50, 50, 250, 250), session.Ghost!.Rect); // 取消＝回到原位
    }
}
