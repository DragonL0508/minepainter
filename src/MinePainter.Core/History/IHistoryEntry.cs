using MinePainter.Core.Documents;
using SkiaSharp;

namespace MinePainter.Core.History;

/// <summary>
/// 一步可復原的操作。Undo/Redo 由 HistoryManager 在 Document.SyncRoot 內呼叫；
/// 實作最後應呼叫 doc.NotifyChanged 使受影響範圍重新合成。
/// Dispose 釋放持有的資源（tile 引用等）。
/// </summary>
public interface IHistoryEntry : IDisposable
{
    string Label { get; }
    SKRectI DirtyRect { get; }
    long MemoryCost { get; }

    void Undo(Document doc);
    void Redo(Document doc);
}
