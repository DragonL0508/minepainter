using MinePainter.App.Rendering;
using MinePainter.Core.Effects;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.App.Tests;

/// <summary>
/// 物件拖曳放開的交接：從放開到合成器追上這段期間，畫面上不能有任何一幀「物件不見了」。
/// </summary>
public class ElementDragHandoverTests
{
    private const int W = 900;
    private const int H = 400;

    [Fact]
    public void 放開之後每一幀都看得到物件()
    {
        using var doc = ImageCodec.CreateBlankDocument(W, H, SKColors.White);
        var layer = new RasterLayer { Name = "文字" };
        var text = new TextElement
        {
            Text = "測試文字",
            Position = new SKPoint(120, 120),
            FontSize = 96,
            Color = SKColors.Black,
        };
        lock (doc.SyncRoot)
        {
            doc.Root.Add(layer);
            layer.AddElement(text);
            layer.SetEffects([LayerEffect.Create(new ObjectOutlineEffect())]);
            doc.ActiveLayer = layer;
        }

        using var session = new EditorSession(doc) { LiveElementRendering = true };
        using var renderer = new GpuLayerRenderer();
        using var target = SKSurface.Create(new SKImageInfo(W, H, SKColorType.Bgra8888, SKAlphaType.Premul));

        int DrawFrame()
        {
            var canvas = target.Canvas;
            canvas.Clear(SKColors.White);
            lock (doc.SyncRoot)
            {
                renderer.TryDraw(canvas, session, new SKRectI(0, 0, W, H), 1.0);
            }
            // CanvasDrawOperation 會另外把殘影畫上去
            if (session.Ghost is { } ghost)
            {
                using var paint = new SKPaint { FilterQuality = SKFilterQuality.None };
                canvas.DrawImage(ghost.Image, ghost.Rect, paint);
            }
            canvas.Flush();

            using var image = target.Snapshot();
            using var bitmap = SKBitmap.FromImage(image);
            var ink = 0;
            for (var y = 0; y < H; y += 2)
            for (var x = 0; x < W; x += 2)
            {
                if (bitmap.GetPixel(x, y) != SKColors.White) ink++;
            }
            return ink;
        }

        // 等效果算完、合成器追上
        var deadline = Environment.TickCount64 + 10000;
        while ((!layer.FxCache.Rendered || session.Compositor.DirtyCount > 0) &&
               Environment.TickCount64 < deadline)
        {
            foreach (var idx in TileIndex.CoveringRect(doc.Bounds)) session.Compositor.TryGetTile(idx, out _);
            Thread.Sleep(10);
        }

        var settled = DrawFrame();
        Assert.True(settled > 200, $"一開始就該看得到文字（著墨 {settled}）");

        // 拖一段距離再放開
        session.ActiveTool = session.Move;
        var start = new SKPoint(text.Bounds.MidX, text.Bounds.MidY);
        session.Move.OnPointerDown(new ToolPointerEvent(start, 1f), session);
        for (var i = 1; i <= 10; i++)
        {
            session.Move.OnPointerMove(new ToolPointerEvent(new SKPoint(start.X + i * 8, start.Y + i * 4), 1f), session);
            DrawFrame();
        }

        // 拖久一點：讓背景把這層的效果快取重算成「沒有這個物件」的樣子（拖曳中原件是藏起來的）。
        // 放開的瞬間快取就是空的，畫面全靠殘影頂著 —— 這正是會閃的那扇窗。
        deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline)
        {
            foreach (var idx in TileIndex.CoveringRect(doc.Bounds)) session.Compositor.TryGetTile(idx, out _);
            if (session.Compositor.DirtyCount == 0 && layer.FxCache.Rendered && !layer.FxCache.HasPending) break;
            DrawFrame();
            Thread.Sleep(10);
        }

        session.Move.OnPointerUp(new ToolPointerEvent(new SKPoint(start.X + 80, start.Y + 40), 1f), session);

        // 放開之後一直畫，直到合成器追上；中間任何一幀都不該掉到「幾乎沒東西」
        var worst = int.MaxValue;
        var worstFrame = -1;
        deadline = Environment.TickCount64 + 10000;
        for (var frame = 0; frame < 600; frame++)
        {
            session.CollectOverlayGhost();
            var ink = DrawFrame();
            if (ink < worst) { worst = ink; worstFrame = frame; }
            if (session.Ghost == null && session.ElementOverlay == null &&
                session.Compositor.DirtyCount == 0 && layer.FxCache.Rendered && frame > 5) break;
            if (Environment.TickCount64 > deadline) break;
            Thread.Sleep(4);
        }

        Assert.True(worst > settled / 2,
            $"放開後第 {worstFrame} 幀著墨掉到 {worst}（穩定時 {settled}）—— 物件閃了一下");
    }
}
