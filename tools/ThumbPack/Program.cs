using System.Text;
using MinePainter.App.Gadgets;

// YouTube 縮圖預覽的週邊縮圖打包器。run.bat／publish.bat 每次建置前都會跑一次，
// 已經轉過且原檔沒動過就什麼事也不做。
//   dotnet run --project tools/ThumbPack
// 原圖放 Assets/YouTubePreview/_source/，或直接丟在 Assets/YouTubePreview/ 也行
// （直接丟的會先被收進 _source/，避免原檔跟著進版控或內嵌進 exe）。
Console.OutputEncoding = Encoding.UTF8; // 檔名幾乎都是中文，cmd 預設編碼會變亂碼

var assets = Path.Combine("src", "MinePainter.App", "Assets", "YouTubePreview");
if (!Directory.Exists(assets))
{
    Console.WriteLine($"找不到資料夾：{Path.GetFullPath(assets)}（請在專案根目錄執行）");
    return 1;
}

var source = Path.Combine(assets, "_source");
Directory.CreateDirectory(source);

string[] sourceExtensions = [".png", ".jpg", ".jpeg", ".bmp"];
foreach (var stray in Directory.EnumerateFiles(assets)
             .Where(f => sourceExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
             .ToList())
{
    var moved = Path.Combine(source, Path.GetFileName(stray));
    File.Move(stray, moved, overwrite: true);
    Console.WriteLine($"收進 _source：{Path.GetFileName(stray)}");
}

var converted = YouTubeThumbLibrary.PackFolder(source, assets);

// 直接丟進來的 .webp 不會經過 PackFolder，尺寸不對就地重轉一次
foreach (var webp in Directory.EnumerateFiles(assets, "*.webp").OrderBy(f => f, StringComparer.Ordinal))
{
    if (!YouTubeThumbLibrary.NormalizeFile(webp)) continue;
    Console.WriteLine($"重轉成 {YouTubeThumbLibrary.Width}×{YouTubeThumbLibrary.Height}：{Path.GetFileName(webp)}");
    converted++;
}
var total = Directory.EnumerateFiles(assets, "*.webp").Count();
Console.WriteLine(converted > 0
    ? $"轉好 {converted} 張，圖庫現在共 {total} 張"
    : $"沒有要轉的（圖庫共 {total} 張）");
return 0; // 沒圖也不該讓建置失敗
