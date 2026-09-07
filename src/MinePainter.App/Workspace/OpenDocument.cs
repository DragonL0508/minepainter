using MinePainter.Core.Tools;

namespace MinePainter.App.Workspace;

/// <summary>
/// 一份在 App 裡開啟中的文件：session、檔案身分、dirty 追蹤，以及這些訂閱的生命週期。
/// 這裡完全不知道視窗、分頁、Dispatcher 的存在 —— 事件可能在任何執行緒發出，
/// 要不要丟回 UI 執行緒是 MainWindow 的事（2026-09：ShortcutMap.Changed 假設在 UI 執行緒才炸過）。
/// </summary>
internal sealed class OpenDocument : IDisposable
{
    private readonly Action _historyChangedHandler;
    private readonly Action _sizeChangedHandler;

    // dirty 不是獨立旗標，而是「目前的 History.StateId ≠ 存檔時的 StateId」。
    // StateId 是文件狀態的身分（undo 回到存檔那步會拿回同一個編號），所以存檔後編輯再 Ctrl+Z
    // 會真的變回乾淨（paint.net／Pinta 都這樣）；單純數 History.Changed 次數做不到，
    // undo 也會讓計數增加，永遠回不去。背景存檔期間又畫了東西，StateId 已經不同，存完自然仍 dirty。
    private long _savedStateId;

    // 縮圖要的是「有沒有動過」，不是「等不等於存檔點」：undo 也算動過，這裡才用計數
    private int _changeVersion;
    private bool _lastNotifiedDirty; // 上次通知 UI 時的 dirty；只在翻轉時再通知

    public EditorSession Session { get; }

    /// <summary>目前的 .mpp 路徑（null = 尚未存過）。</summary>
    public string? FilePath { get; private set; }

    /// <summary>匯入來源的檔名（.pdn／.psd／影像）；只用於標題與存檔預設名。</summary>
    public string? ImportedName { get; }

    public string Name => FilePath != null ? Path.GetFileName(FilePath) : ImportedName ?? "未命名";

    /// <summary>History 累計的變更次數（含 undo/redo）；縮圖用它判斷「有沒有變」。</summary>
    public int ChangeVersion => Volatile.Read(ref _changeVersion);

    public bool IsDirty => Session.History.StateId != Volatile.Read(ref _savedStateId);

    /// <summary>
    /// 檔案路徑或 dirty 狀態變了。編輯只在 dirty 翻轉那一刻發（乾淨→dirty，或 undo 回存檔點），
    /// 不會每一筆都發（一筆筆刷可能發很多次 History.Changed）。不保證在 UI 執行緒。
    /// </summary>
    public event Action? StateChanged;

    /// <summary>文件尺寸變了（含 undo/redo）。不保證在 UI 執行緒。</summary>
    public event Action? DocumentSizeChanged;

    public OpenDocument(Core.Documents.Document document, string? filePath = null, string? importedName = null)
    {
        Session = new EditorSession(document);
        FilePath = filePath;
        ImportedName = importedName;
        _savedStateId = Session.History.StateId; // 剛開啟＝與檔案一致

        _historyChangedHandler = OnHistoryChanged;
        _sizeChangedHandler = () => DocumentSizeChanged?.Invoke();
        Session.History.Changed += _historyChangedHandler;
        document.SizeChanged += _sizeChangedHandler;
    }

    private void OnHistoryChanged()
    {
        Interlocked.Increment(ref _changeVersion); // History.Changed 可能來自非 UI 執行緒
        var dirty = IsDirty;
        if (dirty != _lastNotifiedDirty)
        {
            _lastNotifiedDirty = dirty;
            StateChanged?.Invoke();
        }
    }

    /// <summary>存檔開始時先拿狀態身分：存檔期間的編輯不會被這次存檔涵蓋。</summary>
    public long CaptureSaveVersion() => Session.History.StateId;

    /// <summary>存檔成功：記住路徑，並把 <paramref name="savedStateId"/> 當成已落地的狀態。</summary>
    public void CompleteSave(string path, long savedStateId)
    {
        FilePath = path;
        Volatile.Write(ref _savedStateId, savedStateId);
        _lastNotifiedDirty = IsDirty;
        StateChanged?.Invoke();
    }

    /// <summary>解除自己掛的訂閱再釋放 session；之後不再對外發任何事件。</summary>
    public void Dispose()
    {
        Session.History.Changed -= _historyChangedHandler;
        Session.Document.SizeChanged -= _sizeChangedHandler;
        Session.Dispose();
    }
}
