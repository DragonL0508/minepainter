using SkiaSharp;
using MinePainter.Core.Layers;
using static MinePainter.Core.Effects.EffectMath;

namespace MinePainter.Core.Effects;

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
