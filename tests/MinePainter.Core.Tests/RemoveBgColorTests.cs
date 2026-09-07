using System.Net;
using MinePainter.Core.AI;
using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// remove.bg 回全解析度時要用它的顏色：伺服器已把邊緣像素混到的背景色去掉，
/// 只拿遮罩乘原圖會留一圈背景色毛邊（使用者 2026-09-07：「丟上 remove.bg 就沒有」）。
/// </summary>
// remove.bg 的假傳輸層是靜態的（HandlerFactory），跟 RemoveBgTests 掛同一個 Collection 才不會平行互相干擾
[Collection("RemoveBg")]
public class RemoveBgColorTests : IDisposable
{
    private sealed class FakeServer(Func<byte[], HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var form = (MultipartFormDataContent)request.Content!;
            byte[]? png = null;
            foreach (var part in form)
                if (part.Headers.ContentDisposition!.Name!.Trim('"') == "image_file") png = await part.ReadAsByteArrayAsync(ct);
            return respond(png!);
        }
    }

    public RemoveBgColorTests() => RemoveBgClient.Reset();

    public void Dispose()
    {
        RemoveBgClient.HandlerFactory = null;
        RemoveBgClient.Reset();
    }

    /// <summary>
    /// 伺服器：圓內保留、圓外透明；保留的像素一律塗成純綠（模擬「去汙染後的顏色」跟原圖不同），
    /// 圓周一圈給半透明（模擬軟邊）。
    /// </summary>
    private static HttpResponseMessage GreenCircle(byte[] uploaded, float scale = 1f)
    {
        using var src = SKBitmap.Decode(uploaded);
        var w = Math.Max(1, (int)(src.Width * scale));
        var h = Math.Max(1, (int)(src.Height * scale));
        using var outBmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        var cx = src.Width / 2f; var cy = src.Height / 2f; const float radius = 60f;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var dx = x / scale - cx; var dy = y / scale - cy;
            var d = MathF.Sqrt(dx * dx + dy * dy);
            var a = d <= radius - 1 ? 255 : d >= radius + 1 ? 0 : (int)((radius + 1 - d) / 2 * 255);
            outBmp.SetPixel(x, y, new SKColor(0, 255, 0, (byte)a));
        }
        using var data = outBmp.Encode(SKEncodedImageFormat.Png, 100);
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data.ToArray()) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        return response;
    }

    private static uint PixelAt(RasterLayer layer, int x, int y)
    {
        lock (layer.Document!.SyncRoot)
            return BackgroundRemovalCommand.ReadRegion(layer.Surface, new SKRectI(x, y, x + 1, y + 1))[0];
    }

    private static (EditorSession Session, RasterLayer Layer) GrayDocument()
    {
        var doc = ImageCodec.CreateBlankDocument(256, 256, new SKColor(200, 200, 200));
        var session = new EditorSession(doc);
        return (session, (RasterLayer)doc.ActiveLayer!);
    }

    [Fact]
    public void 伺服器回全解析度_用它的顏色_邊緣不再是原圖的背景色()
    {
        RemoveBgClient.HandlerFactory = () => new FakeServer(png => GreenCircle(png));
        var (session, layer) = GrayDocument();
        using (session)
        {
            Assert.True(BackgroundRemovalCommand.Run(session, layer, new BackgroundRemovalOptions
            {
                RemoveBg = new RemoveBgOptions("k"), HardEdge = false, SolidCore = false,
            }));

            Assert.Equal(0xFF00FF00u, PixelAt(layer, 128, 128));   // 中心：伺服器的綠，不是原圖的灰
            var edge = PixelAt(layer, 128 + 60, 128);              // 圓周：半透明，顏色仍是綠（premul 後 G = alpha）
            var a = edge >> 24;
            Assert.InRange(a, 60u, 200u);
            Assert.Equal(a, (edge >> 8) & 0xFF);
            Assert.Equal(0u, (edge >> 16) & 0xFF);
            Assert.Equal(0u, PixelAt(layer, 5, 5));
        }
    }

    [Fact]
    public void 預設_全解析度結果照伺服器原樣_軟邊一格不差()
    {
        // 使用者 2026-09-07：「app 內 AI 去背跟直接在 remove.bg 看的還是有差距，邊緣比較常不乾淨」——
        // 之前預設硬邊切出＋內部填實，把伺服器算好的軟邊二值化重畫；現在預設什麼都不動，結果就是它的 PNG
        RemoveBgClient.HandlerFactory = () => new FakeServer(png => GreenCircle(png));
        var (session, layer) = GrayDocument();
        using (session)
        {
            Assert.True(BackgroundRemovalCommand.Run(session, layer, new BackgroundRemovalOptions
            {
                RemoveBg = new RemoveBgOptions("k"),   // 其餘全用預設
            }));
            Assert.Equal(0xFF00FF00u, PixelAt(layer, 128, 128));
            // 圓周上伺服器給的是線性軟邊：d = 60 → alpha = 0.5·255 ≈ 127；d = 60.5 → ≈ 64
            for (var x = 128 + 58; x <= 128 + 62; x++)
            {
                var d = x - 128f;
                var expected = d <= 59 ? 255 : d >= 61 ? 0 : (int)((61 - d) / 2 * 255);
                var p = PixelAt(layer, x, 128);
                Assert.InRange((int)(p >> 24), expected - 2, expected + 2);
                Assert.Equal(p >> 24, (p >> 8) & 0xFF);   // 顏色是伺服器的綠（premul 後 G = alpha）
            }
        }
    }

    [Fact]
    public void 硬邊切出_也用伺服器顏色_不再拿內部平均色蓋邊緣()
    {
        RemoveBgClient.HandlerFactory = () => new FakeServer(png => GreenCircle(png));
        var (session, layer) = GrayDocument();
        using (session)
        {
            Assert.True(BackgroundRemovalCommand.Run(session, layer, new BackgroundRemovalOptions
            {
                RemoveBg = new RemoveBgOptions("k"), HardEdge = true,
            }));
            Assert.Equal(0xFF00FF00u, PixelAt(layer, 128, 128));
            // 圓內靠邊 3px：硬邊後仍不透明，顏色是綠（不是原圖灰，也不是任何平均色）
            Assert.Equal(0xFF00FF00u, PixelAt(layer, 128 + 56, 128));
            Assert.Equal(0u, PixelAt(layer, 128 + 66, 128) >> 24);
        }
    }

    [Fact]
    public void 伺服器只回預覽解析度_顏色仍用原圖()
    {
        RemoveBgClient.HandlerFactory = () => new FakeServer(png => GreenCircle(png, scale: 0.25f));
        var (session, layer) = GrayDocument();
        using (session)
        {
            Assert.True(BackgroundRemovalCommand.Run(session, layer, new BackgroundRemovalOptions
            {
                RemoveBg = new RemoveBgOptions("k", RemoveBgSize.Preview), HardEdge = false, SolidCore = false,
            }));
            // 縮過的顏色是糊的，不能用：留原圖的灰（R = G，不是伺服器的綠）；軟遮罩經引導濾波後中心可能差 1
            var p = PixelAt(layer, 128, 128);
            Assert.True(p >> 24 >= 250, $"alpha {p >> 24}");
            Assert.Equal((p >> 16) & 0xFF, (p >> 8) & 0xFF);
            Assert.True(((p >> 8) & 0xFF) >= 190, $"pixel {p:X8}");
        }
    }

    [Fact]
    public void WithServerColors_換顏色不換alpha()
    {
        uint[] original = [0xFFC8C8C8, 0x80646464, 0x00000000, 0xFF0000FF];
        uint[] server = [0xFF00FF00, 0xFF00FF00, 0xFF00FF00, 0x00000000];
        var result = BackgroundRemovalCommand.WithServerColors(original, server);
        Assert.Equal(0xFF00FF00u, result[0]);
        Assert.Equal(0x80008000u, result[1]);   // 原圖 alpha 0x80、伺服器的綠重新預乘
        Assert.Equal(0u, result[2]);            // 原圖本來就透明
        Assert.Equal(0xFF0000FFu, result[3]);   // 伺服器判成背景：顏色維持原圖，交給遮罩處理
    }
}
