using MinePainter.Core.Effects;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 筆劃餘暉：目標圖層有效果堆疊時，筆劃烙進圖層後要繼續疊在（還沒重算的）效果快取上，
/// 否則放開滑鼠的瞬間會先看到擦除前的樣子再消失（使用者 2026-09-06：「橡皮擦擦掉的瞬間會回溯」）。
/// </summary>
public class StrokeLingerTests
{
    private static (EditorSession Session, RasterLayer Layer) NewSession(bool withEffects)
    {
        var doc = ImageCodec.CreateBlankDocument(256, 256, SKColors.White);
        var session = new EditorSession(doc);
        var layer = (RasterLayer)doc.ActiveLayer!;
        if (withEffects)
            lock (doc.SyncRoot) layer.SetEffects([LayerEffect.Create(new ObjectOutlineEffect { Width = 3 })]);
        return (session, layer);
    }

    private static void Erase(EditorSession session)
    {
        var tool = session.Eraser;
        session.Eraser.Settings.Radius = 12;
        tool.OnPointerDown(new ToolPointerEvent(new SKPoint(60, 60), 1f), session);
        tool.OnPointerMove(new ToolPointerEvent(new SKPoint(120, 60), 1f), session);
        tool.OnPointerUp(new ToolPointerEvent(new SKPoint(120, 60), 1f), session);
    }

    [Fact]
    public void Eraser_OnLayerWithEffects_LingersUntilEffectCacheCatchesUp()
    {
        var (session, layer) = NewSession(withEffects: true);
        using var _ = session;

        Erase(session);

        var buffer = session.StrokeBuffer;
        Assert.True(buffer.IsLingering, "烙進去之後筆劃要留成餘暉，不能立刻收掉");
        lock (session.Document.SyncRoot)
        {
            Assert.True(buffer.ShouldOverlay(layer), "效果快取還沒追上之前，渲染端要繼續疊這一筆");
            Assert.False(layer.FxCache.UpToDate, "剛烙完效果快取應該是髒的");
        }

        LayerEffectRenderer.RenderLayerNow(session.Document, layer);

        lock (session.Document.SyncRoot)
        {
            Assert.False(buffer.ShouldOverlay(layer), "效果快取追上之後餘暉要自己過期");
            Assert.False(buffer.IsActive, "過期時順手把緩衝收乾淨");
        }
    }

    [Fact]
    public void Eraser_OnPlainLayer_EndsImmediately()
    {
        var (session, layer) = NewSession(withEffects: false);
        using var _ = session;

        Erase(session);

        Assert.False(session.StrokeBuffer.IsActive, "沒有效果堆疊就沒有舊快取可言，照舊立刻收掉");
        lock (session.Document.SyncRoot) Assert.False(session.StrokeBuffer.ShouldOverlay(layer));
    }

    [Fact]
    public void Linger_ExpiresWhenLayerIsEditedAgainAndNextStrokeCanBegin()
    {
        var (session, layer) = NewSession(withEffects: true);
        using var _ = session;

        Erase(session);
        Assert.True(session.StrokeBuffer.IsLingering);

        // 別的編輯改了這層像素：餘暉畫的是舊的那一筆，必須過期
        lock (session.Document.SyncRoot)
        {
            layer.Surface.Fill(new SKRectI(0, 0, 8, 8), SKColors.Red);
            Assert.False(session.StrokeBuffer.ShouldOverlay(layer));
        }

        // 餘暉還在時直接開始下一筆也不能丟例外
        var (session2, _) = NewSession(withEffects: true);
        using var __ = session2;
        Erase(session2);
        Assert.True(session2.StrokeBuffer.IsLingering);
        Erase(session2);
        Assert.True(session2.StrokeBuffer.IsLingering, "第二筆一樣有效果堆疊，一樣留餘暉");
        Assert.Equal(2, session2.History.UndoStack.Count);
    }
}
