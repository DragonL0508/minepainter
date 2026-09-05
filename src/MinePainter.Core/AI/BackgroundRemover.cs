using SkiaSharp;

namespace MinePainter.Core.AI;

/// <summary>
/// AI 去背的遮罩後處理。遮罩來自 <see cref="RemoveBgClient"/>（伺服器結果的 alpha），
/// 伺服器只回預覽解析度時是低解析度放大來的、邊緣是糊的；
/// <see cref="GuidedFilter"/> 用原圖（全解析度）當引導把遮罩邊緣重新貼回真實的像素邊緣。
/// </summary>
public static class BackgroundRemover
{
    /// <summary>
    /// 最近一次去背的說明（例如「remove.bg 回傳 640×427，已用原圖精修放大」）；沒有話說時是 null。
    /// 給 UI 在完成後顯示用。
    /// </summary>
    public static string? LastNote { get; internal set; }

    /// <summary>遮罩對比 0..100：以 0.5 為中心拉開；0 = 不變、100 ≈ 硬切。</summary>
    public static void ApplyContrast(byte[] mask, int contrast)
    {
        if (contrast <= 0) return;
        var k = 1f + contrast / 100f * 15f;
        var lo = 1f / (1f + MathF.Exp(k));
        var hi = 1f / (1f + MathF.Exp(-k));
        var lut = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            var a = i / 255f;
            a = 1f / (1f + MathF.Exp(-(a - 0.5f) * k * 2f));
            a = Math.Clamp((a - lo) / (hi - lo), 0f, 1f);
            lut[i] = (byte)(a * 255f + 0.5f);
        }
        for (var i = 0; i < mask.Length; i++) mask[i] = lut[mask[i]];
    }

    /// <summary>
    /// Trimap 填實：模型的機率圖在物件內部常只有 0.6～0.9（顏色近背景、紋理），引導濾波又會把
    /// 內部紋理漏進 alpha，結果「去背後物件內部變半透明」。這裡以 model 的 0.5 門檻切二值，
    /// 往內縮 band 的核心一律 255、往外擴 band 之外一律 0，只有邊界一圈保留 soft 的半透明值
    /// （髮絲、毛邊都在這一圈裡）。
    /// </summary>
    /// <param name="edgeContrast">
    /// 邊帶內的 S 曲線強度 0..100：模型在頭髮這類區域常整片給 0.6～0.8、背景給 0.05～0.15，
    /// 直接當 alpha 就是「大片微微透明」與外圈淡暈；把它們推向 1 / 0，只有真正的過渡像素（≈0.5）留著。
    /// </param>
    public static byte[] SolidifyCore(byte[] soft, byte[] model, int w, int h, int band, int edgeContrast = 60)
    {
        var bin = new byte[model.Length];
        for (var i = 0; i < bin.Length; i++) bin[i] = model[i] >= 128 ? (byte)255 : (byte)0;
        FillSmallHoles(bin, w, h, maxArea: band * band * 16); // 身體上的小破洞（模型不確定的高光、皺褶）
        var core = Shift(bin, w, h, -band);  // 內縮：離邊界 ≥ band 的內部
        var outer = Shift(bin, w, h, band);  // 外擴：離邊界 ≥ band 的外部為 0

        var lut = new byte[256];
        for (var i = 0; i < 256; i++) lut[i] = (byte)i;
        ApplyContrast(lut, edgeContrast);

        var result = new byte[soft.Length];
        for (var i = 0; i < result.Length; i++)
            result[i] = core[i] != 0 ? (byte)255 : outer[i] == 0 ? (byte)0 : lut[soft[i]];
        return result;
    }

    /// <summary>
    /// 把二值遮罩裡「被前景包住、面積 ≤ maxArea」的背景區填成前景。
    /// 從影像邊界對背景 flood fill，碰不到的背景就是洞。
    /// </summary>
    public static void FillSmallHoles(byte[] bin, int w, int h, int maxArea)
    {
        var label = new int[w * h]; // 0 = 未訪；1 = 外部背景；2.. = 洞編號
        var stack = new Stack<int>();
        void Flood(int start, int id, out int area)
        {
            area = 0;
            stack.Push(start);
            label[start] = id;
            while (stack.Count > 0)
            {
                var i = stack.Pop();
                area++;
                var x = i % w; var y = i / w;
                if (x > 0 && bin[i - 1] == 0 && label[i - 1] == 0) { label[i - 1] = id; stack.Push(i - 1); }
                if (x < w - 1 && bin[i + 1] == 0 && label[i + 1] == 0) { label[i + 1] = id; stack.Push(i + 1); }
                if (y > 0 && bin[i - w] == 0 && label[i - w] == 0) { label[i - w] = id; stack.Push(i - w); }
                if (y < h - 1 && bin[i + w] == 0 && label[i + w] == 0) { label[i + w] = id; stack.Push(i + w); }
            }
        }
        // 外部：所有邊界上的背景像素
        for (var x = 0; x < w; x++)
        {
            if (bin[x] == 0 && label[x] == 0) Flood(x, 1, out _);
            var b = (h - 1) * w + x;
            if (bin[b] == 0 && label[b] == 0) Flood(b, 1, out _);
        }
        for (var y = 0; y < h; y++)
        {
            var l = y * w; var r = y * w + w - 1;
            if (bin[l] == 0 && label[l] == 0) Flood(l, 1, out _);
            if (bin[r] == 0 && label[r] == 0) Flood(r, 1, out _);
        }
        // 其餘背景 = 洞
        var next = 2;
        var fill = new List<int>();
        for (var i = 0; i < bin.Length; i++)
        {
            if (bin[i] != 0 || label[i] != 0) continue;
            var id = next++;
            Flood(i, id, out var area);
            if (area <= maxArea) fill.Add(id);
        }
        if (fill.Count == 0) return;
        var set = new HashSet<int>(fill);
        for (var i = 0; i < bin.Length; i++)
            if (bin[i] == 0 && set.Contains(label[i])) bin[i] = 255;
    }

    /// <summary>以方框 min/max 濾波收縮（負）或擴張（正）遮罩；回傳新陣列。</summary>
    public static byte[] Shift(byte[] mask, int w, int h, int shift)
    {
        if (shift == 0) return mask;
        var r = Math.Abs(shift);
        var dilate = shift > 0;
        var tmp = new byte[mask.Length];
        var outp = new byte[mask.Length];
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                int v = dilate ? 0 : 255;
                for (var k = -r; k <= r; k++)
                {
                    var m = mask[y * w + Math.Clamp(x + k, 0, w - 1)];
                    v = dilate ? Math.Max(v, m) : Math.Min(v, m);
                }
                tmp[y * w + x] = (byte)v;
            }
        });
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                int v = dilate ? 0 : 255;
                for (var k = -r; k <= r; k++)
                {
                    var m = tmp[Math.Clamp(y + k, 0, h - 1) * w + x];
                    v = dilate ? Math.Max(v, m) : Math.Min(v, m);
                }
                outp[y * w + x] = (byte)v;
            }
        });
        return outp;
    }
}

/// <summary>
/// 快速引導濾波（He et al., Fast Guided Filter）：以高清原圖為引導、模型遮罩為輸入，
/// 輸出 q = a·I + b —— 係數 a、b 在縮小 s 倍的網格上算（記憶體與時間都小），
/// 套用時卻是逐個全解析度像素乘上原圖顏色，所以遮罩邊緣會貼著真實像素邊緣走：
/// 髮絲、毛邊在 1024 遮罩裡糊成一團，經此可拿回原圖的細節。彩色引導（3×3 協方差）比灰階更能分開
/// 亮度相近、色相不同的前景／背景。
/// </summary>
public static class GuidedFilter
{
    /// <summary>
    /// mask（0..255）依 src（premul BGRA）精修；回傳新遮罩。
    /// radius 為全解析度半徑（px），eps 為正則化（顏色以 0..1 計；越小越貼邊緣、越大越平滑）。
    /// </summary>
    public static byte[] Refine(byte[] mask, uint[] src, int width, int height, int radius = 16, float eps = 1e-3f,
        int iterations = 2, CancellationToken ct = default)
    {
        // 單次引導濾波是線性模型的平均，糊得比半徑寬的漸層會留一點殘餘；再跑一次就拉乾淨
        for (var i = 0; i < Math.Max(1, iterations); i++)
            mask = RefineOnce(mask, src, width, height, radius, eps, ct);
        return mask;
    }

    private static byte[] RefineOnce(byte[] mask, uint[] src, int width, int height, int radius, float eps,
        CancellationToken ct)
    {
        // 子取樣倍率：讓係數網格最長邊約 1024
        var s = Math.Max(1, (int)MathF.Ceiling(Math.Max(width, height) / 1024f));
        var sw = (width + s - 1) / s;
        var sh = (height + s - 1) / s;
        var n = sw * sh;
        var r = Math.Max(1, radius / s);

        // 引導 I（直接色 0..1）與輸入 p，先縮小（區塊平均）
        var Ir = new float[n]; var Ig = new float[n]; var Ib = new float[n]; var P = new float[n];
        var cnt = new int[n];
        for (var y = 0; y < height; y++)
        {
            var sy = y / s;
            for (var x = 0; x < width; x++)
            {
                var i = sy * sw + x / s;
                Unpremul(src[y * width + x], out var rr, out var gg, out var bb);
                Ir[i] += rr; Ig[i] += gg; Ib[i] += bb;
                P[i] += mask[y * width + x] / 255f;
                cnt[i]++;
            }
        }
        for (var i = 0; i < n; i++)
        {
            var c = Math.Max(1, cnt[i]);
            Ir[i] /= c; Ig[i] /= c; Ib[i] /= c; P[i] /= c;
        }
        ct.ThrowIfCancellationRequested();

        // 各種均值
        var mIr = Box(Ir, sw, sh, r); var mIg = Box(Ig, sw, sh, r); var mIb = Box(Ib, sw, sh, r);
        var mP = Box(P, sw, sh, r);
        var mIrP = Box(Mul(Ir, P), sw, sh, r); var mIgP = Box(Mul(Ig, P), sw, sh, r); var mIbP = Box(Mul(Ib, P), sw, sh, r);
        var mRR = Box(Mul(Ir, Ir), sw, sh, r); var mRG = Box(Mul(Ir, Ig), sw, sh, r); var mRB = Box(Mul(Ir, Ib), sw, sh, r);
        var mGG = Box(Mul(Ig, Ig), sw, sh, r); var mGB = Box(Mul(Ig, Ib), sw, sh, r); var mBB = Box(Mul(Ib, Ib), sw, sh, r);
        ct.ThrowIfCancellationRequested();

        // 每個像素解 3×3：a = (Σ + eps·I)^-1 · cov(I,p)
        var ar = new float[n]; var ag = new float[n]; var ab = new float[n]; var b = new float[n];
        for (var i = 0; i < n; i++)
        {
            var cRR = mRR[i] - mIr[i] * mIr[i] + eps;
            var cRG = mRG[i] - mIr[i] * mIg[i];
            var cRB = mRB[i] - mIr[i] * mIb[i];
            var cGG = mGG[i] - mIg[i] * mIg[i] + eps;
            var cGB = mGB[i] - mIg[i] * mIb[i];
            var cBB = mBB[i] - mIb[i] * mIb[i] + eps;
            var vR = mIrP[i] - mIr[i] * mP[i];
            var vG = mIgP[i] - mIg[i] * mP[i];
            var vB = mIbP[i] - mIb[i] * mP[i];

            // 對稱矩陣反矩陣（cofactor）
            var i00 = cGG * cBB - cGB * cGB;
            var i01 = cRB * cGB - cRG * cBB;
            var i02 = cRG * cGB - cRB * cGG;
            var i11 = cRR * cBB - cRB * cRB;
            var i12 = cRB * cRG - cRR * cGB;
            var i22 = cRR * cGG - cRG * cRG;
            var det = cRR * i00 + cRG * i01 + cRB * i02;
            if (Math.Abs(det) < 1e-12f) { ar[i] = ag[i] = ab[i] = 0f; b[i] = mP[i]; continue; }
            var inv = 1f / det;
            ar[i] = (i00 * vR + i01 * vG + i02 * vB) * inv;
            ag[i] = (i01 * vR + i11 * vG + i12 * vB) * inv;
            ab[i] = (i02 * vR + i12 * vG + i22 * vB) * inv;
            b[i] = mP[i] - ar[i] * mIr[i] - ag[i] * mIg[i] - ab[i] * mIb[i];
        }
        var mAr = Box(ar, sw, sh, r); var mAg = Box(ag, sw, sh, r); var mAb = Box(ab, sw, sh, r); var mB = Box(b, sw, sh, r);
        ct.ThrowIfCancellationRequested();

        // 全解析度套用：係數雙線性上採樣，乘上原圖顏色
        var outMask = new byte[width * height];
        Parallel.For(0, height, y =>
        {
            var fy = Math.Clamp((y + 0.5f) / s - 0.5f, 0f, sh - 1);
            var y0 = (int)fy; var y1 = Math.Min(y0 + 1, sh - 1); var ty = fy - y0;
            for (var x = 0; x < width; x++)
            {
                var fx = Math.Clamp((x + 0.5f) / s - 0.5f, 0f, sw - 1);
                var x0 = (int)fx; var x1 = Math.Min(x0 + 1, sw - 1); var tx = fx - x0;
                float L(float[] g) =>
                    (g[y0 * sw + x0] * (1 - tx) + g[y0 * sw + x1] * tx) * (1 - ty) +
                    (g[y1 * sw + x0] * (1 - tx) + g[y1 * sw + x1] * tx) * ty;
                Unpremul(src[y * width + x], out var rr, out var gg, out var bb);
                var q = L(mAr) * rr + L(mAg) * gg + L(mAb) * bb + L(mB);
                outMask[y * width + x] = (byte)Math.Clamp(q * 255f + 0.5f, 0f, 255f);
            }
        });
        return outMask;
    }

    private static void Unpremul(uint p, out float r, out float g, out float b)
    {
        var a = (int)(p >> 24);
        if (a == 0) { r = g = b = 0f; return; }
        var inv = 255f / a / 255f;
        b = (p & 0xFF) * inv;
        g = ((p >> 8) & 0xFF) * inv;
        r = ((p >> 16) & 0xFF) * inv;
    }

    private static float[] Mul(float[] a, float[] b)
    {
        var o = new float[a.Length];
        for (var i = 0; i < o.Length; i++) o[i] = a[i] * b[i];
        return o;
    }

    /// <summary>方框均值（邊界以實際涵蓋的像素數正規化），分離式滑動視窗。</summary>
    internal static float[] Box(float[] src, int w, int h, int r)
    {
        var tmp = new float[src.Length];
        var outp = new float[src.Length];
        // 水平
        Parallel.For(0, h, y =>
        {
            var row = y * w;
            var sum = 0f;
            for (var x = 0; x <= Math.Min(r, w - 1); x++) sum += src[row + x];
            for (var x = 0; x < w; x++)
            {
                var lo = x - r; var hi = x + r;
                if (x > 0)
                {
                    if (hi < w) sum += src[row + hi];
                    if (lo - 1 >= 0) sum -= src[row + lo - 1];
                }
                var count = Math.Min(hi, w - 1) - Math.Max(lo, 0) + 1;
                tmp[row + x] = sum / count;
            }
        });
        // 垂直
        Parallel.For(0, w, x =>
        {
            var sum = 0f;
            for (var y = 0; y <= Math.Min(r, h - 1); y++) sum += tmp[y * w + x];
            for (var y = 0; y < h; y++)
            {
                var lo = y - r; var hi = y + r;
                if (y > 0)
                {
                    if (hi < h) sum += tmp[hi * w + x];
                    if (lo - 1 >= 0) sum -= tmp[(lo - 1) * w + x];
                }
                var count = Math.Min(hi, h - 1) - Math.Max(lo, 0) + 1;
                outp[y * w + x] = sum / count;
            }
        });
        return outp;
    }
}
