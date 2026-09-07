using SkiaSharp;
using MinePainter.Core.Layers;
using static MinePainter.Core.Effects.EffectMath;

namespace MinePainter.Core.Effects;

/// <summary>
/// 精確歐氏距離變換（見 <see cref="Propagate"/>）；距離以「到內容邊緣」計（全不透明像素的邊緣＝像素邊界）。
///
/// **抗鋸齒種子**：Skia 畫的邊緣過渡只有一格寬 —— 邊緣像素的覆蓋率 a 就是邊緣在那一格裡的位置
/// （邊緣 ≈ 像素起點 + a）。舊版用 alpha ≥ 128 二值化把這個資訊丟掉，距離場的邊界被量化到像素格，
/// 外框／羽化／光暈全沿著鋸齒走，放大看就是毛邊；門檻換幾個、平均起來也一樣（過渡只有一格，門檻幾乎都落在同一格）。
/// 現在每個 a &gt; 0 的像素都是種子，帶「起始偏移」t = 0.5 − a（軸對齊邊緣下精確：全覆蓋的邊界像素邊緣在中心外 0.5、
/// 半覆蓋在中心、幾乎沒覆蓋在中心內 0.5），傳播結果 = 到種子中心的距離 + 該種子的 t。
/// </summary>
internal static class DistanceTransform
{
    private const float Big = 1e9f;

    /// <summary>覆蓋率 a（0..255）→ 種子起始偏移（0.5 − a）；0 = 不是種子（Big）。</summary>
    private static float SeedFromCoverage(int a) => a <= 0 ? Big : 0.5f - a / 255f;

    public static float[] FromAlpha(EffectContext ctx, int pad)
    {
        var w = ctx.Width + pad * 2;
        var h = ctx.Height + pad * 2;
        var d = new float[w * h];
        ParallelFor(0, h, y =>
        {
            for (var x = 0; x < w; x++)
                d[y * w + x] = SeedFromCoverage(A(ctx.SrcOrTransparent(x - pad, y - pad)));
        });
        Propagate(d, w, h);
        return d;
    }

    /// <summary>
    /// 先做形態學閉運算（膨脹 r 再侵蝕 r）把小於 r 的凹縫／細洞補平，再回傳到「補平後形狀」的距離。
    /// 外框的「平滑」用這個：邊緣的小抖動不會再讓外框跟著抖。r ≤ 0 時等同 <see cref="FromAlpha"/>。
    /// </summary>
    public static float[] FromAlphaClosed(EffectContext ctx, int pad, int r, int distanceBlur = -1)
    {
        var dist = FromAlpha(ctx, pad);
        if (r <= 0) return dist;
        var w = ctx.Width + pad * 2;
        var h = ctx.Height + pad * 2;
        var n = w * h;
        // 膨脹：離邊緣 ≤ r 的都算形狀；接著算「到膨脹形狀之外」的距離。
        // 邊界不二值化：以「在膨脹形狀之外的程度」當覆蓋率（一格內的線性過渡），種子偏移同 FromAlpha。
        var toOutside = new float[n];
        for (var i = 0; i < n; i++)
            toOutside[i] = SeedFromCoverage((int)MathF.Round(Math.Clamp(dist[i] - r + 0.5f, 0f, 1f) * 255));
        Propagate(toOutside, w, h);
        // 侵蝕：離外側 > r 的才留下 = 閉運算結果（一格內線性覆蓋率）
        var coverage = new float[n];
        for (var i = 0; i < n; i++)
            coverage[i] = Math.Clamp(toOutside[i] - r + 0.5f, 0f, 1f);

        // 閉運算只補凹縫，補不掉 1–2px 的「凸起」——外框外緣還是跟著顆粒抖。
        // 再做一次尺度 r 的低通：覆蓋率用半徑 r 的方框模糊，再以 (2r+1) 倍增益拉回一格寬的過渡
        //（直邊經方框模糊是寬 2r+1 的線性斜坡，乘回去就是原邊；單一像素的凸起則被平均掉、只剩 1/(2r+1) 格）。
        // 代價：比 r 還細的筆畫會被平均到消失 —— 平滑是使用者自己開的，半徑由他決定。
        var blurred = BoxBlur(BoxBlur(coverage, w, h, r), w, h, r); // 兩趟方框 ≈ 三角核，高頻壓得更乾淨；中心斜率仍是 1/(2r+1)
        var gain = 2f * r + 1f;
        for (var i = 0; i < n; i++)
        {
            var c = Math.Clamp((blurred[i] - 0.5f) * gain + 0.5f, 0f, 1f);
            dist[i] = SeedFromCoverage((int)MathF.Round(c * 255));
        }
        Propagate(dist, w, h);

        // 最後再把距離場本身低通一次（兩趟方框）：外框外緣是距離場的等值線，
        // 來源的殘餘小起伏在距離場裡是寬約 2√(2·width) 的淺凹凸，這一步把它們抹平。
        // 直邊的距離場是線性的、模糊後不變；離內容 < 2·半徑 的地方會混到內側的 0，
        // 所以半徑由呼叫端依外框寬度限制（外框效果傳 min(r, width/2)），外緣不受影響。
        var rd = distanceBlur < 0 ? r : distanceBlur;
        return rd <= 0 ? dist : BoxBlur(BoxBlur(dist, w, h, rd), w, h, rd);
    }

    /// <summary>
    /// 反向：到最近「透明像素」的距離（羽化用）。canvasEdge = 畫布外也算透明；
    /// 否則畫布外視為與邊緣像素相同（貼齊畫布邊的物件不會被羽化）。
    /// </summary>
    public static float[] ToTransparent(EffectContext ctx, int pad, bool canvasEdge)
    {
        var w = ctx.Width + pad * 2;
        var h = ctx.Height + pad * 2;
        var d = new float[w * h];
        var docLeft = ctx.Region.Left - pad;
        var docTop = ctx.Region.Top - pad;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var dx = docLeft + x;
            var dy = docTop + y;
            var outside = dx < 0 || dy < 0 || dx >= ctx.DocSize.Width || dy >= ctx.DocSize.Height;
            var p = outside
                ? (canvasEdge ? 0u : ctx.SrcAt(x - pad, y - pad))
                : ctx.SrcOrTransparent(x - pad, y - pad);
            d[y * w + x] = SeedFromCoverage(255 - A(p)); // 種子 = 透明程度
        }
        Propagate(d, w, h);
        return d;
    }

    /// <summary>
    /// 加了 pad 的來源快照（目標範圍往外各 pad 格）。畫布外依 <paramref name="canvasEdge"/>
    /// 當空白，或沿用最近的邊緣像素（貼齊畫布邊的物件不會被當成有邊）。
    /// </summary>
    public static uint[] PaddedSource(EffectContext ctx, int pad, bool canvasEdge)
    {
        var w = ctx.Width + pad * 2;
        var h = ctx.Height + pad * 2;
        var buf = new uint[w * h];
        var docLeft = ctx.Region.Left - pad;
        var docTop = ctx.Region.Top - pad;
        ParallelFor(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var dx = docLeft + x;
                var dy = docTop + y;
                var outside = dx < 0 || dy < 0 || dx >= ctx.DocSize.Width || dy >= ctx.DocSize.Height;
                buf[y * w + x] = outside
                    ? (canvasEdge ? 0u : ctx.SrcAt(x - pad, y - pad))
                    : ctx.SrcOrTransparent(x - pad, y - pad);
            }
        });
        return buf;
    }

    /// <summary>
    /// 有號距離場（px）：正 = 在物件內、負 = 在物件外，0 落在次像素精度的邊緣線上。
    /// 羽化用 —— 軟邊要以「原本的邊緣」為中心往內往外各鋪一半，物件才不會被削瘦一圈。
    ///
    /// 輸入是「覆蓋率」而不是 alpha：整片半透明的物件（alpha 128）每一格的 alpha 都 &lt; 255，
    /// 直接拿 alpha 當覆蓋率的話整個內部都會被當成邊，羽化就把整片吃掉了。呼叫端先用
    /// 「鄰近內容的平均 alpha」正規化，半透明物件的內部覆蓋率才會是滿的。
    ///
    /// <see cref="FromAlpha"/>／<see cref="ToTransparent"/> 各自只有一側是準的：每個有內容的像素
    /// 都是 FromAlpha 的種子（值夾在 −0.5..0），所以它在物件內部量不出深度；ToTransparent 反之。
    /// 取「有內容的用到空白的距離、空白的用到內容的距離取負」，兩側就都是真正的距離。
    /// </summary>
    public static float[] SignedFromCoverage(byte[] coverage, int w, int h)
    {
        var n = w * h;
        var toEmpty = new float[n];
        var toContent = new float[n];
        for (var i = 0; i < n; i++)
        {
            var c = coverage[i];
            toEmpty[i] = SeedFromCoverage(255 - c);
            toContent[i] = SeedFromCoverage(c);
        }
        Propagate(toEmpty, w, h);
        Propagate(toContent, w, h);
        for (var i = 0; i < n; i++)
            if (coverage[i] == 0) toEmpty[i] = -toContent[i];
        return toEmpty;
    }

    /// <summary>可分離的方框模糊（半徑 r，邊界取最近值），O(w·h)。羽化也拿它疊出三角核。</summary>
    internal static float[] BoxBlur(float[] src, int w, int h, int r)
    {
        var tmp = new float[w * h];
        var dst = new float[w * h];
        var inv = 1f / (2 * r + 1);
        ParallelFor(0, h, y =>  // 可分離：橫向每列獨立
        {
            var row = y * w;
            float sum = 0;
            for (var k = -r; k <= r; k++) sum += src[row + Math.Clamp(k, 0, w - 1)];
            for (var x = 0; x < w; x++)
            {
                tmp[row + x] = sum * inv;
                sum += src[row + Math.Clamp(x + r + 1, 0, w - 1)] - src[row + Math.Clamp(x - r, 0, w - 1)];
            }
        });
        ParallelFor(0, w, x => // 縱向每欄獨立
        {
            float sum = 0;
            for (var k = -r; k <= r; k++) sum += tmp[Math.Clamp(k, 0, h - 1) * w + x];
            for (var y = 0; y < h; y++)
            {
                dst[y * w + x] = sum * inv;
                sum += tmp[Math.Clamp(y + r + 1, 0, h - 1) * w + x] - tmp[Math.Clamp(y - r, 0, h - 1) * w + x];
            }
        });
        return dst;
    }

    /// <summary>
    /// 精確歐氏距離變換（Meijster 分離式，O(w·h)）：輸入 &lt; inf 的是種子（值＝起始偏移 −0.5..0.5）、
    /// 其餘任意大；輸出每格到最近種子中心的直線距離（px）＋該種子的偏移。
    /// 偏移不參與包絡比較（最多差一格內的次優），換來邊界落在次像素位置。
    /// 外框／羽化的邊角是真正的圓弧，不像 chamfer 近似會出現八角形稜角。
    /// </summary>
    /// <summary>
    /// 兩趟法（Felzenszwalb）：第一趟每欄各自算垂直距離、第二趟每列各自取拋物線下包絡。
    /// 兩趟的「每欄」與「每列」彼此獨立，所以都直接分到所有核心上跑 ——
    /// 4K 的文字外框／陰影一次要掃兩百萬個像素，單執行緒就是拖曳時那半秒的卡頓。
    /// </summary>
    private static void Propagate(float[] d, int w, int h)
    {
        var inf = (float)(w + h + 1);
        // 第一趟：每欄的垂直距離 g（種子＝0），另帶著「最近種子的偏移」gt 一路傳下去
        var g = new float[w * h];
        var gt = new float[w * h];
        ParallelFor(0, w, x =>
        {
            var isSeed = d[x] < inf;
            g[x] = isSeed ? 0 : inf;
            gt[x] = isSeed ? d[x] : 0;
            for (var y = 1; y < h; y++)
            {
                var i = y * w + x;
                if (d[i] < inf)
                {
                    g[i] = 0;
                    gt[i] = d[i];
                }
                else
                {
                    g[i] = g[i - w] + 1;
                    gt[i] = gt[i - w];
                }
            }
            for (var y = h - 2; y >= 0; y--)
            {
                var i = y * w + x;
                if (g[i + w] + 1 < g[i])
                {
                    g[i] = g[i + w] + 1;
                    gt[i] = gt[i + w];
                }
            }
        });

        // 第二趟：每列取拋物線下包絡，最後把該種子的偏移加回去（s/t/gy 是每列的暫存，各執行緒一份）
        ParallelFor(0, h, () => (S: new int[w], T: new int[w], Gy: new float[w]), (y, scratch) =>
        {
            var (s, t, gy) = scratch;
            var row = y * w;
            for (var x = 0; x < w; x++) gy[x] = g[row + x];

            float F(int x, int i) { var dx = x - i; return dx * dx + gy[i] * gy[i]; }
            int Sep(int i, int u) => (int)MathF.Floor((u * u - i * i + gy[u] * gy[u] - gy[i] * gy[i]) / (2f * (u - i)));

            var q = 0;
            s[0] = 0;
            t[0] = 0;
            for (var u = 1; u < w; u++)
            {
                while (q >= 0 && F(t[q], s[q]) > F(t[q], u)) q--;
                if (q < 0)
                {
                    q = 0;
                    s[0] = u;
                }
                else
                {
                    var wv = 1 + Sep(s[q], u);
                    if (wv < w)
                    {
                        q++;
                        s[q] = u;
                        t[q] = wv;
                    }
                }
            }
            for (var u = w - 1; u >= 0; u--)
            {
                d[row + u] = Math.Max(0f, MathF.Sqrt(F(u, s[q])) + gt[row + s[q]]);
                if (u == t[q]) q--;
            }
        });
    }

    /// <summary>小工作量就別開執行緒（開銷比省下的多）。</summary>
    private const int ParallelThreshold = 64;

    private static void ParallelFor(int from, int to, Action<int> body)
    {
        if (to - from < ParallelThreshold || Environment.ProcessorCount < 2)
        {
            for (var i = from; i < to; i++) body(i);
            return;
        }
        System.Threading.Tasks.Parallel.For(from, to, body);
    }

    private static void ParallelFor<TLocal>(int from, int to, Func<TLocal> init, Action<int, TLocal> body)
    {
        if (to - from < ParallelThreshold || Environment.ProcessorCount < 2)
        {
            var local = init();
            for (var i = from; i < to; i++) body(i, local);
            return;
        }
        System.Threading.Tasks.Parallel.For(from, to, init, (i, _, local) =>
        {
            body(i, local);
            return local;
        }, _ => { });
    }
}
