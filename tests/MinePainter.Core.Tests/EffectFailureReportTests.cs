using MinePainter.Core.Effects;
using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 效果堆疊裡某條效果炸掉時：不能拖垮整層（其餘效果照算），但也不能悄悄略過 ——
/// 使用者看到的是「光暈怎麼不見了」而不是任何錯誤。守門 <see cref="LayerEffectRenderer.EffectFailed"/>。
/// </summary>
public class EffectFailureReportTests
{
    /// <summary>會爆的效果：Render 一律丟例外。</summary>
    private sealed record ExplodingEffect : IEffect
    {
        public string Name => "爆炸";
        public string Category => "測試";
        public int SourceMargin => 0;
        public IReadOnlyList<ParamDef> Parameters => Array.Empty<ParamDef>();
        public void Render(EffectContext ctx) => throw new InvalidOperationException("boom");
    }

    private static unsafe uint CachePixel(RasterLayer layer, int lx, int ly)
    {
        var tile = layer.FxCache.Surface.GetTileForRead(TileIndex.FromPixel(lx, ly));
        if (tile == null) return 0;
        return ((uint*)tile.Pixels)[(ly & 255) * Tile.Size + (lx & 255)];
    }

    private static (EditorSession Session, RasterLayer Layer) NewSessionWithSquare()
    {
        var doc = ImageCodec.CreateBlankDocument(256, 256, SKColors.Transparent);
        var session = new EditorSession(doc);
        var layer = (RasterLayer)doc.ActiveLayer!;
        lock (doc.SyncRoot) layer.Surface.Fill(new SKRectI(40, 40, 60, 60), SKColors.Red);
        layer.Invalidate(new SKRectI(40, 40, 60, 60));
        return (session, layer);
    }

    [Fact]
    public void 效果炸掉_會回報一次_其餘效果照算()
    {
        var (session, layer) = NewSessionWithSquare();
        var doc = session.Document;
        var reported = new List<(LayerNode Layer, LayerEffect Entry, Exception Ex)>();
        Action<LayerNode, LayerEffect, Exception> handler = (l, e, ex) => { lock (reported) reported.Add((l, e, ex)); };
        LayerEffectRenderer.EffectFailed += handler;
        try
        {
            var bad = LayerEffect.Create(new ExplodingEffect());
            LayerEffectCommands.Add(doc, session.History, layer, bad);
            LayerEffectCommands.Add(doc, session.History, layer,
                LayerEffect.Create(new ObjectOutlineEffect { Width = 6, Color = SKColors.Black }));
            LayerEffectRenderer.RenderLayerNow(doc, layer);

            Assert.Single(reported);
            Assert.Same(layer, reported[0].Layer);
            Assert.Equal(bad.Id, reported[0].Entry.Id);
            Assert.IsType<InvalidOperationException>(reported[0].Ex);
            // 壞掉的那條被略過，後面的外框仍然算出來了（整層沒被拖垮）
            Assert.True((CachePixel(layer, 36, 50) >> 24) > 200, "爆掉的效果拖垮了整層：後面的外框沒算出來");

            // 同一條效果重算再爆：不再重複回報（髒區重算會一直打到它，toast 不能洗版）
            layer.Invalidate(new SKRectI(40, 40, 60, 60));
            LayerEffectRenderer.RenderLayerNow(doc, layer);
            Assert.Single(reported);
        }
        finally
        {
            LayerEffectRenderer.EffectFailed -= handler;
            session.Dispose();
        }
    }
}
