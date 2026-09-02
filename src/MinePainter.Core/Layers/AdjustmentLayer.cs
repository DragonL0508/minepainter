using MinePainter.Core.Adjustments;
using SkiaSharp;

namespace MinePainter.Core.Layers;

/// <summary>
/// 調整圖層：非破壞性地作用於「同群組內、其下方兄弟節點的合成結果」。
/// 參數（Adjustment）為不可變物件 —— 改參數 = 換參考 + InvalidateAll，undo 同構。
/// 本身的 Opacity 作為調整強度（0 = 無效果，1 = 全套用）。
/// </summary>
public sealed class AdjustmentLayer : LayerNode
{
    public IAdjustment Adjustment { get; set; }

    public AdjustmentLayer(IAdjustment adjustment)
    {
        Adjustment = adjustment;
        Name = adjustment.DisplayName;
    }

    public override SKRectI ContentBounds =>
        Document is { } doc ? new SKRectI(0, 0, doc.Width, doc.Height) : SKRectI.Empty;
}
