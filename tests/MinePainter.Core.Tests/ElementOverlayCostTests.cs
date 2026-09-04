using MinePainter.Core.Effects;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 物件拖曳的覆疊：按下去要「幾乎不用錢」。
///
/// 覆疊是「物件＋效果」的一張快照，手勢期間只變換它。快照本來就有一條快路 ——
/// 效果快取蓋得到這個物件就直接裁一塊；這裡守的是那條快路真的走得到，
/// 因為走不到就等於每次按下去都把整串效果重跑一遍（4K 的大字 200–350 ms）。
/// </summary>
public class ElementOverlayCostTests
{
    private static (EditorSession Session, RasterLayer Layer, TextElement Element) NewText(
        params LayerEffect[] effects)
    {
        var doc = ImageCodec.CreateBlankDocument(512, 512, SKColors.White);
        var layer = new RasterLayer { Name = "文字" };
        var element = new TextElement { Text = "字", FontSize = 96, Position = new SKPoint(40, 160) };
        lock (doc.SyncRoot)
        {
            doc.Root.Add(layer);
            layer.AddElement(element);
            if (effects.Length > 0) layer.SetEffects(effects);
        }
        return (new EditorSession(doc), layer, element);
    }

    [Fact]
    public void 按下去要沿用效果快取而不是整個重算()
    {
        // 覆疊要的範圍比效果邊界多留一圈餘裕（重取樣用）。判斷「快取蓋不蓋得到」時要把那一圈
        // 還回去 —— 否則永遠不成立，每次按下去都整個重算。
        var (session, layer, element) = NewText(
            LayerEffect.Create(new ObjectOutlineEffect { Width = 6 }),
            LayerEffect.Create(new ObjectShadowEffect { OffsetX = 4, OffsetY = 4, Blur = 6 }));
        LayerEffectRenderer.RenderAllNow(session.Document);
        Assert.True(layer.FxCache.Rendered);

        lock (session.Document.SyncRoot) session.BeginElementOverlayLocked(layer, element);

        Assert.NotNull(session.ElementOverlay?.Image);
        Assert.True(session.OverlayReusedCache, "沒沿用效果快取 —— 等於整個物件重算了一遍");
    }

    [Fact]
    public void 剛落地的殘影要能被下一趟手勢接手()
    {
        // 放開之後效果要在背景重算（0.2–0.3 秒），這扇窗內再按下去，快取不是最新的，
        // 本來就得整個重算。而剛落地的那張殘影畫的正好就是這個物件現在的樣子 —— 直接接手。
        var (session, layer, element) = NewText(LayerEffect.Create(new ObjectOutlineEffect { Width = 6 }));
        LayerEffectRenderer.RenderAllNow(session.Document);

        lock (session.Document.SyncRoot)
        {
            session.BeginElementOverlayLocked(layer, element);
            session.MoveElementOverlay(30, 20);
            var moved = element.Translated(30, 20);
            layer.ReplaceElement(moved);
            session.EndElementOverlayLocked(); // 落地：覆疊轉成殘影
            Assert.NotNull(session.Ghost);

            // 這扇窗內（效果還在背景重算）再按下去，就該直接接手那張殘影
            session.BeginElementOverlayLocked(layer, layer.Elements[0]);
        }

        Assert.NotNull(session.ElementOverlay?.Image);
        Assert.True(session.OverlayReusedCache, "沒接手殘影 —— 又把整個物件重算了一遍");
        Assert.Null(session.Ghost); // 影像的擁有權轉給了覆疊
    }
}
