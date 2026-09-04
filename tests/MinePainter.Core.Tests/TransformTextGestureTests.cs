using MinePainter.Core.Effects;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 移動工具對文字圖層做變形手勢（旋轉／縮放）時，物件必須改由一張快照代表。
///
/// 文字的外框／陰影／光暈是圖層效果堆疊，是 CPU 逐像素算的：4K 上一個帶外光暈的字，
/// 整串算一次實測 120 ms。手勢期間每幀 ReplaceElement 一次＝每幀重算一次，就是使用者
/// 回報的「移動工具轉文字會卡（文字工具不會）」與旋轉中的撕裂。
/// </summary>
public class TransformTextGestureTests
{
    private static (Documents.Document Doc, RasterLayer Layer, TextElement Text) NewTextDoc(bool withGlow)
    {
        var doc = ImageCodec.CreateBlankDocument(800, 600, SKColors.Transparent);
        var layer = new RasterLayer { Name = "文字" };
        var text = new TextElement
        {
            Text = "特效文字",
            FontSize = 64f,
            Position = new SKPoint(120, 200),
            Color = SKColors.White,
        };
        layer.AddElement(text);
        doc.Root.Add(layer);
        doc.ActiveLayer = layer;
        if (withGlow)
        {
            layer.SetEffects([LayerEffect.Create(new ObjectGlowEffect
            {
                Size = 20, Spread = 4, Color = SKColors.Yellow, Opacity = 80,
            })]);
            LayerEffectRenderer.RenderLayerNow(doc, layer);
        }
        return (doc, layer, text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 手勢期間物件改由快照代表_原件不動(bool withGlow)
    {
        var (doc, layer, text) = NewTextDoc(withGlow);
        using (doc)
        using (var transform = TransformSession.Begin(doc, layer, out var reason)!)
        {
            Assert.Null(reason);
            transform.BeginGesturePreview(live: true);

            // 只有文字的圖層沒有像素，以前這裡拿不到覆疊 → 只好每幀重算整串效果
            var overlay = transform.Overlay;
            Assert.NotNull(overlay);
            Assert.Single(overlay!.Items);
            Assert.Same(layer, overlay.Items[0].Layer);
            Assert.True(layer.ElementsHidden, "原件要藏起來，否則畫面上會有兩份文字");

            transform.RotationDeg = 30f;
            transform.Apply(preview: true);

            // 手勢中原件完全不動 —— 動一下就是一次整串圖層效果重算
            var during = Assert.IsType<TextElement>(layer.Elements[0]);
            Assert.Equal(text.Rotation, during.Rotation);
            Assert.Equal(text.Position, during.Position);

            transform.EndGesture();

            Assert.False(layer.ElementsHidden);
            var after = Assert.IsType<TextElement>(layer.Elements[0]);
            Assert.Equal(30f, after.Rotation, 2); // 放開才一次落地
        }
    }

    [Fact]
    public void 手勢取消時物件回到原樣()
    {
        var (doc, layer, text) = NewTextDoc(withGlow: true);
        using (doc)
        using (var transform = TransformSession.Begin(doc, layer, out _)!)
        {
            transform.BeginGesturePreview(live: true);
            transform.RotationDeg = 45f;
            transform.Apply(preview: true);
            transform.RotationDeg = 0f; // 轉回原點＝identity
            transform.EndGesture();

            Assert.False(layer.ElementsHidden);
            var after = Assert.IsType<TextElement>(layer.Elements[0]);
            Assert.Equal(text.Rotation, after.Rotation);
            Assert.Equal(text.Position, after.Position);
        }
    }

    [Fact]
    public void 純平移文字圖層不必重算效果堆疊()
    {
        var (doc, layer, text) = NewTextDoc(withGlow: true);
        using (doc)
        {
            Assert.False(layer.FxCache.HasPending);
            var baseOffset = layer.Offset;

            using var transform = TransformSession.Begin(doc, layer, out _)!;
            for (var i = 1; i <= 3; i++)
            {
                transform.TargetRect = SKRect.Create(
                    transform.SourceRect.Left + i * 7, transform.SourceRect.Top + i * 5,
                    transform.SourceRect.Width, transform.SourceRect.Height);
                transform.Apply(preview: true);

                // 物件與圖層 Offset 走同一個整數位移 → 物件在圖層座標裡沒動 → 快取內容沒變
                Assert.False(layer.FxCache.HasPending,
                    $"第 {i} 步就把效果快取標髒了（每步重算一次 = 拖不動）");
                Assert.Equal(new SKPointI(baseOffset.X + i * 7, baseOffset.Y + i * 5), layer.Offset);
                var moved = Assert.IsType<TextElement>(layer.Elements[0]);
                Assert.Equal(text.Position.X + i * 7, moved.Position.X, 2);
                Assert.Equal(text.Position.Y + i * 5, moved.Position.Y, 2);
            }
        }
    }

    [Fact]
    public void 有像素的一般圖層不受影響()
    {
        var doc = ImageCodec.CreateBlankDocument(400, 300, SKColors.Transparent);
        var layer = (RasterLayer)doc.ActiveLayer!;
        layer.Surface.Fill(new SKRectI(50, 50, 200, 200), SKColors.Red);
        using (doc)
        using (var transform = TransformSession.Begin(doc, layer, out _)!)
        {
            transform.BeginGesturePreview(live: true);
            Assert.False(layer.ElementsHidden);
            Assert.NotNull(transform.Overlay);
            transform.EndGesture();
        }
    }
}
