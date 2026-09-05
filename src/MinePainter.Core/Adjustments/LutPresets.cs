namespace MinePainter.Core.Adjustments;

/// <summary>
/// 內建的調色預設集：用函數算出來的 LUT（不用內嵌檔案），存檔只記索引。
/// 順序是存檔格式的一部分 —— 只能在尾端加，不能重排或刪。
/// </summary>
public static class LutPresets
{
    private const int Size = 17;

    public sealed record Preset(string Name, Func<float, float, float, (float R, float G, float B)> Map)
    {
        private Lut3D? _lut;
        public Lut3D Lut => _lut ??= Lut3D.FromFunction(Size, Map, Name);
    }

    public static readonly Preset[] All =
    [
        new("電影感（青橙）", (r, g, b) =>
        {
            var (lr, lg, lb) = Contrast(r, g, b, 1.12f);
            (lr, lg, lb) = Saturate(lr, lg, lb, 1.08f);
            // 暗部往青、亮部往橙：分離色調
            return SplitTone(lr, lg, lb, (0.0f, 0.35f, 0.45f), 0.10f, (1.0f, 0.62f, 0.25f), 0.10f);
        }),
        new("復古底片", (r, g, b) =>
        {
            var (lr, lg, lb) = Lift(r, g, b, 0.06f);
            (lr, lg, lb) = Contrast(lr, lg, lb, 0.92f);
            (lr, lg, lb) = Saturate(lr, lg, lb, 0.82f);
            return (lr * 1.04f + 0.02f, lg * 1.0f + 0.01f, lb * 0.92f);
        }),
        new("冷調", (r, g, b) =>
        {
            var (lr, lg, lb) = Contrast(r, g, b, 1.04f);
            return (lr * 0.93f, lg * 0.99f, Math.Min(1f, lb * 1.06f + 0.03f));
        }),
        new("暖調", (r, g, b) =>
        {
            var (lr, lg, lb) = Contrast(r, g, b, 1.04f);
            return (Math.Min(1f, lr * 1.06f + 0.03f), lg * 1.0f + 0.01f, lb * 0.90f);
        }),
        new("褪色", (r, g, b) =>
        {
            var (lr, lg, lb) = Lift(r, g, b, 0.12f);
            (lr, lg, lb) = Contrast(lr, lg, lb, 0.85f);
            return Saturate(lr, lg, lb, 0.88f);
        }),
        new("高對比", (r, g, b) =>
        {
            var (lr, lg, lb) = Contrast(r, g, b, 1.35f);
            return Saturate(lr, lg, lb, 1.12f);
        }),
        new("鮮豔", (r, g, b) =>
        {
            var (lr, lg, lb) = Contrast(r, g, b, 1.06f);
            return Saturate(lr, lg, lb, 1.4f);
        }),
        new("夜色", (r, g, b) =>
        {
            var (lr, lg, lb) = Contrast(r, g, b, 1.15f);
            (lr, lg, lb) = Saturate(lr, lg, lb, 0.7f);
            (lr, lg, lb) = (lr * 0.72f, lg * 0.80f, Math.Min(1f, lb * 1.0f + 0.04f));
            return SplitTone(lr, lg, lb, (0.05f, 0.10f, 0.40f), 0.12f, (0.9f, 0.9f, 1.0f), 0.04f);
        }),
        new("黑白底片", (r, g, b) =>
        {
            // 紅色濾鏡式權重：天空變深、膚色變亮，比等權重灰階更像底片
            var l = 0.45f * r + 0.45f * g + 0.10f * b;
            var (lr, _, _) = Contrast(l, l, l, 1.25f);
            return (lr, lr, lr);
        }),
    ];

    public static string[] Names => All.Select(p => p.Name).ToArray();

    // ---- 調色積木（輸入輸出都 0..1）----

    private static (float, float, float) Contrast(float r, float g, float b, float k) =>
        (Math.Clamp((r - 0.5f) * k + 0.5f, 0f, 1f), Math.Clamp((g - 0.5f) * k + 0.5f, 0f, 1f), Math.Clamp((b - 0.5f) * k + 0.5f, 0f, 1f));

    private static (float, float, float) Saturate(float r, float g, float b, float s)
    {
        var l = 0.299f * r + 0.587f * g + 0.114f * b;
        return (Math.Clamp(l + (r - l) * s, 0f, 1f), Math.Clamp(l + (g - l) * s, 0f, 1f), Math.Clamp(l + (b - l) * s, 0f, 1f));
    }

    /// <summary>抬黑（底片褪色感）：0 變成 lift、1 不動。</summary>
    private static (float, float, float) Lift(float r, float g, float b, float lift) =>
        (lift + r * (1f - lift), lift + g * (1f - lift), lift + b * (1f - lift));

    /// <summary>分離色調：依亮度把暗部往 shadow 色、亮部往 highlight 色推。</summary>
    private static (float, float, float) SplitTone(float r, float g, float b,
        (float R, float G, float B) shadow, float shadowAmount, (float R, float G, float B) highlight, float highlightAmount)
    {
        var l = 0.299f * r + 0.587f * g + 0.114f * b;
        var sw = (1f - l) * shadowAmount;
        var hw = l * highlightAmount;
        return (
            Math.Clamp(r + (shadow.R - r) * sw + (highlight.R - r) * hw, 0f, 1f),
            Math.Clamp(g + (shadow.G - g) * sw + (highlight.G - g) * hw, 0f, 1f),
            Math.Clamp(b + (shadow.B - b) * sw + (highlight.B - b) * hw, 0f, 1f));
    }
}
