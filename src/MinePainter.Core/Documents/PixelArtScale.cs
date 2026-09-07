using SkiaSharp;

namespace MinePainter.Core.Documents;

/// <summary>
/// 像素圖放大（Scale2x／Scale3x，又稱 EPX／AdvMAME）：只看四鄰是否「同色」決定角落要不要順著對角補色，
/// 不做任何混色 —— 輸出的每個像素都是輸入裡本來就有的顏色，Minecraft 材質的調色盤不會被污染。
/// 最接近像素會把斜線放成樓梯，這裡會把樓梯削成斜線；雙三次則會糊掉。
/// 任意倍率：先用 2×／3× 疊到不小於目標，再由呼叫端縮回精確尺寸（整數倍時一格不差）。
/// </summary>
public static class PixelArtScale
{
    /// <summary>
    /// 湊出一組 2／3 倍的乘積 ≥ <paramref name="factor"/>（最小的那組）。
    /// 例：5 → 2×3=6；4 → 2×2；7 → 2×2×2=8（不是 3×3=9）。
    /// </summary>
    public static int[] PlanPasses(float factor)
    {
        var target = (int)MathF.Ceiling(factor - 1e-4f);
        if (target <= 1) return [];
        int[] best = [];
        var bestProduct = int.MaxValue;
        for (var threes = 0; Pow(3, threes) < target * 2; threes++)
        {
            var product = Pow(3, threes);
            var twos = 0;
            while (product < target) { product *= 2; twos++; }
            if (product < bestProduct || (product == bestProduct && threes + twos < best.Length))
            {
                bestProduct = product;
                best = [.. Enumerable.Repeat(3, threes), .. Enumerable.Repeat(2, twos)];
            }
        }
        return best;
    }

    /// <summary>依 <see cref="PlanPasses"/> 的結果連續放大；回傳新點陣圖（呼叫端釋放），沒有步驟時回傳輸入的複本。</summary>
    public static SKBitmap Upscale(SKBitmap source, IReadOnlyList<int> passes)
    {
        var current = source.Copy();
        foreach (var pass in passes)
        {
            var next = pass == 3 ? Scale3x(current) : Scale2x(current);
            current.Dispose();
            current = next;
        }
        return current;
    }

    /// <summary>
    /// Scale2x：E 的四個角落各看兩個正交鄰居 —— 兩個相同、而且不是「整排都一樣」時，角落取那個顏色。
    /// <code>
    /// A B C     E0 E1     E0 = B==D && B!=F && D!=H ? D : E
    /// D E F  →  E2 E3     E1 = B==F && B!=D && F!=H ? F : E
    /// G H I               E2 = D==H && D!=B && H!=F ? D : E
    ///                     E3 = H==F && D!=H && B!=F ? F : E
    /// </code>
    /// </summary>
    public static unsafe SKBitmap Scale2x(SKBitmap src)
    {
        var w = src.Width;
        var h = src.Height;
        var dst = new SKBitmap(new SKImageInfo(w * 2, h * 2, src.ColorType, src.AlphaType));
        var s = (uint*)src.GetPixels();
        var d = (uint*)dst.GetPixels();
        var dw = w * 2;
        for (var y = 0; y < h; y++)
        {
            var up = Math.Max(0, y - 1) * w;
            var row = y * w;
            var down = Math.Min(h - 1, y + 1) * w;
            for (var x = 0; x < w; x++)
            {
                var left = Math.Max(0, x - 1);
                var right = Math.Min(w - 1, x + 1);
                var b = s[up + x];
                var dd = s[row + left];
                var e = s[row + x];
                var f = s[row + right];
                var hh = s[down + x];

                var o = y * 2 * dw + x * 2;
                d[o] = b == dd && b != f && dd != hh ? dd : e;
                d[o + 1] = b == f && b != dd && f != hh ? f : e;
                d[o + dw] = dd == hh && dd != b && hh != f ? dd : e;
                d[o + dw + 1] = hh == f && dd != hh && b != f ? f : e;
            }
        }
        return dst;
    }

    /// <summary>Scale3x：同一套規則的 3×3 版（中央保留 E，邊中點看兩側是否要延伸）。</summary>
    public static unsafe SKBitmap Scale3x(SKBitmap src)
    {
        var w = src.Width;
        var h = src.Height;
        var dst = new SKBitmap(new SKImageInfo(w * 3, h * 3, src.ColorType, src.AlphaType));
        var s = (uint*)src.GetPixels();
        var d = (uint*)dst.GetPixels();
        var dw = w * 3;
        for (var y = 0; y < h; y++)
        {
            var up = Math.Max(0, y - 1) * w;
            var row = y * w;
            var down = Math.Min(h - 1, y + 1) * w;
            for (var x = 0; x < w; x++)
            {
                var left = Math.Max(0, x - 1);
                var right = Math.Min(w - 1, x + 1);
                var a = s[up + left];
                var b = s[up + x];
                var c = s[up + right];
                var dd = s[row + left];
                var e = s[row + x];
                var f = s[row + right];
                var g = s[down + left];
                var hh = s[down + x];
                var i = s[down + right];

                var o = y * 3 * dw + x * 3;
                if (b != hh && dd != f)
                {
                    d[o] = dd == b ? dd : e;
                    d[o + 1] = (dd == b && e != c) || (b == f && e != a) ? b : e;
                    d[o + 2] = b == f ? f : e;
                    d[o + dw] = (dd == b && e != g) || (dd == hh && e != a) ? dd : e;
                    d[o + dw + 1] = e;
                    d[o + dw + 2] = (b == f && e != i) || (hh == f && e != c) ? f : e;
                    d[o + 2 * dw] = dd == hh ? dd : e;
                    d[o + 2 * dw + 1] = (dd == hh && e != i) || (hh == f && e != g) ? hh : e;
                    d[o + 2 * dw + 2] = hh == f ? f : e;
                }
                else
                {
                    d[o] = d[o + 1] = d[o + 2] = e;
                    d[o + dw] = d[o + dw + 1] = d[o + dw + 2] = e;
                    d[o + 2 * dw] = d[o + 2 * dw + 1] = d[o + 2 * dw + 2] = e;
                }
            }
        }
        return dst;
    }

    private static int Pow(int b, int e)
    {
        var r = 1;
        for (var i = 0; i < e; i++) r *= b;
        return r;
    }
}
