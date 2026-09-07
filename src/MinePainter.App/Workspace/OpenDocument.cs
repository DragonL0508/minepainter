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

    // dirty 不是獨立旗標，而是兩個版本號的差：History 每變一次 _changeVersion +1，
    // 存檔完成時把「這次存檔涵蓋到的版本」寫進 _savedVersion。
    // 背景存檔期間又畫了東西，_changeVersion 已經超前，存完自然仍是 dirty，
    // 不用再靠「先清旗標再補回來」那種容易被蓋掉的寫法。
    private int _changeVersion;
    private int _savedVersion;

    public EditorSession Session { get; }

    /// <summary>目前的 .mpp 路徑（null = 尚未存過）。</summary>
    public string? FilePath { get; private set; }

    /// <summary>匯入來源的檔名（.pdn／.psd／影像）；只用於標題與存檔預設名。</summary>
    public string? ImportedName { get; }

    public string Name => FilePath != null ? Path.GetFileName(FilePath) : ImportedName ?? "未命名";

    /// <summary>History 累計的變更版本；縮圖用它判斷「有沒有變」。</summary>
    public int ChangeVersion => Volatile.Read(ref _changeVersion);

    public bool IsDirty => ChangeVersion != Volatile.Read(ref _savedVersion);

    /// <summary>
    /// 檔案路徑或 dirty 狀態變了。編輯只在「乾淨→dirty」那一刻發一次，
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

        _historyChangedHandler = OnHistoryChanged;
        _sizeChangedHandler = () => DocumentSizeChanged?.Invoke();
        Session.History.Changed += _historyChangedHandler;
        document.SizeChanged += _sizeChangedHandler;
    }

    private void OnHistoryChanged()
    {
        var wasDirty = IsDirty;
        Interlocked.Increment(ref _changeVersion); // History.Changed 可能來自非 UI 執行緒
        if (!wasDirty) StateChanged?.Invoke();
    }

    /// <summary>存檔開始時先拿版本：存檔期間的編輯不會被這次存檔涵蓋。</summary>
    public int CaptureSaveVersion() => ChangeVersion;

    /// <summary>存檔成功：記住路徑，並把 <paramref name="savedVersion"/> 當成已落地的版本。</summary>
    public void CompleteSave(string path, int savedVersion)
    {
        FilePath = path;
        Volatile.Write(ref _savedVersion, savedVersion);
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
