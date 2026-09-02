using MinePainter.Core.Adjustments;
using SkiaSharp;

namespace MinePainter.Core.Effects;

/// <summary>
/// 把 <see cref="IAdjustment"/> 當破壞性效果套用（「調整」選單）：
/// 區域像素經 Skia 色彩濾鏡跑一遍，與調整圖層的合成結果完全一致。
/// </summary>
public sealed record AdjustmentEffect(IAdjustment Adjustment) : IEffect
{
    public string Name => Adjustment.DisplayName;
    public string Category => "調整";
    public int SourceMargin => 0;
    public IReadOnlyList<ParamDef> Parameters => Adjustment.Parameters
        .Select(Wrap)
        .ToList();

    /// <summary>參數描述操作的是內部的 IAdjustment；這裡包一層讓 With 回傳新的 AdjustmentEffect。</summary>
    private ParamDef Wrap(ParamDef def) => def switch
    {
        SliderParam s => s with
        {
            Get = o => s.Get(((AdjustmentEffect)o).Adjustment),
            With = (o, v) => new AdjustmentEffect((IAdjustment)s.With(((AdjustmentEffect)o).Adjustment, v)),
        },
        BoolParam b => b with
        {
            Get = o => b.Get(((AdjustmentEffect)o).Adjustment),
            With = (o, v) => new AdjustmentEffect((IAdjustment)b.With(((AdjustmentEffect)o).Adjustment, v)),
        },
        ChoiceParam c => c with
        {
            Get = o => c.Get(((AdjustmentEffect)o).Adjustment),
            With = (o, v) => new AdjustmentEffect((IAdjustment)c.With(((AdjustmentEffect)o).Adjustment, v)),
        },
        AngleParam an => an with
        {
            Get = o => an.Get(((AdjustmentEffect)o).Adjustment),
            With = (o, v) => new AdjustmentEffect((IAdjustment)an.With(((AdjustmentEffect)o).Adjustment, v)),
        },
        PointParam pt => pt with
        {
            Get = o => pt.Get(((AdjustmentEffect)o).Adjustment),
            With = (o, v) => new AdjustmentEffect((IAdjustment)pt.With(((AdjustmentEffect)o).Adjustment, v)),
        },
        ColorParam col => col with
        {
            Get = o => col.Get(((AdjustmentEffect)o).Adjustment),
            With = (o, v) => new AdjustmentEffect((IAdjustment)col.With(((AdjustmentEffect)o).Adjustment, v)),
        },
        CurvesParam cv => cv with
        {
            Get = o => cv.Get(((AdjustmentEffect)o).Adjustment),
            With = (o, v) => new AdjustmentEffect((IAdjustment)cv.With(((AdjustmentEffect)o).Adjustment, v)),
        },
        _ => def,
    };

    public unsafe void Render(EffectContext ctx)
    {
        if (ctx.Width <= 0 || ctx.Height <= 0) return;
        ctx.CopySrcToDst();
        ctx.Cancellation.ThrowIfCancellationRequested();

        var info = new SKImageInfo(ctx.Width, ctx.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        fixed (uint* ptr = ctx.Dst)
        {
            using var surface = SKSurface.Create(info, (IntPtr)ptr, ctx.Width * 4);
            using var snapshot = surface.Snapshot(); // 拷貝一份當來源
            using var filter = Adjustment.CreateColorFilter();
            using var paint = new SKPaint { ColorFilter = filter, BlendMode = SKBlendMode.Src };
            surface.Canvas.DrawImage(snapshot, 0, 0, paint);
            surface.Canvas.Flush();
        }
    }
}
