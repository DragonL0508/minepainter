using MinePainter.Core.Compositing;
using MinePainter.Core.Documents;
using MinePainter.Core.Effects;
using MinePainter.Core.History;
using MinePainter.Core.Layers;
using MinePainter.Core.Selections;
using MinePainter.Core.Tiles;
using MinePainter.Core.Vectors;
using SkiaSharp;

namespace MinePainter.Core.Tools;


/// <summary>
/// 擁有浮動像素、續接來源與暫定貼上圖層，讓提交／取消維持同一步 Undo。
/// 選取與把手仍由 Session 統一發布；像素鎖與歷史順序沿用原流程。
/// </summary>
internal sealed class FloatingEditor(EditorSession session, Action<FloatingSelection, SKRect, bool> leaveGhost)
{
    private readonly Action<FloatingSelection, SKRect, bool> _leaveGhost = leaveGhost;
    private FloatingSelection? _floating;
    private FloatingResume? _floatingResume;
    private Document Document => session.Document;
    private HistoryManager History => session.History;
    private Compositor Compositor => session.Compositor;
    private SelectionMask? Selection => session.Selection;
    private bool IsFloatingOverlaid => session.IsFloatingOverlaid;
    private void ApplySelection(SelectionMask? selection) => session.ApplySelection(selection);
    private void Notify(string message) => session.Notify(message);
    private void CommitPendingEdits() => session.CommitPendingEdits();

    public FloatingSelection? Floating
    {
        get => _floating;
        private set
        {
            _floating = value;
            session.RefreshSelectionHandles();
        }
    }

    /// <summary>
    /// 浮動內容落地後保留的原始像素：只要 history 頂端還是落地那步、選取還是落地時的那一個，
    /// 再次提起同一塊就改用它（而不是已經重取樣過的圖層像素）。
    /// </summary>
    private sealed class FloatingResume(Guid layerId, IHistoryEntry entry, SelectionMask selection, SKImage pixels)
    {
        public Guid LayerId { get; } = layerId;
        public IHistoryEntry Entry { get; } = entry;
        public SelectionMask Selection { get; } = selection;
        public SKImage Pixels { get; } = pixels;
    }

    private SKImage? TakeFloatingResume(RasterLayer layer, SelectionMask selection)
    {
        var r = _floatingResume;
        if (r == null) return null;
        _floatingResume = null;
        if (r.LayerId == layer.Id && ReferenceEquals(r.Selection, selection) &&
            History.UndoStack.Count > 0 && ReferenceEquals(History.UndoStack[^1], r.Entry))
        {
            return r.Pixels;
        }
        Compositor.Retire(r.Pixels);
        return null;
    }

    /// <summary>history 一動就檢查續接點還有沒有效，沒效就立刻釋放（原始像素可能不小）。</summary>
    public void ReleaseStaleResumes()
    {
        // 變形的原始高清那份掛在圖層上、以像素版本號驗證，不隨 history 起落（見 LayerPixelSource）
        if (_floatingResume is { } f &&
            !(History.UndoStack.Count > 0 && ReferenceEquals(History.UndoStack[^1], f.Entry)))
        {
            _floatingResume = null;
            Compositor.Retire(f.Pixels);
        }
    }

    /// <summary>續接點保留的像素上限（單邊 ≤ 16384 已在提起時擋掉；這裡再限總量，約 128MB）。</summary>
    private const long MaxResumePixels = 32L * 1024 * 1024;

    private static SKImage? CopyImage(SKImage source)
    {
        using var pixmap = source.PeekPixels();
        if (pixmap != null) return SKImage.FromPixelCopy(pixmap);
        using var bitmap = SKBitmap.FromImage(source);
        return bitmap == null ? null : SKImage.FromBitmap(bitmap);
    }

    /// <summary>
    /// 提起目前選取範圍的像素成為浮動內容（可自由移動/縮放）。
    /// 已在浮動中或沒有選取時回傳現有值/null。
    /// </summary>
    public FloatingSelection? LiftSelection()
    {
        if (Floating != null) return Floating;
        if (Selection is not { IsEmpty: false } selection) return null;
        if (Document.ActiveLayer is not RasterLayer layer) return null;

        // 剛落地過縮放且中間沒動別的 → 以落地前的原始像素續接（縮小落地再拉大不糊）
        var original = TakeFloatingResume(layer, selection);
        lock (Document.SyncRoot)
        {
            Floating = FloatingSelection.Lift(layer, selection, originalPixels: original);
        }
        if (Floating == null && original != null) Compositor.Retire(original);
        if (Floating != null) layer.Invalidate(Floating.AffectedBounds);
        return Floating;
    }

    /// <summary>整層內容提起的尺寸上限（單邊）；超過就拒絕，避免配置荒謬大的中繼影像。</summary>
    private const int MaxWholeContentSide = 16384;

    /// <summary>
    /// 把整個圖層內容提起成浮動內容（可移動/縮放）—— GIMP「縮放圖層」的直接操作版。
    /// 圖層可持有畫布外像素（見 DocumentCommands.ResizeCanvas 的註解），
    /// 這是唯一能把它們整批抓回來縮放的操作；入口是移動工具的圖層內容框（拖角）。
    /// 無內容或內容過大時回傳 null。
    /// </summary>
    public FloatingSelection? LiftLayerContent()
    {
        if (Floating != null) return Floating;
        if (Document.ActiveLayer is not RasterLayer layer) return null;

        SKRectI docRect;
        lock (Document.SyncRoot)
        {
            var content = layer.Surface.ExactContentBounds();
            if (content.Width <= 0 || content.Height <= 0) return null;
            docRect = new SKRectI(
                content.Left + layer.Offset.X, content.Top + layer.Offset.Y,
                content.Right + layer.Offset.X, content.Bottom + layer.Offset.Y);
        }
        if (docRect.Width > MaxWholeContentSide || docRect.Height > MaxWholeContentSide)
        {
            Notify("圖層內容過大，無法整批縮放");
            return null;
        }

        using var path = new SKPath();
        path.AddRect(new SKRect(docRect.Left, docRect.Top, docRect.Right, docRect.Bottom));
        // 與貼上同一個原則：浮動期間的幾何允許超出畫布，遮罩涵蓋整個內容矩形
        var mask = SelectionMask.FromPath(path, SKRectI.Union(Document.Bounds, docRect));

        lock (Document.SyncRoot)
        {
            Floating = FloatingSelection.Lift(layer, mask, wholeContent: true);
        }
        if (Floating != null) layer.Invalidate(Floating.AffectedBounds);
        return Floating;
    }

    /// <summary>
    /// 把浮動內容烙回圖層，並記一步 undo。選取框一併落在新位置
    /// （Pinta 的 MoveSelectedTool 也是對選取套用同一個變換）。
    /// </summary>
    public void CommitFloating()
    {
        var floating = Floating;
        if (floating == null) return;
        var overlaid = IsFloatingOverlaid; // 之後 Floating 會被清掉，先問

        // 提起後完全沒動過（例如只是在選取範圍內點了一下）：直接放回去，不記歷史。
        // 否則會留下一步「undo 了卻什麼都沒變」的空步驟 —— 使用者看起來就是 undo 壞掉。
        // 貼上的內容例外：沒動過也要落地（不落地就是把貼的東西丟掉）。
        // Alt 複製又是例外的例外：按著 Alt 點一下沒拖，不該憑空多一個圖層。
        if ((!floating.IsPasted || floating.IsCopy) && floating.TargetRect == new SKRect(
                floating.SourceBounds.Left, floating.SourceBounds.Top,
                floating.SourceBounds.Right, floating.SourceBounds.Bottom))
        {
            CancelFloating();
            return;
        }

        Floating = null;

        if (Document.FindLayer(floating.LayerId) is not RasterLayer layer)
        {
            floating.Dispose();
            return;
        }

        var label = floating.CommitLabel;
        var affected = floating.AffectedBounds;
        var targetRect = floating.TargetRect;
        IHistoryEntry? pixelEntry;
        lock (Document.SyncRoot)
        {
            // 落地後這層的像素「就是」浮動內容（貼到空圖層、或整層內容縮放）且縮小過：
            // 原始那份留成原始高清來源，快速模式輸出時從它重畫而不是拿縮過的放大
            var hiRes = floating.HiResPixels;
            var shrunk = floating.IsScaled
                && targetRect.Width < floating.PixelSize.Width && targetRect.Height < floating.PixelSize.Height;
            var keepSource = (hiRes != null || shrunk)
                && (floating.IsWholeContent || (floating.IsPasted && !floating.BeforeSnapshot.Tiles.Any()))
                && (long)floating.PixelSize.Width * floating.PixelSize.Height <= Documents.ScaleRules.MaxSourcePixels;
            var sourceBefore = layer.ValidPixelSource;
            if (sourceBefore != null) layer.TakePixelSource(); // 留給 undo（StampFloating 會讓它失效）

            StampFloating(layer, floating);

            var layerRect = new SKRectI(
                affected.Left - layer.Offset.X, affected.Top - layer.Offset.Y,
                affected.Right - layer.Offset.X, affected.Bottom - layer.Offset.Y);
            pixelEntry = TileDeltaEntry.Capture(label, layer, floating.BeforeSnapshot, layerRect);

            LayerPixelSource? sourceAfter = null;
            if (keepSource && floating.IsWholeContent && sourceBefore != null)
            {
                // 整層本來就有原圖：串在原圖上（不是拿代理像素當原圖）
                sourceAfter = sourceBefore.Rebased(floating.TransformMatrix, layer.Offset, layer.Offset);
            }
            else if (keepSource && hiRes != null && (long)hiRes.Width * hiRes.Height <= Documents.ScaleRules.MaxSourcePixels)
            {
                // 剪貼簿的原始高清像素：先縮到 SourceBounds（貼上時的代理尺寸）再套浮動變換
                var src = floating.SourceBounds;
                var fit = SKMatrix.CreateScaleTranslation(src.Width / (float)hiRes.Width, src.Height / (float)hiRes.Height,
                    src.Left, src.Top);
                sourceAfter = new LayerPixelSource(floating.DetachHiResPixels()!, new SKRectI(0, 0, hiRes.Width, hiRes.Height),
                    SKMatrix.Concat(floating.TransformMatrix, fit), layer.Offset,
                    targetRect, 0f, new SKSize(hiRes.Width, hiRes.Height), 0);
            }
            else if (keepSource)
            {
                var src = floating.SourceBounds;
                sourceAfter = new LayerPixelSource(floating.DetachPixels(), src, floating.TransformMatrix, layer.Offset,
                    targetRect, 0f, new SKSize(src.Width, src.Height), 0);
            }
            if (sourceAfter != null)
            {
                sourceAfter.Revision = layer.Surface.Revision;
                layer.SetPixelSource(sourceAfter);
            }
            if (pixelEntry != null && (sourceBefore != null || sourceAfter != null))
                pixelEntry = new PixelSourceSwapEntry(pixelEntry, layer, sourceBefore, sourceAfter);
        }

        if (floating.IsWholeContent)
        {
            // 整層內容的縮放不是選取操作：之前沒有選取、之後也不該多出一個
            ApplySelection(null);
            if (pixelEntry != null) History.Push(WithPasteLayer(layer, pixelEntry, label));
        }
        else
        {
            // 選取框跟著落地：這時才柵格化一次（拖曳期間都只變換路徑）。
            // 浮動期間的選取允許超出畫布（貼上「維持畫布大小」）；落地是唯一的裁切點 ——
            // 進 session／history 的選取一律裁回畫布內，超出畫布的選取會讓填色等操作
            // 寫到永遠看不見的像素。
            var oldSelection = floating.SourceSelection;
            var restoredSelection = oldSelection.ClippedTo(Document.Bounds);
            var selectionTarget = floating.TransformMatrix.MapRect(SKRect.Create(
                oldSelection.Bounds.Left, oldSelection.Bounds.Top,
                oldSelection.Bounds.Width, oldSelection.Bounds.Height));
            var newSelection = oldSelection.TransformedTo(selectionTarget, Document.Bounds) ?? restoredSelection;
            ApplySelection(newSelection);

            var selectionEntry = new ActionHistoryEntry("選取範圍", SKRectI.Empty,
                undo: _ => ApplySelection(restoredSelection),
                redo: _ => ApplySelection(newSelection));

            IHistoryEntry entry = pixelEntry != null
                ? new CompositeHistoryEntry(label, pixelEntry, selectionEntry)
                : selectionEntry;
            entry = WithPasteLayer(layer, entry, label);
            History.Push(entry);

            // 縮放過才留續接點（純平移的像素本來就無損）；要在 Push 之後（Push 會清掉舊的）
            if (floating.IsScaled && (long)floating.Pixels.Width * floating.Pixels.Height <= MaxResumePixels &&
                CopyImage(floating.Pixels) is { } copy)
            {
                if (_floatingResume is { } old) Compositor.Retire(old.Pixels);
                _floatingResume = new FloatingResume(layer.Id, entry, newSelection, copy);
            }
        }

        layer.Invalidate(affected);
        _leaveGhost(floating, targetRect, overlaid);
        floating.Dispose();
    }

    /// <summary>放棄浮動內容並還原原本的像素與選取框（Esc）。貼上的內容取消＝直接丟棄。</summary>
    public void CancelFloating()
    {
        var floating = Floating;
        if (floating == null) return;
        var overlaid = IsFloatingOverlaid; // 之後 Floating 會被清掉，先問
        Floating = null;

        if (Document.FindLayer(floating.LayerId) is RasterLayer layer)
        {
            if (!floating.IsPasted)
            {
                lock (Document.SyncRoot)
                {
                    foreach (var (idx, tile) in floating.BeforeSnapshot.Tiles)
                        layer.Surface.RestoreTile(idx, tile);
                }
                // 像素回到原位；合成器追上前先用殘影頂著（貼上的內容是整個丟掉，不必）
                var src = floating.SourceBounds;
                _leaveGhost(floating, new SKRect(src.Left, src.Top, src.Right, src.Bottom), overlaid);
            }
            layer.Invalidate(floating.AffectedBounds); // 貼上也要重繪：浮動預覽要從畫面上消失
            if (floating.IsPasted) DropPasteLayer(layer); // 貼到文字圖層時臨時插入的圖層一起收掉
        }
        // 貼上：原圖層根本沒被動過，只要清掉貼上時建立的選取框。
        // 整層內容：提起前本來就沒有選取，取消後也不該多出一個。
        ApplySelection(floating.IsPasted || floating.IsWholeContent ? null : floating.SourceSelection);
        floating.Dispose();
    }

    /// <summary>貼到文字圖層時臨時插入的新圖層＋它的 history 條目：落地時併進貼上那一步，取消時整個收掉。</summary>
    private (RasterLayer Layer, ActionHistoryEntry Entry)? _pasteLayerEntry;

    /// <summary>浮動內容落地：把「貼上時新增的圖層」那條併進來（同一步 undo）。</summary>
    private IHistoryEntry WithPasteLayer(RasterLayer layer, IHistoryEntry entry, string label)
    {
        if (_pasteLayerEntry is not { } pending || !ReferenceEquals(pending.Layer, layer)) return entry;
        _pasteLayerEntry = null;
        return new CompositeHistoryEntry(label, pending.Entry, entry);
    }

    /// <summary>
    /// 在 <paramref name="anchor"/> 上面一格插一個新圖層，並切過去。
    /// 這個圖層是「暫定的」：浮動內容落地時併進同一步 undo（<see cref="WithPasteLayer"/>），
    /// 取消時整個收掉（<see cref="DropPasteLayer"/>）—— 不會留下一個空圖層。
    /// 貼到文字圖層、Alt 拖曳複製選取像素都走這條。
    /// </summary>
    private RasterLayer InsertPendingLayerAbove(RasterLayer anchor, string name)
    {
        var parent = anchor.Parent ?? Document.Root;
        var index = parent.IndexOf(anchor) + 1;
        var inserted = new RasterLayer { Name = name, Offset = anchor.Offset };
        lock (Document.SyncRoot)
        {
            parent.Insert(index, inserted);
            Document.ActiveLayer = inserted;
        }
        _pasteLayerEntry = (inserted, new ActionHistoryEntry("新增圖層", Document.Bounds,
            undo: d =>
            {
                if (ReferenceEquals(d.ActiveLayer, inserted)) d.ActiveLayer = anchor;
                parent.Remove(inserted);
            },
            redo: _ => parent.Insert(Math.Min(index, parent.Children.Count), inserted),
            onDispose: () =>
            {
                if (inserted.Document == null) inserted.Dispose();
            }));
        return inserted;
    }

    /// <summary>
    /// 移動工具按住 Alt：把選取範圍的像素複製一份到「原圖層上面一格」的新圖層，
    /// 浮動的是那一份 —— 原圖層一個像素都不動（Photoshop 的 Alt 拖曳）。
    /// 新圖層是暫定的：落地時與複製併成同一步 undo，沒拖就取消時整個收掉。
    /// </summary>
    public FloatingSelection? LiftSelectionAsCopy()
    {
        if (Floating != null) return Floating;
        if (Selection is not { IsEmpty: false } selection) return null;
        if (Document.ActiveLayer is not RasterLayer source) return null;
        if (source.IsTextLayer) return null; // 文字圖層沒有像素可複製

        SKImage? pixels;
        lock (Document.SyncRoot) pixels = FloatingSelection.RenderSelected(source, selection);
        if (pixels == null) return null;

        var target = InsertPendingLayerAbove(source, $"{source.Name} 複本");
        lock (Document.SyncRoot)
        {
            Floating = FloatingSelection.CreateCopy(target, pixels, selection.Bounds, selection);
        }
        target.Invalidate(Floating.AffectedBounds);
        Notify($"已複製到新圖層「{target.Name}」");
        return Floating;
    }

    /// <summary>貼上取消：貼上時臨時插入的圖層一起拿掉（沒進 history，直接釋放）。</summary>
    private void DropPasteLayer(RasterLayer layer)
    {
        if (_pasteLayerEntry is not { } pending || !ReferenceEquals(pending.Layer, layer)) return;
        _pasteLayerEntry = null;
        lock (Document.SyncRoot)
        {
            var parent = layer.Parent;
            if (parent == null) return;
            var index = parent.IndexOf(layer);
            parent.Remove(layer);
            if (ReferenceEquals(Document.ActiveLayer, layer) || Document.ActiveLayer == null)
                Document.ActiveLayer = index > 0 && index - 1 < parent.Children.Count ? parent.Children[index - 1] : parent;
        }
        layer.Dispose();
        Document.NotifyChanged(Document.Bounds);
    }

    /// <summary>
    /// 把外部影像貼成浮動內容（貼上）。選取框設為貼上矩形，
    /// 之後移動/縮放/提交都走既有的浮動選取流程。接手 <paramref name="pixels"/> 的擁有權。
    /// 作用中是文字圖層時貼到它上方的新圖層（文字圖層永遠不含像素）。
    /// </summary>
    public bool PasteImage(SKImage pixels, SKPointI position)
    {
        CommitPendingEdits(); // 先落地現有的浮動內容/編輯，貼上才不會蓋在半空中的狀態上

        // 快速模式：貼進來的圖照代理比例縮（同匯入圖層），原始那份留著在落地時當原始高清來源
        SKImage? hiRes = null;
        var (pastedW, pastedH) = PastedSize(pixels.Width, pixels.Height);
        if (pastedW != pixels.Width || pastedH != pixels.Height)
        {
            using var bitmap = SKBitmap.FromImage(pixels);
            using var small = bitmap.Resize(new SKImageInfo(pastedW, pastedH, SKColorType.Bgra8888, SKAlphaType.Premul),
                SKFilterQuality.High);
            if (small != null && SKImage.FromBitmap(small) is { } smallImage)
            {
                hiRes = pixels;
                pixels = smallImage;
            }
        }

        RasterLayer layer;
        switch (Document.ActiveLayer)
        {
            case RasterLayer { IsTextLayer: true } textLayer:
            {
                // 文字圖層不收像素（不變式）：貼到它上方的新圖層，落地時與貼上合成同一步 undo
                layer = InsertPendingLayerAbove(textLayer, "貼上的圖層");
                Notify("文字圖層不能貼上像素，已貼到新圖層");
                break;
            }
            case RasterLayer raster when hiRes != null && raster.Surface.TileCount > 0:
                // 快速模式：原始高清來源代表「整層像素」，貼進已有內容的圖層就留不住原圖 —— 貼到新圖層
                layer = InsertPendingLayerAbove(raster, "貼上的圖層");
                Notify("快速模式：已貼到新圖層，輸出時才能用原始解析度");
                break;
            case RasterLayer raster:
                layer = raster;
                break;
            default:
                Notify("請先選擇一般圖層再貼上");
                pixels.Dispose();
                hiRes?.Dispose();
                return false;
        }

        var bounds = SKRectI.Create(position.X, position.Y, pixels.Width, pixels.Height);
        using var path = new SKPath();
        path.AddRect(new SKRect(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom));
        // 遮罩涵蓋整個貼上矩形 —— 即使超出畫布（「維持畫布大小」）。
        // 螞蟻線、把手框、浮動像素必須是同一個矩形，不然畫面上會出現兩個分開的框
        // （遮罩被裁到畫布、把手框卻是完整影像大小）。paint.net/GIMP 的模型相同：
        // 浮動期間的幾何允許超出畫布，落地（CommitFloating）時才裁回畫布內。
        var mask = SelectionMask.FromPath(path, SKRectI.Union(Document.Bounds, bounds));

        lock (Document.SyncRoot)
        {
            Floating = FloatingSelection.CreatePasted(layer, pixels, bounds, mask, hiRes);
        }
        ApplySelection(mask);
        layer.Invalidate(bounds);
        return true;
    }

    /// <summary>
    /// 貼上一張 width×height 的影像時，它在畫布上會是多大：快速模式照代理比例縮（同匯入圖層），
    /// 不然一張 4K 圖會塞爆 1080p 的畫布。UI 算貼上位置、問「延展畫布」時要用這個尺寸。
    /// </summary>
    public (int Width, int Height) PastedSize(int width, int height)
    {
        var scale = Document.IsFastMode ? 1f / Document.OutputScale : 1f;
        if (scale >= 0.999f) return (width, height);
        return (Math.Max(1, (int)MathF.Round(width * scale)), Math.Max(1, (int)MathF.Round(height * scale)));
    }


    private static void StampFloating(RasterLayer layer, FloatingSelection floating)
    {
        var docRect = SKRectI.Round(floating.TargetRect);
        docRect.Inflate(2, 2);
        var layerRect = new SKRectI(
            docRect.Left - layer.Offset.X, docRect.Top - layer.Offset.Y,
            docRect.Right - layer.Offset.X, docRect.Bottom - layer.Offset.Y);

        foreach (var idx in Tiles.TileIndex.CoveringRect(layerRect))
        {
            var tile = layer.Surface.GetTileForWrite(idx);
            using var surface = SKSurface.Create(Tiles.Tile.Info, tile.Pixels, Tiles.Tile.RowBytes);
            var canvas = surface.Canvas;
            var tileRect = idx.ToPixelRect();
            canvas.Translate(-tileRect.Left - layer.Offset.X, -tileRect.Top - layer.Offset.Y);
            floating.DrawInto(canvas);
            canvas.Flush();

            if (tile.IsBlank()) layer.Surface.RemoveTile(idx);
        }
    }

    /// <summary>合成器停止後釋放浮動內容，再退役續接來源。</summary>
    public void DisposeAfterRenderingStopped()
    {
        Floating?.Dispose();
        Floating = null;
        if (_floatingResume is { } resume) Compositor.Retire(resume.Pixels);
        _floatingResume = null;
    }
}
