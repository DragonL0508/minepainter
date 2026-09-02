using MinePainter.Core.Documents;
using MinePainter.Core.Layers;
using SkiaSharp;

namespace MinePainter.Core.IO;

/// <summary>PNG/JPEG/BMP 匯入匯出（SkiaSharp codec）。</summary>
public static class ImageCodec
{
    /// <summary>載入影像成為單一 RasterLayer 的新文件。</summary>
    public static Document LoadAsDocument(string path)
    {
        using var stream = File.OpenRead(path);
        return LoadAsDocument(stream, Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>解碼影像檔成 premul BGRA 點陣圖（caller 負責 Dispose）。</summary>
    public static SKBitmap LoadBitmap(string path)
    {
        using var stream = File.OpenRead(path);
        return LoadBitmap(stream);
    }

    public static SKBitmap LoadBitmap(Stream stream)
    {
        using var codec = SKCodec.Create(stream)
            ?? throw new InvalidDataException("無法辨識的影像格式。");

        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height,
            SKColorType.Bgra8888, SKAlphaType.Premul);

        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
        {
            bitmap.Dispose();
            throw new InvalidDataException($"影像解碼失敗：{result}");
        }
        return bitmap;
    }

    public static Document LoadAsDocument(Stream stream, string layerName)
    {
        using var bitmap = LoadBitmap(stream);
        var info = bitmap.Info;

        var doc = new Document(info.Width, info.Height);
        var layer = new RasterLayer { Name = layerName };
        using (var pixmap = bitmap.PeekPixels())
        {
            layer.Surface.CopyFrom(pixmap, SKPointI.Empty);
        }

        lock (doc.SyncRoot)
        {
            doc.Root.Add(layer);
            doc.ActiveLayer = layer;
        }
        return doc;
    }

    /// <summary>建立含單一底色圖層的新文件。</summary>
    public static Document CreateBlankDocument(int width, int height, SKColor background, string layerName = "背景")
    {
        var doc = new Document(width, height);
        var layer = new RasterLayer { Name = layerName };
        if (background.Alpha > 0)
            layer.Surface.Fill(new SKRectI(0, 0, width, height), background);

        lock (doc.SyncRoot)
        {
            doc.Root.Add(layer);
            doc.ActiveLayer = layer;
        }
        return doc;
    }
}
