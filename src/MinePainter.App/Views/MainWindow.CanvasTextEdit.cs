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
    // ---- 畫布內文字編輯（雙擊文字或文字工具建立後） ----

    private TextBox? _canvasEditBox;
    private RasterLayer? _canvasEditLayer;
    private TextElement? _canvasEditElement;
    private bool _canvasEditIsNew; // 單擊新建、尚未進 history（空內容落地 = 無事發生）
    private bool _canvasEditComposing; // IME 組字中（注音/拼音）：編輯框文字暫時可見

    private void StartCanvasTextEdit(RasterLayer layer, TextElement element, bool isNew)
    {
        CommitCanvasTextEdit();
        var session = Canvas.Session;
        if (session == null) return;

        // 進入文字編輯就切到文字工具（使用者明示）：工具列跟著換成字型／字級那一組，
        // 編輯完也直接停在文字工具上
        if (_currentToolKey != "text") SelectTool("text");

        _canvasEditLayer = layer;
        _canvasEditElement = element;
        _canvasEditIsNew = isNew;

        // 編輯期間不再隱藏原件：文字逐鍵即時寫回圖層、由 Skia 照常算繪（外框/陰影/漸層
        // 打字當下就看得到）；編輯框自己的字改畫透明，只留游標與選取高亮，避免重影。
        var box = new TextBox
        {
            Text = element.Text,
            AcceptsReturn = true,
            BorderThickness = new Thickness(0), // 無外框（paint.net 式，只有游標）
            Padding = new Thickness(2),
            MinWidth = 60,
        };
        SyncCanvasEditBoxTransform(box, element);
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape)
            {
                CommitCanvasTextEdit(cancel: true);
                Canvas.Focus();
                e.Handled = true;
            }
        };
        box.LostFocus += (_, _) => CommitCanvasTextEdit();
        box.TextChanged += (_, _) => LiveApplyCanvasEditText();
        box.Loaded += (_, _) => HookImeComposition(box); // Loaded 後 TextPresenter 才一定在視覺樹裡

        _canvasEditBox = box;
        EditHost.Children.Add(box);
        UpdateCanvasEditBoxStyle(); // 字型/粗斜體/對齊/行高/顏色 + 定位
        box.Focus();
        box.CaretIndex = box.Text?.Length ?? 0; // 新建為空字串（游標即起點）；既有文字接在最後
    }

    /// <summary>
    /// IME 組字（注音/拼音）期間的可見性處理：組字串是 Avalonia 直接畫在編輯框裡的
    /// （不進 Text、不發 TextChanged），而編輯框前景平常是透明的 —— 不處理就會「打注音看不到」。
    /// 監聽 TextPresenter.PreeditText：組字中 → 編輯框整段文字切回可見、Skia 那份暫時隱藏
    /// （HiddenElementId，避免重影）；選字落地 → 換回透明前景＋即時算繪。
    /// </summary>
    private void HookImeComposition(TextBox box)
    {
        if (box.FindDescendantOfType<TextPresenter>() is not { } presenter) return;
        presenter.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextPresenter.PreeditTextProperty || _canvasEditBox != box) return;
            SetCanvasEditComposing(!string.IsNullOrEmpty(presenter.PreeditText));
        };
    }

    private void SetCanvasEditComposing(bool composing)
    {
        if (_canvasEditComposing == composing) return;
        _canvasEditComposing = composing;
        var session = Canvas.Session;
        if (session == null || _canvasEditLayer is not { } layer ||
            _canvasEditElement is not { } element)
        {
            return;
        }

        lock (session.Document.SyncRoot)
        {
            layer.HiddenElementId = composing ? element.Id : null;
        }
        UpdateCanvasEditBoxStyle(); // 前景可見性跟著切
    }

    /// <summary>
    /// 逐鍵把編輯框的內容寫回圖層裡的元素（不進 history —— CommitCanvasTextEdit 一次落地）。
    /// 這就是「編輯文字即時渲染」：畫布上看到的是 Skia 算繪的最終樣子（含外框/陰影/漸層）。
    /// </summary>
    private void LiveApplyCanvasEditText()
    {
        var session = Canvas.Session;
        if (session == null || _canvasEditBox is not { } box) return;
        if (_canvasEditLayer is not { } layer || CurrentCanvasEditElement() is not { } current) return;

        var text = box.Text ?? "";
        if (text == current.Text) return;
        lock (session.Document.SyncRoot)
        {
            layer.ReplaceElement(current with { Text = text });
        }
        session.RefreshSelectionHandles(); // 邊界跟著內容長
    }

    /// <summary>
    /// 讓編輯框跟上元素目前的樣式（開啟時與編輯期間工具列改樣式時都會呼叫）。
    /// 以圖層中的現行實例為準 —— 工具列的改動是即時 ReplaceElement 進圖層的。
    /// </summary>
    private void UpdateCanvasEditBoxStyle()
    {
        if (_canvasEditBox is not { } box) return;
        var current = CurrentCanvasEditElement();
        if (current == null) return;

        var family = SafeFontFamily(current.FontFamily);
        box.FontFamily = family;
        var weight = (FontWeight)Math.Clamp(
            current.Bold ? Math.Max(700, current.FontWeight) : current.FontWeight, 100, 950);
        // 探測該字重建不建得出 GlyphTypeface —— 建不出就退回一般字重，別讓排版時才炸
        try
        {
            _ = new Typeface(family, FontStyle.Normal, weight).GlyphTypeface;
            box.FontWeight = weight;
        }
        catch
        {
            box.FontWeight = FontWeight.Normal;
        }
        box.FontStyle = current.Italic ? FontStyle.Italic : FontStyle.Normal;
        box.TextAlignment = current.Alignment switch
        {
            Core.Vectors.TextAlign.Center => TextAlignment.Center,
            Core.Vectors.TextAlign.Right => TextAlignment.Right,
            _ => TextAlignment.Left,
        };
        StyleCanvasEditBox(box, current.Color, _canvasEditComposing);
        RepositionCanvasTextEdit();
    }

    /// <summary>
    /// 文字被水平拉寬/拉窄或旋轉過 → 編輯框跟著變形，維持所見即所得。
    /// 編輯期間角度可能變（角度重置鈕），所以每次重新定位都同步一次。
    /// </summary>
    private static void SyncCanvasEditBoxTransform(TextBox box, TextElement element)
    {
        var scaled = Math.Abs(element.ScaleX - 1f) > 0.001f;
        var rotated = Math.Abs(element.Rotation) > 0.01f;
        if (!scaled && !rotated)
        {
            if (box.RenderTransform != null) box.RenderTransform = null;
            return;
        }
        var transforms = new TransformGroup();
        if (scaled) transforms.Children.Add(new ScaleTransform(element.ScaleX, 1));
        if (rotated) transforms.Children.Add(new RotateTransform(element.Rotation));
        box.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative);
        box.RenderTransform = transforms;
    }

    /// <summary>編輯中元素在圖層裡的現行實例（工具列改動會即時替換，Id 不變）。</summary>
    private TextElement? CurrentCanvasEditElement() =>
        _canvasEditElement != null
            ? _canvasEditLayer?.FindElement(_canvasEditElement.Id) as TextElement
            : null;

    /// <summary>
    /// 「最終的文字本身」由 Skia 即時算繪在畫布上（含效果），編輯框只負責游標、選取高亮
    /// 與鍵盤輸入 —— 自己的字畫成透明，避免和 Skia 的算繪重影（paint.net 式，無底色無外框）。
    /// Fluent 的 TextBox 樣板自帶深色底與框線，必須逐一覆蓋主題資源才蓋得掉。
    /// </summary>
    private static void StyleCanvasEditBox(TextBox box, SKColor textColor, bool showOwnText)
    {
        // 平常透明（字由 Skia 即時算繪）；IME 組字期間切回可見，組字串才看得到
        IBrush fgBrush = showOwnText
            ? new SolidColorBrush(Color.FromRgb(textColor.Red, textColor.Green, textColor.Blue))
            : Brushes.Transparent;
        // 游標要看得見：用字色但至少半不透明（字色全透明時游標不能跟著消失）
        var caretBrush = new SolidColorBrush(Color.FromArgb(
            Math.Max((byte)0xB0, textColor.Alpha), textColor.Red, textColor.Green, textColor.Blue));
        var accent = Color.FromRgb(0x2A, 0x9D, 0xF4);

        box.Foreground = fgBrush;
        box.Background = Brushes.Transparent; // Transparent（非 null）才吃得到點擊
        box.BorderBrush = Brushes.Transparent;
        box.CaretBrush = caretBrush;
        box.SelectionBrush = new SolidColorBrush(Color.FromArgb(0x60, accent.R, accent.G, accent.B));
        box.SelectionForegroundBrush = fgBrush;

        // Fluent TextBox 樣板實際使用的資源鍵
        foreach (var key in new[]
                 {
                     "TextControlBackground", "TextControlBackgroundPointerOver",
                     "TextControlBackgroundFocused", "TextControlBackgroundDisabled",
                     "TextControlBorderBrush", "TextControlBorderBrushPointerOver",
                     "TextControlBorderBrushFocused", "TextControlBorderBrushDisabled",
                 })
        {
            box.Resources[key] = Brushes.Transparent;
        }

        foreach (var key in new[]
                 {
                     "TextControlForeground", "TextControlForegroundPointerOver",
                     "TextControlForegroundFocused", "TextControlForegroundDisabled",
                 })
        {
            box.Resources[key] = fgBrush;
        }

        box.Resources["TextControlSelectionHighlightColor"] = new SolidColorBrush(accent);
    }

    private void RepositionCanvasTextEdit()
    {
        if (_canvasEditBox == null || _canvasEditElement == null) return;
        var current = CurrentCanvasEditElement() ?? _canvasEditElement;
        var view = Canvas.DocToView(current.Position);
        // 補償 Padding(2)，讓框內文字的左上角對齊元素的 Position
        Avalonia.Controls.Canvas.SetLeft(_canvasEditBox, view.X - 2);
        Avalonia.Controls.Canvas.SetTop(_canvasEditBox, view.Y - 2);
        var fontSize = Math.Max(8, current.FontSize * Canvas.Scale);
        _canvasEditBox.FontSize = fontSize;
        SyncCanvasEditBoxTransform(_canvasEditBox, current);
        // 與 TextElement 同一套排版參數（行高倍率/字距），游標位置才對得上 Skia 的算繪
        _canvasEditBox.LineHeight = fontSize * current.LineHeightScale;
        _canvasEditBox.LetterSpacing = current.LetterSpacing * Canvas.Scale;
    }

    /// <summary>畫布內文字編輯的 <see cref="IPendingEdit"/> 包裝（見該介面的說明）。</summary>
    private sealed class CanvasTextPendingEdit(MainWindow owner) : IPendingEdit
    {
        public bool IsActive => owner._canvasEditBox != null;
        public void Commit() => owner.CommitCanvasTextEdit();
    }

    private void CommitCanvasTextEdit(bool cancel = false)
    {
        var box = _canvasEditBox;
        var layer = _canvasEditLayer;
        var original = _canvasEditElement;
        var isNew = _canvasEditIsNew;
        _canvasEditBox = null;
        _canvasEditLayer = null;
        _canvasEditElement = null;
        _canvasEditIsNew = false;
        _canvasEditComposing = false;
        if (box == null || layer == null || original == null) return;

        EditHost.Children.Remove(box);
        var session = Canvas.Session;
        if (session == null) return;

        lock (session.Document.SyncRoot)
        {
            layer.HiddenElementId = null; // 可能在組字中途落地（點到別處），把 Skia 那份放回來
        }

        // 內容是逐鍵即時寫進圖層的（LiveApplyCanvasEditText），樣式改動也是 —— 圖層現行實例
        // 就是「編輯後的最終樣子」，這裡只負責一次補成單一步 undo（或取消時整個還原）。
        TextElement? current;
        lock (session.Document.SyncRoot)
        {
            current = layer.FindElement(original.Id) as TextElement;
        }
        if (current == null) return; // 元素已不存在（例如編輯中被 undo 收走）

        var newText = box.Text ?? "";
        var sameDoc = layer.Document == session.Document;
        var final = current with { Text = newText };

        if (isNew)
        {
            // 單擊建立、尚未進 history：空內容/取消 → 靜默移除（誤觸不留痕跡）；
            // 有內容 → 補單一步「新增文字」（undo 一次收掉整個元素）。
            if (cancel || newText.Length == 0 || !sameDoc)
            {
                if (session.SelectedElement?.ElementId == current.Id) session.SelectedElement = null;
                VectorCommands.DiscardElement(layer.Document ?? session.Document, layer, current.Id);
                // 文字一定自己一層：空的文字圖層一起收掉（不留痕跡）
                if (!layer.HasElements && layer.Surface.TileCount == 0 && sameDoc)
                    VectorCommands.DiscardNewTextLayer(session.Document, layer);
            }
            else
            {
                VectorCommands.CommitNewTextLayer(session.Document, session.History, layer, final, "新增文字");
                session.RefreshSelectionHandles();
            }
            _layersContent.Refresh();
        }
        else if (cancel || !sameDoc)
        {
            // Esc = 無損還原：把編輯前的原件放回去（內容與編輯期間的樣式改動一起退掉）
            lock (session.Document.SyncRoot)
            {
                if (!Equals(current, original)) layer.ReplaceElement(original);
            }
            session.RefreshSelectionHandles();
        }
        else if (newText.Length == 0)
        {
            // 內容清空 = 刪除文字（不留看不見的空元素）；undo 要能把「編輯前」的原件找回來
            VectorCommands.RemoveElement(session.Document, session.History, layer, original, "刪除文字");
            if (session.SelectedElement?.ElementId == original.Id) session.SelectedElement = null;
        }
        else if (!Equals(final, original))
        {
            // 與「編輯前」比對 —— 內容或編輯期間的樣式改動合成一步「編輯文字」
            VectorCommands.ReplaceElement(session.Document, session.History, layer,
                original, final, "編輯文字");
            session.RefreshSelectionHandles(); // 文字內容變了，框跟著變
        }
        RefreshUiState();
    }

    /// <summary>選取變化時把選中文字元素的屬性帶回 UI。</summary>
    private void SyncVectorOptionsFromSelection()
    {
        if (SelectedText is not { } sel) return;
        _suppressVectorEvents = true;
        FontSizeBox.Value = sel.Element.FontSize;
        LetterSpacingBox.Value = sel.Element.LetterSpacing;
        ShowFontFamily(sel.Element.FontFamily);
        RepopulateFontStyles(sel.Element.FontFamily, sel.Element.FontWeight);
        BoldToggle.IsChecked = sel.Element.Bold;
        ItalicToggle.IsChecked = sel.Element.Italic;
        UnderlineToggle.IsChecked = sel.Element.Underline;
        StrikeToggle.IsChecked = sel.Element.Strikethrough;
        AlignLeftToggle.IsChecked = sel.Element.Alignment == Core.Vectors.TextAlign.Left;
        AlignCenterToggle.IsChecked = sel.Element.Alignment == Core.Vectors.TextAlign.Center;
        AlignRightToggle.IsChecked = sel.Element.Alignment == Core.Vectors.TextAlign.Right;
        _suppressVectorEvents = false;
    }
}
