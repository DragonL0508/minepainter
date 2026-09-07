using MinePainter.Core.Layers;
using SkiaSharp;

namespace MinePainter.Core.Documents;

/// <summary>送印前檢查的結果。<see cref="Ok"/> ＝ 兩條規則都過。</summary>
public sealed record PrintCheckResult(
    double UncoveredBleedRatio,
    IReadOnlyList<string> LayersOutsideSafe)
{
    /// <summary>出血區有沒有沒鋪滿的地方（裁切公差往內咬時會露白邊）。</summary>
    public bool BleedCovered => UncoveredBleedRatio <= 0;

    public bool Ok => BleedCovered && LayersOutsideSafe.Count == 0;
}

/// <summary>
/// 送印前檢查，對應印刷廠的兩條規則：
/// 1. **底圖要延伸到出血** —— 出血環（裁切線到畫布邊）不能有透明或半透明的地方。
/// 2. **圖文不要貼邊** —— 圖文要待在安全框內。
///
/// 「圖文」的判準是「內容沒有蓋滿整個畫布的圖層」：滿版底圖本來就該延伸到出血，不該被警告；
/// logo、文字、框線這些一定蓋不滿畫布，就是要檢查的對象。比「掃描像素猜哪些是圖文」可靠也解釋得清楚。
/// 圖層效果（外框、陰影、光暈）會把顏色畫到內容之外，所以範圍要加上效果的 margin。
/// </summary>
public static class PrintCheck
{
    /// <summary>半透明也算沒鋪滿：印出來一樣會透出紙白。</summary>
    private const byte OpaqueAlpha = 250;

    /// <summary>沒有印刷規格時回 null。呼叫端須在 <see cref="Document.SyncRoot"/> 外呼叫（內部自己取鎖）。</summary>
    public static PrintCheckResult? Run(Document doc)
    {
        if (doc.Print is not { } spec) return null;

        var outsideSafe = new List<string>();
        SKRect safeRect;
        SKRect trimRect;
        lock (doc.SyncRoot)
        {
            safeRect = spec.SafeRect(doc);
            trimRect = spec.TrimRect(doc);
            var canvas = new SKRectI(0, 0, doc.Width, doc.Height);
            CollectOutsideSafe(doc.Root, canvas, SKRectI.Round(safeRect), outsideSafe);
        }

        var uncovered = spec.BleedMm <= 0 ? 0 : UncoveredBleedRatio(doc, trimRect);
        return new PrintCheckResult(uncovered, outsideSafe);
    }

    private static void CollectOutsideSafe(GroupLayer group, SKRectI canvas, SKRectI safe, List<string> into)
    {
        foreach (var child in group.Children)
        {
            if (!child.IsVisible) continue;
            if (child is GroupLayer nested)
            {
                // 群組本身可能帶效果，但內容是子層的事：逐個子層看，訊息才指得到真正的圖層
                CollectOutsideSafe(nested, canvas, safe, into);
                continue;
            }

            var bounds = InkBounds(child);
            if (bounds.IsEmpty) continue;
            if (bounds.Contains(canvas)) continue;      // 滿版底圖：它就是要延伸到出血的那一層
            if (safe.Contains(bounds)) continue;        // 整個在安全框內
            into.Add(child.Name);
        }
    }

    /// <summary>圖層實際著墨的範圍（doc 座標）：精確像素範圍 ∪ 物件範圍，再加上效果往外畫的距離。</summary>
    private static SKRectI InkBounds(LayerNode node)
    {
        var bounds = SKRectI.Empty;
        if (node is RasterLayer raster)
        {
            // Surface.ContentBounds 是 tile 粒度（256 對齊），拿來量 3 mm 的安全距離會差很多
            var pixels = raster.Surface.ExactContentBounds();
            if (!pixels.IsEmpty)
            {
                bounds = new SKRectI(
                    pixels.Left + raster.Offset.X, pixels.Top + raster.Offset.Y,
                    pixels.Right + raster.Offset.X, pixels.Bottom + raster.Offset.Y);
            }

            foreach (var element in raster.Elements)
            {
                var b = element.Bounds;
                if (b.IsEmpty) continue;
                bounds = bounds.IsEmpty ? b : SKRectI.Union(bounds, b);
            }
        }

        if (bounds.IsEmpty) return bounds;
        var margin = Effects.LayerEffectRenderer.TotalMargin(node);
        if (margin > 0) bounds.Inflate(margin, margin);
        return bounds;
    }

    /// <summary>出血環裡「不夠不透明」的像素比例（0＝鋪滿）。</summary>
    private static unsafe double UncoveredBleedRatio(Document doc, SKRect trimRect)
    {
        using var composite = OutputRender.Render(doc);
        // 快速模式：合成結果是輸出解析度，畫布座標的裁切線要跟著放大
        var scale = doc.OutputScale;
        var trim = SKRectI.Round(new SKRect(
            trimRect.Left * scale, trimRect.Top * scale, trimRect.Right * scale, trimRect.Bottom * scale));

        var info = new SKImageInfo(composite.Width, composite.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        if (!composite.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0)) return 0;

        var pixels = (uint*)bitmap.GetPixels();
        long total = 0;
        long bad = 0;
        for (var y = 0; y < info.Height; y++)
        {
            var insideRows = y >= trim.Top && y < trim.Bottom;
            for (var x = 0; x < info.Width; x++)
            {
                if (insideRows && x >= trim.Left && x < trim.Right)
                {
                    x = trim.Right - 1; // 跳過裁切線內部：只看出血環
                    continue;
                }
                total++;
                if (pixels[y * info.Width + x] >> 24 < OpaqueAlpha) bad++;
            }
        }
        return total == 0 ? 0 : bad / (double)total;
    }
}
