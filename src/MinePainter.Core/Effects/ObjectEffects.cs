using SkiaSharp;
using static MinePainter.Core.Effects.EffectMath;

namespace MinePainter.Core.Effects;

/// <summary>兩趟 chamfer 距離變換（3-4 權重，單位 = 1/3 px）；距離以「到最近不透明像素」計。</summary>
internal static class DistanceTransform
{
    public static float[] FromAlpha(EffectContext ctx, int pad)
    {
        var w = ctx.Width + pad * 2;
        var h = ctx.Height + pad * 2;
        var big = 1e9f;
        var d = new float[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            d[y * w + x] = A(ctx.SrcOrTransparent(x - pad, y - pad)) >= 128 ? 0f : big;
        Propagate(d, w, h);
        return d;
    }

    /// <summary>
    /// 反向：到最近「透明像素」的距離（羽化用）。canvasEdge = 畫布外也算透明；
    /// 否則畫布外視為與邊緣像素相同（貼齊畫布邊的物件不會被羽化）。
    /// </summary>
    public static float[] ToTransparent(EffectContext ctx, int pad, bool canvasEdge)
    {
        var w = ctx.Width + pad * 2;
        var h = ctx.Height + pad * 2;
        var big = 1e9f;
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
            d[y * w + x] = A(p) < 128 ? 0f : big;
        }
        Propagate(d, w, h);
        return d;
    }

    private static void Propagate(float[] d, int w, int h)
    {

        // 前向
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            ref var v = ref d[y * w + x];
            if (v == 0) continue;
            if (x > 0) v = Math.Min(v, d[y * w + x - 1] + 1f);
            if (y > 0)
            {
                v = Math.Min(v, d[(y - 1) * w + x] + 1f);
                if (x > 0) v = Math.Min(v, d[(y - 1) * w + x - 1] + 1.4142f);
                if (x < w - 1) v = Math.Min(v, d[(y - 1) * w + x + 1] + 1.4142f);
            }
        }
        // 後向
        for (var y = h - 1; y >= 0; y--)
        for (var x = w - 1; x >= 0; x--)
        {
            ref var v = ref d[y * w + x];
            if (v == 0) continue;
            if (x < w - 1) v = Math.Min(v, d[y * w + x + 1] + 1f);
            if (y < h - 1)
            {
                v = Math.Min(v, d[(y + 1) * w + x] + 1f);
                if (x < w - 1) v = Math.Min(v, d[(y + 1) * w + x + 1] + 1.4142f);
                if (x > 0) v = Math.Min(v, d[(y + 1) * w + x - 1] + 1.4142f);
            }
        }
    }
}

/// <summary>物件外框：在不透明內容外圍描一圈顏色（文字外框就是這個；疊多筆 = 多層外框）。</summary>
public sealed record ObjectOutlineEffect : IEffect
{
    public int Width { get; init; } = 5;     // 1..200
    public int Softness { get; init; } = 0;  // 0..100
    public SKColor Color { get; init; } = SKColors.Black;

    /// <summary>外框用漸層上色（Color → GradientEnd，沿 GradientAngle，以「內容＋外框」的外接框為準）。</summary>
    public bool Gradient { get; init; }
    public SKColor GradientEnd { get; init; } = SKColors.White;
    public float GradientAngle { get; init; } = 90f;

    public string Name => "物件外框";
    public string Category => "物件";

    /// <summary>漸層要看整個內容的外接框，所以得整層算；純色只需要外框寬度的來源餘裕。</summary>
    public int SourceMargin => Gradient ? EffectContext.WholeLayer : Math.Min(Width, 100) + 2;

    /// <summary>漸層模式下輸出會延伸到內容外多遠（快取範圍用）。</summary>
    public int OutputMargin => Math.Min(Width, 100) + 2;

    private static readonly ParamDef[] Params =
    [
        new SliderParam("width", "寬度", 1, 200, o => ((ObjectOutlineEffect)o).Width,
            (o, v) => ((ObjectOutlineEffect)o) with { Width = (int)v }),
        new SliderParam("softness", "柔邊", 0, 100, o => ((ObjectOutlineEffect)o).Softness,
            (o, v) => ((ObjectOutlineEffect)o) with { Softness = (int)v }),
        new ColorParam("color", "顏色", o => ((ObjectOutlineEffect)o).Color,
            (o, v) => ((ObjectOutlineEffect)o) with { Color = v }) { UsePrimaryByDefault = true },
        new BoolParam("gradient", "漸層外框", o => ((ObjectOutlineEffect)o).Gradient,
            (o, v) => ((ObjectOutlineEffect)o) with { Gradient = v }),
        new ColorParam("gradientEnd", "漸層結束色", o => ((ObjectOutlineEffect)o).GradientEnd,
            (o, v) => ((ObjectOutlineEffect)o) with { GradientEnd = v }),
        new AngleParam("gradientAngle", "漸層角度", 0, 360, o => ((ObjectOutlineEffect)o).GradientAngle,
            (o, v) => ((ObjectOutlineEffect)o) with { GradientAngle = (float)v }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var width = Math.Min(Width, 100);
        var pad = width + 2;
        var dist = DistanceTransform.FromAlpha(ctx, pad);
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
                ramp = new GradientRamp(bbox, GradientAngle, radial: false, Color, GradientEnd);
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
        for (var i = 0; i <= 256; i++)
        {
            var t = i / 256f;
            _lut[i] = new SKColor(
                (byte)(start.Red + (end.Red - start.Red) * t),
                (byte)(start.Green + (end.Green - start.Green) * t),
                (byte)(start.Blue + (end.Blue - start.Blue) * t),
                (byte)(start.Alpha + (end.Alpha - start.Alpha) * t));
        }
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

/// <summary>物件陰影：alpha 位移＋模糊後上色，墊在內容底下。</summary>
public sealed record ObjectShadowEffect : IEffect
{
    public int OffsetX { get; init; } = 5;     // -100..100
    public int OffsetY { get; init; } = 5;
    public int Blur { get; init; } = 5;        // 0..50
    public int Opacity { get; init; } = 60;    // 0..100
    public SKColor Color { get; init; } = SKColors.Black;

    public string Name => "物件陰影";
    public string Category => "物件";
    public int SourceMargin => Math.Max(Math.Abs(OffsetX), Math.Abs(OffsetY)) + GaussianMargin(Blur);

    private static readonly ParamDef[] Params =
    [
        new SliderParam("ox", "位移 X", -100, 100, o => ((ObjectShadowEffect)o).OffsetX,
            (o, v) => ((ObjectShadowEffect)o) with { OffsetX = (int)v }),
        new SliderParam("oy", "位移 Y", -100, 100, o => ((ObjectShadowEffect)o).OffsetY,
            (o, v) => ((ObjectShadowEffect)o) with { OffsetY = (int)v }),
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
        var shadow = ShadowMask(ctx, OffsetX, OffsetY, 0, Blur, Color, Opacity / 100f);
        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var s = shadow[(y + ctx.SrcOffsetY) * ctx.SrcWidth + (x + ctx.SrcOffsetX)];
                ctx.Dst[y * ctx.Width + x] = Over(ctx.SrcAt(x, y), s);
            }
        });
    }

    /// <summary>來源 alpha → 位移、外擴（spread，方形近似）、模糊、上色（Src 大小）。</summary>
    internal static uint[] ShadowMask(EffectContext ctx, int offsetX, int offsetY, int spread, int blur, SKColor color, float opacity)
    {
        var w = ctx.SrcWidth;
        var h = ctx.SrcHeight;
        var alpha = new byte[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var sx = x - offsetX;
            var sy = y - offsetY;
            if ((uint)sx >= (uint)w || (uint)sy >= (uint)h) continue;
            alpha[y * w + x] = (byte)A(ctx.Src[sy * w + sx]);
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
        new SliderParam("size", "大小", 1, 100, o => ((ObjectGlowEffect)o).Size,
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

/// <summary>物件漸層：把不透明內容重新上色成兩色漸層（線性可轉角度，或放射狀）。</summary>
public sealed record ObjectGradientEffect : IEffect
{
    public SKColor Start { get; init; } = SKColors.White;
    public SKColor End { get; init; } = new(0x3A, 0x7B, 0xD5);
    public float Angle { get; init; } = 90f;
    public bool Radial { get; init; }

    public string Name => "物件漸層";
    public string Category => "物件";

    /// <summary>以內容外接框為準：任何一處變了整層重算，但與畫布位置無關（圖層平移不重算）。</summary>
    public int SourceMargin => EffectContext.WholeLayer;

    private static readonly ParamDef[] Params =
    [
        new ColorParam("start", "起始色", o => ((ObjectGradientEffect)o).Start,
            (o, v) => ((ObjectGradientEffect)o) with { Start = v }),
        new ColorParam("end", "結束色", o => ((ObjectGradientEffect)o).End,
            (o, v) => ((ObjectGradientEffect)o) with { End = v }),
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
        var lut = new uint[257];
        for (var i = 0; i <= 256; i++)
        {
            var t = i / 256f;
            lut[i] = Pack(
                (int)(Start.Blue + (End.Blue - Start.Blue) * t),
                (int)(Start.Green + (End.Green - Start.Green) * t),
                (int)(Start.Red + (End.Red - Start.Red) * t),
                (int)(Start.Alpha + (End.Alpha - Start.Alpha) * t));
        }

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
        new SliderParam("radius", "半徑", 1, 100, o => ((ObjectFeatherEffect)o).Radius,
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
/// 顏色透明化：把指定顏色變成透明。兩種模式 ——
///
/// 　「漸進（抽離這個顏色）」（預設，GIMP Color to Alpha 的作法）：每個像素都問
/// 　「要多少不透明度，才能用這個顏色當底把它疊出來」。白→黑漸層指定黑色時，
/// 　黑的地方全透明、中間灰是半透明、白的地方原封不動 —— 漸層會變成「白色的濃淡」，
/// 　而不是只有純黑那一小段被挖掉。
///
/// 　「門檻（只清掉相近色）」：與指定顏色的距離在容許度內＝全透明，容許度到容許度＋柔邊
/// 　之間依距離漸進，之外原樣保留。純色背景去背用這個最乾脆。
/// </summary>
public sealed record ColorToAlphaEffect : IEffect
{
    public SKColor Color { get; init; } = SKColors.White;

    /// <summary>0＝漸進抽離；1＝門檻。</summary>
    public int Mode { get; init; }

    /// <summary>漸進模式的強度（%）：100＝完全抽離，0＝原樣。</summary>
    public int Strength { get; init; } = 100;

    public int Tolerance { get; init; } = 30;  // 0..255（門檻模式）
    public int Softness { get; init; } = 20;   // 0..255（門檻模式）
    public bool Invert { get; init; }          // 門檻模式：反過來，只留這個顏色

    public string Name => "顏色透明化";
    public string Category => "物件";
    public int SourceMargin => 0;

    private static readonly ParamDef ColorDef =
        new ColorParam("color", "顏色", o => ((ColorToAlphaEffect)o).Color,
            (o, v) => ((ColorToAlphaEffect)o) with { Color = v }) { UsePrimaryByDefault = true };

    private static readonly ParamDef ModeDef =
        new ChoiceParam("mode", "模式", ["漸進（抽離這個顏色）", "門檻（只清掉相近色）"],
            o => ((ColorToAlphaEffect)o).Mode, (o, v) => ((ColorToAlphaEffect)o) with { Mode = v });

    private static readonly ParamDef[] GradualParams =
    [
        ColorDef,
        ModeDef,
        new SliderParam("strength", "強度", 0, 100, o => ((ColorToAlphaEffect)o).Strength,
            (o, v) => ((ColorToAlphaEffect)o) with { Strength = (int)v }, "%"),
    ];

    private static readonly ParamDef[] ThresholdParams =
    [
        ColorDef,
        ModeDef,
        new SliderParam("tolerance", "容許度", 0, 255, o => ((ColorToAlphaEffect)o).Tolerance,
            (o, v) => ((ColorToAlphaEffect)o) with { Tolerance = (int)v }),
        new SliderParam("softness", "柔邊", 0, 255, o => ((ColorToAlphaEffect)o).Softness,
            (o, v) => ((ColorToAlphaEffect)o) with { Softness = (int)v }),
        new BoolParam("invert", "反轉（只保留這個顏色）", o => ((ColorToAlphaEffect)o).Invert,
            (o, v) => ((ColorToAlphaEffect)o) with { Invert = v }),
    ];

    /// <summary>參數隨模式換一組（用不到的滑桿不該還留在對話框裡）。</summary>
    public IReadOnlyList<ParamDef> Parameters => Mode == 1 ? ThresholdParams : GradualParams;

    public void Render(EffectContext ctx)
    {
        if (Mode == 1) RenderThreshold(ctx);
        else RenderGradual(ctx);
    }

    // ---- 漸進：把指定顏色從每個像素裡「抽掉」 ----

    private void RenderGradual(EffectContext ctx)
    {
        int cr = Color.Red, cg = Color.Green, cb = Color.Blue;
        var strength = Math.Clamp(Strength, 0, 100) / 100f;
        if (strength <= 0f) { ctx.CopySrcToDst(); return; }

        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var p = ctx.SrcAt(x, y);
                var i = y * ctx.Width + x;
                if (A(p) == 0) { ctx.Dst[i] = p; continue; }

                Unpremul(p, out var b, out var g, out var r, out var a);

                // 每個通道要多少不透明度才蓋得出這個值；取最大的那一個
                var k = MathF.Max(Need(r, cr), MathF.Max(Need(g, cg), Need(b, cb)));
                k = 1f - strength * (1f - k); // 強度：往「原樣」那邊拉回來
                if (k <= 0f) { ctx.Dst[i] = 0; continue; }
                if (k >= 1f) { ctx.Dst[i] = p; continue; }

                // 抽掉底色之後剩下的顏色（疊回指定顏色上要能還原成原本的樣子）
                var nr = Clamp255(cr + (r - cr) / k);
                var ng = Clamp255(cg + (g - cg) / k);
                var nb = Clamp255(cb + (b - cb) / k);
                ctx.Dst[i] = Premul(nb, ng, nr, Clamp255(a * k));
            }
        });
    }

    /// <summary>單一通道需要的不透明度：與目標色差愈大，需要愈不透明才蓋得住。</summary>
    private static float Need(int value, int target)
    {
        if (value > target) return target >= 255 ? 0f : (value - target) / (255f - target);
        if (value < target) return target <= 0 ? 0f : (target - value) / target;
        return 0f;
    }

    // ---- 門檻：只清掉「夠像」的顏色 ----

    private void RenderThreshold(EffectContext ctx)
    {
        var tol = Math.Clamp(Tolerance, 0, 255);
        var soft = Math.Clamp(Softness, 0, 255);
        int cr = Color.Red, cg = Color.Green, cb = Color.Blue;
        var invert = Invert;

        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var p = ctx.SrcAt(x, y);
                var i = y * ctx.Width + x;
                if (A(p) == 0) { ctx.Dst[i] = p; continue; }

                Unpremul(p, out var b, out var g, out var r, out var a);
                var d = Math.Max(Math.Abs(r - cr), Math.Max(Math.Abs(g - cg), Math.Abs(b - cb)));

                // keep = 這個像素保留多少不透明度
                float keep;
                if (d <= tol) keep = 0f;
                else if (soft <= 0 || d >= tol + soft) keep = 1f;
                else keep = (d - tol) / (float)soft;
                if (invert) keep = 1f - keep;

                var na = Clamp255(a * keep);
                ctx.Dst[i] = na <= 0 ? 0u : na >= a ? p : Premul(b, g, r, na);
            }
        });
    }
}
