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
        return d;
    }
}

/// <summary>物件外框：在不透明內容外圍描一圈顏色（文字外框就是這個；疊多筆 = 多層外框）。</summary>
public sealed record ObjectOutlineEffect : IEffect
{
    public int Width { get; init; } = 5;     // 1..200
    public int Softness { get; init; } = 0;  // 0..100
    public SKColor Color { get; init; } = SKColors.Black;

    public string Name => "物件外框";
    public string Category => "物件";
    public int SourceMargin => Math.Min(Width, 100) + 2;

    private static readonly ParamDef[] Params =
    [
        new SliderParam("width", "寬度", 1, 200, o => ((ObjectOutlineEffect)o).Width,
            (o, v) => ((ObjectOutlineEffect)o) with { Width = (int)v }),
        new SliderParam("softness", "柔邊", 0, 100, o => ((ObjectOutlineEffect)o).Softness,
            (o, v) => ((ObjectOutlineEffect)o) with { Softness = (int)v }),
        new ColorParam("color", "顏色", o => ((ObjectOutlineEffect)o).Color,
            (o, v) => ((ObjectOutlineEffect)o) with { Color = v }) { UsePrimaryByDefault = true },
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
        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var src = ctx.SrcAt(x, y);
                var d = dist[(y + pad) * dw + (x + pad)];
                var coverage = soft <= 0.5f
                    ? Math.Clamp(width - d + 0.5f, 0f, 1f)
                    : Math.Clamp((width - d + 0.5f) / soft, 0f, 1f);
                var outline = FromColor(color, (int)(color.Alpha * coverage));
                ctx.Dst[y * ctx.Width + x] = Over(src, outline);
            }
        });
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
    public int SourceMargin => 0;
    public bool IsPositionIndependent => false; // 以內容外接框為準

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
