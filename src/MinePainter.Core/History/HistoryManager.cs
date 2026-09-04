using MinePainter.Core.Documents;

namespace MinePainter.Core.History;

/// <summary>
/// undo/redo 雙堆疊。Push/Undo/Redo 都在 UI thread 呼叫；
/// 內部進入 Document.SyncRoot 執行 entry。
/// 記憶體總量超過上限時從最舊淘汰。
///
/// 上限是「所有開啟中的文件共用」（<see cref="GlobalMemoryLimit"/>）而不是每份文件各自一份 ——
/// 每份各 1 GB 的話，開五個分頁的 undo 就能吃掉 5 GB。每份文件分到 1/N，
/// 但不低於 <see cref="MinimumShareBytes"/>，開再多分頁每一份都還留得住幾步。
///
/// 「開新分頁時把既有文件也縮到新的份額」意味著會從別的執行緒動到別份文件的堆疊，
/// 所以兩個堆疊的存取都用 <c>_gate</c> 保護（進 Document.SyncRoot 之前才拿，沒有反向路徑）。
/// </summary>
public sealed class HistoryManager : IDisposable
{
    private readonly Document _document;
    private readonly List<IHistoryEntry> _undo = new();
    private readonly List<IHistoryEntry> _redo = new();
    private readonly object _gate = new();

    /// <summary>這份文件自己的上限（實際生效值還會再取「全域預算 ÷ 文件數」的較小者）。</summary>
    public long MemoryLimit { get; set; } = 1L << 30; // 1 GB

    /// <summary>每份文件保底的份額 —— 分頁再多也還留得住幾步 undo。</summary>
    public const long MinimumShareBytes = 64L << 20;

    private static readonly List<HistoryManager> Live = new();

    private int _suspendDepth;
    private bool _changedPending;

    /// <summary>
    /// 暫時不發 <see cref="Changed"/>，解除時若期間有變動就補發一次。
    ///
    /// 給「一個手勢連續壓很多步」用（方向鍵按住滑行是每幀一步）：圖層面板與歷史面板
    /// 收到 Changed 就整份重建清單，每幀發一次會讓 UI 執行緒被自己排的重建塞爆 ——
    /// 連放開按鍵的事件都排不進去，看起來就是當掉而且東西停不下來。
    /// </summary>
    public IDisposable SuspendNotifications()
    {
        lock (_gate) _suspendDepth++;
        return new Suspension(this);
    }

    private void RaiseChanged()
    {
        lock (_gate)
        {
            if (_suspendDepth > 0)
            {
                _changedPending = true;
                return;
            }
        }
        Changed?.Invoke();
    }

    private sealed class Suspension(HistoryManager owner) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            bool fire;
            lock (owner._gate)
            {
                owner._suspendDepth--;
                fire = owner._suspendDepth == 0 && owner._changedPending;
                if (fire) owner._changedPending = false;
            }
            if (fire) owner.Changed?.Invoke();
        }
    }

    private static long _globalMemoryLimit = DefaultGlobalLimit();

    /// <summary>
    /// 所有開啟中文件的 undo/redo 記憶體總預算。預設取實體記憶體的 1/8（夾在 512 MB～2 GB）。
    /// </summary>
    public static long GlobalMemoryLimit
    {
        get => Volatile.Read(ref _globalMemoryLimit);
        set
        {
            Volatile.Write(ref _globalMemoryLimit, Math.Max(MinimumShareBytes, value));
            RebalanceAll();
        }
    }

    private static long DefaultGlobalLimit()
    {
        var total = (long)AI.SystemMemory.TotalPhysicalBytes;
        return total > 0 ? Math.Clamp(total / 8, 512L << 20, 2L << 30) : 1L << 30;
    }

    /// <summary>目前這份文件實際生效的上限（自己的上限 vs 全域預算的份額，取小）。</summary>
    public long EffectiveMemoryLimit
    {
        get
        {
            int count;
            lock (Live) count = Math.Max(1, Live.Count);
            return Math.Min(MemoryLimit, Math.Max(MinimumShareBytes, GlobalMemoryLimit / count));
        }
    }

    /// <summary>堆疊內容變化（UI 更新用）。</summary>
    public event Action? Changed;

    public HistoryManager(Document document)
    {
        _document = document;
        lock (Live) Live.Add(this);
        RebalanceAll(); // 多一份文件 = 每份的份額變小，既有的文件也要跟著淘汰
    }

    /// <summary>
    /// 文件數變了：讓每份文件重新對齊自己的份額。
    /// 這裡刻意不發 <see cref="Changed"/> —— 那是「文件被編輯」的訊號，
    /// 拿來報告淘汰會把背景分頁誤標成未存檔。
    /// </summary>
    private static void RebalanceAll()
    {
        HistoryManager[] all;
        lock (Live) all = Live.ToArray();
        foreach (var m in all) m.EvictIfNeeded();
    }

    public bool CanUndo { get { lock (_gate) return _undo.Count > 0; } }
    public bool CanRedo { get { lock (_gate) return _redo.Count > 0; } }
    public string? UndoLabel { get { lock (_gate) return _undo.Count > 0 ? _undo[^1].Label : null; } }
    public string? RedoLabel { get { lock (_gate) return _redo.Count > 0 ? _redo[^1].Label : null; } }
    public IReadOnlyList<IHistoryEntry> UndoStack => _undo;
    public IReadOnlyList<IHistoryEntry> RedoStack => _redo;

    /// <summary>
    /// 跳到指定的 undo 深度（歷史面板點擊跳轉用）。
    /// internal：UI 一律走 <see cref="Tools.EditorSession.JumpTo"/>，它會先落地進行中的編輯。
    /// </summary>
    internal void JumpTo(int undoDepth)
    {
        while (UndoDepth > undoDepth && Undo())
        {
        }
        while (UndoDepth < undoDepth && Redo())
        {
        }
    }

    private int UndoDepth { get { lock (_gate) return _undo.Count; } }

    public long TotalMemoryCost
    {
        get
        {
            lock (_gate)
            {
                long total = 0;
                foreach (var e in _undo) total += e.MemoryCost;
                foreach (var e in _redo) total += e.MemoryCost;
                return total;
            }
        }
    }

    /// <summary>記錄一步已執行完的操作（清空 redo）。</summary>
    public void Push(IHistoryEntry entry)
    {
        lock (_gate)
        {
            foreach (var e in _redo) e.Dispose();
            _redo.Clear();

            _undo.Add(entry);
            EvictLocked();
        }
        RaiseChanged();
    }

    /// <summary>
    /// internal：UI 一律走 <see cref="Tools.EditorSession.Undo"/>。
    ///
    /// 直接呼叫這裡會略過「還沒進 history 的進行中編輯」（浮動選取、畫布內文字編輯），
    /// 導致復原跳到上一步、畫面狀態互相矛盾。曾經有三個 UI 入口各自漏掉這件事，
    /// 所以乾脆把入口收掉 —— 讓 App 組件在編譯期就碰不到。
    /// </summary>
    /// <summary>
    /// 把最後 <paramref name="count"/> 步併成一步（<see cref="CompositeHistoryEntry"/>）。
    /// 給「一個手勢被拆成很多幀」的操作收尾用 —— 方向鍵按住滑行時每幀壓一步，
    /// 放開時併回一步，Ctrl+Z 才會一次回到滑行開始前。
    /// count ≤ 1 或超過堆疊深度時不做事。
    /// </summary>
    public void CollapseLast(int count, string? label = null)
    {
        lock (_gate)
        {
            if (count <= 1 || count > _undo.Count) return;
            var steps = _undo.GetRange(_undo.Count - count, count);
            _undo.RemoveRange(_undo.Count - count, count);
            _undo.Add(new CompositeHistoryEntry(label ?? steps[^1].Label, steps.ToArray()));
        }
        RaiseChanged();
    }

    internal bool Undo()
    {
        lock (_gate)
        {
            if (_undo.Count == 0) return false;
            var entry = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);

            lock (_document.SyncRoot)
            {
                entry.Undo(_document);
            }

            _redo.Add(entry);
        }
        RaiseChanged();
        return true;
    }

    /// <summary>internal：UI 一律走 <see cref="Tools.EditorSession.Redo"/>。</summary>
    internal bool Redo()
    {
        lock (_gate)
        {
            if (_redo.Count == 0) return false;
            var entry = _redo[^1];
            _redo.RemoveAt(_redo.Count - 1);

            lock (_document.SyncRoot)
            {
                entry.Redo(_document);
            }

            _undo.Add(entry);
        }
        RaiseChanged();
        return true;
    }

    private void EvictIfNeeded()
    {
        lock (_gate) EvictLocked();
    }

    private void EvictLocked()
    {
        var limit = EffectiveMemoryLimit;
        long total = 0;
        foreach (var e in _undo) total += e.MemoryCost;
        foreach (var e in _redo) total += e.MemoryCost;
        while (total > limit && _undo.Count > 1)
        {
            var oldest = _undo[0];
            _undo.RemoveAt(0);
            total -= oldest.MemoryCost;
            oldest.Dispose();
        }
    }

    public void Dispose()
    {
        lock (Live) Live.Remove(this);
        lock (_gate)
        {
            foreach (var e in _undo) e.Dispose();
            foreach (var e in _redo) e.Dispose();
            _undo.Clear();
            _redo.Clear();
        }
    }
}
