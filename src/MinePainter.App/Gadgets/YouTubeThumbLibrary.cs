using System.Reflection;
using SkiaSharp;

namespace MinePainter.App.Gadgets;

/// <summary>一張內建的週邊影片縮圖：標題（＝檔名）與縮好的 WebP 位元組。</summary>
public sealed record YouTubeThumb(string Title, byte[] Webp);

/// <summary>
/// 「YouTube 縮圖預覽」週邊影片用的內建縮圖庫：Assets/YouTubePreview/ 下的圖片
/// 以 <c>ytthumb/檔名</c> 內嵌進組件，檔名（去副檔名）就是影片標題。
/// <para>
/// 讀進來後一律置中裁切成 16:9 並縮到 480×270 再轉 WebP：預覽網頁把每張圖 base64
/// 內嵌，原尺寸直接塞會讓單一 HTML 破十 MB。結果快取在靜態欄位，只算一次。
/// </para>
/// </summary>
public static class YouTubeThumbLibrary
{
    private const string Prefix = "ytthumb/";
    private const int Width = 480;
    private const int Height = 270;

    private static IReadOnlyList<YouTubeThumb>? _cache;

    /// <summary>資料夾沒放圖時回傳空清單（預覽照樣能開，週邊縮圖退回純色底）。</summary>
    public static IReadOnlyList<YouTubeThumb> All => _cache ??= Load();

    private static IReadOnlyList<YouTubeThumb> Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var list = new List<YouTubeThumb>();
        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            try
            {
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream == null) continue;
                using var data = SKData.Create(stream);
                using var bitmap = SKBitmap.Decode(data);
                if (bitmap == null) continue; // 壞檔就跳過，不要讓整個小工具開不起來

                var webp = Encode(bitmap);
                if (webp == null) continue;
                var file = name[Prefix.Length..];
                list.Add(new YouTubeThumb(Path.GetFileNameWithoutExtension(file), webp));
            }
            catch
            {
                // 單張圖有問題不值得打斷預覽
            }
        }
        return list;
    }

    /// <summary>置中裁切成 16:9 後縮到 480×270，轉 WebP（q80）。</summary>
    private static byte[]? Encode(SKBitmap source)
    {
        var scale = Math.Max(Width / (double)source.Width, Height / (double)source.Height);
        var drawW = (float)(source.Width * scale);
        var drawH = (float)(source.Height * scale);
        var dest = SKRect.Create((Width - drawW) / 2, (Height - drawH) / 2, drawW, drawH);

        var info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        surface.Canvas.Clear(SKColors.Black); // 透明區在 WebP 上會變黑，先鋪好比較可預期
        using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High })
        {
            surface.Canvas.DrawBitmap(source, dest, paint);
        }
        surface.Canvas.Flush();
        using var image = surface.Snapshot();
        using var encoded = image.Encode(SKEncodedImageFormat.Webp, 80);
        return encoded?.ToArray();
    }
}
