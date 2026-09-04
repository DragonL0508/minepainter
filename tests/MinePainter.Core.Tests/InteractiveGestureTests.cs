using MinePainter.Core.Adjustments;
using MinePainter.Core.Effects;
using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 手勢（移動／旋轉／縮放）期間不算效果堆疊：一段帶外框＋陰影的大文字，效果算一次要上百毫秒，
/// 每動一步就排一次的話合成器永遠追不上，畫面上看起來就是「手勢期間完全沒有渲染」。
/// 手勢中改畫原始內容（看得到、只是暫時沒有效果），放開再算回來。
/// </summary>
public class InteractiveGestureTests
{
    private static (EditorSession Session, RasterLayer Layer) WithEffect()
    {
        var doc = ImageCodec.CreateBlankDocument(64, 64, new SKColor(100, 100, 100));
        var session = new EditorSession(doc);
        var layer = (RasterLayer)doc.ActiveLayer!;
        LayerEffectCommands.Add(doc, session.History, layer,
            LayerEffect.Create(new AdjustmentEffect(new InvertAdjustment())));
        LayerEffectRenderer.RenderLayerNow(doc, layer);
        Assert.True(layer.EffectsRendered);
        return (session, layer);
    }

    [Fact]
    public void DuringGesture_CompositorUsesTheRawContent()
    {
        var (session, layer) = WithEffect();
        Assert.Same(layer.FxCache.Surface, layer.DisplaySurface); // 平常畫的是效果快取

        session.BeginInteractiveGesture();
        Assert.False(layer.EffectsRendered);
        Assert.Same(layer.Surface, layer.DisplaySurface); // 手勢中畫原始內容 —— 看得到，只是沒有效果

        session.EndInteractiveGesture();
        LayerEffectRenderer.RenderLayerNow(session.Document, layer);
        Assert.True(layer.EffectsRendered);
        Assert.Same(layer.FxCache.Surface, layer.DisplaySurface); // 放開後效果回來
        session.Dispose();
    }

    [Fact]
    public void DuringGesture_NoEffectJobsAreTaken()
    {
        var (session, layer) = WithEffect();
        var doc = session.Document;

        session.BeginInteractiveGesture();
        lock (doc.SyncRoot) layer.Surface.Fill(new SKRectI(0, 0, 64, 64), new SKColor(10, 10, 10));
        layer.Invalidate(new SKRectI(0, 0, 64, 64));
        Assert.True(layer.FxCache.HasPending);

        Assert.False(LayerEffectRenderer.RenderPending(doc)); // 手勢中一件工作都不接
        Assert.True(layer.FxCache.HasPending);                // 髒區留著，沒有被吃掉

        session.EndInteractiveGesture();
        Assert.True(LayerEffectRenderer.RenderPending(doc));   // 放開後才算
        Assert.False(layer.FxCache.HasPending);
        session.Dispose();
    }

    [Fact]
    public void EndGesture_MarksEffectsDirtyEvenIfNothingChanged()
    {
        var (session, layer) = WithEffect();
        session.BeginInteractiveGesture();
        session.EndInteractiveGesture();
        // 手勢中被跳過的重算要補回來，不然畫面會停在「沒有效果」的樣子
        Assert.True(layer.FxCache.HasPending);
        session.Dispose();
    }

    [Fact]
    public void ExplicitPerLayerRender_StillWorksDuringAGesture()
    {
        // 烙印／匯出走的是「指定這一層、現在就算」，不能被手勢旗標擋掉
        var (session, layer) = WithEffect();
        session.BeginInteractiveGesture();
        layer.FxCache.MarkAllDirty();
        LayerEffectRenderer.RenderLayerNow(session.Document, layer);
        Assert.False(layer.FxCache.HasPending);
        session.Dispose();
    }
}
