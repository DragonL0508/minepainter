using SkiaSharp;

namespace MinePainter.Core.Effects;

/// <summary>
/// 效果渲染上下文。像素為 premultiplied BGRA（與 tile 相同），以 uint 表示：
/// B = bits 0..7、G = 8..15、R = 16..23、A = 24..31。
/// Src 比 Dst 多一圈 margin（來源座標 = 目標座標 + SrcOffset）；取樣一律 clamp 到 Src 邊界，
/// 或以透明取代（<see cref="SrcOrTransparent"/>）。
/// </summary>
public sealed class EffectContext
{
    public const int WholeLayer = -1;

    /// <summary>目標範圍（doc 座標）。</summary>
    public SKRectI Region { get; }
    public int Width => Region.Width;
    public int Height => Region.Height;

    /// <summary>來源範圍（doc 座標，含 margin，已與畫布相交）。</summary>
    public SKRectI SrcRect { get; }
    public int SrcWidth => SrcRect.Width;
    public int SrcHeight => SrcRect.Height;

    /// <summary>目標 (0,0) 對應到來源緩衝的位置。</summary>
    public int SrcOffsetX => Region.Left - SrcRect.Left;
    public int SrcOffsetY => Region.Top - SrcRect.Top;

    public uint[] Src { get; }
    public uint[] Dst { get; }

    public SKSizeI DocSize { get; }
    public SKColor PrimaryColor { get; init; } = SKColors.Black;
    public SKColor SecondaryColor { get; init; } = SKColors.White;
    public CancellationToken Cancellation { get; init; }

    /// <summary>
    /// 來源內容自己的旋轉角度（度，逆時針為正；不知道或不適用時為 0）。
    /// 「物件」類的效果要跟著物件轉 —— 文字轉了 45°，它的漸層角度也該跟著轉，
    /// 不然使用者調好的角度會在轉動物件的瞬間變成另一個方向（使用者 2026-09-04 明示）。
    /// 由 LayerEffectRenderer 依這層唯一的文字物件填入。
    /// </summary>
    public float ContentRotation { get; init; }

    public EffectContext(SKRectI region, SKRectI srcRect, uint[] src, SKSizeI docSize)
    {
        Region = region;
        SrcRect = srcRect;
        Src = src;
        Dst = new uint[Math.Max(0, region.Width * region.Height)];
        DocSize = docSize;
    }

    /// <summary>測試／獨立使用：來源即目標（無 margin）。</summary>
    public static EffectContext FromPixels(uint[] pixels, int width, int height, int margin = 0)
    {
        var region = new SKRectI(0, 0, width, height);
        if (margin <= 0) return new EffectContext(region, region, pixels, new SKSizeI(width, height));

        // 以 clamp 填出 margin
        var sw = width + margin * 2;
        var sh = height + margin * 2;
        var src = new uint[sw * sh];
        for (var y = 0; y < sh; y++)
        {
            var sy = Math.Clamp(y - margin, 0, height - 1);
            for (var x = 0; x < sw; x++)
            {
                var sx = Math.Clamp(x - margin, 0, width - 1);
                src[y * sw + x] = pixels[sy * width + sx];
            }
        }
        return new EffectContext(region, new SKRectI(-margin, -margin, width + margin, height + margin), src,
            new SKSizeI(width, height));
    }

    /// <summary>來源像素（目標座標；超出來源範圍時 clamp 到邊緣）。</summary>
    public uint SrcAt(int x, int y)
    {
        var sx = Math.Clamp(x + SrcOffsetX, 0, SrcWidth - 1);
        var sy = Math.Clamp(y + SrcOffsetY, 0, SrcHeight - 1);
        return Src[sy * SrcWidth + sx];
    }

    /// <summary>來源像素（目標座標；超出來源範圍為透明）。</summary>
    public uint SrcOrTransparent(int x, int y)
    {
        var sx = x + SrcOffsetX;
        var sy = y + SrcOffsetY;
        if ((uint)sx >= (uint)SrcWidth || (uint)sy >= (uint)SrcHeight) return 0;
        return Src[sy * SrcWidth + sx];
    }

    /// <summary>雙線性取樣（目標座標，像素中心在 +0.5；超出來源範圍以透明混入）。</summary>
    public uint SrcBilinear(float x, float y)
    {
        var fx = x - 0.5f;
        var fy = y - 0.5f;
        var x0 = (int)MathF.Floor(fx);
        var y0 = (int)MathF.Floor(fy);
        var tx = fx - x0;
        var ty = fy - y0;

        var p00 = SrcOrTransparent(x0, y0);
        var p10 = SrcOrTransparent(x0 + 1, y0);
        var p01 = SrcOrTransparent(x0, y0 + 1);
        var p11 = SrcOrTransparent(x0 + 1, y0 + 1);
        return EffectMath.Lerp(EffectMath.Lerp(p00, p10, tx), EffectMath.Lerp(p01, p11, tx), ty);
    }

    /// <summary>雙線性取樣（超出來源範圍時 clamp 到邊緣）。</summary>
    public uint SrcBilinearClamp(float x, float y)
    {
        var fx = x - 0.5f;
        var fy = y - 0.5f;
        var x0 = (int)MathF.Floor(fx);
        var y0 = (int)MathF.Floor(fy);
        var tx = fx - x0;
        var ty = fy - y0;

        var p00 = SrcAt(x0, y0);
        var p10 = SrcAt(x0 + 1, y0);
        var p01 = SrcAt(x0, y0 + 1);
        var p11 = SrcAt(x0 + 1, y0 + 1);
        return EffectMath.Lerp(EffectMath.Lerp(p00, p10, tx), EffectMath.Lerp(p01, p11, tx), ty);
    }

    /// <summary>逐列平行處理（每列檢查取消）。</summary>
    public void ForRows(Action<int> body)
    {
        var options = new ParallelOptions { CancellationToken = Cancellation };
        Parallel.For(0, Height, options, y =>
        {
            Cancellation.ThrowIfCancellationRequested();
            body(y);
        });
    }

    /// <summary>來源原樣複製到目標。</summary>
    public void CopySrcToDst()
    {
        for (var y = 0; y < Height; y++)
        {
            var srcRow = (y + SrcOffsetY) * SrcWidth + SrcOffsetX;
            Array.Copy(Src, srcRow, Dst, y * Width, Width);
        }
    }

    /// <summary>把「與 Src 同尺寸」的緩衝裁成目標大小寫進 Dst。</summary>
    public void CropToDst(uint[] srcSized)
    {
        for (var y = 0; y < Height; y++)
        {
            var srcRow = (y + SrcOffsetY) * SrcWidth + SrcOffsetX;
            Array.Copy(srcSized, srcRow, Dst, y * Width, Width);
        }
    }

    /// <summary>目標區中心（目標座標），供以中心為準的效果使用。</summary>
    public (float X, float Y) Center(float offsetX = 0f, float offsetY = 0f) =>
        (Width / 2f + offsetX * Width / 2f, Height / 2f + offsetY * Height / 2f);
}
