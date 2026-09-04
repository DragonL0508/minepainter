using MinePainter.Core.Effects;
using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 手勢代理圖：移動／旋轉／縮放開始時，把整層「看得到的樣子」（含效果與文字）拍成一張低解析度的圖，
/// 手勢期間只畫這張圖套矩陣 —— 不重算效果、不重新合成，所以畫面跟得上，而且特效還在。
/// </summary>
public class GestureProxyTests
{
    private static (EditorSession Session, RasterLayer Layer) TextWithOutline(int fontSize, int canvas = 400)
    {
        var doc = ImageCodec.CreateBlankDocument(canvas, canvas * 3 / 4, SKColors.White);
        var session = new EditorSession(doc);
        var layer = VectorCommands.CreateTextLayerSilently(doc);
        var element = new TextElement
        {
            Text = "標題", Position = new SKPoint(20, 20), FontSize = fontSize, Color = SKColors.Red,
        };
        lock (doc.SyncRoot) layer.AddElement(element);
        VectorCommands.CommitNewTextLayer(doc, session.History, layer, element, "文字");
        LayerEffectCommands.Add(doc, session.History, layer,
            LayerEffect.Create(new ObjectOutlineEffect { Width = 6 }, color: SKColors.Black));
        LayerEffectRenderer.RenderLayerNow(doc, layer);
        session.SelectedElement = (layer.Id, element.Id);
        session.ActiveTool = session.Move;
        return (session, layer);
    }

    [Fact]
    public void TextLayer_GetsAGestureOverlay()
    {
        var (session, layer) = TextWithOutline(80);
        var transform = session.EnterTransformMode(TransformMode.Free);
        Assert.NotNull(transform);

        transform!.BeginGesturePreview();
        // 以前只收「有像素」的項目，文字圖層完全沒有覆疊 → 每一步都重算效果
        Assert.NotNull(transform.Overlay);
        Assert.Single(transform.Overlay!.Items);
        Assert.True(layer.ElementsHidden, "物件已經在代理圖裡，合成器不該再畫一次");
        session.Dispose();
    }

    [Fact]
    public void Proxy_IsCappedAndKeepsTheEffect()
    {
        // 物件很大但整個在畫布內：效果快取蓋得到，代理圖就帶著外框
        var (session, layer) = TextWithOutline(1600, canvas: 8000);
        var transform = session.EnterTransformMode(TransformMode.Free)!;
        transform.BeginGesturePreview();

        var (image, bounds) = transform.Overlay!.Items[0];
        Assert.True(bounds.Width > 2048, "這個測試要的就是超過上限的大物件");
        Assert.True(image.Width <= 2048 && image.Height <= 2048,
            $"代理圖 {image.Width}x{image.Height} 沒有被縮下來");

        using var bmp = SKBitmap.FromImage(image);
        var painted = 0;
        var black = 0;
        for (var y = 0; y < bmp.Height; y += 2)
        for (var x = 0; x < bmp.Width; x += 2)
        {
            var c = bmp.GetPixel(x, y);
            if (c.Alpha == 0) continue;
            painted++;
            if (c.Red < 80 && c.Green < 80 && c.Blue < 80) black++;
        }
        Assert.True(painted > 0, "代理圖是空的，手勢中會看不到東西");
        Assert.True(black > 0, "代理圖裡沒有外框 —— 特效沒被拍進去");
        session.Dispose();
    }

    [Fact]
    public void ObjectMuchBiggerThanTheCanvas_StillGetsACompleteProxy()
    {
        // 效果快取只算畫布看得到的那塊，蓋不到整個物件；代理圖要改畫「基底＋物件」，
        // 形狀完整（只是暫時沒有效果），不然手勢中把畫布外那段拉進來會是空白
        var (session, layer) = TextWithOutline(2000, canvas: 400);
        var transform = session.EnterTransformMode(TransformMode.Free)!;
        transform.BeginGesturePreview();

        var (image, bounds) = transform.Overlay!.Items[0];
        using var bmp = SKBitmap.FromImage(image);
        var painted = 0;
        for (var y = 0; y < bmp.Height; y += 2)
        for (var x = 0; x < bmp.Width; x += 2)
            if (bmp.GetPixel(x, y).Alpha > 0) painted++;
        Assert.True(painted > 0, $"代理圖 {image.Width}x{image.Height}（框 {bounds.Width}x{bounds.Height}）是空的");
        session.Dispose();
    }

    [Fact]
    public void DuringGesture_ElementsAreNotRewrittenEveryStep()
    {
        var (session, layer) = TextWithOutline(80);
        var original = layer.Elements[0];
        var transform = session.EnterTransformMode(TransformMode.Free)!;
        transform.BeginGesturePreview();

        transform.RotationDeg = 30f;
        transform.Apply(preview: true);
        // 手勢中原件不動（動的是代理圖）：不動元素就不會觸發效果堆疊重算
        Assert.Same(original, layer.Elements[0]);

        transform.EndGesture();
        Assert.False(layer.ElementsHidden);          // 物件放回去
        Assert.NotSame(original, layer.Elements[0]); // 放開才真的套上去
        session.Dispose();
    }

    [Fact]
    public void EndGesture_PutsElementsBack_EvenWhenNothingMoved()
    {
        var (session, layer) = TextWithOutline(80);
        var transform = session.EnterTransformMode(TransformMode.Free)!;
        transform.BeginGesturePreview();
        Assert.True(layer.ElementsHidden);
        transform.EndGesture();
        Assert.False(layer.ElementsHidden);
        session.Dispose();
    }
}
