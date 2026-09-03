using System.Reflection;
using SkiaSharp;

namespace MinePainter.App.Gadgets;

/// <summary>一張內建的週邊影片縮圖：標題（＝檔名）與縮好的 WebP 位元組。</summary>
public sealed record YouTubeThumb(string Title, byte[] Webp);

/// <summary>
/// 「YouTube 縮圖預覽」週邊影片用的內建縮圖庫：Assets/YouTubePreview/ 下的 <c>.webp</c>
/// 以 <c>ytthumb/檔名</c> 內嵌進組件，檔名（去副檔名）就是影片標題。
/// <para>
/// 進版控的一律是 <see cref="PackFolder"/> 轉好的 960×540 WebP，不是原檔：原尺寸 PNG
/// 一張就一兩 MB，直接內嵌會讓 exe 肥好幾十 MB，而預覽網頁還要再 base64 一次。
/// 尺寸不合的圖仍會在載入時補轉一次（安全網），結果快取在靜態欄位。
/// </para>
/// </summary>
public static class YouTubeThumbLibrary
{
    private const string Prefix = "ytthumb/";

    /// <summary>
    /// 內嵌尺寸：卡片在 1920 下寬 517 CSS px，但高 DPI（125%／150%／2x 縮放很常見）
    /// 會拿實際像素去填，480 在那些螢幕上會被放大而糊掉，所以抓 2 倍頭寸留餘裕。
    /// </summary>
    public const int Width = 960;

    public const int Height = 540;

    /// <summary>WebP 品質：85 在這個尺寸下文字邊緣不會糊，一張約 40–90 KB。</summary>
    public const int Quality = 85;

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
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                var raw = memory.ToArray();

                var webp = Normalize(raw);
                if (webp == null) continue; // 壞檔就跳過，不要讓整個小工具開不起來
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

    /// <summary>已經是 480×270 的就原封不動用（正常情況），否則當場補轉一次。</summary>
    private static byte[]? Normalize(byte[] raw)
    {
        using var bitmap = SKBitmap.Decode(raw);
        if (bitmap == null) return null;
        return bitmap is { Width: Width, Height: Height } ? raw : Encode(bitmap);
    }

    /// <summary>
    /// 把來源資料夾裡的圖片轉成內嵌用的 480×270 WebP 寫進輸出資料夾（檔名沿用＝標題）。
    /// 已經有對應的 .webp 且比原檔新就跳過——這支會掛在每次 build 前跑，不能每次都重轉。
    /// 給 tools/ThumbPack 用；回傳這次實際轉出的張數。
    /// </summary>
    public static int PackFolder(string sourceDir, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(sourceDir)
                     .Where(f => Path.GetExtension(f).ToLowerInvariant()
                         is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp")
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            var target = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(path) + ".webp");
            if (File.Exists(target) && File.GetLastWriteTimeUtc(target) >= File.GetLastWriteTimeUtc(path))
                continue;

            using var bitmap = SKBitmap.Decode(path);
            if (bitmap == null)
            {
                Console.WriteLine($"跳過（讀不出來）：{Path.GetFileName(path)}");
                continue;
            }

            var webp = Encode(bitmap);
            if (webp == null)
            {
                Console.WriteLine($"跳過（編碼失敗）：{Path.GetFileName(path)}");
                continue;
            }

            File.WriteAllBytes(target, webp);
            Console.WriteLine($"{Path.GetFileName(path)} → {Path.GetFileName(target)}（{webp.Length / 1024.0:0.0} KB）");
            count++;
        }
        return count;
    }

    /// <summary>
    /// 就地把一張圖轉成內嵌尺寸的 WebP（已經是 480×270 就不動它，回傳 false）。
    /// 給「直接丟一張 .webp 進來」的情況用：尺寸不對照樣會把 exe 撐肥。
    /// </summary>
    public static bool NormalizeFile(string path)
    {
        using var bitmap = SKBitmap.Decode(path);
        if (bitmap == null) return false;
        if (bitmap is { Width: Width, Height: Height }) return false;

        var webp = Encode(bitmap);
        if (webp == null) return false;
        File.WriteAllBytes(path, webp);
        return true;
    }

    /// <summary>置中裁切成 16:9 後縮到 480×270，轉 WebP。</summary>
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
        using var encoded = image.Encode(SKEncodedImageFormat.Webp, Quality);
        return encoded?.ToArray();
    }
}
