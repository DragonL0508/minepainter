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
    // ---- 向量（文字）工具選項 ----

    private string[] _fontFamilies = [];
    private bool _suppressVectorEvents;
    private VectorElement? _textEditStart;

    private void InitVectorOptions()
    {
        // 讀取本機安裝字體；下拉清單以各字型自己的字面顯示（paint.net 式預覽）
        _fontFamilies = Services.FontCatalog.Families;
        FontFamilyCombo.ItemTemplate = Services.FontCatalog.FamilyItemTemplate(150);
        FontFamilyCombo.SelectionBoxItemTemplate = Services.FontCatalog.SelectionBoxTemplate();
        foreach (var f in _fontFamilies) FontFamilyCombo.Items.Add(f);

        // 預設微軟正黑；英文版 Windows 常常沒裝中文字型，退到內嵌的 Noto Sans TC
        var defaultIdx = Array.IndexOf(_fontFamilies, "Microsoft JhengHei");
        if (defaultIdx < 0) defaultIdx = Array.IndexOf(_fontFamilies, Services.EmbeddedFonts.FamilyName);
        FontFamilyCombo.SelectedIndex = defaultIdx >= 0 ? defaultIdx : 0;
        if (FontFamilyCombo.SelectedItem is string picked && Canvas.Session is { } textSession)
            textSession.Text.FontFamily = picked;
        RepopulateFontStyles(FontFamilyCombo.SelectedItem as string ?? "", 400);
        // 程式跑著時裝了新字型：清單重填、選取維持原字型（使用者 2026-09-07：不用重開就要讀得到新字體）
        Services.FontCatalog.Changed += () =>
        {
            var keep = FontFamilyCombo.SelectedItem as string;
            _suppressVectorEvents = true;
            _fontFamilies = Services.FontCatalog.Families;
            FontFamilyCombo.Items.Clear();
            foreach (var f in _fontFamilies) FontFamilyCombo.Items.Add(f);
            var index = keep == null ? -1 : Array.IndexOf(_fontFamilies, keep);
            FontFamilyCombo.SelectedIndex = index >= 0 ? index : 0;
            _suppressVectorEvents = false;
        };

        foreach (var k in new[] { "矩形", "橢圓", "直線" }) ShapeKindCombo.Items.Add(k);
        ShapeKindCombo.SelectedIndex = 0;

        FontSizeBox.Value = 48;
        FontSizeBox.ValueChanged += v =>
        {
            if (_suppressVectorEvents) return;
            if (Canvas.Session is { } s) s.Text.FontSize = (float)v;
            ApplyTextEdit(el => el.WithFontSize((float)v));
            CommitTextEdit();
            UpdateCanvasEditBoxStyle();
        };
        LetterSpacingBox.Value = 0;
        LetterSpacingBox.ValueChanged += v =>
        {
            if (_suppressVectorEvents) return;
            if (Canvas.Session is { } s) s.Text.LetterSpacing = (float)v;
            ApplyTextEdit(el => el with { LetterSpacing = (float)v });
            CommitTextEdit();
            UpdateCanvasEditBoxStyle();
        };
        FontFamilyCombo.SelectionChanged += (_, _) =>
        {
            if (_suppressVectorEvents) return;
            var family = FontFamilyCombo.SelectedItem as string;
            if (family == null) return;
            // 換家族時重列可用字重，並落在最接近目前字重的一檔
            var currentWeight = SelectedText?.Element.FontWeight ?? Canvas.Session?.Text.FontWeight ?? 400;
            PerfTrace.Begin();
            RepopulateFontStyles(family, currentWeight);
            PerfTrace.Lap("styles");
            var weight = SelectedFontWeight();
            if (Canvas.Session is { } s)
            {
                s.Text.FontFamily = family;
                s.Text.FontWeight = weight;
            }
            ApplyTextEdit(el => el with { FontFamily = family, FontWeight = weight });
            PerfTrace.Lap("applyEdit");
            CommitTextEdit();
            PerfTrace.Lap("commit");
            UpdateCanvasEditBoxStyle();
            PerfTrace.Lap("editBox");
            PerfTrace.End("fontSwitch");
        };
        FontStyleCombo.SelectionChanged += (_, _) =>
        {
            if (_suppressVectorEvents) return;
            var weight = SelectedFontWeight();
            if (Canvas.Session is { } s) s.Text.FontWeight = weight;
            ApplyTextEdit(el => el with { FontWeight = weight });
            CommitTextEdit();
            UpdateCanvasEditBoxStyle();
        };

        BoldToggle.IsCheckedChanged += (_, _) => OnTextStyleToggled();
        ItalicToggle.IsCheckedChanged += (_, _) => OnTextStyleToggled();
        UnderlineToggle.IsCheckedChanged += (_, _) => OnTextStyleToggled();
        StrikeToggle.IsCheckedChanged += (_, _) => OnTextStyleToggled();
        WireAlignToggle(AlignLeftToggle, TextAlign.Left);
        WireAlignToggle(AlignCenterToggle, TextAlign.Center);
        WireAlignToggle(AlignRightToggle, TextAlign.Right);

        ShapeKindCombo.SelectionChanged += (_, _) => ApplyShapeOptions();
        ShapeFilledCheck.IsCheckedChanged += (_, _) => ApplyShapeOptions();

        WireTransformToggle(TransformFreeToggle, TransformMode.Free);
        WireTransformToggle(TransformPerspectiveToggle, TransformMode.Perspective);
        WireTransformToggle(TransformWarpToggle, TransformMode.Warp);

        PenSelectButton.Click += (_, _) => PenMakeSelection();
        PenStrokeButton.Click += (_, _) => RunPenCommand(s => PenCommands.StrokePath(s, s.Pen.StrokeWidth), "已沿路徑描邊");
        PenFillButton.Click += (_, _) => RunPenCommand(PenCommands.FillPath, "已填滿路徑");
        PenClearButton.Click += (_, _) => RunPenCommand(s => { PenCommands.Clear(s); return true; }, "已清除路徑");
    }

    // ---- 字重／變種（Noto Sans TC 的 Light/Black 這類命名字重）----

    private Services.FontStyleOption[] _fontStyleOptions = [];
    private string? _fontStylesFamily; // 目前清單對應的家族（同家族只移選取，不重建）

    /// <summary>目前字重下拉選中的字重值（清單保證至少一項）。</summary>
    private int SelectedFontWeight()
    {
        var idx = FontStyleCombo.SelectedIndex;
        return idx >= 0 && idx < _fontStyleOptions.Length ? _fontStyleOptions[idx].Weight : 400;
    }

    /// <summary>
    /// 依家族列舉可用的直立字重（斜體交給 I 鈕），並選中最接近 preferredWeight 的一檔。
    /// 只動下拉內容，不觸發套用（呼叫端自行決定要不要套用）。
    /// </summary>
    private void RepopulateFontStyles(string family, int preferredWeight)
    {
        // 同家族：清單內容不變，只把選取移到最接近的字重。
        // 這不只是省事 —— 選字重時 CommitTextEdit → RefreshUiState → Sync 會繞回這裡，
        // 在 FontStyleCombo 自己的 SelectionChanged 裡 Items.Clear() 重建會直接 crash（重入）。
        if (family == _fontStylesFamily && _fontStyleOptions.Length > 0)
        {
            SelectClosestFontStyle(preferredWeight);
            return;
        }
        _fontStylesFamily = family;
        _fontStyleOptions = Services.FontCatalog.StylesFor(family);

        var wasSuppressed = _suppressVectorEvents;
        _suppressVectorEvents = true;
        FontStyleCombo.Items.Clear();
        foreach (var o in _fontStyleOptions) FontStyleCombo.Items.Add(o.Name);
        _suppressVectorEvents = wasSuppressed;
        SelectClosestFontStyle(preferredWeight);
    }

    /// <summary>把字重下拉的選取移到最接近的一檔（不觸發套用）。</summary>
    private void SelectClosestFontStyle(int preferredWeight)
    {
        var best = 0;
        for (var i = 1; i < _fontStyleOptions.Length; i++)
        {
            if (Math.Abs(_fontStyleOptions[i].Weight - preferredWeight) <
                Math.Abs(_fontStyleOptions[best].Weight - preferredWeight))
            {
                best = i;
            }
        }
        if (FontStyleCombo.SelectedIndex == best) return; // 同值不觸碰（重入時是 no-op）
        var wasSuppressed = _suppressVectorEvents;
        _suppressVectorEvents = true;
        FontStyleCombo.SelectedIndex = best;
        _suppressVectorEvents = wasSuppressed;
    }

    /// <summary>
    /// 某些字型 Avalonia 建不出 GlyphTypeface（變數字型集合、名稱含 # 等），直接指定
    /// FontFamily 會在排版時 crash —— 先探測，失敗就退回預設字面（Skia 渲染端自己會 fallback）。
    /// </summary>
    private static FontFamily SafeFontFamily(string name) => Services.FontCatalog.SafeFontFamily(name);

    /// <summary>B/I/U/S 任一顆變動：同步工具預設值 + 套到選中元素。</summary>
    private void OnTextStyleToggled()
    {
        if (_suppressVectorEvents) return;
        var bold = BoldToggle.IsChecked == true;
        var italic = ItalicToggle.IsChecked == true;
        var underline = UnderlineToggle.IsChecked == true;
        var strike = StrikeToggle.IsChecked == true;
        if (Canvas.Session is { } s)
        {
            s.Text.Bold = bold;
            s.Text.Italic = italic;
            s.Text.Underline = underline;
            s.Text.Strikethrough = strike;
        }
        ApplyTextEdit(el => el with
        {
            Bold = bold, Italic = italic, Underline = underline, Strikethrough = strike,
        });
        CommitTextEdit();
        UpdateCanvasEditBoxStyle();
    }

    /// <summary>對齊三顆是單選群：點下切換過去，點已選中的維持不變。</summary>
    private void WireAlignToggle(ToggleButton button, TextAlign align)
    {
        button.IsCheckedChanged += (_, _) =>
        {
            if (_suppressVectorEvents) return;
            if (button.IsChecked == true)
            {
                SetAlignment(align);
            }
            else if (AlignLeftToggle.IsChecked != true &&
                     AlignCenterToggle.IsChecked != true &&
                     AlignRightToggle.IsChecked != true)
            {
                _suppressVectorEvents = true;
                button.IsChecked = true;
                _suppressVectorEvents = false;
            }
        };
    }

    private void SetAlignment(TextAlign align)
    {
        _suppressVectorEvents = true;
        AlignLeftToggle.IsChecked = align == TextAlign.Left;
        AlignCenterToggle.IsChecked = align == TextAlign.Center;
        AlignRightToggle.IsChecked = align == TextAlign.Right;
        _suppressVectorEvents = false;

        if (Canvas.Session is { } s) s.Text.Alignment = align;
        ApplyTextEdit(el => el with { Alignment = align });
        CommitTextEdit();
        UpdateCanvasEditBoxStyle();
    }

    /// <summary>取得目前選中的文字元素（layer, element）。</summary>
    private (RasterLayer Layer, TextElement Element)? SelectedText
    {
        get
        {
            var session = Canvas.Session;
            if (session?.SelectedElement is not { } sel) return null;
            if (session.Document.FindLayer(sel.LayerId) is not RasterLayer layer) return null;
            if (layer.FindElement(sel.ElementId) is not TextElement text) return null;
            return (layer, text);
        }
    }

    /// <summary>即時套用文字編輯（不進 history；CommitTextEdit 時一次補）。</summary>
    private void ApplyTextEdit(Func<TextElement, TextElement> transform)
    {
        if (_suppressVectorEvents) return;
        var session = Canvas.Session;
        if (session == null || SelectedText is not { } sel) return;

        // 畫布內編輯期間（新建或既有都一樣）：所有改動由 CommitCanvasTextEdit 一次落地成一步，
        // 不另記步驟 —— 文字內容現在是逐鍵即時寫進圖層的，中途插一步樣式 undo 會把半打好的字捲進去
        var editingCanvas = _canvasEditBox != null && _canvasEditElement?.Id == sel.Element.Id;
        if (!editingCanvas) _textEditStart ??= sel.Element;
        var updated = transform(sel.Element);
        if (Equals(updated, sel.Element)) return;

        lock (session.Document.SyncRoot)
        {
            sel.Layer.ReplaceElement(updated);
        }
        session.RefreshSelectionHandles(); // 物件的邊界變了，框要重算
    }

    /// <summary>
    /// 選色時若正在編輯或選著文字，就把顏色套到那段文字上（paint.net 式：文字工具跟著主色走）。
    /// 沒選著文字時什麼都不做 —— 主色照常更新，供之後新建的文字使用。
    /// </summary>
    private void ApplyTextColor(SKColor color)
    {
        if (SelectedText is not { } sel || sel.Element.Color == color) return;
        // 漸層的起點色跟著主色走（進階視窗的規則也是「起點＝填色」），選色才看得到變化
        ApplyTextEdit(el => el with
        {
            Color = color,
            Gradient = el.Gradient is { } g ? g with { Start = color } : null,
        }); // 落地成 undo 步驟的時機在 ColorCommitted
        UpdateCanvasEditBoxStyle(); // 畫布內編輯框的字色跟著換
    }

    /// <summary>
    /// 工具列字型下拉顯示指定家族。沒裝的字型（開別人的檔）清單裡選不到 ——
    /// 清掉選取、用 placeholder 顯示名字，別讓它停在上一個不相干的字型上。
    /// </summary>
    private void ShowFontFamily(string family)
    {
        var fi = Array.IndexOf(_fontFamilies, family);
        if (FontFamilyCombo.SelectedIndex != fi) FontFamilyCombo.SelectedIndex = fi;
        FontFamilyCombo.PlaceholderText = family;
        FontFamilyCombo.PlaceholderForeground = AppTheme.TextBrush;
    }

    private void CommitTextEdit()
    {
        var session = Canvas.Session;
        if (session == null || _textEditStart == null) return;
        var start = _textEditStart;
        _textEditStart = null;
        if (SelectedText is not { } sel || sel.Element.Id != start.Id) return;

        VectorCommands.ReplaceElement(session.Document, session.History, sel.Layer, start, sel.Element, "編輯文字");
        RefreshUiState();
    }

    // ---- 選取框旁的小按鈕（進階文字設定／角度重置） ----

    private Border _frameActions = null!;
    private Button _frameResetButton = null!;
    private Button _frameSplitButton = null!;
    private Rect _frameActionsLast = default;

    /// <summary>
    /// 疊在畫布上、跟著把手框走的一小條按鈕：選著文字時有「進階文字設定」，
    /// 任何有框的情況都有「角度重置」（框住的東西沒有角度可重設時變灰）。
    /// 放在 EditHost（與畫布內文字編輯框同一層）；按鈕不可聚焦，點了不會把焦點從編輯框搶走。
    /// </summary>
    private void BuildFrameActions()
    {
        _frameResetButton = FrameActionButton(MaterialIconKind.Restore, "重置角度與比例（轉回 0°、回到原始比例）");
        _frameResetButton.Click += (_, _) => ResetFrameTransform();
        // 只在「編輯文字且框選了一段」時出現：把那一段拆成獨立的文字圖層（前／中／後收進群組）各自改樣式
        _frameSplitButton = FrameActionButton(MaterialIconKind.ContentCut, "分離選取的文字成獨立圖層（前／中／後收進一個群組，位置不變）");
        _frameSplitButton.IsVisible = false;
        _frameSplitButton.Click += (_, _) => SplitSelectedText();

        _frameActions = new Border
        {
            Background = AppTheme.PanelBrush,
            BorderBrush = AppTheme.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(2),
            IsVisible = false,
            Child = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 1,
                Children = { _frameResetButton, _frameSplitButton },
            },
        };
        EditHost.Children.Add(_frameActions);

        static Button FrameActionButton(MaterialIconKind icon, string tip)
        {
            var b = new Button
            {
                Content = new MaterialIcon { Kind = icon, Width = 15, Height = 15 },
                Width = 26,
                Height = 24,
                Padding = new Thickness(0),
                Focusable = false, // 不搶焦點：畫布內編輯框的 LostFocus 會落地編輯
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            ToolTip.SetTip(b, tip);
            return b;
        }
    }

    /// <summary>每幀對位：把手框的右上角外側；框在畫面外時夾回可視範圍，按鈕永遠按得到。</summary>
    private void UpdateFrameActions()
    {
        var session = Canvas.Session;
        if (session?.SelectionHandles is not { } frame || Canvas.Bounds.Width <= 0)
        {
            Motion.SetVisible(_frameActions, false);
            return;
        }

        var hasText = SelectedText != null;
        _frameResetButton.IsEnabled = session.CanResetTransform;
        _frameResetButton.Opacity = _frameResetButton.IsEnabled ? 1.0 : 0.4;
        _frameSplitButton.IsVisible = _canvasEditBox is { } editBox && editBox.SelectionStart != editBox.SelectionEnd;

        // 框可能整個旋轉（變形 session）：取四個角旋轉後的外接矩形
        var deg = session.SelectionHandlesRotation;
        Span<SKPoint> corners =
        [
            new(frame.Left, frame.Top), new(frame.Right, frame.Top),
            new(frame.Right, frame.Bottom), new(frame.Left, frame.Bottom),
        ];
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue;
        foreach (var c in corners)
        {
            var p = Math.Abs(deg) > 0.01f
                ? MoveTool.RotatePoint(c, new SKPoint(frame.MidX, frame.MidY), deg)
                : c;
            var v = Canvas.DocToView(p);
            minX = Math.Min(minX, v.X);
            minY = Math.Min(minY, v.Y);
            maxX = Math.Max(maxX, v.X);
        }

        var w = _frameActions.Bounds.Width > 0 ? _frameActions.Bounds.Width : 60;
        var h = _frameActions.Bounds.Height > 0 ? _frameActions.Bounds.Height : 28;
        var x = Math.Clamp(maxX + 10, 4, Math.Max(4, Canvas.Bounds.Width - w - 4));
        var y = Math.Clamp(minY, 4, Math.Max(4, Canvas.Bounds.Height - h - 4));
        var rect = new Rect(Math.Round(x), Math.Round(y), w, h);
        if (rect != _frameActionsLast)
        {
            _frameActionsLast = rect;
            Avalonia.Controls.Canvas.SetLeft(_frameActions, rect.X);
            Avalonia.Controls.Canvas.SetTop(_frameActions, rect.Y);
        }
        Motion.SetVisible(_frameActions, true);
    }

    /// <summary>
    /// 重置角度與比例。畫布內編輯中的文字走 ApplyTextEdit（摺進那一步「編輯文字」），
    /// 其餘交給 session（文字物件記一步「重設角度與比例」；變形 session 回到原尺寸與 0° 不記步）。
    /// </summary>
    private void ResetFrameTransform()
    {
        var session = Canvas.Session;
        if (session == null) return;

        if (_canvasEditBox != null && SelectedText is { } sel && _canvasEditElement?.Id == sel.Element.Id)
        {
            ApplyTextEdit(el => el.WithTransformReset());
            UpdateCanvasEditBoxStyle(); // 編輯框的旋轉／拉伸跟著解掉
        }
        else
        {
            session.CommitFloating();
            session.ResetTransform();
        }
        RefreshUiState();
    }
}
