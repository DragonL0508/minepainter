using MinePainter.Core.Effects;
using MinePainter.Core.Layers;
using static MinePainter.Core.Effects.EffectMath;

namespace MinePainter.Core.AI;

/// <summary>
/// 去背遮罩的「硬邊切出」：模型給的是機率圖，直接當 alpha 會留下幾像素寬的半透明毛邊、
/// 邊上還帶著背景色（使用者 2026-09-06：「不應該出現半透明的像素，要確確實實把主體切出來」）。
///
/// 步驟：
/// 1. 以 0.5 切成二值，丟掉太小的碎片、補掉主體上的小洞（模型在高光、皺褶處常猶豫）。
/// 2. 輪廓用小半徑的方框模糊再重新切一次 —— 逐像素門檻切出來的邊是鋸齒的，這一步把它磨圓。
/// 3. 從（帶次像素位置的）二值輪廓算有號距離場，邊緣只留恰好一個像素寬的抗鋸齒過渡
///    （關掉抗鋸齒就是純 0／255）。所以結果裡除了那一圈以外沒有半透明像素。
/// 4. 去除色彩汙染：邊緣一圈的像素原本混著背景色（綠幕的綠、白底的白），
///    把它們的顏色換成往內最近的「乾淨」像素平均色，切出來的邊才不會有一圈異色。
/// </summary>
public static class HardEdgeCut
{
    /// <summary>邊緣往內幾格算「還可能被背景汙染」，這一圈的顏色會被內部的顏色蓋掉。</summary>
    private const float ContaminationBand = 2f;

    /// <summary>
    /// 套用。<paramref name="pixels"/> 是原圖（premul BGRA，未乘遮罩）；回傳硬切後的遮罩與
    /// 已經乘上遮罩、邊緣去汙染的像素（可以直接寫回圖層）。
    /// </summary>
    public static (byte[] Mask, uint[] Pixels) Apply(byte[] mask, uint[] pixels, int w, int h, bool antialias = true)
    {
        var n = w * h;
        var minArea = Math.Max(64, n / 5000);   // 0.02% 以下的碎片當雜訊

        // 1. 二值 + 清碎片 + 補洞
        var bin = new byte[n];
        for (var i = 0; i < n; i++) bin[i] = mask[i] >= 128 ? (byte)255 : (byte)0;
        RemoveSmallIslands(bin, w, h, minArea);
        BackgroundRemover.FillSmallHoles(bin, w, h, minArea);

        // 2. 磨圓輪廓：半徑 1 的方框模糊兩次（≈ 三角核）再切回二值，次像素位置留在覆蓋率裡
        var coverage = Smooth(bin, w, h);

        // 3. 有號距離 → 一像素寬的抗鋸齒（或純硬切）
        var distance = DistanceTransform.SignedFromCoverage(coverage, w, h);
        var result = new byte[n];
        for (var i = 0; i < n; i++)
        {
            var d = distance[i];
            result[i] = antialias
                ? (byte)Math.Clamp(MathF.Round((0.5f + d) * 255f), 0, 255)
                : d >= 0 ? (byte)255 : (byte)0;
        }

        // 4. 邊緣一圈換成內部乾淨的顏色
        var output = Decontaminate(pixels, result, distance, w, h);
        return (result, output);
    }

    /// <summary>連通的前景碎片面積 &lt; minArea 就丟掉（4 連通）。</summary>
    internal static void RemoveSmallIslands(byte[] bin, int w, int h, int minArea)
    {
        var label = new int[bin.Length];
        var stack = new Stack<int>();
        var current = 0;
        for (var start = 0; start < bin.Length; start++)
        {
            if (bin[start] == 0 || label[start] != 0) continue;
            current++;
            var members = new List<int>();
            stack.Push(start);
            label[start] = current;
            while (stack.Count > 0)
            {
                var i = stack.Pop();
                members.Add(i);
                var x = i % w;
                var y = i / w;
                if (x > 0 && bin[i - 1] != 0 && label[i - 1] == 0) { label[i - 1] = current; stack.Push(i - 1); }
                if (x < w - 1 && bin[i + 1] != 0 && label[i + 1] == 0) { label[i + 1] = current; stack.Push(i + 1); }
                if (y > 0 && bin[i - w] != 0 && label[i - w] == 0) { label[i - w] = current; stack.Push(i - w); }
                if (y < h - 1 && bin[i + w] != 0 && label[i + w] == 0) { label[i + w] = current; stack.Push(i + w); }
            }
            if (members.Count < minArea)
                foreach (var i in members) bin[i] = 0;
        }
    }

    /// <summary>半徑 1 的方框模糊兩次，再把 0.5 當邊界：輪廓變圓，覆蓋率（0..255）保留次像素位置。</summary>
    private static byte[] Smooth(byte[] bin, int w, int h)
    {
        var a = new float[bin.Length];
        for (var i = 0; i < a.Length; i++) a[i] = bin[i] / 255f;
        for (var pass = 0; pass < 2; pass++)
        {
            var tmp = new float[a.Length];
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var sum = 0f;
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                    sum += a[Math.Clamp(y + dy, 0, h - 1) * w + Math.Clamp(x + dx, 0, w - 1)];
                tmp[y * w + x] = sum / 9f;
            }
            a = tmp;
        }
        // 兩次方框模糊後直邊的過渡約 5 格寬；乘回去讓過渡回到一格內（磨圓的是角，直邊不動）
        var coverage = new byte[bin.Length];
        for (var i = 0; i < coverage.Length; i++)
            coverage[i] = (byte)Math.Clamp(MathF.Round(((a[i] - 0.5f) * 3f + 0.5f) * 255f), 0, 255);
        return coverage;
    }

    /// <summary>
    /// 邊緣一圈（離邊界 &lt; ContaminationBand 的前景像素）的顏色換成內部乾淨像素的平均色：
    /// 由內往外一圈圈擴（每一輪把「鄰居裡已經乾淨的」平均過來），最後乘上新遮罩輸出 premul。
    /// </summary>
    private static uint[] Decontaminate(uint[] pixels, byte[] mask, float[] distance, int w, int h)
    {
        var n = w * h;
        var output = new uint[n];
        var clean = new bool[n];        // 顏色可信（內部、且原本就不透明）
        var straight = new (int R, int G, int B)[n];
        var pending = new List<int>();
        for (var i = 0; i < n; i++)
        {
            if (mask[i] == 0) continue;
            Unpremul(pixels[i], out var b, out var g, out var r, out var a);
            straight[i] = (r, g, b);
            // 內部：顏色可信（原本就不透明的才拿來當種子；原本就半透明的內部像素照舊，不去動它）
            if (distance[i] >= ContaminationBand) clean[i] = a == 255;
            else pending.Add(i);
        }

        // 由內而外一輪輪擴散；先處理離內部最近的（距離大的）
        pending.Sort((x, y) => distance[y].CompareTo(distance[x]));
        var assigned = new bool[n];
        for (var round = 0; round < 8 && pending.Count > 0; round++)
        {
            var next = new List<int>();
            var newlyClean = new List<int>();
            foreach (var i in pending)
            {
                var x = i % w;
                var y = i / w;
                int sr = 0, sg = 0, sb = 0, count = 0;
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    var xx = x + dx;
                    var yy = y + dy;
                    if ((uint)xx >= (uint)w || (uint)yy >= (uint)h) continue;
                    var j = yy * w + xx;
                    if (!clean[j]) continue;
                    var c = straight[j];
                    sr += c.R; sg += c.G; sb += c.B; count++;
                }
                if (count == 0)
                {
                    next.Add(i);
                    continue;
                }
                straight[i] = (sr / count, sg / count, sb / count);
                assigned[i] = true;
                newlyClean.Add(i);
            }
            foreach (var i in newlyClean) clean[i] = true;
            pending = next;
        }

        for (var i = 0; i < n; i++)
        {
            var m = mask[i];
            if (m == 0) continue;
            var original = pixels[i];
            var a = A(original);
            if (assigned[i])
            {
                var c = straight[i];
                output[i] = Premul(c.B, c.G, c.R, a * m / 255);
            }
            else
            {
                output[i] = m == 255 ? original : LayerPixelSource.ScalePremul(original, m);
            }
        }
        return output;
    }
}
