using SkiaSharp;

namespace MinePainter.Core.Tools;

/// <summary>目前吸附中的導線（doc 座標；每軸最多一條）。render thread 直接讀。</summary>
public sealed record SnapGuides(float? X, float? Y);

/// <summary>
/// 對齊模式（按住 Tab）：移動「有把手的框」時，把框的左/中/右（上/中/下）
/// 吸到畫布的四邊與兩條中線。只調整位移量，各種移動路徑（浮動內容、變形框、
/// 圖層平移、文字物件）都套同一條規則。
/// </summary>
public static class CanvasSnap
{
    /// <summary>
    /// 依對齊模式調整位移。<paramref name="startRect"/> 是拖曳起始時的框（doc 座標），
    /// dx/dy 是呼叫端已算好的原始位移。未開啟對齊模式時原樣返回並清掉導線。
    /// <paramref name="wholePixels"/>：像素內容（浮動/圖層）位移必須是整數，
    /// 吸附量取整；向量物件（文字）可精確貼齊。
    /// </summary>
    public static (float Dx, float Dy) Adjust(
        EditorSession session, SKRect startRect, float dx, float dy, bool wholePixels = true)
    {
        if (!session.SnapToCanvas || startRect.IsEmpty)
        {
            session.SnapGuides = null;
            return (dx, dy);
        }

        var doc = session.Document.Bounds;
        var (sdx, sdy, guides) = Compute(startRect, dx, dy, doc, session.SnapTolerance, wholePixels);
        session.SnapGuides = guides;
        return (sdx, sdy);
    }

    /// <summary>純函數版（可單元測試）。</summary>
    public static (float Dx, float Dy, SnapGuides? Guides) Compute(
        SKRect startRect, float dx, float dy, SKRectI doc, float tolerance, bool wholePixels)
    {
        var adjX = SnapAxis(
            startRect.Left + dx, startRect.MidX + dx, startRect.Right + dx,
            doc.Left, doc.Left + doc.Width / 2f, doc.Right,
            tolerance, wholePixels, out var guideX);
        var adjY = SnapAxis(
            startRect.Top + dy, startRect.MidY + dy, startRect.Bottom + dy,
            doc.Top, doc.Top + doc.Height / 2f, doc.Bottom,
            tolerance, wholePixels, out var guideY);

        var guides = guideX.HasValue || guideY.HasValue ? new SnapGuides(guideX, guideY) : null;
        return (dx + adjX, dy + adjY, guides);
    }

    /// <summary>
    /// 單軸吸附：框的三個關鍵位置（低邊/中心/高邊）對畫布的三條線（低邊/中線/高邊），
    /// 取距離最近的一組；在容差內就吸過去，並回報吸到哪條線（畫導線用）。
    /// </summary>
    private static float SnapAxis(
        float lo, float mid, float hi,
        float targetLo, float targetMid, float targetHi,
        float tolerance, bool wholePixels, out float? guide)
    {
        Span<float> positions = [lo, mid, hi];
        Span<float> targets = [targetLo, targetMid, targetHi];

        var bestDistance = float.MaxValue;
        var bestAdj = 0f;
        guide = null;

        foreach (var target in targets)
        {
            foreach (var position in positions)
            {
                var adj = target - position;
                if (Math.Abs(adj) < Math.Abs(bestDistance))
                {
                    bestDistance = adj;
                    bestAdj = adj;
                    guide = target;
                }
            }
        }

        if (Math.Abs(bestDistance) > tolerance)
        {
            guide = null;
            return 0f;
        }
        // 像素內容的位移要維持整數（子像素平移會重取樣模糊）；
        // 中線目標可能是 x.5，貼到最近的整數格（差半格肉眼看不出，導線仍畫在正中）
        return wholePixels ? MathF.Round(bestAdj) : bestAdj;
    }
}
