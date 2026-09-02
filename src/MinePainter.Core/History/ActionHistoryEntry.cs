using MinePainter.Core.Documents;
using SkiaSharp;

namespace MinePainter.Core.History;

/// <summary>
/// 以閉包表達的 undo 步驟：圖層屬性變更、樹結構變更（insert/remove/move）都用它。
/// 閉包在 Document.SyncRoot 內執行，結尾自行 NotifyChanged/Invalidate。
/// </summary>
public sealed class ActionHistoryEntry : IHistoryEntry
{
    private readonly Action<Document> _undo;
    private readonly Action<Document> _redo;
    private readonly Action? _onDispose;

    public string Label { get; }
    public SKRectI DirtyRect { get; }
    public long MemoryCost { get; }

    public ActionHistoryEntry(string label, SKRectI dirtyRect,
        Action<Document> undo, Action<Document> redo,
        long memoryCost = 0, Action? onDispose = null)
    {
        Label = label;
        DirtyRect = dirtyRect;
        _undo = undo;
        _redo = redo;
        MemoryCost = memoryCost;
        _onDispose = onDispose;
    }

    public void Undo(Document doc) => _undo(doc);

    public void Redo(Document doc) => _redo(doc);

    public void Dispose() => _onDispose?.Invoke();
}
