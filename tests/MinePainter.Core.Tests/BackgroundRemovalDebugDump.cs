using MinePainter.Core.AI;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 開發用：把某張圖的去背中間結果輸出成 PNG 看。只在環境變數 MINEPAINTER_DUMP_IMAGE 有設時執行。
/// 輸出到 MINEPAINTER_DUMP_OUT：model.png（模型原始遮罩）、refined.png（引導濾波後）、final.png（填實後）、cut.png（去背結果）。
/// </summary>
public class BackgroundRemovalDebugDump
{
    [Fact]
    public unsafe void Dump()
    {
        var image = Environment.GetEnvironmentVariable("MINEPAINTER_DUMP_IMAGE");
        var outDir = Environment.GetEnvironmentVariable("MINEPAINTER_DUMP_OUT");
        var models = Environment.GetEnvironmentVariable("MINEPAINTER_TEST_MODELS");
        var modelName = Environment.GetEnvironmentVariable("MINEPAINTER_DUMP_MODEL") ?? "isnet-general-use";
        if (string.IsNullOrEmpty(image) || string.IsNullOrEmpty(outDir) || string.IsNullOrEmpty(models)) return;

        using var bmp = SKBitmap.Decode(image).Copy(SKColorType.Bgra8888);
        var w = bmp.Width; var h = bmp.Height;
        var src = new uint[w * h];
        // premul
        using (var pm = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul)))
        {
            using var c = new SKCanvas(pm);
            c.DrawBitmap(bmp, 0, 0);
            c.Flush();
            fixed (uint* p = src) Buffer.MemoryCopy((void*)pm.GetPixels(), p, src.Length * 4L, src.Length * 4L);
        }

        var model = new OnnxModelInfo(modelName, Path.Combine(models, modelName + ".onnx"));
        var raw = BackgroundRemover.Infer(model, src, w, h, gpu: Environment.GetEnvironmentVariable("MINEPAINTER_DUMP_GPU") != "0", CancellationToken.None);
        var scale = Math.Max(1, (int)MathF.Ceiling(Math.Max(w, h) / 1024f));
        var radius = Math.Max(16, 6 * scale);
        var refined = GuidedFilter.Refine(raw, src, w, h, radius);
        var final = BackgroundRemover.SolidifyCore(refined, raw, w, h, radius);

        Directory.CreateDirectory(outDir);
        SaveGray(raw, w, h, Path.Combine(outDir, "model.png"));
        SaveGray(refined, w, h, Path.Combine(outDir, "refined.png"));
        SaveGray(final, w, h, Path.Combine(outDir, "final.png"));

        // 去背結果疊在洋紅底上，微微透明會看得出來
        using var cut = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var c = new SKCanvas(cut))
        {
            c.Clear(new SKColor(255, 0, 255));
            using var paint = new SKPaint();
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var m = final[y * w + x];
                if (m == 0) continue;
                var p = bmp.GetPixel(x, y);
                c.DrawPoint(x, y, new SKPaint { Color = p.WithAlpha(m), BlendMode = SKBlendMode.SrcOver });
            }
        }
        using (var img = SKImage.FromBitmap(cut))
        using (var data = img.Encode(SKEncodedImageFormat.Png, 90))
        using (var fs = File.Create(Path.Combine(outDir, "cut.png")))
            data.SaveTo(fs);

        // 統計：final 在 1..254 的像素比例、直方圖分佈
        int soft = 0, full = 0, zero = 0;
        var hist = new int[8];
        foreach (var v in final)
        {
            if (v == 0) zero++; else if (v == 255) full++; else { soft++; hist[v / 32]++; }
        }
        File.WriteAllText(Path.Combine(outDir, "stats.txt"),
            $"w={w} h={h} radius={radius} zero={zero} full={full} soft={soft}\nsoft hist by 32: {string.Join(",", hist)}\n");
    }

    private static unsafe void SaveGray(byte[] mask, int w, int h, string path)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Gray8, SKAlphaType.Opaque));
        var p = (byte*)bmp.GetPixels();
        for (var y = 0; y < h; y++) mask.AsSpan(y * w, w).CopyTo(new Span<byte>(p + y * bmp.RowBytes, w));
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 90);
        using var fs = File.Create(path);
        data.SaveTo(fs);
    }
}
