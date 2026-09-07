using Avalonia.Input;
using MinePainter.Core.Tools;

namespace MinePainter.App.Views;

/// <summary>擁有一段鍵盤微調，以及這段滑行的 Undo 併步與通知生命週期。</summary>
internal sealed class CanvasNudgeController
{
    internal bool IsActive => !_glide.IsIdle || _glide.AnyHeld;
    internal bool Shift { get => _glide.Shift; set => _glide.Shift = value; }
    // ---- 方向鍵微調：按一下走一格，按住則由動畫迴圈等速滑行 ----
    //
    // 節奏本身在 Core 的 NudgeGlide（可單元測試）；這裡只負責把按鍵與幀時間餵進去、
    // 把它吐出的整數位移交給 MoveTool.Nudge。所有微調目標都適用（浮動內容／變形框／
    // 選取的像素／文字物件／整個圖層）；會壓 undo 的那幾條在滑行結束時併回一步。

    private readonly Core.Tools.NudgeGlide _glide = new();

    /// <summary>滑行期間壓進歷史的步數起算點（放開時併回一步）。</summary>
    private int _nudgeUndoBase = -1;

    /// <summary>
    /// 滑行期間擋住 History.Changed。每幀壓一步、每步都讓圖層面板與歷史面板整份重建清單的話，
    /// UI 執行緒會被自己排的重建塞爆 —— 連放開按鍵的事件都排不進去，看起來就是當掉、
    /// 而且物件停不下來。放開時併回一步，那時才發一次事件。
    /// </summary>
    private IDisposable? _nudgeHistoryHold;

    /// <summary>微調的四個方向也是可自訂的按鍵（預設方向鍵）；不是微調鍵就回 null。</summary>
    internal static (int X, int Y)? Direction(Key key)
    {
        if (Services.ShortcutMap.MatchesKey("nudge.left", key)) return (-1, 0);
        if (Services.ShortcutMap.MatchesKey("nudge.right", key)) return (1, 0);
        if (Services.ShortcutMap.MatchesKey("nudge.up", key)) return (0, -1);
        if (Services.ShortcutMap.MatchesKey("nudge.down", key)) return (0, 1);
        return null;
    }

    /// <summary>方向鍵按下：第一次按記一格，之後的 OS 重複事件交給滑行處理。</summary>
    internal void Begin(EditorSession session, Key key, bool shift)
    {
        if (!Core.Tools.MoveTool.HasNudgeTarget(session)) return;
        if (Direction(key) is not { } dir) return;
        var (dirX, dirY) = dir;
        _glide.Shift = shift;
        if (!_glide.Press(dirX, dirY, shift ? 10 : 1)) return; // 按鍵重複：滑行已經在動了
        if (_nudgeUndoBase < 0)
        {
            _nudgeUndoBase = session.History.UndoStack.Count;
            _nudgeHistoryHold ??= session.History.SuspendNotifications();
        }
    }

    internal void End(Key key)
    {
        if (Direction(key) is not { } dir) return;
        var (dirX, dirY) = dir;
        _glide.Release(dirX, dirY);
    }

    /// <summary>一段微調結束：滑行期間每幀壓的那些步併回一步，Ctrl+Z 一次回到起點。</summary>
    internal void Finish(EditorSession? session)
    {
        if (_nudgeUndoBase >= 0 && session != null)
        {
            var added = session.History.UndoStack.Count - _nudgeUndoBase;
            if (added > 1) session.History.CollapseLast(added);
        }
        _nudgeUndoBase = -1;
        _glide.Reset();
        // 併回一步之後才解除，面板只會重建一次（順序不能反，否則中間那幾百步會先送出去）
        var hold = _nudgeHistoryHold;
        _nudgeHistoryHold = null;
        hold?.Dispose();
    }

    /// <summary>這一幀的微調。</summary>
    internal void Step(EditorSession? session, double dt, Action changed)
    {
        if (_glide.IsIdle && !_glide.AnyHeld && _nudgeUndoBase < 0) return; // 沒在微調

        // 中途落地／取消（Enter、Esc、切工具）：剩下的位移就此作廢，不要事後補跳一段
        if (session == null || !Core.Tools.MoveTool.HasNudgeTarget(session))
        {
            Finish(session);
            return;
        }

        var (dx, dy) = _glide.Step(dt);
        if ((dx != 0 || dy != 0) && Core.Tools.MoveTool.Nudge(session, dx, dy)) changed();

        // 都放開、殘餘也送完了 → 收尾（把這一段的歷史併回一步）
        if (!_glide.AnyHeld && _glide.IsIdle) Finish(session);
    }

}
