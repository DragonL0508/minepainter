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

/// <summary>物件外框：在不透明內容外圍描一圈顏色（文字外框就是這個；疊多筆 = 多層外框）。</summary>
public sealed record ObjectOutlineEffect : IEffect
{
    public int Width { get; init; } = 5;     // 1..60（滑桿；內部上限 100）
    public int Softness { get; init; } = 0;  // 0..100
    /// <summary>平滑半徑（px）：先把邊緣小於此尺寸的凹縫／細洞補平再描外框，內側小抖動不會帶動外框。</summary>
    public int Smooth { get; init; } = 0;    // 0..20
    public SKColor Color { get; init; } = SKColors.Black;

    /// <summary>外框用漸層上色（GradientStops 沿 GradientAngle，以「內容＋外框」的外接框為準）。</summary>
    public bool Gradient { get; init; }
    public float GradientAngle { get; init; } = 90f;

    private readonly GradientStops? _gradientStops;

    /// <summary>漸層節點；沒設定過時預設「外框色 → 白」（跟著 Color 走，改主色時漸層起點也跟著換）。</summary>
    public GradientStops GradientStops
    {
        get => _gradientStops ?? GradientStops.Two(Color, SKColors.White);
        init => _gradientStops = value;
    }

    /// <summary>相容舊欄位：漸層末節點的顏色。</summary>
    public SKColor GradientEnd
    {
        get => GradientStops.Last;
        init => GradientStops = GradientStops.WithEnd(value);
    }


    /// <summary>角度跟著物件轉（預設）：文字轉了 45°，這個方向也跟著轉；關掉＝以畫布為準。
    /// 與傾斜、漸層的同名選項是同一件事（見 <see cref="EffectContext.ContentRotation"/>）。</summary>
    public bool RelativeToObject { get; init; } = true;

    public static readonly string[] PositionNames = ["外側", "中央", "內側"];

    /// <summary>
    /// 外框畫在邊緣的哪一側（PS 筆畫的「位置」）：0 外側（預設，往外長）、1 中央（內外各一半）、2 內側（往內長，不會變胖）。
    /// 內側那一半以「到透明的距離」量，只畫在物件本身的像素上，形狀外框不會長大。
    /// </summary>
    public int Position { get; init; }

    public string Name => "外框";
    public string Category => "物件";

    private int ClampedWidth => Math.Min(Width, 100);
    private int ClampedSmooth => Math.Clamp(Smooth, 0, 20);

    /// <summary>往外長的那一部分寬度（外側＝全部、中央＝一半、內側＝0）。</summary>
    private int OuterWidth => Position switch { 1 => (ClampedWidth + 1) / 2, 2 => 0, _ => ClampedWidth };

    /// <summary>往內長的那一部分寬度。</summary>
    private int InnerWidth => Position switch { 1 => ClampedWidth / 2, 2 => ClampedWidth, _ => 0 };

    /// <summary>
    /// 漸層要看整個內容的外接框，所以得整層算；純色只需要外框寬度的來源餘裕。
    /// 平滑（閉運算）膨脹再侵蝕各 r，所以來源餘裕要再加 2r。
    /// </summary>
    public int SourceMargin => Gradient ? EffectContext.WholeLayer : ClampedWidth + ClampedSmooth * 2 + 2;


    /// <summary>輸出會延伸到內容外多遠（快取範圍用）：補平的凹縫最遠離原內容 r；內側外框不會長出去。</summary>
    public int OutputMargin => OuterWidth == 0 ? 0 : OuterWidth + ClampedSmooth + 2;

    private static readonly ParamDef[] Params =
    [
        new SliderParam("width", "寬度", 1, 60, o => ((ObjectOutlineEffect)o).Width,
            (o, v) => ((ObjectOutlineEffect)o) with { Width = (int)v }) { Geometric = true },
        new ChoiceParam("position", "位置", PositionNames, o => ((ObjectOutlineEffect)o).Position,
            (o, v) => ((ObjectOutlineEffect)o) with { Position = Math.Clamp(v, 0, 2) }),
        new SliderParam("softness", "柔邊", 0, 100, o => ((ObjectOutlineEffect)o).Softness,
            (o, v) => ((ObjectOutlineEffect)o) with { Softness = (int)v }),
        new SliderParam("smooth", "平滑", 0, 20, o => ((ObjectOutlineEffect)o).Smooth,
            (o, v) => ((ObjectOutlineEffect)o) with { Smooth = (int)v }) { Geometric = true },
        new ColorParam("color", "顏色", o => ((ObjectOutlineEffect)o).Color,
            (o, v) => ((ObjectOutlineEffect)o) with { Color = v }) { UsePrimaryByDefault = true },
        new BoolParam("gradient", "漸層外框", o => ((ObjectOutlineEffect)o).Gradient,
            (o, v) => ((ObjectOutlineEffect)o) with { Gradient = v }),
        new GradientParam("gradientStops", "漸層", o => ((ObjectOutlineEffect)o).GradientStops,
            (o, v) => ((ObjectOutlineEffect)o) with { GradientStops = v })
            { LegacyStartKey = "color", LegacyEndKey = "gradientEnd" },
        new AngleParam("gradientAngle", "漸層角度", 0, 360, o => ((ObjectOutlineEffect)o).GradientAngle,
            (o, v) => ((ObjectOutlineEffect)o) with { GradientAngle = (float)v }),
        new BoolParam("relative", "角度跟著物件轉", o => ((ObjectOutlineEffect)o).RelativeToObject,
            (o, v) => ((ObjectOutlineEffect)o) with { RelativeToObject = v }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var width = ClampedWidth;
        var outer = OuterWidth;
        var inner = InnerWidth;
        var smooth = ClampedSmooth;
        var pad = width + smooth * 2 + 2;
        var dist = outer > 0 ? DistanceTransform.FromAlphaClosed(ctx, pad, smooth, Math.Min(smooth, outer / 2)) : null;
        var distIn = inner > 0 ? DistanceTransform.ToTransparent(ctx, pad, canvasEdge: false) : null;
        var dw = ctx.Width + pad * 2;
        var soft = Math.Max(0.5f, width * Softness / 100f);
        var color = Color;

        // 漸層：以「內容外接框外擴外框寬度」為漸層框，沿角度由 Color 到 GradientEnd
        GradientRamp? ramp = null;
        if (Gradient)
        {
            var bbox = ContentBox(ctx);
            if (!bbox.IsEmpty)
            {
                bbox.Inflate(width, width);
                ramp = new GradientRamp(bbox, ctx.FollowedAngleCw(GradientAngle, RelativeToObject),
                    radial: false, GradientStops);
            }
        }

        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var src = ctx.SrcAt(x, y);
                var i = (y + pad) * dw + (x + pad);
                var result = src;
                SKColor? c = null;
                if (dist != null)
                {
                    // 外側：墊在內容底下
                    var d = dist[i];
                    var coverage = soft <= 0.5f
                        ? Math.Clamp(outer - d + 0.5f, 0f, 1f)
                        : Math.Clamp((outer - d + 0.5f) / soft, 0f, 1f);
                    if (coverage > 0f)
                    {
                        c ??= ramp?.At(x, y) ?? color;
                        result = Over(result, FromColor(c.Value, (int)(c.Value.Alpha * coverage)));
                    }
                }
                if (distIn != null && A(src) > 0)
                {
                    // 內側：離透明愈近愈滿；畫在內容上面、只畫在物件自己的像素上（乘上自己的 alpha）
                    var d = distIn[i];
                    var coverage = soft <= 0.5f
                        ? Math.Clamp(inner - d + 0.5f, 0f, 1f)
                        : Math.Clamp((inner - d + 0.5f) / soft, 0f, 1f);
                    if (coverage > 0f)
                    {
                        c ??= ramp?.At(x, y) ?? color;
                        result = Over(FromColor(c.Value, (int)(c.Value.Alpha * coverage * A(src) / 255f)), result);
                    }
                }
                ctx.Dst[y * ctx.Width + x] = result;
            }
        });
    }

    /// <summary>來源內容（alpha > 0）的外接框，目標座標。</summary>
    internal static SKRectI ContentBox(EffectContext ctx)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < ctx.Height; y++)
        for (var x = 0; x < ctx.Width; x++)
        {
            if (A(ctx.SrcAt(x, y)) == 0) continue;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }
        return maxX < 0 ? SKRectI.Empty : new SKRectI(minX, minY, maxX + 1, maxY + 1);
    }
}

/// <summary>兩色漸層取樣：給定漸層框、角度（或放射狀），回傳某像素的顏色（未預乘 SKColor）。</summary>
internal sealed class GradientRamp
{
    private readonly float _cx, _cy, _dx, _dy, _half, _maxR;
    private readonly bool _radial;
    private readonly SKColor[] _lut = new SKColor[257];

    public GradientRamp(SKRectI box, float angleDeg, bool radial, SKColor start, SKColor end)
        : this(box, angleDeg, radial, GradientStops.Two(start, end)) { }

    public GradientRamp(SKRectI box, float angleDeg, bool radial, GradientStops stops)
    {
        var bw = Math.Max(1, box.Width);
        var bh = Math.Max(1, box.Height);
        _cx = box.Left + bw / 2f;
        _cy = box.Top + bh / 2f;
        var rad = angleDeg * MathF.PI / 180f;
        _dx = MathF.Cos(rad);
        _dy = MathF.Sin(rad);
        _half = Math.Abs(_dx) * bw / 2f + Math.Abs(_dy) * bh / 2f;
        _maxR = MathF.Sqrt(bw * bw + bh * bh) / 2f;
        _radial = radial;
        for (var i = 0; i <= 256; i++) _lut[i] = stops.ColorAt(i / 256f);
    }

    public SKColor At(int x, int y)
    {
        var px = x + 0.5f - _cx;
        var py = y + 0.5f - _cy;
        float t;
        if (_radial) t = MathF.Sqrt(px * px + py * py) / Math.Max(1f, _maxR);
        else t = _half <= 0 ? 0.5f : (px * _dx + py * _dy) / (2 * _half) + 0.5f;
        return _lut[(int)(Math.Clamp(t, 0f, 1f) * 256)];
    }
}

/// <summary>
/// 物件陰影：alpha 位移＋模糊後上色，墊在內容底下。
/// <see cref="Thickness"/> &gt; 0 時陰影沿位移方向再延伸（把每一步的輪廓疊起來），
/// 看起來像有厚度的立體塊；位移設小、厚度設大就是 Minecraft 標題那種擠出感。
/// </summary>
public sealed record ObjectShadowEffect : IEffect
{
    public int OffsetX { get; init; } = 5;     // -100..100
    public int OffsetY { get; init; } = 5;
    public int Thickness { get; init; } = 0;   // 0..100（沿位移方向擠出的 px）
    public int Blur { get; init; } = 5;        // 0..50
    public int Opacity { get; init; } = 60;    // 0..100
    public SKColor Color { get; init; } = SKColors.Black;

    /// <summary>方向跟著物件轉（預設）：文字轉了 45°，陰影也甩到 45° 那一側；關掉＝以畫布為準。
    /// 與傾斜、漸層的同名選項是同一件事（見 <see cref="EffectContext.ContentRotation"/>）。</summary>
    public bool RelativeToObject { get; init; } = true;

    public string Name => "陰影";
    public string Category => "物件";

    /// <summary>
    /// 位移在單一軸上可能達到的最大值。跟著物件轉時方向會變、長度不變，
    /// 所以餘裕要用向量長度算 —— 用 max(|X|,|Y|) 的話，轉 45° 的陰影會被裁掉一角。
    /// </summary>
    private int OffsetReach => RelativeToObject
        ? (int)MathF.Ceiling(MathF.Sqrt((float)OffsetX * OffsetX + (float)OffsetY * OffsetY))
        : Math.Max(Math.Abs(OffsetX), Math.Abs(OffsetY));

    public int SourceMargin => OffsetReach + Thickness + GaussianMargin(Blur);

    private static readonly ParamDef[] Params =
    [
        new SliderParam("ox", "位移 X", -50, 50, o => ((ObjectShadowEffect)o).OffsetX,
            (o, v) => ((ObjectShadowEffect)o) with { OffsetX = (int)v }) { Geometric = true },
        new SliderParam("oy", "位移 Y", -50, 50, o => ((ObjectShadowEffect)o).OffsetY,
            (o, v) => ((ObjectShadowEffect)o) with { OffsetY = (int)v }) { Geometric = true },
        new SliderParam("thickness", "厚度", 0, 50, o => ((ObjectShadowEffect)o).Thickness,
            (o, v) => ((ObjectShadowEffect)o) with { Thickness = (int)v }) { Geometric = true },
        new SliderParam("blur", "模糊", 0, 50, o => ((ObjectShadowEffect)o).Blur,
            (o, v) => ((ObjectShadowEffect)o) with { Blur = (int)v }) { Geometric = true },
        new SliderParam("opacity", "不透明度", 0, 100, o => ((ObjectShadowEffect)o).Opacity,
            (o, v) => ((ObjectShadowEffect)o) with { Opacity = (int)v }, "%"),
        new ColorParam("color", "顏色", o => ((ObjectShadowEffect)o).Color,
            (o, v) => ((ObjectShadowEffect)o) with { Color = v }),
        new BoolParam("relative", "方向跟著物件轉", o => ((ObjectShadowEffect)o).RelativeToObject,
            (o, v) => ((ObjectShadowEffect)o) with { RelativeToObject = v }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        // 位移方向跟著物件轉：厚度是沿位移方向擠出的，所以擠出方向也一起跟著轉
        var (ox, oy) = ctx.FollowedOffset(OffsetX, OffsetY, RelativeToObject);
        var shadow = ShadowMask(ctx, (int)MathF.Round(ox), (int)MathF.Round(oy), 0, Blur,
            Color, Opacity / 100f, Thickness);
        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var s = shadow[(y + ctx.SrcOffsetY) * ctx.SrcWidth + (x + ctx.SrcOffsetX)];
                ctx.Dst[y * ctx.Width + x] = Over(ctx.SrcAt(x, y), s);
            }
        });
    }

    /// <summary>
    /// 來源 alpha → 位移、擠出（thickness：沿位移方向每 px 疊一次輪廓）、外擴（spread，方形近似）、模糊、上色（Src 大小）。
    /// </summary>
    internal static uint[] ShadowMask(EffectContext ctx, int offsetX, int offsetY, int spread, int blur, SKColor color, float opacity, int thickness = 0)
    {
        var w = ctx.SrcWidth;
        var h = ctx.SrcHeight;
        var alpha = new byte[w * h];
        foreach (var (ox, oy) in ExtrusionOffsets(offsetX, offsetY, thickness))
        {
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var sx = x - ox;
                var sy = y - oy;
                if ((uint)sx >= (uint)w || (uint)sy >= (uint)h) continue;
                var a = (byte)A(ctx.Src[sy * w + sx]);
                if (a > alpha[y * w + x]) alpha[y * w + x] = a;
            }
        }

        if (spread > 0)
        {
            // 外擴：分離的最大值濾波（水平 + 垂直）
            var tmp = new byte[w * h];
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                byte m = 0;
                for (var i = -spread; i <= spread; i++)
                {
                    var xx = x + i;
                    if ((uint)xx >= (uint)w) continue;
                    if (alpha[y * w + xx] > m) m = alpha[y * w + xx];
                }
                tmp[y * w + x] = m;
            }
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                byte m = 0;
                for (var i = -spread; i <= spread; i++)
                {
                    var yy = y + i;
                    if ((uint)yy >= (uint)h) continue;
                    if (tmp[yy * w + x] > m) m = tmp[yy * w + x];
                }
                alpha[y * w + x] = m;
            }
        }

        var result = new uint[w * h];
        for (var i = 0; i < result.Length; i++)
        {
            if (alpha[i] == 0) continue;
            result[i] = FromColor(color, (int)(alpha[i] * opacity * color.Alpha / 255f));
        }
        if (blur > 0) result = GaussianBlur(result, w, h, blur, ctx.Cancellation);
        return result;
    }

    /// <summary>
    /// 擠出用的位移清單：從 (offsetX, offsetY) 起，沿位移方向每 1px 一步、共 thickness 步（去重）。
    /// 位移為零時沿右下 45° 擠出，厚度才不會沒地方長。
    /// </summary>
    internal static IReadOnlyList<(int X, int Y)> ExtrusionOffsets(int offsetX, int offsetY, int thickness)
    {
        var list = new List<(int, int)> { (offsetX, offsetY) };
        if (thickness <= 0) return list;
        float dx = offsetX, dy = offsetY;
        var len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.5f) { dx = 1; dy = 1; len = MathF.Sqrt(2); }
        dx /= len; dy /= len;
        var seen = new HashSet<(int, int)> { (offsetX, offsetY) };
        // 步距取 1/√2 才不會在斜向走出缺口（每步至少一軸前進 <1px）
        var step = 0.7f;
        for (var t = step; t <= thickness + 1e-3f; t += step)
        {
            var o = ((int)MathF.Round(offsetX + dx * t), (int)MathF.Round(offsetY + dy * t));
            if (seen.Add(o)) list.Add(o);
        }
        return list;
    }
}

/// <summary>物件光暈：內容外圍發光（外擴＋模糊的同色暈），墊在內容底下。</summary>
public sealed record ObjectGlowEffect : IEffect
{
    public int Size { get; init; } = 12;     // 1..100（模糊半徑）
    public int Spread { get; init; } = 2;    // 0..30（先外擴幾 px）
    public int Opacity { get; init; } = 85;  // 0..100
    public SKColor Color { get; init; } = new(0xFF, 0xD3, 0x4A);


    public string Name => "光暈";
    public string Category => "物件";
    public int SourceMargin => Spread + GaussianMargin(Size);

    private static readonly ParamDef[] Params =
    [
        new SliderParam("size", "大小", 1, 50, o => ((ObjectGlowEffect)o).Size,
            (o, v) => ((ObjectGlowEffect)o) with { Size = (int)v }) { Geometric = true },
        new SliderParam("spread", "擴散", 0, 30, o => ((ObjectGlowEffect)o).Spread,
            (o, v) => ((ObjectGlowEffect)o) with { Spread = (int)v }) { Geometric = true },
        new SliderParam("opacity", "不透明度", 0, 100, o => ((ObjectGlowEffect)o).Opacity,
            (o, v) => ((ObjectGlowEffect)o) with { Opacity = (int)v }, "%"),
        new ColorParam("color", "顏色", o => ((ObjectGlowEffect)o).Color,
            (o, v) => ((ObjectGlowEffect)o) with { Color = v }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var glow = ObjectShadowEffect.ShadowMask(ctx, 0, 0, Spread, Size, Color, Opacity / 100f);
        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var g = glow[(y + ctx.SrcOffsetY) * ctx.SrcWidth + (x + ctx.SrcOffsetX)];
                // 光暈用 screen 疊在自己上會太亮；直接墊底
                ctx.Dst[y * ctx.Width + x] = Over(ctx.SrcAt(x, y), g);
            }
        });
    }
}

/// <summary>
/// 物件塗色（PS 的「顏色覆蓋」）：把物件的不透明像素整片換成單一顏色，形狀與邊緣的
/// 抗鋸齒完全保留。跟「漸層」是同一類的上色手段，只是單色 —— 想換個顏色試配色時，
/// 比去改原始像素快得多，而且是非破壞性的。
/// </summary>
public sealed record ObjectFillEffect : IEffect
{
    public SKColor Color { get; init; } = new(0xE0, 0x4B, 0x4B);

    /// <summary>0..100：塗上去的濃度（不是整層透明度，是這片顏色蓋過原色的程度）。</summary>
    public int Opacity { get; init; } = 100;


    public string Name => "塗色";
    public string Category => "物件";

    /// <summary>逐像素、不看鄰居；輸出不會長到內容外。</summary>
    public int SourceMargin => 0;

    private static readonly ParamDef[] Params =
    [
        new ColorParam("color", "顏色", o => ((ObjectFillEffect)o).Color,
            (o, v) => ((ObjectFillEffect)o) with { Color = v }),
        new SliderParam("opacity", "濃度", 0, 100, o => ((ObjectFillEffect)o).Opacity,
            (o, v) => ((ObjectFillEffect)o) with { Opacity = (int)v }, "%"),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var amount = Math.Clamp(Opacity, 0, 100) * 255 / 100;
        if (amount <= 0)
        {
            ctx.CopySrcToDst();
            return;
        }
        var fr = Color.Red;
        var fg = Color.Green;
        var fb = Color.Blue;
        var fa = Color.Alpha;

        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var src = ctx.SrcAt(x, y);
                var a = A(src);
                if (a == 0)
                {
                    ctx.Dst[y * ctx.Width + x] = 0;
                    continue;
                }
                // 塗上去的顏色也保有自己的 alpha；再乘上「濃度」與原像素的 alpha，
                // 邊緣的半透明像素才不會被塗成硬邊
                var cover = fa * amount / 255;
                Unpremul(src, out var sb, out var sg, out var sr, out _);
                var r = sr + (fr - sr) * cover / 255;
                var g = sg + (fg - sg) * cover / 255;
                var b = sb + (fb - sb) * cover / 255;
                ctx.Dst[y * ctx.Width + x] = Premul((byte)b, (byte)g, (byte)r, a);
            }
        });
    }
}

/// <summary>物件漸層：把不透明內容重新上色成多節點漸層（線性可轉角度，或放射狀）。</summary>
public sealed record ObjectGradientEffect : IEffect
{
    public GradientStops Stops { get; init; } = GradientStops.Two(SKColors.White, new SKColor(0x3A, 0x7B, 0xD5));
    public float Angle { get; init; } = 90f;
    public bool Radial { get; init; }

    /// <summary>
    /// 角度以「物件自己的方向」為準（預設）：文字轉了 45°，漸層也跟著轉 45° ——
    /// 這才叫「物件的漸層」（使用者 2026-09-04 明示）。關掉就是以畫布為準（舊行為）。
    /// 只有「整層剛好就是一個文字物件」時知道角度，其他情況兩者相同。
    /// </summary>
    public bool RelativeToObject { get; init; } = true;

    /// <summary>相容舊欄位：首節點顏色。</summary>
    public SKColor Start
    {
        get => Stops.First;
        init => Stops = Stops.WithStart(value);
    }

    /// <summary>相容舊欄位：末節點顏色。</summary>
    public SKColor End
    {
        get => Stops.Last;
        init => Stops = Stops.WithEnd(value);
    }

    public string Name => "漸層";
    public string Category => "物件";

    /// <summary>以內容外接框為準：任何一處變了整層重算，但與畫布位置無關（圖層平移不重算）。</summary>
    public int SourceMargin => EffectContext.WholeLayer;

    private static readonly ParamDef[] Params =
    [
        new GradientParam("stops", "漸層", o => ((ObjectGradientEffect)o).Stops,
            (o, v) => ((ObjectGradientEffect)o) with { Stops = v })
            { LegacyStartKey = "start", LegacyEndKey = "end" },
        new AngleParam("angle", "角度", 0, 360, o => ((ObjectGradientEffect)o).Angle,
            (o, v) => ((ObjectGradientEffect)o) with { Angle = (float)v }),
        new BoolParam("radial", "放射狀", o => ((ObjectGradientEffect)o).Radial,
            (o, v) => ((ObjectGradientEffect)o) with { Radial = v }),
        new BoolParam("relative", "角度跟著物件轉", o => ((ObjectGradientEffect)o).RelativeToObject,
            (o, v) => ((ObjectGradientEffect)o) with { RelativeToObject = v }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        // 物件自己的角度（文字的 Rotation）加進來，漸層才會跟著物件轉
        var angle = RelativeToObject ? Angle + ctx.ContentRotation : Angle;
        var rad = angle * MathF.PI / 180f;
        var dx = MathF.Cos(rad);
        var dy = MathF.Sin(rad);

        // 內容外接框（alpha > 0），同時量出內容在漸層方向上真正的頭尾。
        //
        // 頭尾不能用外接框推算（|dx|·寬 + |dy|·高 那種）：那是「外接框在這個方向上的支撐寬度」，
        // 只有方向沿著軸時才等於內容的長度。物件一轉，外接框就變大一塊，斜過去的支撐寬度
        // 遠大於內容自己的厚度 —— 漸層被拉到那個大範圍上，物件上看得到的只剩中間一小段，
        // 看起來就像「漸層不見了、只剩一個顏色」（勾了「角度跟著物件轉」再旋轉就會遇到）。
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        var minP = float.MaxValue;
        var maxP = float.MinValue;
        for (var y = 0; y < ctx.Height; y++)
        for (var x = 0; x < ctx.Width; x++)
        {
            if (A(ctx.SrcAt(x, y)) == 0) continue;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
            var p = (x + 0.5f) * dx + (y + 0.5f) * dy;
            if (p < minP) minP = p;
            if (p > maxP) maxP = p;
        }
        if (maxX < 0)
        {
            ctx.CopySrcToDst();
            return;
        }

        var bw = Math.Max(1, maxX - minX + 1);
        var bh = Math.Max(1, maxY - minY + 1);
        var cx = minX + bw / 2f;
        var cy = minY + bh / 2f;
        var span = MathF.Max(1e-3f, maxP - minP);
        var maxR = MathF.Sqrt(bw * bw + bh * bh) / 2f;
        var colors = Stops.BuildLut(257);
        var lut = new uint[257];
        for (var i = 0; i <= 256; i++)
            lut[i] = Pack(colors[i].Blue, colors[i].Green, colors[i].Red, colors[i].Alpha);

        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var src = ctx.SrcAt(x, y);
                var a = A(src);
                if (a == 0)
                {
                    ctx.Dst[y * ctx.Width + x] = 0;
                    continue;
                }
                float t;
                if (Radial)
                {
                    var px = x + 0.5f - cx;
                    var py = y + 0.5f - cy;
                    t = MathF.Sqrt(px * px + py * py) / Math.Max(1f, maxR);
                }
                else
                {
                    t = ((x + 0.5f) * dx + (y + 0.5f) * dy - minP) / span;
                }
                var c = lut[(int)(Math.Clamp(t, 0f, 1f) * 256)];
                // 漸層色的 alpha × 原 alpha
                var ca = A(c) * a / 255;
                ctx.Dst[y * ctx.Width + x] = Premul(B(c), G(c), R(c), ca);
            }
        });
    }
}

/// <summary>
/// 羽化物件，照 paint.net BoltBait Feather Object v3.0 的原始碼：整層 alpha 做半徑 <c>寬度</c> 的高斯模糊，
/// 物件內的像素保留原色、alpha 換成模糊後的 alpha。直邊的最外圍那一格剩約一半、往內 <c>寬度</c> px 回滿，
/// 過渡是模糊核的累積曲線（中段平緩、兩端收斂）；離邊緣比寬度遠的內部一格都不動，顏色一律不動。
///
/// 之前兩版都不對（使用者 2026-09-07 兩次回報）：以邊緣線為中心往外鋪＋外圈模糊會讓物件「外圍變不透明、變糊」；
/// 距離場＋smoothstep 又把最外圍啃到幾乎透明，看起來是物件被削掉一圈而不是羽化。BoltBait 的版本
/// 邊緣停在一半、曲線是模糊核，這才是使用者習慣的手感。
///
/// 與 BoltBait 刻意不同的兩點：(1) 物件外不長出模糊尾巴（他的版本外圈直接用模糊結果，物件會微微長大；
/// 使用者明示羽化只能往內啃）。(2) alpha 只降不升（取 min）：抗鋸齒邊緣那一格模糊後可能比原本更不透明，
/// 照抄會讓細邊變厚。半透明物件（整片 alpha 128）內部模糊後仍是 128，不會被誤當成邊緣。
/// 模糊核用 paint.net GaussianBlurEffect 的三角核（權重 16·(i+1)，半徑 R），以兩趟方框模糊疊出來，O(w·h)。
/// </summary>
public sealed record ObjectFeatherEffect : IEffect
{
    /// <summary>軟帶寬度（px）：從邊緣往內這麼多像素回到原本的濃度。</summary>
    public int Radius { get; init; } = 4;
    /// <summary>強度 0..100：0 = 完全不動，100 = 整條軟帶都照羽化的結果走。</summary>
    public int Strength { get; init; } = 100;
    /// <summary>畫布邊界也視為物件邊（貼齊畫布邊的物件是否也羽化）。</summary>
    public bool FeatherCanvasEdge { get; init; }


    public string Name => "羽化";
    public string Category => "物件";

    /// <summary>模糊核要看到軟帶外一點才算得準；輸出不會長出去。</summary>
    public int SourceMargin => Math.Clamp(Radius, 1, 100) + 2;
    public int OutputMargin => 0;

    private static readonly ParamDef[] Params =
    [
        new SliderParam("radius", "寬度", 1, 50, o => ((ObjectFeatherEffect)o).Radius,
            (o, v) => ((ObjectFeatherEffect)o) with { Radius = (int)v }, "px") { Geometric = true },
        new SliderParam("strength", "強度", 0, 100, o => ((ObjectFeatherEffect)o).Strength,
            (o, v) => ((ObjectFeatherEffect)o) with { Strength = (int)v }, "%"),
        new BoolParam("canvasEdge", "畫布邊緣也羽化", o => ((ObjectFeatherEffect)o).FeatherCanvasEdge,
            (o, v) => ((ObjectFeatherEffect)o) with { FeatherCanvasEdge = v }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var radius = Math.Clamp(Radius, 1, 100);
        var pad = SourceMargin;
        var w = ctx.Width + pad * 2;
        var h = ctx.Height + pad * 2;
        var strength = Math.Clamp(Strength, 0, 100) / 100f;

        var padded = DistanceTransform.PaddedSource(ctx, pad, FeatherCanvasEdge);
        var alpha = new float[padded.Length];
        for (var i = 0; i < padded.Length; i++) alpha[i] = A(padded[i]);
        // 半徑 R 的三角核 = 兩個方框模糊疊起來（k + k' = R，寬度 2R+1）；R 是奇數時第二趟多 1
        var k = radius / 2;
        var blurred = DistanceTransform.BoxBlur(DistanceTransform.BoxBlur(alpha, w, h, k), w, h, radius - k);

        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var di = (y + pad) * w + (x + pad);
                var oi = y * ctx.Width + x;
                var src = padded[di];
                var a = A(src);
                if (a == 0) { ctx.Dst[oi] = 0; continue; }
                var target = MathF.Min(a, blurred[di]);          // 只降不升
                var newA = a + (target - a) * strength;
                if (newA >= a - 0.5f) { ctx.Dst[oi] = src; continue; }
                var m = (byte)Math.Clamp(MathF.Round(newA / a * 255f), 0f, 255f);
                ctx.Dst[oi] = m == 0 ? 0 : LayerPixelSource.ScalePremul(src, m);
            }
        });
    }

}

/// <summary>
/// 物件內光暈：光從物件的邊緣往內亮，形狀不變（只染色，不動 alpha）——
/// 外光暈是把光畫在物件外面，內光暈是畫在自己身上，所以剪影一模一樣。
/// 距離＝到最近透明像素的距離：邊緣 <c>擴散</c> px 內是滿的，再往內 <c>大小</c> px 淡出。
/// 「指定角度」模式：只有朝向光源那一側的邊緣會亮——距離改成「沿光源方向走到透明為止」的長度，
/// 背光側走不到透明（或走太遠）就不亮，像打了一盞側光。
/// </summary>
public sealed record InnerGlowEffect : IEffect
{
    public int Size { get; init; } = 12;      // 1..100（往內淡出幾 px）
    public int Spread { get; init; } = 0;     // 0..30（貼著邊緣全滿的厚度）
    public int Opacity { get; init; } = 85;   // 0..100
    public SKColor Color { get; init; } = new(0xFF, 0xD3, 0x4A);

    /// <summary>true = 指定角度（只亮朝向光源的邊）；false = 均勻（四周一樣亮）。</summary>
    public bool Directional { get; init; }

    /// <summary>光源方向（度，數學慣例：0 = 右、90 = 上；與角度轉盤一致）。</summary>
    public float Angle { get; init; } = 90f;

    /// <summary>畫布邊界也算物件邊（貼齊畫布邊的物件那一側要不要也發光）。</summary>
    public bool GlowCanvasEdge { get; init; }

    /// <summary>角度跟著物件轉（預設）：文字轉了 45°，這個方向也跟著轉；關掉＝以畫布為準。
    /// 與傾斜、漸層的同名選項是同一件事（見 <see cref="EffectContext.ContentRotation"/>）。</summary>
    public bool RelativeToObject { get; init; } = true;


    public string Name => "內光暈";
    public string Category => "物件";
    public int SourceMargin => Pad;

    private int Pad => Math.Min(Size, 100) + Math.Min(Spread, 30) + 2;

    private static readonly ParamDef ModeParam =
        new ChoiceParam("mode", "模式", ["均勻", "指定角度"], o => ((InnerGlowEffect)o).Directional ? 1 : 0,
            (o, v) => ((InnerGlowEffect)o) with { Directional = v == 1 });
    private static readonly ParamDef AngleDef =
        new AngleParam("angle", "光源角度", 0, 360, o => ((InnerGlowEffect)o).Angle,
            (o, v) => ((InnerGlowEffect)o) with { Angle = (float)v });
    private static readonly ParamDef RelativeDef =
        new BoolParam("relative", "角度跟著物件轉", o => ((InnerGlowEffect)o).RelativeToObject,
            (o, v) => ((InnerGlowEffect)o) with { RelativeToObject = v });
    private static readonly ParamDef[] Common =
    [
        new SliderParam("size", "大小", 1, 50, o => ((InnerGlowEffect)o).Size,
            (o, v) => ((InnerGlowEffect)o) with { Size = (int)v }, "px") { Geometric = true },
        new SliderParam("spread", "擴散", 0, 30, o => ((InnerGlowEffect)o).Spread,
            (o, v) => ((InnerGlowEffect)o) with { Spread = (int)v }, "px") { Geometric = true },
        new SliderParam("opacity", "不透明度", 0, 100, o => ((InnerGlowEffect)o).Opacity,
            (o, v) => ((InnerGlowEffect)o) with { Opacity = (int)v }, "%"),
        new ColorParam("color", "顏色", o => ((InnerGlowEffect)o).Color,
            (o, v) => ((InnerGlowEffect)o) with { Color = v }),
        new BoolParam("canvasEdge", "畫布邊緣也發光", o => ((InnerGlowEffect)o).GlowCanvasEdge,
            (o, v) => ((InnerGlowEffect)o) with { GlowCanvasEdge = v }),
    ];
    private static readonly ParamDef[] UniformParams = [ModeParam, .. Common];
    private static readonly ParamDef[] DirectionalParams = [ModeParam, AngleDef, RelativeDef, .. Common];

    // 角度轉盤只在「指定角度」時出現（ChoiceParam 改動會讓 ParamEditor 重建）
    public IReadOnlyList<ParamDef> Parameters => Directional ? DirectionalParams : UniformParams;

    public void Render(EffectContext ctx)
    {
        var size = Math.Min(Size, 100);
        var spread = Math.Min(Spread, 30);
        var opacity = Math.Clamp(Opacity, 0, 100) / 100f;
        if (opacity <= 0f) { ctx.CopySrcToDst(); return; }

        var pad = Pad;
        var dist = Directional ? null : DistanceTransform.ToTransparent(ctx, pad, GlowCanvasEdge);
        var dw = ctx.Width + pad * 2;
        int cr = Color.Red, cg = Color.Green, cb = Color.Blue;
        var reach = spread + size;

        // 朝光源走的單位向量（螢幕座標 y 朝下，所以 sin 取負：90° = 往上找邊）
        var rad = ctx.FollowedAngleCcw(Angle, RelativeToObject) * MathF.PI / 180f;
        var ux = MathF.Cos(rad);
        var uy = -MathF.Sin(rad);

        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var src = ctx.SrcAt(x, y);
                var i = y * ctx.Width + x;
                if (A(src) == 0) { ctx.Dst[i] = src; continue; } // 物件外不畫

                var d = dist != null
                    ? dist[(y + pad) * dw + (x + pad)]
                    : DirectionalDistance(ctx, x, y, ux, uy, reach);
                if (d >= reach) { ctx.Dst[i] = src; continue; }

                // 邊緣 spread px 內全滿，再往內 size px 用 smoothstep 淡出
                var t = Math.Clamp((d - spread) / size, 0f, 1f);
                var k = 1f - t * t * (3f - 2f * t);
                var f = opacity * k;
                if (f <= 0f) { ctx.Dst[i] = src; continue; }

                // 只把顏色往光暈色推，alpha 原封不動 —— 剪影不會變胖也不會變糊
                Unpremul(src, out var b, out var g, out var r, out var a);
                ctx.Dst[i] = Premul(
                    Clamp255(b + (cb - b) * f),
                    Clamp255(g + (cg - g) * f),
                    Clamp255(r + (cr - r) * f),
                    a);
            }
        });
    }

    /// <summary>
    /// 從 (x, y) 沿 (ux, uy) 每次走 1px，走到第一個透明像素為止的距離；走了 reach 還沒碰到就回 reach。
    /// 用前後兩點的 alpha 線性內插把碰邊的位置補到次像素，斜向的邊才不會一階一階。
    /// 畫布外：GlowCanvasEdge 時算透明（貼齊畫布邊那側也會亮），否則視為走不到邊。
    /// </summary>
    private float DirectionalDistance(EffectContext ctx, int x, int y, float ux, float uy, int reach)
    {
        var prevA = 255;
        for (var k = 1; k <= reach; k++)
        {
            var sx = (int)MathF.Floor(x + 0.5f + ux * k);
            var sy = (int)MathF.Floor(y + 0.5f + uy * k);
            var dx = ctx.Region.Left + sx;
            var dy = ctx.Region.Top + sy;
            int a;
            if (dx < 0 || dy < 0 || dx >= ctx.DocSize.Width || dy >= ctx.DocSize.Height)
            {
                if (!GlowCanvasEdge) return reach;
                a = 0;
            }
            else a = A(ctx.SrcOrTransparent(sx, sy));

            if (a < 128)
            {
                // prevA ≥ 128 > a：邊界落在 k-1 與 k 之間
                var frac = prevA == a ? 0f : (prevA - 128f) / (prevA - a);
                return Math.Max(0f, k - 1 + frac);
            }
            prevA = a;
        }
        return reach;
    }
}
