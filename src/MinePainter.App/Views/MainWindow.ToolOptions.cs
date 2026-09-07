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
    // ---- 工具選項 ----

    private void WireToolOptionBars()
    {
        SizeBox.Value = 8;
        SizeBox.ValueChanged += _ => ApplyBrushOptions();
        HardnessBar.ValueChanged += _ => ApplyBrushOptions();
        SmoothingBar.ValueChanged += _ => ApplyBrushOptions();
        OpacityBar.ValueChanged += _ => ApplyBrushOptions();
        ToleranceBar.ValueChanged += _ => ApplyBrushOptions();
        WireSelectModeToggle(SelectModeShapeToggle, objectMode: false);
        WireSelectModeToggle(SelectModeObjectToggle, objectMode: true);
        SoftnessBar.ValueChanged += _ => ApplyBrushOptions();
        foreach (var k in new[] { "連續", "一次" }) BgSamplingCombo.Items.Add(k);
        foreach (var k in new[] { "連續", "不連續" }) BgLimitCombo.Items.Add(k);
        BgSamplingCombo.SelectedIndex = 0;
        BgLimitCombo.SelectedIndex = 0;
        BgSamplingCombo.SelectionChanged += (_, _) => ApplyBrushOptions();
        BgLimitCombo.SelectionChanged += (_, _) => ApplyBrushOptions();
        ProtectFgCheck.IsCheckedChanged += (_, _) => ApplyBrushOptions();

        ZoomBar.ValueChanged += v =>
        {
            if (!_suppressZoomEvents) Canvas.SetZoomPercent(v);
        };
    }

    private void ApplyBrushOptions()
    {
        var session = Canvas.Session;
        if (session == null) return;

        var radius = (float)(SizeBox.Value / 2);
        var hardness = (float)(HardnessBar.Value / 100);
        var opacity = (float)(OpacityBar.Value / 100);
        var smoothing = (float)SmoothingBar.Value;
        session.Pen.StrokeWidth = (float)SizeBox.Value; // 鋼筆「描邊路徑」的線寬共用「大小」

        foreach (var settings in new[] { session.Brush.Settings, session.Eraser.Settings })
        {
            settings.Radius = radius;
            settings.Hardness = hardness;
            settings.Opacity = opacity;
            settings.Smoothing = smoothing;
        }

        // 鉛筆：大小與不透明度跟著工具列，硬度／平滑固定（像素繪圖不做羽化與手抖平滑）
        var pencil = session.Pencil.Settings;
        pencil.Radius = radius;
        pencil.Opacity = opacity;

        session.Shape.StrokeWidth = Math.Max(1f, (float)SizeBox.Value / 4);
        session.Tolerance = (byte)Math.Round(ToleranceBar.Value * 2.55); // 滑桿 0..100%，工具吃 0..255
        session.ObjectSelect = SelectModeObjectToggle.IsChecked == true;

        var bg = session.BackgroundEraser.Settings;
        bg.Radius = radius;
        bg.Hardness = hardness;
        bg.Tolerance = session.Tolerance;
        bg.Softness = (float)(SoftnessBar.Value / 100);
        bg.Sampling = BgSamplingCombo.SelectedIndex == 1 ? BackgroundSampling.Once : BackgroundSampling.Continuous;
        bg.Contiguous = BgLimitCombo.SelectedIndex != 1;
        bg.ProtectForeground = ProtectFgCheck.IsChecked == true;
    }

    private void ApplyShapeOptions()
    {
        var session = Canvas.Session;
        if (session == null) return;
        session.Shape.Kind = ShapeKindCombo.SelectedIndex switch
        {
            1 => ShapeKind.Ellipse,
            2 => ShapeKind.Line,
            _ => ShapeKind.Rectangle,
        };
        session.Shape.Filled = ShapeFilledCheck.IsChecked == true;

        // 下拉切到／離開「直線」時，工具面板的高亮跟著換（兩顆鈕是同一個工具的兩種形狀）
        if (_currentToolKey is "shape" or "line")
        {
            _currentToolKey = session.Shape.Kind == ShapeKind.Line ? "line" : "shape";
            _toolsContent.SetActive(_currentToolKey);
            ActiveToolLabel.Text = _currentToolKey == "line" ? "直線" : session.Shape.Name;
        }
    }

    /// <summary>
    /// 把工具列上的文字選項寫進 session 的文字工具（新建文字的預設樣式）。
    /// 每份文件是各自的 session／工具實例，工具列才是這些預設值的真相來源 ——
    /// 少了這一步，開檔／新分頁後新建的文字會退回 TextTool 的硬編碼預設（曾經連第一份文件都是）。
    /// </summary>
    private void ApplyTextOptions()
    {
        var session = Canvas.Session;
        if (session == null) return;
        if (FontFamilyCombo.SelectedItem is string family) session.Text.FontFamily = family;
        session.Text.FontWeight = SelectedFontWeight();
        session.Text.FontSize = (float)FontSizeBox.Value;
        session.Text.LetterSpacing = (float)LetterSpacingBox.Value;
        session.Text.Bold = BoldToggle.IsChecked == true;
        session.Text.Italic = ItalicToggle.IsChecked == true;
        session.Text.Underline = UnderlineToggle.IsChecked == true;
        session.Text.Strikethrough = StrikeToggle.IsChecked == true;
        session.Text.Alignment =
            AlignCenterToggle.IsChecked == true ? Core.Vectors.TextAlign.Center :
            AlignRightToggle.IsChecked == true ? Core.Vectors.TextAlign.Right :
            Core.Vectors.TextAlign.Left;
    }

    // ---- 選取模式（矩形／橢圓／套索的工具列群組：形狀／物件）----

    private bool _suppressSelectModeToggle;

    /// <summary>兩個互斥的選取模式鈕：選一個另一個自動關；點已選中的維持選中。</summary>
    private void WireSelectModeToggle(ToggleButton button, bool objectMode)
    {
        button.IsCheckedChanged += (_, _) =>
        {
            if (_suppressSelectModeToggle) return;
            if (button.IsChecked == true) SetSelectMode(objectMode);
            else if ((SelectModeObjectToggle.IsChecked == true) == objectMode)
            {
                _suppressSelectModeToggle = true;
                button.IsChecked = true;
                _suppressSelectModeToggle = false;
            }
        };
    }

    private void SetSelectMode(bool objectMode)
    {
        _suppressSelectModeToggle = true;
        SelectModeShapeToggle.IsChecked = !objectMode;
        SelectModeObjectToggle.IsChecked = objectMode;
        _suppressSelectModeToggle = false;
        ApplyBrushOptions();
    }

    // ---- 變形模式（移動工具的工具列群組）----

    private bool _suppressTransformToggle;

    /// <summary>三個互斥的變形模式鈕：選一個其餘自動關；點已選中的維持選中。</summary>
    private void WireTransformToggle(ToggleButton button, TransformMode mode)
    {
        button.IsCheckedChanged += (_, _) =>
        {
            if (_suppressTransformToggle) return;
            if (button.IsChecked == true)
            {
                SetTransformMode(mode);
            }
            else if (CurrentTransformMode() == mode)
            {
                _suppressTransformToggle = true;
                button.IsChecked = true;
                _suppressTransformToggle = false;
            }
        };
    }

    private TransformMode CurrentTransformMode()
    {
        if (TransformPerspectiveToggle.IsChecked == true) return TransformMode.Perspective;
        if (TransformWarpToggle.IsChecked == true) return TransformMode.Warp;
        return TransformMode.Free;
    }

    /// <summary>
    /// 切換變形模式。變形中切到自由變形時先落地目前的四角變形（四角映射回不到矩形模式；
    /// 落地後再拖角會從原始像素續接，不糊）；切到透視／扭曲時現有 session 直接進四角模式。
    /// </summary>
    private void SetTransformMode(TransformMode mode)
    {
        _suppressTransformToggle = true;
        TransformFreeToggle.IsChecked = mode == TransformMode.Free;
        TransformPerspectiveToggle.IsChecked = mode == TransformMode.Perspective;
        TransformWarpToggle.IsChecked = mode == TransformMode.Warp;
        _suppressTransformToggle = false;

        var session = Canvas.Session;
        if (session == null) return;
        session.Move.TransformMode = mode;

        if (session.Transform is { } t)
        {
            // 網格模式之間或回到自由變形：網格映射回不到矩形模式，先落地（續接／重新框都不糊）
            var needsCommit = mode == TransformMode.Free ? t.IsMeshMode
                : mode == TransformMode.Perspective ? t.Warp != null
                : t.Quad != null && t.IsQuadChanged;
            if (needsCommit) session.CommitTransform();
            if (mode != TransformMode.Free) session.EnterTransformMode(mode);
        }
        session.RefreshSelectionHandles(); // 沒在變形也要換把手（4 角／16 控制點／8 把手）
        RefreshUiState();
    }

    /// <summary>把工具列的變形模式推進 session（每份文件各自的工具實例）。</summary>
    private void ApplyMoveOptions()
    {
        var session = Canvas.Session;
        if (session == null) return;
        session.Move.TransformMode = CurrentTransformMode();
    }

    /// <summary>圖層 → 變形 → …：切到移動工具、設定模式、立刻框住圖層內容開始變形。</summary>
    private void BeginTransformFromMenu(TransformMode mode)
    {
        var session = Canvas.Session;
        if (session == null) return;
        session.CommitPendingEdits();
        SelectTool("move");
        SetTransformMode(mode);
        if (session.EnterTransformMode(mode) == null) return;
        Toasts.Show(mode switch
        {
            TransformMode.Perspective => "透視：拖四角（Shift＝只動一角）；Enter 套用、Esc 還原",
            TransformMode.Warp => "扭曲：拖網格上的 16 個控制點；Enter 套用、Esc 還原",
            _ => "自由變形：拖角縮放、右鍵旋轉；Enter 套用、Esc 還原",
        });
        RefreshUiState();
    }

    private void OnTransformFreeClicked(object? sender, RoutedEventArgs e) => BeginTransformFromMenu(TransformMode.Free);
    private void OnTransformPerspectiveClicked(object? sender, RoutedEventArgs e) => BeginTransformFromMenu(TransformMode.Perspective);
    private void OnTransformDistortClicked(object? sender, RoutedEventArgs e) => BeginTransformFromMenu(TransformMode.Warp);

    // ---- 鋼筆 ----

    private void PenMakeSelection()
    {
        var session = Canvas.Session;
        if (session == null) return;
        if (PenCommands.MakeSelection(session)) Toasts.Show("路徑已轉為選取範圍");
        RefreshUiState();
        Canvas.Focus();
    }

    private void RunPenCommand(Func<EditorSession, bool> command, string doneMessage)
    {
        var session = Canvas.Session;
        if (session == null) return;
        if (command(session)) Toasts.Show(doneMessage);
        RefreshUiState();
        Canvas.Focus();
    }
}
