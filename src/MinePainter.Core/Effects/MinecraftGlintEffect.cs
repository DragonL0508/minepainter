using SkiaSharp;
using static MinePainter.Core.Effects.EffectMath;

namespace MinePainter.Core.Effects;

/// <summary>
/// Minecraft 風格的靜態附魔光澤：兩層帶方格紋理的斜向亮帶，僅改原有像素的顏色。
/// 紋理由來源內容的相對座標決定，不依時間或亂數狀態；存檔重開及輸出不會隨機換圖。
/// </summary>
public sealed record MinecraftGlintEffect : IEffect
{
    public int Strength { get; init; } = 85;
    public float Scale { get; init; } = 96f;
    public float Angle { get; init; } = -45f;
    public int Phase { get; init; }
    public bool RelativeToObject { get; init; } = true;
    public SKColor Color { get; init; } = new(190, 80, 255);

    public string Name => "Minecraft 附魔效果";
    public string Category => "物件";
    // 快速模式輸出會將圖層 Offset 烙入像素；以內容原點定位才能維持相同亮帶。
    // 原點需要完整來源，也因此內容範圍改變時必須重算整層；輸出不延伸到透明背景。
    public int SourceMargin => EffectContext.WholeLayer;

    private static readonly ParamDef[] Params =
    [
        new SliderParam("strength", "強度", 0, 100, o => ((MinecraftGlintEffect)o).Strength,
            (o, v) => ((MinecraftGlintEffect)o) with { Strength = (int)v }, "%"),
        new SliderParam("scale", "紋理大小", 4, 512, o => ((MinecraftGlintEffect)o).Scale,
            (o, v) => ((MinecraftGlintEffect)o) with { Scale = (float)v }, "px") { Geometric = true },
        new AngleParam("angle", "方向", -180, 180, o => ((MinecraftGlintEffect)o).Angle,
            (o, v) => ((MinecraftGlintEffect)o) with { Angle = (float)v }),
        new BoolParam("relative", "角度跟著物件轉", o => ((MinecraftGlintEffect)o).RelativeToObject,
            (o, v) => ((MinecraftGlintEffect)o) with { RelativeToObject = v }),
        new SliderParam("phase", "光澤位置", 0, 100, o => ((MinecraftGlintEffect)o).Phase,
            (o, v) => ((MinecraftGlintEffect)o) with { Phase = (int)v }, "%"),
        new ColorParam("color", "光澤顏色", o => ((MinecraftGlintEffect)o).Color,
            (o, v) => ((MinecraftGlintEffect)o) with { Color = v }),
    ];

    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var strength = Math.Clamp(Strength, 0, 100) / 100f * Color.Alpha / 255f;
        if (strength <= 0)
        {
            ctx.CopySrcToDst();
            return;
        }

        // 幾何縮放可把參數縮到 UI 下限以下，因此這裡只防止零／非有限值，不夾回滑桿範圍。
        var scale = float.IsFinite(Scale) ? Math.Max(0.01f, Scale) : 96f;
        var angle = float.IsFinite(Angle) ? Angle : -45f;
        var radians = ctx.FollowedAngleCw(angle, RelativeToObject) * MathF.PI / 180f;
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        var phase = Math.Clamp(Phase, 0, 100) / 100f;
        var red = Color.Red / 255f;
        var green = Color.Green / 255f;
        var blue = Color.Blue / 255f;
        var origin = ContentOrigin(ctx);

        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var src = ctx.SrcAt(x, y);
                var alpha = A(src);
                if (alpha == 0)
                {
                    ctx.Dst[y * ctx.Width + x] = 0;
                    continue;
                }

                var px = (ctx.Region.Left + x - origin.X + 0.5f) / scale;
                var py = (ctx.Region.Top + y - origin.Y + 0.5f) / scale;
                var u = px * cos + py * sin;
                var v = -px * sin + py * cos;
                var grain = Grain((int)MathF.Floor(u * 8), (int)MathF.Floor(v * 8));
                var first = Band(u + phase + grain * 0.16f);
                var second = Band(v * 0.7f - u * 0.45f - phase + 0.37f + grain * 0.12f);
                var amount = strength * (0.18f + (first * 0.56f + second * 0.26f) * (0.65f + grain * 0.35f));

                Unpremul(src, out var b, out var g, out var r, out _);
                ctx.Dst[y * ctx.Width + x] = Premul(
                    Shine(b, blue, amount), Shine(g, green, amount), Shine(r, red, amount), alpha);
            }
        });
    }

    private static SKPointI ContentOrigin(EffectContext ctx)
    {
        var left = ctx.SrcWidth;
        var top = ctx.SrcHeight;
        for (var y = 0; y < ctx.SrcHeight; y++)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            // 只需找比目前原點更靠左的像素；第一次找到內容的那列就是 top。
            for (var x = 0; x < left; x++)
            {
                if (A(ctx.Src[y * ctx.SrcWidth + x]) == 0) continue;
                left = x;
                top = Math.Min(top, y);
                break;
            }
            if (left == 0) break;
        }
        return left < ctx.SrcWidth
            ? new SKPointI(ctx.SrcRect.Left + left, ctx.SrcRect.Top + top)
            : new SKPointI(ctx.SrcRect.Left, ctx.SrcRect.Top);
    }

    private static float Band(float value)
    {
        var t = value - MathF.Floor(value);
        var ridge = Math.Max(0f, 1f - Math.Abs(t - 0.5f) * 3.5f);
        return ridge * ridge;
    }

    private static float Grain(int x, int y)
    {
        // 固定種子的座標雜訊；不能用 Random，否則每次預覽與輸出紋理都不同。
        var hash = unchecked((uint)x * 0x9E3779B9u ^ (uint)y * 0x85EBCA6Bu ^ 0xC2B2AE35u);
        hash ^= hash >> 16;
        hash = unchecked(hash * 0x7FEB352Du);
        hash ^= hash >> 15;
        return (hash & 255) / 255f;
    }

    private static byte Shine(int original, float color, float amount)
    {
        // 染色讓淺色物件也看得出紫光，再以濾色提亮；保留明暗細節且不改 alpha。
        var tinted = original + (color * 255f - original) * amount * 0.7f;
        return (byte)Math.Clamp((int)MathF.Round(tinted + (255f - tinted) * color * amount * 0.4f), 0, 255);
    }
}
