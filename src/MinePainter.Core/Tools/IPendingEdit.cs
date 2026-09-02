namespace MinePainter.Core.Tools;

/// <summary>
/// 「進行中、尚未進 history 的編輯」。
///
/// 這類狀態是 undo 邏輯最常見的破口：像素已經動了（或已從圖層挖走），
/// 但還沒有對應的 history entry。任何指令、undo/redo、存檔在動手之前
/// 都必須先讓它落地，否則歷史會跳到上一步、畫面狀態互相矛盾。
///
/// 與其要每個新功能自己記得處理，改成：實作這個介面並向
/// <see cref="EditorSession.RegisterPendingEdit"/> 註冊，
/// 之後所有入口都會自動涵蓋到（唯一的落地點是 <see cref="EditorSession.CommitPendingEdits"/>）。
///
/// 目前的實作：浮動選取內容（Core）、畫布內文字編輯（App）。
/// </summary>
public interface IPendingEdit
{
    /// <summary>目前是否有未落地的編輯。</summary>
    bool IsActive { get; }

    /// <summary>把編輯落地並推對應的 history entry。沒有進行中的編輯時必須是 no-op。</summary>
    void Commit();
}
