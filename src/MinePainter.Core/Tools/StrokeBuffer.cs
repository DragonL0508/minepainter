using MinePainter.Core.Tiles;
using SkiaSharp;

namespace MinePainter.Core.Tools;

/// <summary>
/// 一劃筆劃的暫存：覆蓋度遮罩（doc 座標）+ 顏色/不透明度。
/// 筆劃期間 compositor 把它疊在目標圖層上預覽；PointerUp 才 commit 進圖層。
/// 「整劃不透明度」語意：重疊 dab 取 max 覆蓋度，不會自我加深。
/// 存取一律在 Document.SyncRoot 內。
/// </summary>
public sealed class StrokeBuffer
{
    public MaskSurface Mask { get; } = new();

    public bool IsActive { get; private set; }
    public Guid TargetLayerId { get; private set; }
    public SKColor Color { get; private set; }
    public float Opacity { get; private set; } = 1f;
    public bool IsEraser { get; private set; }

    public SKRectI DirtyBounds => Mask.Bounds;

    public void Begin(Guid targetLayerId, SKColor color, float opacity, bool isEraser)
    {
        if (IsActive) throw new InvalidOperationException("前一劃尚未結束。");
        IsActive = true;
        TargetLayerId = targetLayerId;
        Color = color;
        Opacity = Math.Clamp(opacity, 0f, 1f);
        IsEraser = isEraser;
        Mask.Clear();
    }

    public void End()
    {
        IsActive = false;
        Mask.Clear();
    }
}
