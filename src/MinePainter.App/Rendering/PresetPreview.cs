using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MinePainter.App.Services;
using MinePainter.Core.Effects;
using MinePainter.Core.Vectors;
using SkiaSharp;

namespace MinePainter.App.Rendering;

/// <summary>
/// 預設集面板的縮圖：把「Aa」畫成一小張透明圖，整個效果堆疊跑一遍，再疊在灰色棋盤格上。
/// 以 <see cref="Supersample"/> 倍解析度算再縮小 —— 效果的像素參數（外框 10px 之類）
/// 相對大字才不會粗得離譜，看起來接近實際套在標題文字上的樣子。
/// 計算在背景執行緒（<see cref="Compute"/>），包成 WriteableBitmap 在 UI 執行緒（<see cref="ToBitmap"/>）。
/// </summary>
public static class PresetPreview
{
    /// <summary>「Aa」的字色（淺灰白：白色光暈與黑色陰影都看得出來）。</summary>
    private static readonly SKColor TextColor = new(0xFFE8E8EC);

    private const int Supersample = 3;

    /// <summary>背景執行緒：回傳 premul BGRA 像素（已含棋盤格背景，尺寸 = width×height）。</summary>
    public static unsafe uint[] Compute(EffectPreset preset, int width, int height, CancellationToken ct = default)
    {
        var w = width * Supersample;
        var h = height * Supersample;
        var pixels = new uint[w * h];
        fixed (uint* ptr = pixels)
        {
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info, (IntPtr)ptr, w * 4);
            if (surface == null) return new uint[width * height];
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);

            var typeface = BundledFont.Typeface ?? SKTypeface.Default;
            using var paint = new SKPaint
            {
                Color = TextColor,
                IsAntialias = true,
                SubpixelText = true,
                Typeface = typeface,
                TextSize = h * 0.8f,
            };
            const string text = "Aa";
            var bounds = SKRect.Empty;
            paint.MeasureText(text, ref bounds);
            var x = (w - bounds.Width) / 2f - bounds.Left;
            var y = (h - bounds.Height) / 2f - bounds.Top;
            canvas.DrawText(text, x, y, paint);
            canvas.Flush();
        }

        var current = pixels;
        foreach (var (effect, enabled) in preset.Effects)
        {
            if (!enabled) continue;
            ct.ThrowIfCancellationRequested();
            try
            {
                var margin = effect.SourceMargin;
                // 整層來源的效果：這張小圖就是整層（來源＝目標）
                var ctx = EffectContext.FromPixels(current, w, h,
                    margin == EffectContext.WholeLayer ? 0 : Math.Clamp(margin, 0, 96));
                var ctx2 = new EffectContext(ctx.Region, ctx.SrcRect, ctx.Src, ctx.DocSize)
                {
                    PrimaryColor = SKColors.Black,
                    Cancellation = ct,
                };
                effect.Render(ctx2);
                current = ctx2.Dst;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // 單一效果壞掉就略過它，縮圖照樣給
            }
        }

        // 疊到棋盤格上（中灰兩色：白光暈與黑影都有對比；格子以輸出尺寸的 8px 為準）
        var cell = 8 * Supersample;
        for (var yy = 0; yy < h; yy++)
        {
            for (var xx = 0; xx < w; xx++)
            {
                var light = (xx / cell + yy / cell & 1) == 0;
                var bg = light ? 0xFF8C8C92u : 0xFF6C6C72u;
                current[yy * w + xx] = EffectMath.Over(current[yy * w + xx], bg);
            }
        }

        // 縮回輸出尺寸
        var result = new uint[width * height];
        fixed (uint* src = current)
        fixed (uint* dst = result)
        {
            var srcInfo = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            var dstInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var srcPixmap = new SKPixmap(srcInfo, (IntPtr)src, w * 4);
            using var dstPixmap = new SKPixmap(dstInfo, (IntPtr)dst, width * 4);
            srcPixmap.ScalePixels(dstPixmap, SKFilterQuality.High);
        }
        return result;
    }

    /// <summary>UI 執行緒：像素包成 Avalonia 點陣圖。</summary>
    public static unsafe WriteableBitmap ToBitmap(uint[] pixels, int width, int height)
    {
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var fb = bitmap.Lock();
        for (var y = 0; y < height; y++)
        {
            var row = new Span<uint>((void*)(fb.Address + y * fb.RowBytes), width);
            pixels.AsSpan(y * width, width).CopyTo(row);
        }
        return bitmap;
    }
}
