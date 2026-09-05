using System.Net;
using MinePainter.Core.AI;
using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Selections;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// remove.bg 線上去背：用假的 HTTP handler 驗請求長得跟 paint.net 插件一樣、回應怎麼貼回圖層，
/// 不碰網路。
/// </summary>
[Collection("RemoveBg")]
public class RemoveBgTests : IDisposable
{
    /// <summary>假伺服器：記下收到的請求、依設定回 PNG 或錯誤 JSON。</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        public string? ApiKey;
        public Dictionary<string, string> Fields = new();
        public byte[]? UploadedPng;
        public Func<byte[], HttpResponseMessage> Respond = _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(RemoveBgClient.Endpoint, request.RequestUri!.ToString());
            Assert.Equal(HttpMethod.Post, request.Method);
            var form = Assert.IsType<MultipartFormDataContent>(request.Content);
            ApiKey = form.Headers.GetValues("X-Api-Key").Single();
            foreach (var part in form)
            {
                var name = part.Headers.ContentDisposition!.Name!.Trim('"');
                if (name == "image_file") UploadedPng = await part.ReadAsByteArrayAsync(ct);
                else Fields[name] = await part.ReadAsStringAsync(ct);
            }
            return Respond(UploadedPng!);
        }
    }

    private readonly FakeServer _server = new();

    public RemoveBgTests()
    {
        RemoveBgClient.Reset();
        RemoveBgClient.HandlerFactory = () => _server;
    }

    public void Dispose()
    {
        RemoveBgClient.HandlerFactory = null;
        RemoveBgClient.Reset();
    }

    /// <summary>把上傳的圖解回來，圓內保留、圓外透明，當作伺服器的去背結果；可選擇縮小輸出。</summary>
    private static HttpResponseMessage CutoutCircle(byte[] uploaded, SKPoint center, float radius, float scale = 1f,
        double? credits = null)
    {
        using var src = SKBitmap.Decode(uploaded);
        var w = Math.Max(1, (int)(src.Width * scale));
        var h = Math.Max(1, (int)(src.Height * scale));
        using var outBmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var sx = (int)(x / scale); var sy = (int)(y / scale);
            var c = src.GetPixel(Math.Min(sx, src.Width - 1), Math.Min(sy, src.Height - 1));
            var dx = x / scale - center.X; var dy = y / scale - center.Y;
            outBmp.SetPixel(x, y, dx * dx + dy * dy <= radius * radius ? c.WithAlpha(255) : SKColors.Transparent);
        }
        using var data = outBmp.Encode(SKEncodedImageFormat.Png, 100);
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data.ToArray()) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        if (credits is { } cr) response.Headers.Add("X-Credits-Charged", cr.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return response;
    }

    private static uint[] Solid(int w, int h, uint color)
    {
        var a = new uint[w * h];
        Array.Fill(a, color);
        return a;
    }

    [Fact]
    public void Cutout_SendsSameRequestAsPaintNetPlugin_AndReturnsServerPixels()
    {
        _server.Respond = png => CutoutCircle(png, new SKPoint(32, 32), 16, credits: 1);
        var src = Solid(64, 64, 0xFFC8C8C8);

        var result = RemoveBgClient.Cutout(src, 64, 64, new RemoveBgOptions("  key-123 "), CancellationToken.None);

        Assert.Equal("key-123", _server.ApiKey);          // 修掉頭尾空白
        Assert.Equal("auto", _server.Fields["size"]);     // 插件用 size=auto
        Assert.Equal("png", _server.Fields["format"]);
        Assert.NotNull(_server.UploadedPng);
        using (var up = SKBitmap.Decode(_server.UploadedPng!))
        {
            Assert.Equal(64, up.Width);
            Assert.Equal(new SKColor(200, 200, 200), up.GetPixel(5, 5));
        }
        Assert.Equal(64 * 64, result.Length);
        Assert.Equal(0xFFC8C8C8u, result[32 * 64 + 32]); // 圓心：伺服器保留
        Assert.Equal(0u, result[2 * 64 + 2]);             // 角落：伺服器清掉
        Assert.Equal(1, RemoveBgClient.LastCreditsCharged);
    }

    [Fact]
    public void Cutout_Preview_ScalesServerResultBackToSourceSize()
    {
        _server.Respond = png => CutoutCircle(png, new SKPoint(32, 32), 20, scale: 0.5f);
        var src = Solid(64, 64, 0xFF0000FF);

        var result = RemoveBgClient.Cutout(src, 64, 64, new RemoveBgOptions("k", RemoveBgSize.Preview), CancellationToken.None);

        Assert.Equal("preview", _server.Fields["size"]);
        Assert.Equal(64 * 64, result.Length);
        Assert.Equal(255, (int)(result[32 * 64 + 32] >> 24));
        Assert.Equal(0, (int)(result[1 * 64 + 1] >> 24));
    }

    [Fact]
    public void Cutout_ApiError_ThrowsWithServerMessageAndCode()
    {
        _server.Respond = _ => new HttpResponseMessage(HttpStatusCode.PaymentRequired)
        {
            Content = new StringContent("""{"errors":[{"title":"Insufficient credits","code":"insufficient_credits","detail":"buy more"}]}"""),
        };
        var ex = Assert.Throws<RemoveBgException>(() =>
            RemoveBgClient.Cutout(Solid(8, 8, 0xFFFFFFFF), 8, 8, new RemoveBgOptions("k"), CancellationToken.None));
        Assert.Equal("insufficient_credits", ex.Code);
        Assert.Contains("Insufficient credits", ex.Message);
        Assert.Contains("buy more", ex.Message);
    }

    [Fact]
    public void Cutout_NonJsonError_FallsBackToStatusCode()
    {
        _server.Respond = _ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>") };
        var ex = Assert.Throws<RemoveBgException>(() =>
            RemoveBgClient.Cutout(Solid(8, 8, 0xFFFFFFFF), 8, 8, new RemoveBgOptions("k"), CancellationToken.None));
        Assert.Null(ex.Code);
        Assert.Contains("502", ex.Message);
    }

    [Fact]
    public void Cutout_EmptyKey_FailsBeforeUploading()
    {
        var ex = Assert.Throws<RemoveBgException>(() =>
            RemoveBgClient.Cutout(Solid(8, 8, 0xFFFFFFFF), 8, 8, new RemoveBgOptions("   "), CancellationToken.None));
        Assert.Equal("auth_failed", ex.Code);
        Assert.Null(_server.UploadedPng);
    }

    private static byte AlphaAt(RasterLayer layer, int x, int y)
    {
        var lx = x - layer.Offset.X;
        var ly = y - layer.Offset.Y;
        var tile = layer.Surface.GetTileForRead(TileIndex.FromPixel(lx, ly));
        if (tile == null) return 0;
        var rect = TileIndex.FromPixel(lx, ly).ToPixelRect();
        return tile.PixelSpan[((ly - rect.Top) * Tile.Size + (lx - rect.Left)) * 4 + 3];
    }

    /// <summary>命令走 remove.bg：伺服器的結果整張貼回、一步 undo；不需要本機模型。</summary>
    [Fact]
    public void Command_RemoveBg_WritesServerResult_OneUndoStep()
    {
        _server.Respond = png => CutoutCircle(png, new SKPoint(128, 128), 60, credits: 1);
        var doc = ImageCodec.CreateBlankDocument(256, 256, new SKColor(200, 200, 200));
        using var session = new EditorSession(doc);
        var layer = (RasterLayer)doc.ActiveLayer!;

        var ok = BackgroundRemovalCommand.Run(session, layer, new BackgroundRemovalOptions
        {
            RemoveBg = new RemoveBgOptions("k"),
        });
        Assert.True(ok);
        Assert.Equal(255, AlphaAt(layer, 128, 128));
        Assert.Equal(0, AlphaAt(layer, 250, 250));
        Assert.Equal("AI 去背", session.History.UndoLabel);
        Assert.Contains("1 點", BackgroundRemover.LastPlanNote);

        session.Undo();
        Assert.Equal(255, AlphaAt(layer, 250, 250));
        session.Redo();
        Assert.Equal(0, AlphaAt(layer, 250, 250));
    }

    /// <summary>只處理選取範圍：只上傳範圍的外接框，範圍外整個清掉。</summary>
    [Fact]
    public void Command_RemoveBg_SelectionOnly_UploadsCropAndClearsOutside()
    {
        _server.Respond = png => CutoutCircle(png, new SKPoint(108, 108), 60); // 相對 crop 原點 (20,20)
        var doc = ImageCodec.CreateBlankDocument(512, 256, new SKColor(200, 200, 200));
        using var session = new EditorSession(doc);
        var layer = (RasterLayer)doc.ActiveLayer!;
        using var path = new SKPath();
        path.AddRect(SKRect.Create(20, 20, 216, 216));
        var selection = SelectionMask.FromPath(path, doc.Bounds);

        var ok = BackgroundRemovalCommand.Run(session, layer, new BackgroundRemovalOptions
        {
            RemoveBg = new RemoveBgOptions("k"),
            Selection = selection,
        });
        Assert.True(ok);
        using (var up = SKBitmap.Decode(_server.UploadedPng!))
        {
            Assert.Equal(216, up.Width);
            Assert.Equal(216, up.Height);
        }
        Assert.Equal(255, AlphaAt(layer, 128, 128));
        Assert.Equal(0, AlphaAt(layer, 384, 128));
        Assert.Equal(0, AlphaAt(layer, 30, 30));

        session.Undo();
        Assert.Equal(255, AlphaAt(layer, 384, 128));
    }

    /// <summary>伺服器回錯：圖層不動、錯誤往上丟。</summary>
    [Fact]
    public void Command_RemoveBg_Error_RollsBack()
    {
        _server.Respond = _ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"errors":[{"title":"Authentication failed","code":"auth_failed"}]}"""),
        };
        var doc = ImageCodec.CreateBlankDocument(64, 64, new SKColor(200, 200, 200));
        using var session = new EditorSession(doc);
        var layer = (RasterLayer)doc.ActiveLayer!;

        var ex = Assert.Throws<RemoveBgException>(() => BackgroundRemovalCommand.Run(session, layer,
            new BackgroundRemovalOptions { RemoveBg = new RemoveBgOptions("bad") }));
        Assert.Equal("auth_failed", ex.Code);
        Assert.Equal(255, AlphaAt(layer, 60, 60));
        Assert.False(session.History.CanUndo);
    }
}
