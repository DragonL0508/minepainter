using MinePainter.App.Rendering;
using MinePainter.Core.Effects;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace MinePainter.App.Tests;

/// <summary>
/// 旋轉手勢的畫面：從按下、轉動到放開，內容不能中途不見，也不能卡在原本的角度。
/// </summary>
public class RotationDisplayTests(ITestOutputHelper output)
{
    private const int W = 800;
    private const int H = 600;

    /// <summary>畫一幀（照 CanvasDrawOperation 的順序：GPU 圖層樹 → 殘影 → 交接中的手勢覆疊）。</summary>
    private static SKBitmap Frame(EditorSession session, GpuLayerRenderer renderer, SKSurface target)
    {
        var canvas = target.Canvas;
        canvas.Clear(SKColors.White);
        bool gpuDrew;
        lock (session.Document.SyncRoot)
        {
            gpuDrew = renderer.TryDraw(canvas, session, new SKRectI(0, 0, W, H), 1.0);
        }
        if (session.Ghost is { } ghost)
        {
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.Low, IsAntialias = true };
            if (ghost.Rotation != 0)
            {
                canvas.Save();
                canvas.RotateDegrees(ghost.Rotation, ghost.Pivot.X, ghost.Pivot.Y);
            }
            canvas.DrawImage(ghost.Image, ghost.Rect, paint);
            if (ghost.Rotation != 0) canvas.Restore();
        }
        if (session.Transform?.Overlay is { } overlay && (!gpuDrew || overlay.HandingOver))
        {
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.Low, IsAntialias = true };
            foreach (var (_, image, src, m) in overlay.Items)
            {
                var matrix = m;
                canvas.Save();
                canvas.Concat(ref matrix);
                canvas.DrawImage(image, src.Left, src.Top, paint);
                canvas.Restore();
            }
        }
        canvas.Flush();
        using var image2 = target.Snapshot();
        return SKBitmap.FromImage(image2);
    }

    private static int Ink(SKBitmap bitmap)
    {
        var ink = 0;
        for (var y = 0; y < H; y += 2)
        for (var x = 0; x < W; x += 2)
        {
            if (bitmap.GetPixel(x, y) != SKColors.White) ink++;
        }
        return ink;
    }

    private static void Settle(EditorSession session, int ms = 10000)
    {
        var deadline = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < deadline)
        {
            foreach (var idx in TileIndex.CoveringRect(session.Document.Bounds))
                session.Compositor.TryGetTile(idx, out _);
            var pending = session.Document.Descendants()
                .Any(n => n.HasActiveEffects && (n.FxCache.HasPending || !n.FxCache.Rendered));
            if (session.Compositor.DirtyCount == 0 && !pending) return;
            Thread.Sleep(10);
        }
    }

    private void RotateAndCheck(EditorSession session, string what)
    {
        using var renderer = new GpuLayerRenderer();
        using var target = SKSurface.Create(new SKImageInfo(W, H, SKColorType.Bgra8888, SKAlphaType.Premul));

        Settle(session);
        var settled = Ink(Frame(session, renderer, target));
        Assert.True(settled > 200, $"[{what}] 一開始就該看得到內容（{settled}）");

        var center = new SKPoint(W / 2f, H / 2f);
        Assert.True(session.Move.BeginRotate(session, new SKPoint(center.X + 200, center.Y)), $"[{what}] 進不了旋轉");

        var worst = int.MaxValue;
        var worstStep = -1;
        var log = new List<int>();
        for (var i = 1; i <= 24; i++)
        {
            var a = i * 7.5 * Math.PI / 180.0;
            var p = new SKPoint(center.X + (float)(200 * Math.Cos(a)), center.Y + (float)(200 * Math.Sin(a)));
            session.Move.ContinueRotate(session, p, ToolModifiers.None);
            var ink = Ink(Frame(session, renderer, target));
            log.Add(ink);
            if (ink < worst) { worst = ink; worstStep = i; }
            Thread.Sleep(8);
        }
        output.WriteLine($"[{what}] 轉動中著墨：{string.Join(",", log)}（穩定 {settled}）");
        Assert.True(worst > settled / 2, $"[{what}] 第 {worstStep} 步著墨掉到 {worst}（穩定 {settled}）—— 轉動中內容不見了");

        session.Move.EndRotate(session);

        var after = new List<int>();
        var worstAfter = int.MaxValue;
        for (var frame = 0; frame < 200; frame++)
        {
            session.CollectOverlayGhost();
            var ink = Ink(Frame(session, renderer, target));
            after.Add(ink);
            worstAfter = Math.Min(worstAfter, ink);
            var pending = session.Document.Descendants()
                .Any(n => n.HasActiveEffects && (n.FxCache.HasPending || !n.FxCache.Rendered));
            foreach (var idx in TileIndex.CoveringRect(session.Document.Bounds))
                session.Compositor.TryGetTile(idx, out _);
            if (frame > 5 && !pending && session.Compositor.DirtyCount == 0 &&
                session.Transform?.Overlay == null && session.Ghost == null) break;
            Thread.Sleep(8);
        }
        output.WriteLine($"[{what}] 放開後著墨：{string.Join(",", after.Take(40))}");
        Assert.True(worstAfter > settled / 2, $"[{what}] 放開後著墨掉到 {worstAfter}（穩定 {settled}）");
    }

    [Fact]
    public void 一般圖層旋轉中內容不消失()
    {
        using var doc = ImageCodec.CreateBlankDocument(W, H, SKColors.White);
        var art = new RasterLayer { Name = "方塊" };
        art.Surface.Fill(new SKRectI(250, 200, 550, 400), SKColors.Red);
        lock (doc.SyncRoot)
        {
            doc.Root.Add(art);
            doc.ActiveLayer = art;
        }
        using var session = new EditorSession(doc) { LiveElementRendering = true };
        session.ActiveTool = session.Move;
        RotateAndCheck(session, "一般圖層");
    }

    [Fact]
    public void 帶效果的文字旋轉中內容不消失()
    {
        using var doc = ImageCodec.CreateBlankDocument(W, H, SKColors.White);
        var layer = new RasterLayer { Name = "文字" };
        var text = new TextElement
        {
            Text = "測試文字",
            Position = new SKPoint(150, 250),
            FontSize = 96,
            Color = SKColors.Black,
        };
        lock (doc.SyncRoot)
        {
            doc.Root.Add(layer);
            layer.AddElement(text);
            layer.SetEffects([
                LayerEffect.Create(new ObjectOutlineEffect()),
                LayerEffect.Create(new ObjectGlowEffect()),
            ]);
            doc.ActiveLayer = layer;
        }
        using var session = new EditorSession(doc) { LiveElementRendering = true };
        session.ActiveTool = session.Move;
        RotateAndCheck(session, "帶效果的文字");
    }
}
