using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Material.Icons;
using Material.Icons.Avalonia;
using MinePainter.App.Controls;
using MinePainter.App.Services;
using MinePainter.Core.Adjustments;
using MinePainter.Core.AI;
using MinePainter.Core.Effects;
using IEffect = MinePainter.Core.Effects.IEffect;
using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;

namespace MinePainter.App.Views;
public partial class MainWindow
{
    // ---- 浮動面板 ----

    private void BuildFloatingPanels()
    {
        CreatePanelWindows();

        // 開關按鈕只綁一次；面板實例可能被重建，所以透過 getter 取當下的那一個
        Wire(() => _toolsPanel, ToolsToggle);
        Wire(() => _historyPanel, HistoryToggle);
        Wire(() => _layersPanel, LayersToggle);
        Wire(() => _palettePanel, PaletteToggle);
        Wire(() => _presetsPanel, PresetsToggle);

        void Wire(Func<PanelWindow> panel, ToggleButton toggle)
        {
            toggle.IsCheckedChanged += (_, _) =>
            {
                if (toggle.IsChecked == true)
                {
                    panel().EnsureShown(this);
                    Activate(); // 焦點留在主視窗
                }
                else
                {
                    panel().HideAnimated();
                }
                SchedulePanelLayoutSave(); // 開關狀態也記住
            };
        }
    }

    private void CreatePanelWindows()
    {
        _toolsPanel = Create(new PanelWindow("工具", _toolsContent, 86), ToolsToggle);
        // 圖層／歷史記錄的清單會長，給固定起手尺寸並允許拉大小（工具／調色盤維持隨內容）
        _historyPanel = Create(new PanelWindow("歷史記錄", _historyContent, 216, resizableHeight: 292), HistoryToggle);
        _layersPanel = Create(new PanelWindow("圖層", _layersContent, 330, resizableHeight: 436), LayersToggle);
        _palettePanel = Create(new PanelWindow("調色盤", _paletteContent, 330), PaletteToggle);
        // 440 寬是「資料夾清單 + 三欄縮圖」的最小值，再窄就掉成兩欄；高度只留三排縮圖
        _presetsPanel = Create(new PanelWindow("預設集", _presetsContent, 440, resizableHeight: 356), PresetsToggle);

        PanelWindow Create(PanelWindow panel, ToggleButton toggle)
        {
            panel.CloseRequested += () => toggle.IsChecked = false;
            panel.PositionChanged += (_, _) => OnPanelMoved(panel);
            panel.KeyFallback = HandlePanelKey; // 焦點在面板上時的快捷鍵（Ctrl+Z…）
            panel.SizeChanged += (_, _) => SchedulePanelLayoutSave(); // 拉大小也記住
            return panel;
        }
    }

    /// <summary>
    /// 關閉被取消後（未存檔提示按了取消）把浮窗接回來。
    /// Avalonia 在主視窗的 Closing 之前就把子視窗真的關掉了，關掉的 Window 不能再 Show，
    /// 只能以原位置重建 —— 面板內容是常駐控制項，重建的只有外框視窗。
    /// </summary>
    private void RecreateFloatingPanels()
    {
        if (PanelPairs().All(p => !p.Item1.IsClosed)) return;

        var state = PanelPairs().Select(p => (p.Item1.LastPosition, p.Item1.Anchor)).ToList();
        foreach (var (panel, _) in PanelPairs()) panel.AllowClose(); // 還沒關掉的先收乾淨
        CreatePanelWindows();

        var i = 0;
        foreach (var (panel, toggle) in PanelPairs())
        {
            var (position, anchor) = state[i++];
            panel.Anchor = anchor;
            panel.Position = position;
            if (toggle.IsChecked == true) panel.Show(this);
        }
        Activate();
    }

    private void ShowPanels()
    {
        if (!_panelsPlaced)
        {
            _panelSaveTimer.Tick += (_, _) => SavePanelLayout(withWindowState: false);
            _panelsPlaced = true;
            var frame = MainWorkArea();
            Place(_toolsPanel, new PanelAnchor(false, false, Px(18), Px(96)));
            Place(_palettePanel, new PanelAnchor(false, true, Px(18), Px(470)));
            Place(_layersPanel, new PanelAnchor(true, false, Px(348), Px(96)));
            Place(_historyPanel, new PanelAnchor(true, true, Px(286), Px(380)));
            Place(_presetsPanel, new PanelAnchor(false, true, Px(18 + 330 + 12), Px(420))); // 調色盤右邊、貼底
            RestorePanelLayout(frame); // 上次關掉時的位置／大小／開關蓋過預設排法

            void Place(PanelWindow panel, PanelAnchor anchor)
            {
                panel.Anchor = anchor;
                panel.Position = AnchoredPosition(anchor, frame);
                _defaultAnchors[PanelPairs().First(p => p.Item1 == panel).Item2] = anchor;
            }
        }

        foreach (var (panel, toggle) in PanelPairs())
        {
            if (toggle.IsChecked == true) panel.EnsureShown(this);
        }
        Activate();
    }

    private readonly Dictionary<ToggleButton, PanelAnchor> _defaultAnchors = new();

    /// <summary>
    /// 自我修復（每 500ms 與主視窗取得焦點時）：開關亮著的面板就必須看得到。
    /// 面板會不見的路徑不只一條（退場動畫中途又被 Show、被框架連帶關掉、位置跑到所有螢幕之外…），
    /// 與其逐一追，不如以開關為真相來源定期對齊 —— 使用者不該需要「點兩下」才把面板叫回來。
    /// </summary>
    private bool _closingPrompt;

    private void EnsurePanelsVisible()
    {
        if (!_panelsPlaced || !IsVisible || _closingPrompt || _forceClose ||
            WindowState == WindowState.Minimized)
        {
            return;
        }
        if (PanelPairs().Any(p => p.Item1.IsClosed))
        {
            RecreateFloatingPanels();
            return;
        }

        var area = MainWorkArea();
        if (IsBogusArea(area)) return;
        var screens = Screens.All;
        foreach (var (panel, toggle) in PanelPairs())
        {
            if (toggle.IsChecked != true) continue;

            // 整個面板落在所有螢幕之外（例如換了螢幕配置、主視窗曾被縮到很小）→ 回到預設錨點
            var w = Px(panel.Bounds.Width > 0 ? panel.Bounds.Width : panel.Width);
            var h = Px(panel.Bounds.Height > 0 ? panel.Bounds.Height : panel.Height);
            var rect = new PixelRect(panel.Position, new PixelSize(Math.Max(w, 1), Math.Max(h, 1)));
            if (screens.Count > 0 && !screens.Any(s => s.Bounds.Intersects(rect)) &&
                _defaultAnchors.TryGetValue(toggle, out var anchor))
            {
                panel.Anchor = anchor;
                panel.Position = AnchoredPosition(anchor, area);
            }

            panel.EnsureShown(this);
        }
    }

    /// <summary>最小化時 Windows 會把視窗搬到 (-32000, -32000)，那個座標不能拿來當基準。</summary>
    private static bool IsBogusArea(PixelRect area) => area.X <= -30000 || area.Y <= -30000;

    // ---- 面板錨定（跟著主視窗移動／最大化）----

    /// <summary>主視窗工作區在螢幕上的矩形（像素）。面板的相對位置都以它為基準。</summary>
    private PixelRect MainWorkArea() =>
        new(this.PointToScreen(default), PixelSize.FromSize(ClientSize, RenderScaling));

    private int Px(double dip) => (int)Math.Round(dip * RenderScaling);

    private static PixelPoint AnchoredPosition(PanelAnchor a, PixelRect area) => new(
        a.Right ? area.Right - a.OffsetX : area.X + a.OffsetX,
        a.Bottom ? area.Bottom - a.OffsetY : area.Y + a.OffsetY);

    /// <summary>
    /// 主視窗移動或改變大小（含最大化）時，浮動面板照相對位置跟著走 ——
    /// paint.net 的浮動面板也是黏在主視窗四角，不然每次最大化都要重拉一次。
    /// 靠左/上的維持與該邊的距離，靠右/下的維持與右/下緣的距離。
    /// </summary>
    private void RepositionPanels()
    {
        if (!_panelsPlaced || WindowState == WindowState.Minimized) return;

        var area = MainWorkArea();
        if (IsBogusArea(area)) return; // 最小化途中的 PositionChanged 可能先於 WindowState 更新
        foreach (var (panel, _) in PanelPairs())
        {
            if (panel.Anchor is { } anchor) panel.Position = AnchoredPosition(anchor, area);
        }
    }

    /// <summary>
    /// 面板位置變了。若剛好等於錨點算出來的位置，就是上面自己搬的，不動錨點；
    /// 否則是使用者拖動的，重新記下它現在貼近哪一組邊。
    /// </summary>
    private void OnPanelMoved(PanelWindow panel)
    {
        if (!_panelsPlaced) return;
        var area = MainWorkArea();
        if (IsBogusArea(area) || panel.Position.X <= -30000 || panel.Position.Y <= -30000) return;
        if (panel.Anchor is { } a && AnchoredPosition(a, area) == panel.Position) return;

        SchedulePanelLayoutSave();

        var pos = panel.Position;
        var w = Px(panel.Bounds.Width > 0 ? panel.Bounds.Width : panel.Width);
        var h = Px(panel.Bounds.Height);
        var right = pos.X + w / 2 > area.X + area.Width / 2;
        var bottom = pos.Y + h / 2 > area.Y + area.Height / 2;
        panel.Anchor = new PanelAnchor(right, bottom,
            right ? area.Right - pos.X : pos.X - area.X,
            bottom ? area.Bottom - pos.Y : pos.Y - area.Y);
    }

    private IEnumerable<(PanelWindow, ToggleButton)> PanelPairs()
    {
        yield return (_toolsPanel, ToolsToggle);
        yield return (_historyPanel, HistoryToggle);
        yield return (_layersPanel, LayersToggle);
        yield return (_palettePanel, PaletteToggle);
        yield return (_presetsPanel, PresetsToggle);
    }

    // ---- 面板配置的記憶（settings.json）----

    /// <summary>設定檔裡的面板 id（與 <see cref="PanelPairs"/> 同順序）。</summary>
    private static readonly string[] PanelIds = ["tools", "history", "layers", "palette", "presets"];

    /// <summary>
    /// 套用上次關掉時記下的面板配置：貼哪一組邊、距離、大小、開關。
    /// 沒記錄過（第一次啟動、或設定檔壞掉）就維持內建預設排法。
    /// </summary>
    private void RestorePanelLayout(PixelRect frame)
    {
        var saved = Services.AppSettings.Instance.Panels;
        if (saved.Count == 0) return;

        var i = 0;
        foreach (var (panel, toggle) in PanelPairs())
        {
            var id = PanelIds[i++];
            if (!saved.TryGetValue(id, out var layout)) continue;

            var anchor = new PanelAnchor(layout.Right, layout.Bottom, layout.OffsetX, layout.OffsetY);
            panel.Anchor = anchor;
            panel.Position = AnchoredPosition(anchor, frame);
            // 大小只有「可拉大小」的面板記得住（工具／調色盤的高度是隨內容算的）
            if (panel.IsResizable && layout.Width >= panel.MinWidth && layout.Height >= panel.MinHeight)
            {
                panel.Width = layout.Width;
                panel.Height = layout.Height;
            }
            toggle.IsChecked = layout.Visible;
        }
    }

    /// <summary>
    /// 面板動過了 → 1 秒後把配置寫回設定。拖曳中每一次 PositionChanged 都寫檔太吵，
    /// 只在關閉時寫又會被當掉／強制結束整碗端走。
    /// </summary>
    private readonly Avalonia.Threading.DispatcherTimer _panelSaveTimer = new()
    {
        Interval = TimeSpan.FromSeconds(1),
    };

    private void SchedulePanelLayoutSave()
    {
        if (!_panelsPlaced) return;
        _panelSaveTimer.Stop();
        _panelSaveTimer.Start();
    }

    /// <summary>
    /// 把目前的面板配置寫回設定。<paramref name="withWindowState"/>＝連「有沒有最大化」一起記
    /// （只有真的關視窗那次才算數：拖曳中的存檔看到的可能是最小化／驗證模式硬拉成視窗的狀態）。
    /// </summary>
    private void SavePanelLayout(bool withWindowState)
    {
        _panelSaveTimer.Stop();
        if (!_panelsPlaced || _debugOffscreen) return;
        var settings = Services.AppSettings.Instance;
        var i = 0;
        foreach (var (panel, toggle) in PanelPairs())
        {
            var id = PanelIds[i++];
            if (panel.Anchor is not { } a) continue;
            settings.Panels[id] = new Services.AppSettings.PanelLayout
            {
                Right = a.Right,
                Bottom = a.Bottom,
                OffsetX = a.OffsetX,
                OffsetY = a.OffsetY,
                Width = panel.Bounds.Width > 0 ? panel.Bounds.Width : panel.Width,
                Height = panel.Bounds.Height > 0 ? panel.Bounds.Height : panel.Height,
                Visible = toggle.IsChecked == true,
            };
        }
        // 最小化中關掉的話 WindowState 是 Minimized，那不是使用者想記住的狀態
        if (withWindowState && WindowState != WindowState.Minimized)
            settings.WindowMaximized = WindowState == WindowState.Maximized;
        settings.Save();
    }
}
