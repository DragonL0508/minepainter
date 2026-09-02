using MinePainter.Core.Documents;

namespace MinePainter.Core.History;

/// <summary>
/// undo/redo 雙堆疊。Push/Undo/Redo 都在 UI thread 呼叫；
/// 內部進入 Document.SyncRoot 執行 entry。
/// 記憶體總量超過上限時從最舊淘汰。
/// </summary>
public sealed class HistoryManager : IDisposable
{
    private readonly Document _document;
    private readonly List<IHistoryEntry> _undo = new();
    private readonly List<IHistoryEntry> _redo = new();

    public long MemoryLimit { get; set; } = 1L << 30; // 1 GB

    /// <summary>堆疊內容變化（UI 更新用）。</summary>
    public event Action? Changed;

    public HistoryManager(Document document) => _document = document;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoLabel => _undo.Count > 0 ? _undo[^1].Label : null;
    public string? RedoLabel => _redo.Count > 0 ? _redo[^1].Label : null;
    public IReadOnlyList<IHistoryEntry> UndoStack => _undo;
    public IReadOnlyList<IHistoryEntry> RedoStack => _redo;

    /// <summary>
    /// 跳到指定的 undo 深度（歷史面板點擊跳轉用）。
    /// internal：UI 一律走 <see cref="Tools.EditorSession.JumpTo"/>，它會先落地進行中的編輯。
    /// </summary>
    internal void JumpTo(int undoDepth)
    {
        while (_undo.Count > undoDepth && Undo())
        {
        }
        while (_undo.Count < undoDepth && Redo())
        {
        }
    }

    public long TotalMemoryCost
    {
        get
        {
            long total = 0;
            foreach (var e in _undo) total += e.MemoryCost;
            foreach (var e in _redo) total += e.MemoryCost;
            return total;
        }
    }

    /// <summary>記錄一步已執行完的操作（清空 redo）。</summary>
    public void Push(IHistoryEntry entry)
    {
        foreach (var e in _redo) e.Dispose();
        _redo.Clear();

        _undo.Add(entry);
        EvictIfNeeded();
        Changed?.Invoke();
    }

    /// <summary>
    /// internal：UI 一律走 <see cref="Tools.EditorSession.Undo"/>。
    ///
    /// 直接呼叫這裡會略過「還沒進 history 的進行中編輯」（浮動選取、畫布內文字編輯），
    /// 導致復原跳到上一步、畫面狀態互相矛盾。曾經有三個 UI 入口各自漏掉這件事，
    /// 所以乾脆把入口收掉 —— 讓 App 組件在編譯期就碰不到。
    /// </summary>
    internal bool Undo()
    {
        if (_undo.Count == 0) return false;
        var entry = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);

        lock (_document.SyncRoot)
        {
            entry.Undo(_document);
        }

        _redo.Add(entry);
        Changed?.Invoke();
        return true;
    }

    /// <summary>internal：UI 一律走 <see cref="Tools.EditorSession.Redo"/>。</summary>
    internal bool Redo()
    {
        if (_redo.Count == 0) return false;
        var entry = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);

        lock (_document.SyncRoot)
        {
            entry.Redo(_document);
        }

        _undo.Add(entry);
        Changed?.Invoke();
        return true;
    }

    private void EvictIfNeeded()
    {
        var total = TotalMemoryCost;
        while (total > MemoryLimit && _undo.Count > 1)
        {
            var oldest = _undo[0];
            _undo.RemoveAt(0);
            total -= oldest.MemoryCost;
            oldest.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var e in _undo) e.Dispose();
        foreach (var e in _redo) e.Dispose();
        _undo.Clear();
        _redo.Clear();
    }
}
