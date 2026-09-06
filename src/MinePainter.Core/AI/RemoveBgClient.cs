using System.Net.Http.Headers;
using System.Text.Json;
using SkiaSharp;

namespace MinePainter.Core.AI;

/// <summary>remove.bg 的輸出解析度。</summary>
public enum RemoveBgSize
{
    /// <summary>最高可用解析度（依圖片大小與帳號點數；每張扣點）。paint.net 插件用的就是這個。</summary>
    Auto,
    /// <summary>預覽（最長邊縮到約 0.25 百萬像素；免費額度）。</summary>
    Preview,
}

/// <summary>remove.bg 線上去背的參數。多組 API Key 依序試：一組沒點數／失效／被限流就換下一組。</summary>
public sealed record RemoveBgOptions(IReadOnlyList<string> ApiKeys, RemoveBgSize Size = RemoveBgSize.Auto)
{
    public RemoveBgOptions(string apiKey, RemoveBgSize size = RemoveBgSize.Auto) : this([apiKey], size) { }
}

/// <summary>
/// remove.bg 的回應：<see cref="Alpha"/> 是縮回來源尺寸的前景遮罩（0..255），
/// <see cref="ServerWidth"/>／<see cref="ServerHeight"/> 是伺服器實際回的解析度（沒點數時只給預覽尺寸）。
/// <see cref="Pixels"/> 是伺服器回的整張結果（premul BGRA，來源尺寸）—— 伺服器已把邊緣像素的背景色
/// 去掉（去汙染），只有它跟來源同尺寸時才有；縮過的回 null（放大回來的顏色是糊的，不能用）。
/// </summary>
public sealed record RemoveBgResult(byte[] Alpha, int ServerWidth, int ServerHeight, uint[]? Pixels = null)
{
    /// <summary>伺服器回的圖比來源小（預覽解析度、或超過 25 MP）。</summary>
    public bool Downscaled(int width, int height) => ServerWidth < width || ServerHeight < height;
}

/// <summary>remove.bg 回的錯誤（HTTP 4xx／5xx 附 JSON errors[]）。</summary>
public sealed class RemoveBgException(string message, string? code = null) : Exception(message)
{
    /// <summary>API 的錯誤代碼（例如 auth_failed、insufficient_credits）；解析不出來時是 null。</summary>
    public string? Code { get; } = code;
}

/// <summary>
/// remove.bg 線上去背，做法與 paint.net 的 Remove Background 插件（WhelanB/PDN-RemoveBG）相同：
/// 影像編成 PNG 以 multipart POST 到 https://api.remove.bg/v1.0/removebg（X-Api-Key、size=auto），
/// 回來的 PNG 是去背結果：alpha 當前景遮罩；伺服器回全解析度時連它的顏色也一起用 ——
/// 它已經把邊緣像素混到的背景色去掉了，拿原圖的顏色只乘遮罩，邊上會留一圈背景色的毛邊
/// （使用者 2026-09-07 回報：「丟上 remove.bg 就沒有毛邊」）。
/// 帳號沒點數時伺服器只回預覽解析度（約 0.25 MP），整張貼回會糊；那時只拿遮罩回原圖摳才保得住原解析度。
/// </summary>
public static class RemoveBgClient
{
    public const string Endpoint = "https://api.remove.bg/v1.0/removebg";
    public const string ApiKeyUrl = "https://www.remove.bg/dashboard#api-key";

    /// <summary>測試可以換掉傳輸層（假的 handler）；null = 真的走網路。</summary>
    internal static Func<HttpMessageHandler>? HandlerFactory { get; set; }

    private static HttpClient? _http;
    private static readonly object Gate = new();

    private static HttpClient Http
    {
        get
        {
            lock (Gate)
            {
                if (_http != null) return _http;
                _http = HandlerFactory != null ? new HttpClient(HandlerFactory()) : new HttpClient();
                _http.Timeout = TimeSpan.FromMinutes(5);
                _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MinePainter", "1.0"));
                return _http;
            }
        }
    }

    /// <summary>丟掉快取的 HttpClient（測試換 handler 時用）。</summary>
    internal static void Reset()
    {
        lock (Gate)
        {
            _http?.Dispose();
            _http = null;
        }
    }

    /// <summary>最近一次成功呼叫扣的點數（X-Credits-Charged）；沒有這個標頭時是 null。</summary>
    public static double? LastCreditsCharged { get; private set; }

    /// <summary>
    /// 把 premul BGRA 像素送去 remove.bg，回傳來源尺寸的前景遮罩（伺服器結果的 alpha）。
    /// 伺服器回的圖若比較小（預覽尺寸、超過 25 MP），遮罩以高品質縮放放回原尺寸；
    /// 呼叫端再拿原圖做引導濾波把邊緣貼回真實像素。
    /// </summary>
    public static unsafe RemoveBgResult Cutout(uint[] src, int width, int height, RemoveBgOptions options, CancellationToken ct)
    {
        if (options.ApiKeys.All(string.IsNullOrWhiteSpace))
            throw new RemoveBgException("沒有 API Key", "auth_failed");

        byte[] png;
        using (var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul)))
        {
            fixed (uint* p = src)
                Buffer.MemoryCopy(p, (void*)bmp.GetPixels(), (long)width * height * 4, (long)width * height * 4);
            using var data = bmp.Encode(SKEncodedImageFormat.Png, 100)
                ?? throw new InvalidOperationException("影像編成 PNG 失敗");
            png = data.ToArray();
        }
        ct.ThrowIfCancellationRequested();

        var result = Post(png, options, ct);
        ct.ThrowIfCancellationRequested();

        // 一律解成 premul BGRA（PNG 本身是直接色）
        using var codec = SKCodec.Create(new SKMemoryStream(result))
            ?? throw new RemoveBgException("remove.bg 回傳的影像無法解碼");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var decoded = SKBitmap.Decode(codec, info) ?? throw new RemoveBgException("remove.bg 回傳的影像無法解碼");

        // 只要 alpha：先抽成灰階，再（需要時）縮回來源尺寸
        using var small = new SKBitmap(new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Gray8, SKAlphaType.Opaque));
        {
            var dp = (byte*)decoded.GetPixels();
            var gp = (byte*)small.GetPixels();
            for (var y = 0; y < decoded.Height; y++)
            {
                var row = dp + y * decoded.RowBytes;
                var grow = gp + y * small.RowBytes;
                for (var x = 0; x < decoded.Width; x++) grow[x] = row[x * 4 + 3];
            }
        }
        using var big = small.Width == width && small.Height == height ? null
            : small.Resize(new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque), SKFilterQuality.High)
              ?? throw new InvalidOperationException("縮放 remove.bg 遮罩失敗");
        var output = big ?? small;

        var alpha = new byte[width * height];
        var basePtr = (byte*)output.GetPixels();
        for (var y = 0; y < height; y++)
            new ReadOnlySpan<byte>(basePtr + y * output.RowBytes, width).CopyTo(alpha.AsSpan(y * width, width));

        // 同尺寸：整張結果留下來（顏色已去汙染），呼叫端拿它的顏色、我們的遮罩
        uint[]? pixels = null;
        if (big == null)
        {
            pixels = new uint[width * height];
            var dp = (byte*)decoded.GetPixels();
            for (var y = 0; y < height; y++)
                new ReadOnlySpan<uint>(dp + y * decoded.RowBytes, width).CopyTo(pixels.AsSpan(y * width, width));
        }
        return new RemoveBgResult(alpha, decoded.Width, decoded.Height, pixels);
    }

    /// <summary>
    /// 依序用每組 key 送；某組回「沒點數／key 失效／被限流」就換下一組，其他錯誤直接丟。
    /// 全部都不行時丟最後一組的錯。
    /// </summary>
    public static byte[] Post(byte[] png, RemoveBgOptions options, CancellationToken ct)
    {
        RemoveBgException? last = null;
        foreach (var key in options.ApiKeys)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            try
            {
                return PostWithKey(png, key.Trim(), options.Size, ct);
            }
            catch (RemoveBgException e) when (e.Code is "insufficient_credits" or "auth_failed" or "rate_limit")
            {
                last = e;
            }
        }
        throw last ?? new RemoveBgException("沒有 API Key", "auth_failed");
    }

    /// <summary>送 multipart；成功回影像 bytes，失敗丟 <see cref="RemoveBgException"/>（訊息取自 API 的 errors[]）。</summary>
    private static byte[] PostWithKey(byte[] png, string apiKey, RemoveBgSize size, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        form.Headers.Add("X-Api-Key", apiKey);
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "image_file", "image.png");
        form.Add(new StringContent(size == RemoveBgSize.Preview ? "preview" : "auto"), "size");
        form.Add(new StringContent("png"), "format");

        HttpResponseMessage response;
        try
        {
            response = Http.PostAsync(Endpoint, form, ct).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RemoveBgException("remove.bg 逾時", "timeout");
        }
        catch (HttpRequestException e)
        {
            throw new RemoveBgException("連不上 remove.bg：" + e.Message, "network");
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                LastCreditsCharged = response.Headers.TryGetValues("X-Credits-Charged", out var v)
                    && double.TryParse(v.FirstOrDefault(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var charged)
                    ? charged : null;
                return response.Content.ReadAsByteArrayAsync(ct).GetAwaiter().GetResult();
            }

            var body = response.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            var (message, code) = ParseError(body);
            // 沒附 code 時依 HTTP 狀態補：402 沒點數、401/403 key 失效、429 限流（換下一組 key 的依據）
            code ??= (int)response.StatusCode switch
            {
                402 => "insufficient_credits",
                401 or 403 => "auth_failed",
                429 => "rate_limit",
                _ => null,
            };
            throw new RemoveBgException(message ?? $"remove.bg 回應 {(int)response.StatusCode} {response.ReasonPhrase}", code);
        }
    }

    /// <summary>解析 {"errors":[{"title","code","detail"}]}；解析不出來回 (null, null)。</summary>
    internal static (string? Message, string? Code) ParseError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array)
                return (null, null);
            var parts = new List<string>();
            string? code = null;
            foreach (var e in errors.EnumerateArray())
            {
                var title = e.TryGetProperty("title", out var t) ? t.GetString() : null;
                var detail = e.TryGetProperty("detail", out var d) ? d.GetString() : null;
                code ??= e.TryGetProperty("code", out var c) ? c.GetString() : null;
                var text = string.IsNullOrWhiteSpace(detail) ? title : $"{title}（{detail}）";
                if (!string.IsNullOrWhiteSpace(text)) parts.Add(text!);
            }
            return (parts.Count == 0 ? null : string.Join("；", parts), code);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
