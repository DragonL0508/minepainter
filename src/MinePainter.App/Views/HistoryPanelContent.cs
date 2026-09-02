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

    private void OnHistoryChanged() => Dispatcher.UIThread.Post(Refresh);

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
        }

        _suppress = false;
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppress || _session == null || _list.SelectedIndex < 0) return;
        _session.JumpTo(_list.SelectedIndex); // 內部會先落地浮動選取等進行中的編輯
        StateChanged?.Invoke();
    }
}
