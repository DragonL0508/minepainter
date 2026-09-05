using MinePainter.Core.Documents;
using MinePainter.Core.Layers;
using SkiaSharp;

namespace MinePainter.Core.History;

/// <summary>
/// 像素變更之外，連圖層的「原始高清來源」（<see cref="LayerPixelSource"/>）一起換：
/// undo 放回舊的、redo 放回新的，且每次都把來源的 Revision 對齊剛還原的表面
/// （不然會被當成失效釋放）。兩份來源都由本 entry 持有；還掛在圖層上的那份不釋放。
///
/// 快速模式靠這個讓去背、清除選取、貼上縮小等操作之後，輸出大圖仍能從原圖重畫。
/// </summary>
internal sealed class PixelSourceSwapEntry(IHistoryEntry pixels, RasterLayer layer,
    LayerPixelSource? before, LayerPixelSource? after) : IHistoryEntry
{
    public string Label => pixels.Label;
    public SKRectI DirtyRect => pixels.DirtyRect;
    public long MemoryCost => pixels.MemoryCost + Cost(before) + Cost(after);

    private static long Cost(LayerPixelSource? s) => s == null ? 0 : (long)s.Bounds.Width * s.Bounds.Height * 4;

    public void Undo(Document doc)
    {
        pixels.Undo(doc);
        Swap(doc, before);
    }

    public void Redo(Document doc)
    {
        pixels.Redo(doc);
        Swap(doc, after);
    }

    private void Swap(Document doc, LayerPixelSource? source)
    {
        lock (doc.SyncRoot)
        {
            if (layer.Document != doc) return;
            layer.TakePixelSource(); // 兩份都由本 entry 持有，不在這裡釋放
            if (source != null)
            {
                source.Revision = layer.Surface.Revision;
                layer.SetPixelSource(source);
            }
        }
    }

    public void Dispose()
    {
        pixels.Dispose();
        // 來源不在這裡釋放：縮放／翻轉會讓好幾份來源共用同一張原圖（見 LayerPixelSource.Rebased），
        // 從這裡釋放會把還掛在圖層上的那份一起弄死；交給 GC 收。
    }
}
