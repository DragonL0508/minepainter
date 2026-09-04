using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using MinePainter.Core.Tools;

namespace MinePainter.App.Views;

/// <summary>浮動歷史記錄面板：列出步驟、點擊跳轉（連續 undo/redo）。</summary>
public sealed class HistoryPanelContent : UserControl
{
    private readonly ListBox _list;
    private EditorSession? _session;
    private bool _suppress;
    private int _lastCount;

    public event Action? StateChanged;

    public HistoryPanelContent()
    {
        _list = new ListBox
        {
            Background = AppTheme.InnerBrush,
            MinHeight = 80, // 填滿面板（面板視窗可拉大小）；不隨步驟數變大
            FontSize = 12,
        };
        _list.SelectionChanged += OnSelectionChanged;
        Content = _list;
    }

    public void SetSession(EditorSession? session)
    {
        if (_session != null) _session.History.Changed -= OnHistoryChanged;
        _session = session;
        if (_session != null) _session.History.Changed += OnHistoryChanged;
        Refresh();
    }

    private bool _refreshQueued;

    /// <summary>
    /// 同 LayersPanel：Post 不去重，連續壓步時佇列會積滿整份清單重建。
    /// 這裡更貴 —— 清單長度就是步數本身，愈積愈慢。
    /// </summary>
    private void OnHistoryChanged()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshQueued = false;
            Refresh();
        });
    }

    public void Refresh()
    {
        var session = _session;
        _suppress = true;
        _list.Items.Clear();

        if (session != null)
        {
            var history = session.History;
            _list.Items.Add(new ListBoxItem { Content = "◆ 起點", FontSize = 12 });

            foreach (var entry in history.UndoStack)
                _list.Items.Add(new ListBoxItem { Content = entry.Label, FontSize = 12 });

            // redo 端（灰色，最先可 redo 的在前）
            for (var i = history.RedoStack.Count - 1; i >= 0; i--)
            {
                _list.Items.Add(new ListBoxItem
                {
                    Content = history.RedoStack[i].Label,
                    FontSize = 12,
                    Foreground = AppTheme.TextMutedBrush,
                });
            }

            _list.SelectedIndex = history.UndoStack.Count; // 目前位置

            // 新做的步驟從下方浮出來（只動新增的那幾列；undo/redo 只是選取位置移動，底色自己會過渡）
            var count = _list.Items.Count;
            if (_lastCount > 0 && count > _lastCount)
            {
                for (var i = _lastCount; i < count; i++)
                    if (_list.Items[i] is Control c) Controls.Motion.FadeSlideIn(c, "translateY(6px)");
            }
            _lastCount = count;
            if (_list.SelectedItem is { } sel) Dispatcher.UIThread.Post(() => _list.ScrollIntoView(sel), DispatcherPriority.Loaded);
        }
        else _lastCount = 0;

        _suppress = false;
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppress || _session == null || _list.SelectedIndex < 0) return;
        _session.JumpTo(_list.SelectedIndex); // 內部會先落地浮動選取等進行中的編輯
        StateChanged?.Invoke();
    }
}
