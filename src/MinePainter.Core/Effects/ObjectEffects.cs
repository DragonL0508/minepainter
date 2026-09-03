using SkiaSharp;
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
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            d[y * w + x] = SeedFromCoverage(A(ctx.SrcOrTransparent(x - pad, y - pad)));
        Propagate(d, w, h);
        return d;
    }

    /// <summary>
    /// 先做形態學閉運算（膨脹 r 再侵蝕 r）把小於 r 的凹縫／細洞補平，再回傳到「補平後形狀」的距離。
    /// 外框的「平滑」用這個：邊緣的小抖動不會再讓外框跟著抖。r ≤ 0 時等同 <see cref="FromAlpha"/>。
    /// </summary>
    public static float[] FromAlphaClosed(EffectContext ctx, int pad, int r)
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
        // 侵蝕：離外側 > r 的才留下 = 閉運算結果；最後回到「到閉運算形狀」的距離
        for (var i = 0; i < n; i++)
            dist[i] = SeedFromCoverage((int)MathF.Round(Math.Clamp(toOutside[i] - r + 0.5f, 0f, 1f) * 255));
        Propagate(dist, w, h);
        return dist;
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
    /// 精確歐氏距離變換（Meijster 分離式，O(w·h)）：輸入 &lt; inf 的是種子（值＝起始偏移 −0.5..0.5）、
    /// 其餘任意大；輸出每格到最近種子中心的直線距離（px）＋該種子的偏移。
    /// 偏移不參與包絡比較（最多差一格內的次優），換來邊界落在次像素位置。
    /// 外框／羽化的邊角是真正的圓弧，不像 chamfer 近似會出現八角形稜角。
    /// </summary>
    private static void Propagate(float[] d, int w, int h)
    {
        var inf = (float)(w + h + 1);
        // 第一趟：每欄的垂直距離 g（種子＝0），另帶著「最近種子的偏移」gt 一路傳下去
        var g = new float[w * h];
        var gt = new float[w * h];
        for (var x = 0; x < w; x++)
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
        }

        // 第二趟：每列取拋物線下包絡，最後把該種子的偏移加回去
        var s = new int[w];
        var t = new int[w];
        var gy = new float[w];
        for (var y = 0; y < h; y++)
        {
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
        }
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

    public string Name => "物件外框";
    public string Category => "物件";

    private int ClampedWidth => Math.Min(Width, 100);
    private int ClampedSmooth => Math.Clamp(Smooth, 0, 20);

    /// <summary>
    /// 漸層要看整個內容的外接框，所以得整層算；純色只需要外框寬度的來源餘裕。
    /// 平滑（閉運算）膨脹再侵蝕各 r，所以來源餘裕要再加 2r。
    /// </summary>
    public int SourceMargin => Gradient ? EffectContext.WholeLayer : ClampedWidth + ClampedSmooth * 2 + 2;

    /// <summary>輸出會延伸到內容外多遠（快取範圍用）：補平的凹縫最遠離原內容 r。</summary>
    public int OutputMargin => ClampedWidth + ClampedSmooth + 2;

    private static readonly ParamDef[] Params =
    [
        new SliderParam("width", "寬度", 1, 60, o => ((ObjectOutlineEffect)o).Width,
            (o, v) => ((ObjectOutlineEffect)o) with { Width = (int)v }),
        new SliderParam("softness", "柔邊", 0, 100, o => ((ObjectOutlineEffect)o).Softness,
            (o, v) => ((ObjectOutlineEffect)o) with { Softness = (int)v }),
        new SliderParam("smooth", "平滑", 0, 20, o => ((ObjectOutlineEffect)o).Smooth,
            (o, v) => ((ObjectOutlineEffect)o) with { Smooth = (int)v }),
        new ColorParam("color", "顏色", o => ((ObjectOutlineEffect)o).Color,
            (o, v) => ((ObjectOutlineEffect)o) with { Color = v }) { UsePrimaryByDefault = true },
        new BoolParam("gradient", "漸層外框", o => ((ObjectOutlineEffect)o).Gradient,
            (o, v) => ((ObjectOutlineEffect)o) with { Gradient = v }),
        new GradientParam("gradientStops", "漸層", o => ((ObjectOutlineEffect)o).GradientStops,
            (o, v) => ((ObjectOutlineEffect)o) with { GradientStops = v })
            { LegacyStartKey = "color", LegacyEndKey = "gradientEnd" },
        new AngleParam("gradientAngle", "漸層角度", 0, 360, o => ((ObjectOutlineEffect)o).GradientAngle,
            (o, v) => ((ObjectOutlineEffect)o) with { GradientAngle = (float)v }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var width = ClampedWidth;
        var smooth = ClampedSmooth;
        var pad = width + smooth * 2 + 2;
        var dist = DistanceTransform.FromAlphaClosed(ctx, pad, smooth);
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
                ramp = new GradientRamp(bbox, GradientAngle, radial: false, GradientStops);
            }
        }

        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var src = ctx.SrcAt(x, y);
                var d = dist[(y + pad) * dw + (x + pad)];
                var coverage = soft <= 0.5f
                    ? Math.Clamp(width - d + 0.5f, 0f, 1f)
                    : Math.Clamp((width - d + 0.5f) / soft, 0f, 1f);
                if (coverage <= 0f)
                {
                    ctx.Dst[y * ctx.Width + x] = src;
                    continue;
                }
                var c = ramp?.At(x, y) ?? color;
                var outline = FromColor(c, (int)(c.Alpha * coverage));
                ctx.Dst[y * ctx.Width + x] = Over(src, outline);
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

    public string Name => "物件陰影";
    public string Category => "物件";
    public int SourceMargin => Math.Max(Math.Abs(OffsetX), Math.Abs(OffsetY)) + Thickness + GaussianMargin(Blur);

    private static readonly ParamDef[] Params =
    [
        new SliderParam("ox", "位移 X", -50, 50, o => ((ObjectShadowEffect)o).OffsetX,
            (o, v) => ((ObjectShadowEffect)o) with { OffsetX = (int)v }),
        new SliderParam("oy", "位移 Y", -50, 50, o => ((ObjectShadowEffect)o).OffsetY,
            (o, v) => ((ObjectShadowEffect)o) with { OffsetY = (int)v }),
        new SliderParam("thickness", "厚度", 0, 50, o => ((ObjectShadowEffect)o).Thickness,
            (o, v) => ((ObjectShadowEffect)o) with { Thickness = (int)v }),
        new SliderParam("blur", "模糊", 0, 50, o => ((ObjectShadowEffect)o).Blur,
            (o, v) => ((ObjectShadowEffect)o) with { Blur = (int)v }),
        new SliderParam("opacity", "不透明度", 0, 100, o => ((ObjectShadowEffect)o).Opacity,
            (o, v) => ((ObjectShadowEffect)o) with { Opacity = (int)v }, "%"),
        new ColorParam("color", "顏色", o => ((ObjectShadowEffect)o).Color,
            (o, v) => ((ObjectShadowEffect)o) with { Color = v }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var shadow = ShadowMask(ctx, OffsetX, OffsetY, 0, Blur, Color, Opacity / 100f, Thickness);
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

    public string Name => "物件光暈";
    public string Category => "物件";
    public int SourceMargin => Spread + GaussianMargin(Size);

    private static readonly ParamDef[] Params =
    [
        new SliderParam("size", "大小", 1, 50, o => ((ObjectGlowEffect)o).Size,
            (o, v) => ((ObjectGlowEffect)o) with { Size = (int)v }),
        new SliderParam("spread", "擴散", 0, 30, o => ((ObjectGlowEffect)o).Spread,
            (o, v) => ((ObjectGlowEffect)o) with { Spread = (int)v }),
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

/// <summary>物件漸層：把不透明內容重新上色成多節點漸層（線性可轉角度，或放射狀）。</summary>
public sealed record ObjectGradientEffect : IEffect
{
    public GradientStops Stops { get; init; } = GradientStops.Two(SKColors.White, new SKColor(0x3A, 0x7B, 0xD5));
    public float Angle { get; init; } = 90f;
    public bool Radial { get; init; }

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

    public string Name => "物件漸層";
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
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        // 內容外接框（alpha > 0）
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
        if (maxX < 0)
        {
            ctx.CopySrcToDst();
            return;
        }

        var bw = Math.Max(1, maxX - minX + 1);
        var bh = Math.Max(1, maxY - minY + 1);
        var cx = minX + bw / 2f;
        var cy = minY + bh / 2f;
        var rad = Angle * MathF.PI / 180f;
        var dx = MathF.Cos(rad);
        var dy = MathF.Sin(rad);
        var half = Math.Abs(dx) * bw / 2f + Math.Abs(dy) * bh / 2f;
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
                var px = x + 0.5f - cx;
                var py = y + 0.5f - cy;
                float t;
                if (Radial) t = MathF.Sqrt(px * px + py * py) / Math.Max(1f, maxR);
                else t = half <= 0 ? 0.5f : (px * dx + py * dy) / (2 * half) + 0.5f;
                var c = lut[(int)(Math.Clamp(t, 0f, 1f) * 256)];
                // 漸層色的 alpha × 原 alpha
                var ca = A(c) * a / 255;
                ctx.Dst[y * ctx.Width + x] = Premul(B(c), G(c), R(c), ca);
            }
        });
    }
}

/// <summary>
/// 羽化物件（paint.net 的 Feather Object 外掛）：物件邊緣往內漸淡到透明。
/// 以「到最近透明像素的距離」為準：距離 ≥ 半徑 → 原 alpha；越靠邊越透明。
/// 用來柔化去背後的硬邊，或做出淡出的貼圖邊緣。
/// </summary>
public sealed record ObjectFeatherEffect : IEffect
{
    public int Radius { get; init; } = 10;     // 1..100
    /// <summary>強度 0..100：邊緣最外圈剩下多少 alpha（0 = 完全透明）。</summary>
    public int Strength { get; init; } = 100;
    /// <summary>畫布邊界也視為物件邊（貼齊畫布邊的物件是否也羽化）。</summary>
    public bool FeatherCanvasEdge { get; init; }

    public string Name => "羽化物件";
    public string Category => "物件";
    public int SourceMargin => Math.Min(Radius, 100) + 2;

    private static readonly ParamDef[] Params =
    [
        new SliderParam("radius", "半徑", 1, 50, o => ((ObjectFeatherEffect)o).Radius,
            (o, v) => ((ObjectFeatherEffect)o) with { Radius = (int)v }, "px"),
        new SliderParam("strength", "強度", 0, 100, o => ((ObjectFeatherEffect)o).Strength,
            (o, v) => ((ObjectFeatherEffect)o) with { Strength = (int)v }, "%"),
        new BoolParam("canvasEdge", "畫布邊緣也羽化", o => ((ObjectFeatherEffect)o).FeatherCanvasEdge,
            (o, v) => ((ObjectFeatherEffect)o) with { FeatherCanvasEdge = v }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var radius = Math.Min(Radius, 100);
        var pad = radius + 2;
        var dist = DistanceTransform.ToTransparent(ctx, pad, FeatherCanvasEdge);
        var dw = ctx.Width + pad * 2;
        var floor = 1f - Strength / 100f;

        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var src = ctx.SrcAt(x, y);
                if (A(src) == 0) { ctx.Dst[y * ctx.Width + x] = 0; continue; }
                var d = dist[(y + pad) * dw + (x + pad)];
                if (d >= radius) { ctx.Dst[y * ctx.Width + x] = src; continue; }
                // 距離 0.5（邊緣像素中心）→ 幾乎透明；smoothstep 讓過渡沒有折角
                var t = Math.Clamp((d - 0.5f) / radius, 0f, 1f);
                var s = t * t * (3f - 2f * t);
                var keep = floor + (1f - floor) * s;
                var mul = (int)(keep * 256f + 0.5f);
                ctx.Dst[y * ctx.Width + x] = mul >= 256 ? src : mul <= 0 ? 0 : ScalePremul(src, mul);
            }
        });
    }

    private static uint ScalePremul(uint p, int mul)
    {
        var b = (int)(p & 0xFF) * mul >> 8;
        var g = (int)((p >> 8) & 0xFF) * mul >> 8;
        var r = (int)((p >> 16) & 0xFF) * mul >> 8;
        var a = (int)(p >> 24) * mul >> 8;
        return (uint)b | ((uint)g << 8) | ((uint)r << 16) | ((uint)a << 24);
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

    public string Name => "物件內光暈";
    public string Category => "物件";
    public int SourceMargin => Pad;

    private int Pad => Math.Min(Size, 100) + Math.Min(Spread, 30) + 2;

    private static readonly ParamDef ModeParam =
        new ChoiceParam("mode", "模式", ["均勻", "指定角度"], o => ((InnerGlowEffect)o).Directional ? 1 : 0,
            (o, v) => ((InnerGlowEffect)o) with { Directional = v == 1 });
    private static readonly ParamDef AngleDef =
        new AngleParam("angle", "光源角度", 0, 360, o => ((InnerGlowEffect)o).Angle,
            (o, v) => ((InnerGlowEffect)o) with { Angle = (float)v });
    private static readonly ParamDef[] Common =
    [
        new SliderParam("size", "大小", 1, 50, o => ((InnerGlowEffect)o).Size,
            (o, v) => ((InnerGlowEffect)o) with { Size = (int)v }, "px"),
        new SliderParam("spread", "擴散", 0, 30, o => ((InnerGlowEffect)o).Spread,
            (o, v) => ((InnerGlowEffect)o) with { Spread = (int)v }, "px"),
        new SliderParam("opacity", "不透明度", 0, 100, o => ((InnerGlowEffect)o).Opacity,
            (o, v) => ((InnerGlowEffect)o) with { Opacity = (int)v }, "%"),
        new ColorParam("color", "顏色", o => ((InnerGlowEffect)o).Color,
            (o, v) => ((InnerGlowEffect)o) with { Color = v }),
        new BoolParam("canvasEdge", "畫布邊緣也發光", o => ((InnerGlowEffect)o).GlowCanvasEdge,
            (o, v) => ((InnerGlowEffect)o) with { GlowCanvasEdge = v }),
    ];
    private static readonly ParamDef[] UniformParams = [ModeParam, .. Common];
    private static readonly ParamDef[] DirectionalParams = [ModeParam, AngleDef, .. Common];

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
        var rad = Angle * MathF.PI / 180f;
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
