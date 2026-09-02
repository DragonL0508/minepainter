using MinePainter.Core.Documents;
using SkiaSharp;

namespace MinePainter.Core.History;

/// <summary>
/// 把數個步驟綁成單一 undo 步驟（例如「落地浮動內容」= 像素變更 + 選取框變更）。
/// Undo 以反序執行，Redo 以正序執行。
/// </summary>
public sealed class CompositeHistoryEntry : IHistoryEntry
{
    private readonly IHistoryEntry[] _entries;

    public CompositeHistoryEntry(string label, params IHistoryEntry[] entries)
    {
        if (entries.Length == 0) throw new ArgumentException("至少需要一個步驟。", nameof(entries));
        Label = label;
        _entries = entries;

        var rect = SKRectI.Empty;
        long cost = 0;
        foreach (var e in entries)
        {
            if (!e.DirtyRect.IsEmpty)
                rect = rect.IsEmpty ? e.DirtyRect : SKRectI.Union(rect, e.DirtyRect);
            cost += e.MemoryCost;
        }
        DirtyRect = rect;
        MemoryCost = cost;
    }

    public string Label { get; }
    public SKRectI DirtyRect { get; }
    public long MemoryCost { get; }

    public void Undo(Document doc)
    {
        for (var i = _entries.Length - 1; i >= 0; i--)
            _entries[i].Undo(doc);
    }

    public void Redo(Document doc)
    {
        foreach (var e in _entries) e.Redo(doc);
    }

    public void Dispose()
    {
        foreach (var e in _entries) e.Dispose();
    }
}
