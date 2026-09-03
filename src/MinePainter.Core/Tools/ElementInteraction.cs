using MinePainter.Core.Documents;
using MinePainter.Core.History;
using MinePainter.Core.Layers;
using MinePainter.Core.Vectors;
using SkiaSharp;

namespace MinePainter.Core.Tools;

/// <summary>物件命中測試（雙擊編輯等「不限工具」的互動用）。</summary>
public static class VectorHitTest
{
    /// <summary>
    /// 在「作用中圖層」上找命中的文字物件。
    /// 物件屬於它所在的圖層 —— 沒選到那個圖層就選不到、編輯不到（paint.net 式圖層邏輯）。
    /// 在 SyncRoot 內呼叫。
    /// </summary>
    public static (RasterLayer Layer, TextElement Element)? FindTextAt(Document doc, SKPoint p)
    {
        if (doc.ActiveLayer is not RasterLayer { IsVisible: true } layer) return null;
        return layer.HitTest(p) is TextElement text ? (layer, text) : null;
    }
}

/// <summary>
/// 選中向量元素的直接操作（不限目前工具）：拖角把手縮放（文字 = 縮放字級）、拖曳移動。
/// 拖曳期間即時 Replace（不進 history），End 時補一筆 entry。
/// </summary>
public sealed class ElementDragHelper
{
    private enum Mode
    {
        None,
        Move,
        Resize,
        Rotate,
    }

    private Mode _mode;
    private RasterLayer? _layer;
    private VectorElement? _original;
    private SKPoint _dragStart;
    private SKPoint _moveDelta;   // move 模式：目前位移（覆疊圖在挪，原件還沒動）
    private SKPoint _anchor;      // resize 時固定不動的對角
    private enum ResizeAxis { Both, Vertical, Horizontal }
    private ResizeAxis _resizeAxis; // 邊把手只動一軸
    private float _origHeight;
    private float _origWidth;
    private float _framePad;      // 使用者看到的框比排版框多出的效果外擴量（每邊）
    private SKPoint _rotateCenter;   // rotate 時的軸心（起始框中心，整趟固定）
    private VectorElement? _preview; // 手勢中算好、但還沒套到原件上的樣子（覆疊路徑；End 時才套）
    private float _rotateAnchorDeg;  // 右鍵按下時指標相對軸心的角度

    public bool IsActive => _mode != Mode.None;

    /// <summary>
    /// 嘗試從把手（四角）或元素內部開始拖曳。
    /// handleTolerance 為 doc 像素；allowInsideMove = false 時只攔截把手。
    /// </summary>
    public bool TryBegin(EditorSession session, SKPoint p, float handleTolerance, bool allowInsideMove)
    {
        var doc = session.Document;
        lock (doc.SyncRoot)
        {
            if (session.SelectedElement is not { } sel) return false;
            if (doc.FindLayer(sel.LayerId) is not RasterLayer layer) return false;
            if (!ReferenceEquals(layer, doc.ActiveLayer)) return false; // 只操作作用中圖層的物件
            if (layer.FindElement(sel.ElementId) is not { } element) return false;

            var b = element.FrameBounds;
            // 把手畫在使用者看到的框（含效果外擴）上，命中也要對同一個框
            var shown = HandleDragController.ElementFrame(layer, element);
            var handles = MoveTool.HandlePoints(shown);
            var hit = MoveTool.HitCorner(shown, p, handleTolerance);
            if (hit >= 0)
            {
                _framePad = HandleDragController.ElementEffectPad(layer);
                _mode = Mode.Resize;
                _layer = layer;
                _original = element;
                _preview = null;
                _dragStart = p;
                // 角：對角固定；邊：對邊中點固定、只動一軸
                _anchor = MoveTool.IsEdgeHandle(hit) ? handles[4 + (hit - 4 + 2) % 4] : handles[(hit + 2) % 4];
                _resizeAxis = hit switch { 4 or 6 => ResizeAxis.Vertical, 5 or 7 => ResizeAxis.Horizontal, _ => ResizeAxis.Both };
                _origHeight = Math.Max(1, b.Height);
                _origWidth = Math.Max(1, b.Width);
                // 手勢期間只縮覆疊圖（同旋轉）
                session.BeginElementOverlayLocked(layer, element);
                return true;
            }

            if (allowInsideMove && element.HitTest(p))
            {
                BeginMoveLocked(session, layer, element, p);
                return true;
            }
        }
        return false;
    }

    /// <summary>直接開始移動指定元素（MoveTool 點中元素時用）。在 SyncRoot 內呼叫。</summary>
    public void BeginMoveLocked(EditorSession session, RasterLayer layer, VectorElement element, SKPoint p)
    {
        _mode = Mode.Move;
        _layer = layer;
        _original = element;
        _dragStart = p;
        _moveDelta = SKPoint.Empty;
        session.BeginSnapDrag(element.Id); // 對齊參考：畫布與其他物件，但不含自己
        SetSelected(session, layer, element);
        // 拖曳期間用覆疊圖代替原件：不重排版、不逐格重畫（文字帶外框／陰影時每步重畫很貴）
        session.BeginElementOverlayLocked(layer, element);
    }

    /// <summary>
    /// 右鍵拖曳＝旋轉選中的文字物件（文字工具/移動工具皆可；與變形框的旋轉手勢同一套習慣）。
    /// 以「使用者看到的框」中心為軸；Shift 吸附 15°。只有文字支援旋轉參數。
    /// </summary>
    public bool TryBeginRotate(EditorSession session, SKPoint p)
    {
        var doc = session.Document;
        lock (doc.SyncRoot)
        {
            if (session.SelectedElement is not { } sel) return false;
            if (doc.FindLayer(sel.LayerId) is not RasterLayer layer) return false;
            if (!ReferenceEquals(layer, doc.ActiveLayer)) return false;
            if (layer.FindElement(sel.ElementId) is not TextElement element) return false;

            _mode = Mode.Rotate;
            _layer = layer;
            _original = element;
            _preview = null;
            var frame = element.FrameBounds;
            _rotateCenter = new SKPoint(frame.MidX, frame.MidY);
            _rotateAnchorDeg = AngleDeg(p, _rotateCenter);
            // 手勢期間只轉覆疊圖：帶外框／陰影的文字每步重算效果堆疊在 4K 要 0.26 秒
            session.BeginElementOverlayLocked(layer, element);
            return true;
        }
    }

    public void ContinueRotate(EditorSession session, SKPoint p,
        ToolModifiers modifiers = ToolModifiers.None)
    {
        if (_mode != Mode.Rotate || _layer == null || _original is not TextElement original) return;

        var target = original.Rotation + AngleDeg(p, _rotateCenter) - _rotateAnchorDeg;
        if (modifiers.HasFlag(ToolModifiers.Shift))
            target = MathF.Round(target / 15f) * 15f;
        target = NormalizeDeg(target);

        // 一律從起始快照換算（軸心固定），不逐步累積誤差
        var delta = target - original.Rotation;
        var updated = Math.Abs(delta) < 0.005f
            ? original
            : (TextElement)original.TransformedBy(
                SKMatrix.CreateRotationDegrees(delta, _rotateCenter.X, _rotateCenter.Y),
                1f, 1f, delta);
        _preview = updated;

        // 覆疊中：只轉那張圖，原件放開才改（見 End）
        if (session.ElementOverlay is { } overlay && overlay.ElementId == original.Id)
        {
            session.RotateElementOverlay(delta);
            return;
        }

        lock (session.Document.SyncRoot)
        {
            if (!ReferenceEquals(_layer.FindElement(updated.Id), updated))
                _layer.ReplaceElement(updated);
        }
        SetSelected(session, _layer, updated);
    }

    private static float AngleDeg(SKPoint p, SKPoint center) =>
        MathF.Atan2(p.Y - center.Y, p.X - center.X) * 180f / MathF.PI;

    private static float NormalizeDeg(float deg)
    {
        deg %= 360f;
        if (deg > 180f) deg -= 360f;
        if (deg < -180f) deg += 360f;
        return deg;
    }

    public void Continue(EditorSession session, SKPoint p, ToolModifiers modifiers = ToolModifiers.None)
    {
        if (_layer == null || _original == null) return;
        var doc = session.Document;

        VectorElement updated;
        switch (_mode)
        {
            case Mode.Move:
            {
                var dx = p.X - _dragStart.X;
                var dy = p.Y - _dragStart.Y;
                // 對齊模式：對「使用者看到的框」吸附；文字是向量，精確貼齊（不取整）
                (dx, dy) = CanvasSnap.Adjust(session, _original.FrameBounds, dx, dy, wholePixels: false);
                _moveDelta = new SKPoint(dx, dy);
                if (session.ElementOverlay is { } overlay && overlay.ElementId == _original.Id)
                {
                    session.MoveElementOverlay(dx, dy); // 只挪覆疊圖；原件放開時才動
                    return;
                }
                updated = _original.Translated(dx, dy);
                break;
            }

            case Mode.Resize when _original is TextElement text:
            {
                // 垂直 → 字級；水平 → ScaleX（自由拉寬窄）。
                // Shift：以文字「最原始」的比例（ScaleX = 1，字型本來的寬高）等比縮放，
                // 之前拉寬拉窄的變形一併歸零；以拉得比較多的那一軸為準。
                var keepAspect = modifiers.HasFlag(ToolModifiers.Shift);
                // 錨點在含效果外擴的框上；扣掉兩邊外擴才是排版框的新尺寸
                var newHeight = Math.Max(1f, Math.Abs(p.Y - _anchor.Y) - _framePad * 2);
                var newWidth = Math.Max(1f, Math.Abs(p.X - _anchor.X) - _framePad * 2);
                // 邊把手＝單純往那個方向壓扁／拉長：另一軸的尺寸完全不動
                //（上下邊改字級、ScaleX 反向補償讓寬度不變；左右邊只動 ScaleX）
                if (_resizeAxis == ResizeAxis.Vertical) newWidth = _origWidth;
                else if (_resizeAxis == ResizeAxis.Horizontal) newHeight = _origHeight;

                var vScale = _origHeight > 0 ? newHeight / _origHeight : 1f;
                var hScale = _origWidth > 0 ? newWidth / _origWidth : 1f;
                if (keepAspect)
                {
                    // 原始寬度 = 目前框寬 ÷ 目前 ScaleX
                    var originalWidth = text.ScaleX > 0.001f ? _origWidth / text.ScaleX : _origWidth;
                    hScale = originalWidth > 0 ? newWidth / originalWidth : 1f;
                    vScale = Math.Max(vScale, hScale);
                }
                var newSize = Math.Max(1f, text.FontSize * vScale);

                float newScaleX;
                if (keepAspect)
                {
                    newScaleX = 1f;
                }
                else
                {
                    var unscaled = text.UnscaledWidth * (newSize / Math.Max(1f, text.FontSize));
                    newScaleX = unscaled > 0.01f ? Math.Max(0.01f, newWidth / unscaled) : text.ScaleX;
                }

                var resized = text with
                {
                    FontSize = newSize,
                    ScaleX = newScaleX,
                    LetterSpacing = text.LetterSpacing * (newSize / Math.Max(1f, text.FontSize)),
                    BaseFontSize = text.BaseFontSize ?? text.FontSize, // 記住縮放前的字級，重設時退回
                };

                // 固定對角：量測新框後平移 Position（用使用者看到的框，錨點才是把手位置）
                var b0 = _original.FrameBounds;
                var b1 = resized.FrameBounds;
                var anchorIsLeft = Math.Abs(_anchor.X - b0.Left) < Math.Abs(_anchor.X - b0.Right);
                var anchorIsTop = Math.Abs(_anchor.Y - b0.Top) < Math.Abs(_anchor.Y - b0.Bottom);
                var dx = anchorIsLeft ? b0.Left - b1.Left : b0.Right - b1.Right;
                var dy = anchorIsTop ? b0.Top - b1.Top : b0.Bottom - b1.Bottom;
                // 邊把手：沒被拉的那一軸以中心對齊（錨點在邊的中點）
                if (_resizeAxis == ResizeAxis.Vertical) dx = b0.MidX - b1.MidX;
                else if (_resizeAxis == ResizeAxis.Horizontal) dy = b0.MidY - b1.MidY;
                updated = resized.Translated(dx, dy);
                break;
            }

            default:
                return;
        }

        _preview = updated;

        // 覆疊中：只變換那張圖，原件放開才改（見 End）
        if (session.ElementOverlay is { } scaleOverlay && scaleOverlay.ElementId == _original.Id)
        {
            session.ScaleElementOverlay(_original.FrameBounds, updated.FrameBounds);
            return;
        }

        lock (doc.SyncRoot)
        {
            _layer.ReplaceElement(updated);
        }
        SetSelected(session, _layer, updated);
    }

    /// <summary>縮到這個尺寸以下的物件視為捨棄（doc 像素）。</summary>
    public const float MinimumSize = 4f;

    /// <summary>結束拖曳；有實際變更時補 undo entry。縮得太小的物件會被刪除。</summary>
    public void End(EditorSession session)
    {
        session.EndSnapDrag(); // 導線只在拖曳中顯示；參考框快取跟著這趟拖曳結束
        var layer = _layer;
        var original = _original;
        var mode = _mode;
        var preview = _preview;
        _mode = Mode.None;
        _layer = null;
        _original = null;
        _preview = null;
        if (layer == null || original == null) return;

        VectorElement? current;
        lock (session.Document.SyncRoot)
        {
            if (session.ElementOverlay is { } overlay && overlay.ElementId == original.Id)
            {
                // 覆疊手勢：現在才把原件改成最後的樣子；覆疊圖轉殘影蓋到合成器追上
                if (mode == Mode.Move)
                {
                    if (_moveDelta != SKPoint.Empty) layer.ReplaceElement(original.Translated(_moveDelta.X, _moveDelta.Y));
                }
                else if (preview != null && !ReferenceEquals(preview, original))
                {
                    layer.ReplaceElement(preview); // 旋轉／縮放：手勢中只動了覆疊圖
                }
                session.EndElementOverlayLocked();
            }
            current = layer.FindElement(original.Id);
        }
        session.RefreshSelectionHandles();
        if (current == null || Equals(current, original)) return;

        // 太小 → 刪除（單一 undo 步驟即可還原）
        var bounds = current.Bounds;
        if (bounds.Width < MinimumSize || bounds.Height < MinimumSize)
        {
            lock (session.Document.SyncRoot)
            {
                layer.ReplaceElement(original); // 先還原，讓刪除的 undo 回到原始樣貌
            }
            VectorCommands.RemoveElement(session.Document, session.History, layer, original, "刪除過小物件");
            session.SelectedElement = null;
            session.Notify("物件太小，已刪除");
            return;
        }

        VectorCommands.ReplaceElement(session.Document, session.History, layer, original, current,
            mode switch
            {
                Mode.Resize => "調整大小",
                Mode.Rotate => "旋轉文字",
                _ => "移動元素",
            });
    }

    /// <summary>把某元素設為目前選中（把手框由 EditorSession 自動推導）。</summary>
    public static void SetSelected(EditorSession session, RasterLayer layer, VectorElement element) =>
        session.SelectedElement = (layer.Id, element.Id);
}
