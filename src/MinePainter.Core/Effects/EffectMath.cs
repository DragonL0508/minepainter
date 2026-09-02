using System.Runtime.CompilerServices;

namespace MinePainter.Core.Effects;

/// <summary>效果共用的像素數學（premul BGRA uint）、模糊、雜訊、亂數。</summary>
public static class EffectMath
{
    // ---- 像素打包 ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int B(uint p) => (int)(p & 0xFF);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int G(uint p) => (int)((p >> 8) & 0xFF);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int R(uint p) => (int)((p >> 16) & 0xFF);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int A(uint p) => (int)(p >> 24);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Pack(int b, int g, int r, int a) =>
        (uint)(Clamp255(b) | (Clamp255(g) << 8) | (Clamp255(r) << 16) | (Clamp255(a) << 24));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Clamp255(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Clamp255(float v) => v < 0 ? 0 : v > 255 ? 255 : (int)(v + 0.5f);

    /// <summary>premul → straight（alpha 0 回傳 0）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Unpremul(uint p, out int b, out int g, out int r, out int a)
    {
        a = A(p);
        if (a == 0)
        {
            b = g = r = 0;
            return;
        }
        if (a == 255)
        {
            b = B(p);
            g = G(p);
            r = R(p);
            return;
        }
        b = Math.Min(255, (B(p) * 255 + a / 2) / a);
        g = Math.Min(255, (G(p) * 255 + a / 2) / a);
        r = Math.Min(255, (R(p) * 255 + a / 2) / a);
    }

    /// <summary>straight → premul。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Premul(int b, int g, int r, int a)
    {
        a = Clamp255(a);
        if (a == 0) return 0;
        if (a == 255) return Pack(b, g, r, 255);
        return Pack((Clamp255(b) * a + 127) / 255, (Clamp255(g) * a + 127) / 255, (Clamp255(r) * a + 127) / 255, a);
    }

    /// <summary>亮度（straight 色，Rec.601）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Intensity(int b, int g, int r) => (7471 * b + 38470 * g + 19595 * r) >> 16;

    /// <summary>premul 像素的亮度（先反 premul）。</summary>
    public static int Intensity(uint p)
    {
        Unpremul(p, out var b, out var g, out var r, out _);
        return Intensity(b, g, r);
    }

    /// <summary>premul 線性內插（所有通道一起，premul 下正確）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Lerp(uint a, uint b, float t)
    {
        if (t <= 0f) return a;
        if (t >= 1f) return b;
        var ti = (int)(t * 256f + 0.5f);
        return Lerp256(a, b, ti);
    }

    /// <summary>t 為 0..256。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Lerp256(uint a, uint b, int t)
    {
        if (t <= 0) return a;
        if (t >= 256) return b;
        var ab = B(a) + (((B(b) - B(a)) * t) >> 8);
        var ag = G(a) + (((G(b) - G(a)) * t) >> 8);
        var ar = R(a) + (((R(b) - R(a)) * t) >> 8);
        var aa = A(a) + (((A(b) - A(a)) * t) >> 8);
        return Pack(ab, ag, ar, aa);
    }

    /// <summary>SrcOver（premul）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Over(uint src, uint dst)
    {
        var sa = A(src);
        if (sa == 255) return src;
        if (sa == 0) return dst;
        var inv = 255 - sa;
        return Pack(
            B(src) + (B(dst) * inv + 127) / 255,
            G(src) + (G(dst) * inv + 127) / 255,
            R(src) + (R(dst) * inv + 127) / 255,
            sa + (A(dst) * inv + 127) / 255);
    }

    /// <summary>以 straight 色 + alpha 建 premul 像素。</summary>
    public static uint FromColor(SkiaSharp.SKColor c, int alpha = -1) =>
        Premul(c.Blue, c.Green, c.Red, alpha < 0 ? c.Alpha : alpha);

    // ---- 模糊 ----

    /// <summary>
    /// 水平＋垂直盒狀模糊（premul，四通道一起平均），半徑 r → 視窗 2r+1；邊緣以 clamp 取樣。
    /// O(n)：滑動視窗。
    /// </summary>
    public static uint[] BoxBlur(uint[] src, int w, int h, int radius, CancellationToken ct = default)
    {
        if (radius <= 0) return (uint[])src.Clone();
        var tmp = new uint[w * h];
        var dst = new uint[w * h];
        BoxBlurH(src, tmp, w, h, radius, ct);
        BoxBlurV(tmp, dst, w, h, radius, ct);
        return dst;
    }

    private static void BoxBlurH(uint[] src, uint[] dst, int w, int h, int r, CancellationToken ct)
    {
        var options = new ParallelOptions { CancellationToken = ct };
        var div = 2 * r + 1;
        Parallel.For(0, h, options, y =>
        {
            var row = y * w;
            long sb = 0, sg = 0, sr = 0, sa = 0;
            for (var i = -r; i <= r; i++)
            {
                var p = src[row + Math.Clamp(i, 0, w - 1)];
                sb += B(p); sg += G(p); sr += R(p); sa += A(p);
            }
            for (var x = 0; x < w; x++)
            {
                dst[row + x] = Pack((int)((sb + div / 2) / div), (int)((sg + div / 2) / div),
                    (int)((sr + div / 2) / div), (int)((sa + div / 2) / div));
                var pOut = src[row + Math.Clamp(x - r, 0, w - 1)];
                var pIn = src[row + Math.Clamp(x + r + 1, 0, w - 1)];
                sb += B(pIn) - B(pOut); sg += G(pIn) - G(pOut); sr += R(pIn) - R(pOut); sa += A(pIn) - A(pOut);
            }
        });
    }

    private static void BoxBlurV(uint[] src, uint[] dst, int w, int h, int r, CancellationToken ct)
    {
        var options = new ParallelOptions { CancellationToken = ct };
        var div = 2 * r + 1;
        Parallel.For(0, w, options, x =>
        {
            long sb = 0, sg = 0, sr = 0, sa = 0;
            for (var i = -r; i <= r; i++)
            {
                var p = src[Math.Clamp(i, 0, h - 1) * w + x];
                sb += B(p); sg += G(p); sr += R(p); sa += A(p);
            }
            for (var y = 0; y < h; y++)
            {
                dst[y * w + x] = Pack((int)((sb + div / 2) / div), (int)((sg + div / 2) / div),
                    (int)((sr + div / 2) / div), (int)((sa + div / 2) / div));
                var pOut = src[Math.Clamp(y - r, 0, h - 1) * w + x];
                var pIn = src[Math.Clamp(y + r + 1, 0, h - 1) * w + x];
                sb += B(pIn) - B(pOut); sg += G(pIn) - G(pOut); sr += R(pIn) - R(pOut); sa += A(pIn) - A(pOut);
            }
        });
    }

    /// <summary>高斯模糊近似：三次盒狀模糊（Kutskir 的 boxes-for-gauss）。radius ≈ 2σ。</summary>
    public static uint[] GaussianBlur(uint[] src, int w, int h, float radius, CancellationToken ct = default)
    {
        if (radius <= 0.25f) return (uint[])src.Clone();
        var sigma = radius / 2f;
        var boxes = BoxesForGauss(sigma, 3);
        var cur = src;
        foreach (var box in boxes)
        {
            var r = (box - 1) / 2;
            if (r <= 0) continue;
            cur = BoxBlur(cur, w, h, r, ct);
        }
        return ReferenceEquals(cur, src) ? (uint[])src.Clone() : cur;
    }

    private static int[] BoxesForGauss(float sigma, int n)
    {
        var wIdeal = Math.Sqrt(12 * sigma * sigma / n + 1);
        var wl = (int)Math.Floor(wIdeal);
        if (wl % 2 == 0) wl--;
        var wu = wl + 2;
        var mIdeal = (12 * sigma * sigma - n * wl * wl - 4 * n * wl - 3 * n) / (-4.0 * wl - 4);
        var m = (int)Math.Round(mIdeal);
        var sizes = new int[n];
        for (var i = 0; i < n; i++) sizes[i] = i < m ? wl : wu;
        return sizes;
    }

    /// <summary>來源 margin 建議：高斯模糊需要約 3σ ≈ 1.5 × radius 的外圍。</summary>
    public static int GaussianMargin(float radius) => (int)MathF.Ceiling(radius * 1.5f) + 2;

    // ---- 亂數與雜訊 ----

    /// <summary>可重現的 xorshift 亂數（每列可各自建一個，平行安全）。</summary>
    public struct XorShift
    {
        private uint _state;

        public XorShift(uint seed)
        {
            _state = seed == 0 ? 0x9E3779B9u : seed;
            Next();
            Next();
        }

        public uint Next()
        {
            var x = _state;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            _state = x;
            return x;
        }

        /// <summary>0..1（不含 1）。</summary>
        public float NextFloat() => (Next() & 0xFFFFFF) / 16777216f;

        /// <summary>-1..1。</summary>
        public float NextSigned() => NextFloat() * 2f - 1f;

        /// <summary>近似常態（Box–Muller 的簡化：三個均勻和）。</summary>
        public float NextGaussian() => (NextFloat() + NextFloat() + NextFloat() - 1.5f) * 2f;
    }

    /// <summary>雜湊型 2D 亂數（決定性，任何座標都能直接取，用於 Perlin 梯度）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Hash(int x, int y, uint seed)
    {
        var h = (uint)x * 0x8DA6B343u ^ (uint)y * 0xD8163841u ^ seed * 0xCB1AB31Fu;
        h ^= h >> 15;
        h *= 0x2C1B3C6Du;
        h ^= h >> 12;
        h *= 0x297A2D39u;
        h ^= h >> 15;
        return h;
    }

    /// <summary>Perlin 梯度雜訊，回傳約 -1..1。</summary>
    public static float Perlin(float x, float y, uint seed)
    {
        var x0 = (int)MathF.Floor(x);
        var y0 = (int)MathF.Floor(y);
        var fx = x - x0;
        var fy = y - y0;
        var u = fx * fx * fx * (fx * (fx * 6 - 15) + 10);
        var v = fy * fy * fy * (fy * (fy * 6 - 15) + 10);

        var n00 = Grad(x0, y0, fx, fy, seed);
        var n10 = Grad(x0 + 1, y0, fx - 1, fy, seed);
        var n01 = Grad(x0, y0 + 1, fx, fy - 1, seed);
        var n11 = Grad(x0 + 1, y0 + 1, fx - 1, fy - 1, seed);
        var nx0 = n00 + (n10 - n00) * u;
        var nx1 = n01 + (n11 - n01) * u;
        return (nx0 + (nx1 - nx0) * v) * 1.414f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Grad(int ix, int iy, float dx, float dy, uint seed)
    {
        var h = Hash(ix, iy, seed);
        var angle = (h & 0xFFFF) * (MathF.Tau / 65536f);
        return MathF.Cos(angle) * dx + MathF.Sin(angle) * dy;
    }

    /// <summary>分形雜訊（多個八度疊加），回傳約 -1..1。</summary>
    public static float Fbm(float x, float y, int octaves, float roughness, uint seed)
    {
        var sum = 0f;
        var amp = 1f;
        var norm = 0f;
        var freq = 1f;
        for (var i = 0; i < octaves; i++)
        {
            sum += Perlin(x * freq, y * freq, seed + (uint)i * 131u) * amp;
            norm += amp;
            amp *= roughness;
            freq *= 2f;
        }
        return norm > 0 ? sum / norm : 0f;
    }
}
