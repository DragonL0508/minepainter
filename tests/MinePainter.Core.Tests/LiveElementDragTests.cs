using MinePainter.Core.Documents;
using MinePainter.Core.Effects;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 物件拖曳的「即時渲染」路徑：畫面端（GPU 圖層渲染）能自己把手勢中的物件畫出來時，
/// 手勢開始就不再先渲染一張「物件＋效果」的快照 —— 那筆開場費用在 4K 帶效果的大字上
/// 是好幾百毫秒，正是「一按下去就頓一下」的來源。
///
/// 條件必須跟 GpuLayerRenderer.CanHandle 對得上：那邊退回舊路、這邊又沒快照的話，
/// 手勢中的物件會整個不見。
/// </summary>
public class LiveElementDragTests
{
    private static (EditorSession Session, RasterLayer Layer, TextElement Element) NewText(
        params LayerEffect[] effects)
    {
        var doc = ImageCodec.CreateBlankDocument(256, 256, SKColors.White);
        var layer = new RasterLayer { Name = "文字" };
        var element = new TextElement { Text = "字", FontSize = 48, Position = new SKPoint(20, 60) };
        lock (doc.SyncRoot)
        {
            doc.Root.Add(layer);
            layer.AddElement(element);
            if (effects.Length > 0) layer.SetEffects(effects);
        }
        return (new EditorSession(doc), layer, element);
    }

    private static SKImage? BeginDrag(EditorSession session, RasterLayer layer, VectorElement element, bool live)
    {
        session.LiveElementRendering = live;
        lock (session.Document.SyncRoot) session.BeginElementOverlayLocked(layer, element);
        return session.ElementOverlay?.Image;
    }

    [Fact]
    public void 即時渲染開著時不做快照()
    {
        var (session, layer, element) = NewText();
        Assert.Null(BeginDrag(session, layer, element, live: true));
        Assert.NotNull(session.ElementOverlay); // 覆疊本身還在（框、角度都靠它）
    }

    [Fact]
    public void 即時渲染關著時照舊做快照()
    {
        var (session, layer, element) = NewText();
        Assert.NotNull(BeginDrag(session, layer, element, live: false));
    }

    [Fact]
    public void 效果翻得成GPU濾鏡時不做快照()
    {
        var (session, layer, element) = NewText(
            LayerEffect.Create(new ObjectOutlineEffect { Width = 6 }),
            LayerEffect.Create(new ObjectShadowEffect { OffsetX = 4, OffsetY = 4, Blur = 6 }));
        Assert.True(GpuEffectFilters.CanTranslate(layer.Effects));
        Assert.Null(BeginDrag(session, layer, element, live: true));
    }

    [Fact]
    public void 效果翻不成GPU濾鏡時要有快照()
    {
        // 漸層外框沒有 Skia 對應，畫面端會退回舊路 —— 這時候少了快照，拖曳中物件會不見
        var (session, layer, element) = NewText(
            LayerEffect.Create(new ObjectOutlineEffect { Width = 6, Gradient = true }));
        Assert.False(GpuEffectFilters.CanTranslate(layer.Effects));
        Assert.NotNull(BeginDrag(session, layer, element, live: true));
    }

    [Fact]
    public void 有調整圖層時照樣即時渲染()
    {
        // 調整圖層已經接進 GPU 路徑（每種調整都給得出 SKColorFilter），不必再退回舊路
        var (session, layer, element) = NewText(LayerEffect.Create(new ObjectOutlineEffect { Width = 6 }));
        lock (session.Document.SyncRoot)
            session.Document.Root.Add(new AdjustmentLayer(new Adjustments.BrightnessContrastAdjustment(Brightness: 0.2f)));
        Assert.Null(BeginDrag(session, layer, element, live: true));
    }

    [Fact]
    public void 沒有快照時收手不留殘影()
    {
        var (session, layer, element) = NewText();
        BeginDrag(session, layer, element, live: true);
        lock (session.Document.SyncRoot) session.EndElementOverlayLocked();
        Assert.Null(session.Ghost);          // 殘影是拿來蓋合成器的延遲的，即時渲染不需要
        Assert.Null(layer.HiddenElementId);  // 原件解除隱藏，畫面上馬上就是它
    }

    [Fact]
    public void 效果進不了GPU時要沿用效果快取而不是整個重算()
    {
        // 快取蓋得到物件時就該直接裁一塊用。覆疊範圍比效果邊界多留一圈餘裕（重取樣用），
        // 判斷「蓋不蓋得到」時要把那一圈還回去 —— 否則永遠不成立，每次按下去都整個重算
        // （4K 帶效果的大字 200–350 ms，就是「點下去卡死」）。
        var (session, layer, element) = NewText(
            LayerEffect.Create(new ObjectGradientEffect()),                 // 翻不成 GPU 濾鏡
            LayerEffect.Create(new ObjectOutlineEffect { Width = 6 }));
        LayerEffectRenderer.RenderAllNow(session.Document);
        Assert.True(layer.FxCache.Rendered);

        session.LiveElementRendering = true; // 即使開著，這層也只能走快照那條路
        lock (session.Document.SyncRoot) session.BeginElementOverlayLocked(layer, element);

        Assert.NotNull(session.ElementOverlay?.Image);
        Assert.True(session.OverlayReusedCache);
    }
}
