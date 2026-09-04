using MinePainter.Core.Layers;
using SkiaSharp;

namespace MinePainter.Core.Tools;

/// <summary>
/// 一個吸附參考框。<c>Thirds</c>＝除了邊與中線，還提供兩條三分線（構圖用；目前只有畫布給）。
/// </summary>
public readonly record struct SnapTarget(SKRect Rect, bool Thirds = false);

/// <summary>一條吸附導線（doc 座標）：吸到的位置，以及沿另一軸要畫多長（涵蓋雙方的框）。</summary>
public readonly record struct SnapGuide(float Position, float Start, float End);

/// <summary>目前吸附中的導線（doc 座標）。render thread 直接讀，發布後視為 immutable。</summary>
public sealed record SnapGuides(IReadOnlyList<SnapGuide> XLines, IReadOnlyList<SnapGuide> YLines)
{
    /// <summary>只給單條導線的簡便建構（測試／舊路徑）；長度未知（NaN），畫的時候補滿整個畫布。</summary>
    public SnapGuides(float? x, float? y)
        : this(
            x is { } vx ? [new SnapGuide(vx, float.NaN, float.NaN)] : [],
            y is { } vy ? [new SnapGuide(vy, float.NaN, float.NaN)] : [])
    {
    }

    /// <summary>第一條垂直導線的位置（null = 這一軸沒吸到）。</summary>
    public float? X => XLines.Count > 0 ? XLines[0].Position : null;

    /// <summary>第一條水平導線的位置（null = 這一軸沒吸到）。</summary>
    public float? Y => YLines.Count > 0 ? YLines[0].Position : null;
}

/// <summary>
/// 對齊模式（按住 Tab）：移動「有把手的框」時，把框的左/中/右（上/中/下）吸到附近的參考線 ——
/// 畫布的四邊、兩條中線、四條三分線，以及其他可見圖層的實際內容、其他文字物件、目前的選取範圍。
/// 只調整位移量，各種移動路徑（浮動內容、變形框、圖層平移、文字物件）都套同一條規則。
/// </summary>
public static class CanvasSnap
{
    /// <summary>參考框上限（圖層／物件很多時不要拖垮拖曳）。</summary>
    private const int MaxTargets = 200;

    /// <summary>一個參考框在一軸上最多幾條線（低邊/中線/高邊＋兩條三分線）。</summary>
    private const int MaxAxisLines = 5;

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

        var (sdx, sdy, guides) = Compute(startRect, dx, dy, session.SnapTargets, session.SnapTolerance, wholePixels);
        session.SnapGuides = guides;
        return (sdx, sdy);
    }

    /// <summary>只對畫布吸附的純函數版（單元測試／沒有 session 的呼叫端）。</summary>
    public static (float Dx, float Dy, SnapGuides? Guides) Compute(
        SKRect startRect, float dx, float dy, SKRectI doc, float tolerance, bool wholePixels) =>
        Compute(
            startRect, dx, dy,
            [new SnapTarget(SKRect.Create(doc.Left, doc.Top, doc.Width, doc.Height), Thirds: true)],
            tolerance, wholePixels);

    /// <summary>純函數版（可單元測試）：對一組參考框吸附。</summary>
    public static (float Dx, float Dy, SnapGuides? Guides) Compute(
        SKRect startRect, float dx, float dy, IReadOnlyList<SnapTarget> targets, float tolerance, bool wholePixels)
    {
        var moved = Translated(startRect, dx, dy);
        var adjX = SnapAxis(moved, targets, horizontal: true, tolerance, wholePixels, out var linesX);
        // Y 軸的導線長度用「X 已吸好」的框來算，畫出來才貼著最後的位置
        var adjY = SnapAxis(
            Translated(moved, adjX, 0), targets, horizontal: false, tolerance, wholePixels, out var linesY);

        var guides = linesX.Count > 0 || linesY.Count > 0 ? new SnapGuides(linesX, linesY) : null;
        return (dx + adjX, dy + adjY, guides);
    }

    private static SKRect Translated(SKRect r, float dx, float dy) =>
        new(r.Left + dx, r.Top + dy, r.Right + dx, r.Bottom + dy);

    /// <summary>
    /// 單軸吸附：框的三個關鍵位置（低邊/中心/高邊）對每個參考框提供的線，取距離最近的一組
    /// （同距離時：中心對中心 &gt; 邊與中線 &gt; 三分線）；在容差內就吸過去，
    /// 並把所有「同一個吸附量下正好貼齊」的線都回報出來（可能同時對齊好幾個物件）。
    /// </summary>
    private static float SnapAxis(
        SKRect moved, IReadOnlyList<SnapTarget> targets, bool horizontal,
        float tolerance, bool wholePixels, out List<SnapGuide> lines)
    {
        lines = [];
        var lo = horizontal ? moved.Left : moved.Top;
        var hi = horizontal ? moved.Right : moved.Bottom;
        Span<float> positions = [lo, (lo + hi) / 2f, hi];

        var best = 0f;
        var bestDistance = float.MaxValue;
        var bestRank = int.MaxValue;

        Span<float> candidates = stackalloc float[MaxAxisLines];
        foreach (var t in targets)
        {
            var count = AxisLines(t, horizontal, candidates);
            for (var ti = 0; ti < count; ti++)
            {
                for (var pi = 0; pi < 3; pi++)
                {
                    var adj = candidates[ti] - positions[pi];
                    var distance = MathF.Abs(adj);
                    if (distance > tolerance) continue;
                    // 一樣近時的優先序：中心對中心 > 邊/中線 > 三分線（三分線最弱，不然到處都黏）
                    var rank = ti == 1 && pi == 1 ? 0 : ti >= 3 ? 2 : 1;
                    if (distance < bestDistance - 0.01f ||
                        (distance <= bestDistance + 0.01f && rank < bestRank))
                    {
                        best = adj;
                        bestDistance = distance;
                        bestRank = rank;
                    }
                }
            }
        }

        if (bestDistance > tolerance) return 0f;

        // 像素內容的位移要維持整數（子像素平移會重取樣模糊）；
        // 中線／三分線目標可能不是整數，貼到最近的整數格（差半格肉眼看不出，導線仍畫在正確位置）
        var adjust = wholePixels ? MathF.Round(best) : best;
        CollectGuides(moved, targets, horizontal, adjust, wholePixels ? 0.75f : 0.01f, lines);
        return adjust;
    }

    /// <summary>吸附完成後，把所有貼齊的參考線收成導線（同位置合併，線段涵蓋雙方的框）。</summary>
    private static void CollectGuides(
        SKRect moved, IReadOnlyList<SnapTarget> targets, bool horizontal,
        float adjust, float epsilon, List<SnapGuide> lines)
    {
        var final = horizontal ? Translated(moved, adjust, 0) : Translated(moved, 0, adjust);
        var lo = horizontal ? final.Left : final.Top;
        var hi = horizontal ? final.Right : final.Bottom;
        Span<float> positions = [lo, (lo + hi) / 2f, hi];
        var movedStart = horizontal ? final.Top : final.Left;
        var movedEnd = horizontal ? final.Bottom : final.Right;

        Span<float> candidates = stackalloc float[MaxAxisLines];
        foreach (var t in targets)
        {
            var count = AxisLines(t, horizontal, candidates);
            for (var ti = 0; ti < count; ti++)
            {
                var matched = false;
                for (var pi = 0; pi < 3 && !matched; pi++)
                    matched = MathF.Abs(candidates[ti] - positions[pi]) <= epsilon;
                if (!matched) continue;

                var start = MathF.Min(movedStart, horizontal ? t.Rect.Top : t.Rect.Left);
                var end = MathF.Max(movedEnd, horizontal ? t.Rect.Bottom : t.Rect.Right);
                Merge(lines, new SnapGuide(candidates[ti], start, end));
            }
        }
    }

    /// <summary>一個參考框在某一軸上提供的線：低邊、中線、高邊（＋兩條三分線）。</summary>
    private static int AxisLines(in SnapTarget t, bool horizontal, Span<float> into)
    {
        var lo = horizontal ? t.Rect.Left : t.Rect.Top;
        var hi = horizontal ? t.Rect.Right : t.Rect.Bottom;
        into[0] = lo;
        into[1] = (lo + hi) / 2f;
        into[2] = hi;
        if (!t.Thirds) return 3;
        var span = hi - lo;
        into[3] = lo + span / 3f;
        into[4] = lo + span * 2f / 3f;
        return 5;
    }

    private static void Merge(List<SnapGuide> lines, SnapGuide guide)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (MathF.Abs(lines[i].Position - guide.Position) > 0.01f) continue;
            lines[i] = new SnapGuide(
                lines[i].Position,
                MathF.Min(lines[i].Start, guide.Start),
                MathF.Max(lines[i].End, guide.End));
            return;
        }
        lines.Add(guide);
    }

    /// <summary>
    /// 蒐集這次拖曳的參考框：畫布（含三分線）、選取範圍、其他可見圖層的實際內容、其他文字物件。
    /// <paramref name="exclude"/> 是「正在被拖的東西」的圖層／物件 Id（不能拿自己當參考）。
    /// </summary>
    public static List<SnapTarget> Collect(EditorSession session, IReadOnlySet<Guid> exclude)
    {
        var doc = session.Document;
        // 畫布：四邊＋兩條中線＋四條三分線（構圖）
        var targets = new List<SnapTarget> { new(SKRect.Create(0, 0, doc.Width, doc.Height), Thirds: true) };

        lock (doc.SyncRoot)
        {
            // 選取範圍：拖的就是它（浮動／變形中）時不算，否則會吸回自己原來的位置
            if (session.Floating == null && session.Transform == null &&
                session.Selection is { IsEmpty: false } selection)
            {
                var b = selection.Bounds;
                targets.Add(new SnapTarget(SKRect.Create(b.Left, b.Top, b.Width, b.Height)));
            }

            CollectLayers(doc.Root, exclude, targets);
        }
        return targets;
    }

    private static void CollectLayers(GroupLayer group, IReadOnlySet<Guid> exclude, List<SnapTarget> into)
    {
        foreach (var child in group.Children)
        {
            if (into.Count >= MaxTargets) return;
            if (!child.IsVisible || exclude.Contains(child.Id)) continue;

            switch (child)
            {
                case GroupLayer nested:
                    CollectLayers(nested, exclude, into);
                    break;

                case RasterLayer raster:
                    // 精確內容框（按寫入版本快取，內容沒變時 O(1)）—— 不能用 tile 粒度的 ContentBounds，
                    // 那是 256 對齊的保守外擴，會吸到看不見的 tile 邊界
                    var px = raster.Surface.ExactContentBounds();
                    if (!px.IsEmpty)
                    {
                        into.Add(new SnapTarget(SKRect.Create(
                            px.Left + raster.Offset.X, px.Top + raster.Offset.Y, px.Width, px.Height)));
                    }
                    foreach (var element in raster.Elements)
                    {
                        if (into.Count >= MaxTargets) return;
                        if (raster.ElementsHidden) break;
                        if (exclude.Contains(element.Id) || element.Id == raster.HiddenElementId) continue;
                        var frame = element.FrameBounds; // 使用者看到的框（不含效果外擴）
                        if (frame.Width > 0 && frame.Height > 0) into.Add(new SnapTarget(frame));
                    }
                    break;
            }
        }
    }
}
