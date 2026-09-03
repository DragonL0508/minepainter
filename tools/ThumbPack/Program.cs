using MinePainter.App.Gadgets;

// YouTube 縮圖預覽的週邊縮圖打包器：把原圖轉成內嵌用的 480×270 WebP。
//   dotnet run --project tools/ThumbPack
// 原圖放 Assets/YouTubePreview/_source/，或直接丟在 Assets/YouTubePreview/ 也行
// （直接丟的會先被收進 _source/，避免原檔跟著進版控或內嵌進 exe）。
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

var count = YouTubeThumbLibrary.PackFolder(source, assets);
Console.WriteLine($"完成：{count} 張 → {Path.GetFullPath(assets)}");
return count > 0 ? 0 : 2;
