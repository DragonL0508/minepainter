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
/// 一份文件的編輯狀態總成：文件 + 合成器 + 歷史 + 筆劃緩衝 + 前景色 + 作用中工具。
/// UI 層（App）持有並綁定它；工具透過它操作一切。
/// </summary>
public sealed class EditorSession : IDisposable
{
    public Document Document { get; }
    public Compositor Compositor { get; }
    public HistoryManager History { get; }
    public StrokeBuffer StrokeBuffer { get; } = new();

    public SKColor Foreground { get; set; } = SKColors.Black;

    private SelectionMask? _selection;
    private (Guid LayerId, Guid ElementId)? _selectedElement;
    private FloatingSelection? _floating;

    /// <summary>目前選取（null = 無選取 = 全選語意）。發布後的遮罩視為 immutable。</summary>
    public SelectionMask? Selection
    {
        get => _selection;
        set
        {
            _selection = value;
            RefreshSelectionHandles();
        }
    }

    /// <summary>工具進行中的幾何預覽（render thread 直接讀，immutable record）。</summary>
    public OverlayPreview? Preview { get; set; }

    /// <summary>對齊模式（按住 Tab）：移動框時吸附畫布四邊與兩條中線。UI 依按鍵狀態設定。</summary>
    public bool SnapToCanvas { get; set; }

    /// <summary>吸附距離（doc 像素；UI 依縮放換算，約螢幕 8px）。</summary>
    public float SnapTolerance { get; set; } = 8f;

    private volatile SnapGuides? _snapGuides;

    /// <summary>吸附中的導線（render thread 讀，畫在畫布上；null = 沒吸到）。</summary>
    public SnapGuides? SnapGuides
    {
        get => _snapGuides;
        set => _snapGuides = value;
    }

    /// <summary>魔術棒 / 油漆桶容差（0..255）。</summary>
    public byte Tolerance { get; set; } = 32;

    /// <summary>目前選中的向量元素（圖層 Id + 元素 Id）。</summary>
    public (Guid LayerId, Guid ElementId)? SelectedElement
    {
        get => _selectedElement;
        set
        {
            // 選取換人時，若還有拖曳覆疊掛著（拖到一半被打斷），先收掉，原件才會重新顯示
            if (_elementOverlay is { } overlay && value?.ElementId != overlay.ElementId)
                lock (Document.SyncRoot) EndElementOverlayLocked(discardGhost: true);
            _selectedElement = value;
            RefreshSelectionHandles();
        }
    }

    /// <summary>
    /// 畫布上「被框住的那個東西」的外框（doc 座標；render thread 直接讀）。
    ///
    /// 唯讀 —— 一律由 <see cref="RefreshSelectionHandles"/> 從目前狀態推導
    /// （浮動內容 → 選中的物件 → 選取範圍 → 圖層內容（僅移動工具），
    /// 與 <see cref="HandleDragController"/> 的優先序同一份）。
    /// 這個欄位曾經是公開可寫、散在 15 個地方各自同步；只要有人漏掉一處，
    /// 螞蟻線與把手框就會各在一邊。
    /// </summary>
    public SKRect? SelectionHandles { get; private set; }

    /// <summary>把手框的旋轉角度（度，以框中心為軸）；只有變形 session 會非 0。</summary>
    public float SelectionHandlesRotation { get; private set; }

    /// <summary>
    /// 依目前狀態重算把手框。改變選取／選中物件的路徑會自動呼叫；
    /// 拖曳浮動內容時因為改的是 FloatingSelection 內部的 TargetRect，需要手動呼叫一次。
    /// </summary>
    public void RefreshSelectionHandles()
    {
        lock (Document.SyncRoot)
        {
            var frame = HandleDragController.GetFrame(this);
            // 物件拖曳覆疊中：把手跟著覆疊圖走（原件還在原位）
            if (frame is { } f && _elementOverlay is { } overlay && SelectedElement?.ElementId == overlay.ElementId)
                frame = SKRect.Create(f.Left + overlay.OffsetX, f.Top + overlay.OffsetY, f.Width, f.Height);
            SelectionHandles = frame;
            SelectionHandlesRotation = Transform?.RotationDeg ?? 0f;
        }
    }

    /// <summary>
    /// 目前框住的東西「可以重設的旋轉角度」：變形 session 的角度、或選中文字物件的 Rotation；
    /// null＝框住的東西沒有角度這回事（純選取範圍、圖層內容框）。
    /// </summary>
    public float? FrameRotation
    {
        get
        {
            if (Transform is { } t) return t.RotationDeg;
            lock (Document.SyncRoot)
            {
                return SelectedTextLocked() is { } sel ? sel.Element.Rotation : null;
            }
        }
    }

    /// <summary>框住的東西有沒有「角度或比例」可以重設（畫布上那顆重置鈕亮不亮）。</summary>
    public bool CanResetTransform
    {
        get
        {
            if (Transform is { } t)
            {
                return Math.Abs(t.RotationDeg) > 0.01f ||
                       Math.Abs(t.TargetRect.Width - t.ResetSize.Width) > 0.5f ||
                       Math.Abs(t.TargetRect.Height - t.ResetSize.Height) > 0.5f;
            }
            lock (Document.SyncRoot)
            {
                return SelectedTextLocked() is { } sel && sel.Element.IsTransformed;
            }
        }
    }

    /// <summary>
    /// 把框住的東西轉回 0°、比例回到原始（畫布上選取框旁的重置鈕）。
    /// 變形 session：角度歸零、框回到原始尺寸（維持目前中心；session 仍開著，落地時仍是單一步，
    /// 恰好回到原位就是無損還原）；文字物件：以框中心為軸轉正、ScaleX 回 1、記一步「重設角度與比例」。
    /// 回傳 false＝沒有可重設的東西。
    /// </summary>
    public bool ResetTransform()
    {
        if (Transform is { } t)
        {
            if (!CanResetTransform) return false;
            var cx = t.TargetRect.MidX;
            var cy = t.TargetRect.MidY;
            t.RotationDeg = 0f;
            t.TargetRect = SKRect.Create(
                cx - t.ResetSize.Width / 2f, cy - t.ResetSize.Height / 2f,
                t.ResetSize.Width, t.ResetSize.Height);
            t.Apply(preview: false);
            RefreshSelectionHandles();
            return true;
        }

        RasterLayer layer;
        TextElement element;
        lock (Document.SyncRoot)
        {
            if (SelectedTextLocked() is not { } sel || !sel.Element.IsTransformed) return false;
            (layer, element) = sel;
        }
        VectorCommands.ReplaceElement(Document, History, layer, element,
            element.WithTransformReset(), "重設角度與比例");
        RefreshSelectionHandles();
        return true;
    }

    /// <summary>選中、且在作用中圖層上的文字物件（須在 SyncRoot 內）。</summary>
    private (RasterLayer Layer, TextElement Element)? SelectedTextLocked()
    {
        if (SelectedElement is not { } sel) return null;
        if (Document.FindLayer(sel.LayerId) is not RasterLayer layer) return null;
        if (!ReferenceEquals(layer, Document.ActiveLayer)) return null;
        if (layer.FindElement(sel.ElementId) is not TextElement element) return null;
        return (layer, element);
    }

    /// <summary>文字工具剛建立的元素（UI 應立即開啟畫布內編輯）。</summary>
    public (Guid LayerId, Guid ElementId)? PendingTextEdit { get; set; }

    /// <summary>目前浮動中的選取內容（已從圖層提起，尚未提交）。</summary>
    public FloatingSelection? Floating
    {
        get => _floating;
        private set
        {
            _floating = value;
            RefreshSelectionHandles();
        }
    }

    private TransformSession? _transform;

    /// <summary>進行中的變形框 session（移動工具的移動/縮放/旋轉；null = 無）。</summary>
    public TransformSession? Transform
    {
        get => _transform;
        private set
        {
            _transform = value;
            RefreshSelectionHandles();
        }
    }

    /// <summary>
    /// 對作用中圖層／群組開始變形 session（已在變形中則回傳現有的）。
    /// 與浮動選取互斥：先把浮動內容落地。
    /// </summary>
    public TransformSession? BeginTransform()
    {
        if (Transform != null) return Transform;
        CommitFloating();
        if (Document.ActiveLayer is not { } target) return null;

        // 上一輪剛落地、中間沒別的編輯 → 從最初的原始像素續接（縮小落地後再拉大不糊）
        TransformSession? session = null;
        if (TakeTransformResume(target) is { } resume)
        {
            session = TransformSession.Resume(Document, target, resume);
            if (session == null) resume.Release(Compositor);
        }
        if (session == null)
        {
            session = TransformSession.Begin(Document, target, out var reason);
            if (session == null)
            {
                if (reason != null) Notify(reason);
                return null;
            }
        }
        Transform = session;
        return session;
    }

    // ---- 變形／浮動內容落地後的續接點（「縮小落地再拉大不能糊」）----

    private TransformResume? _transformResume;
    private FloatingResume? _floatingResume;

    /// <summary>取出對 <paramref name="target"/> 有效的變形續接點（一次性；無效的順手釋放）。</summary>
    private TransformResume? TakeTransformResume(LayerNode target)
    {
        var r = _transformResume;
        if (r == null) return null;
        _transformResume = null;
        if (r.IsValid(History) && ReferenceEquals(r.Target, target)) return r;
        r.Release(Compositor);
        return null;
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
    private void ReleaseStaleResumes()
    {
        if (_transformResume is { } t && !t.IsValid(History))
        {
            _transformResume = null;
            t.Release(Compositor);
        }
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

    /// <summary>把變形結果烙進圖層並記單一步 undo；恰好回到原狀時無損還原、不記步驟。</summary>
    public void CommitTransform()
    {
        var t = Transform;
        if (t == null) return;
        Transform = null;

        if (t.IsIdentity)
        {
            t.RestoreOriginal(); // 蓋章期間的 Low/High 重取樣通通不留下 —— 逐位元回到原狀
        }
        else
        {
            var entry = t.BuildCommit(t.IsGroup ? "變形群組" : "變形圖層");
            if (entry != null)
            {
                History.Push(entry);
                // 留下續接點：之後對同一目標再變形就從原始像素重取樣（要在 Push 之後，
                // Push 會觸發 ReleaseStaleResumes）
                _transformResume?.Release(Compositor);
                _transformResume = t.BuildResume(entry);
            }
        }
        t.DisposeDeferred(Compositor); // render thread 可能還在畫覆疊影像
        RefreshSelectionHandles();
    }

    /// <summary>放棄變形，無損還原開始時的像素與文字物件（Esc）。</summary>
    public void CancelTransform()
    {
        var t = Transform;
        if (t == null) return;
        Transform = null;
        t.RestoreOriginal();
        t.DisposeDeferred(Compositor);
        RefreshSelectionHandles();
    }

    /// <summary>
    /// 改由 render thread 直接覆疊呈現的浮動內容（null＝交給合成器逐格畫）。
    ///
    /// 這是「移動大量像素會卡」的解法：走合成器的話，每一次滑鼠移動都要把
    /// 浮動內容涵蓋的每一格 tile 整個重新合成一次（成本正比於選取面積，
    /// 大選取一次要十幾毫秒，跟不上滑鼠就會看到內容分格更新、一格一格追上來）。
    /// 覆疊路徑則是一張圖直接畫在合成結果上，成本與選取大小無關。
    ///
    /// 只在「結果完全相同」時才走（見 <see cref="FloatingSelection.CanOverlay"/>）；
    /// 條件不成立就退回合成器路徑。每次讀取都重新判斷 —— 浮動期間使用者仍可能
    /// 去改圖層可見性或順序，判斷結果跟著變，兩條路徑因此永遠只有一條在畫。
    /// </summary>
    public FloatingSelection? FloatingOverlay
    {
        get
        {
            var floating = _floating;
            if (floating == null) return null;
            lock (Document.SyncRoot)
            {
                return Document.FindLayer(floating.LayerId) is { } layer &&
                       FloatingSelection.CanOverlay(layer)
                    ? floating
                    : null;
            }
        }
    }

    /// <summary>合成器要畫的浮動內容：走覆疊路徑時就不該由它來畫。</summary>
    private FloatingSelection? FloatingForCompositor => FloatingOverlay == null ? _floating : null;

    /// <summary>浮動內容目前是否走覆疊路徑（工具用來決定要不要通知合成器重畫）。</summary>
    public bool IsFloatingOverlaid => FloatingOverlay != null;

    /// <summary>
    /// 落地／取消後的殘影：浮動內容已經沒了，但合成器還沒把那塊重畫完。
    ///
    /// 覆疊路徑下，快取裡的舊 tile 本來就不含浮動內容，少了這層畫面會閃一下
    /// 「東西不見了」再跳出來。合成器追上（<see cref="Compositor.IsRegionClean"/>）就收掉。
    /// </summary>
    public sealed class OverlayGhost(SKImage image, SKRect rect, SKRectI region)
    {
        public SKImage Image { get; } = image;

        /// <summary>殘影該出現的位置（落地＝新位置，取消＝原位置）。</summary>
        public SKRect Rect { get; } = rect;

        /// <summary>等這塊合成完就可以收掉。</summary>
        public SKRectI Region { get; } = region;
    }

    private volatile OverlayGhost? _ghost;

    /// <summary>render thread 讀：等合成器追上前要繼續顯示的殘影。</summary>
    public OverlayGhost? Ghost => _ghost;

    /// <summary>UI thread 每幀呼叫：合成器追上了就把殘影／圖層覆疊收掉。</summary>
    public void CollectOverlayGhost()
    {
        var ghost = _ghost;
        if (ghost != null && Compositor.IsRegionClean(ghost.Region))
        {
            _ghost = null;
            Compositor.Retire(ghost.Image); // render thread 這一幀可能還在畫它，不能就地 Dispose
        }

        if (_layerOverlay is { HandingOver: true } overlay &&
            Compositor.IsRegionClean(overlay.Region))
        {
            _layerOverlay = null;
            overlay.Retire(Compositor);
        }

        Transform?.CollectOverlay(Compositor); // 變形手勢覆疊的殘影：合成器追上就收
    }

    /// <summary>
    /// 拖曳中的文字物件覆疊：拖曳開始時把物件渲染成一張圖、隱藏原件，
    /// 拖曳期間只挪這張圖（不重排版、不逐格重畫文字），放開才真正改物件。
    /// </summary>
    public sealed class ElementDragOverlay(RasterLayer layer, Guid elementId, SKImage image, SKRectI bounds)
    {
        public RasterLayer Layer { get; } = layer;
        public Guid ElementId { get; } = elementId;
        public SKImage Image { get; } = image;

        /// <summary>物件原本的（含效果外擴的）外框，doc 座標。</summary>
        public SKRectI Bounds { get; } = bounds;

        /// <summary>目前位移（render thread 讀；UI thread 寫）。</summary>
        public volatile float OffsetX;
        public volatile float OffsetY;

        public SKRect CurrentRect => SKRect.Create(Bounds.Left + OffsetX, Bounds.Top + OffsetY, Bounds.Width, Bounds.Height);
    }

    private volatile ElementDragOverlay? _elementOverlay;

    /// <summary>render thread 讀：拖曳中的文字物件覆疊。</summary>
    public ElementDragOverlay? ElementOverlay => _elementOverlay;

    /// <summary>開始物件拖曳覆疊（在 Document.SyncRoot 內呼叫）。</summary>
    public unsafe void BeginElementOverlayLocked(RasterLayer layer, Vectors.VectorElement element)
    {
        EndElementOverlayLocked(discardGhost: true);
        var bounds = element.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        SKImage image;
        if (RenderEffectsWhileDragging && layer.HasActiveEffects)
        {
            // 帶效果拖曳：物件單獨跑一遍這層的效果堆疊（外框／陰影／漸層跟著走）
            var pixels = LayerEffectRenderer.RenderElementPreview(layer, element, out bounds);
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            var info = new SKImageInfo(bounds.Width, bounds.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            fixed (uint* ptr = pixels)
            {
                image = SKImage.FromPixelCopy(info, (IntPtr)ptr, bounds.Width * 4);
            }
            if (image == null) return;
        }
        else
        {
            bounds.Inflate(1, 1);
            var info = new SKImageInfo(bounds.Width, bounds.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            if (surface == null) return;
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            canvas.Translate(-bounds.Left, -bounds.Top);
            element.Render(canvas);
            canvas.Flush();
            image = surface.Snapshot();
        }

        _elementOverlay = new ElementDragOverlay(layer, element.Id, image, bounds);
        layer.HiddenElementId = element.Id; // 原件先藏起來（合成器重畫一次少了它的樣子）
    }

    public void MoveElementOverlay(float dx, float dy)
    {
        var overlay = _elementOverlay;
        if (overlay == null) return;
        overlay.OffsetX = dx;
        overlay.OffsetY = dy;
        RefreshSelectionHandles();
    }

    /// <summary>
    /// 結束覆疊（在 Document.SyncRoot 內呼叫）：原件重新顯示；覆疊那張圖轉成殘影留在最後位置，
    /// 等合成器把新位置畫出來再收掉，畫面才不會閃一下。
    /// </summary>
    public void EndElementOverlayLocked(bool discardGhost = false)
    {
        var overlay = _elementOverlay;
        if (overlay == null) return;
        _elementOverlay = null;
        if (overlay.Layer.HiddenElementId == overlay.ElementId) overlay.Layer.HiddenElementId = null;

        if (discardGhost)
        {
            Compositor.Retire(overlay.Image);
            return;
        }
        var final = overlay.CurrentRect;
        var region = SKRectI.Union(overlay.Bounds, SKRectI.Ceiling(final));
        var old = _ghost;
        _ghost = new OverlayGhost(overlay.Image, final, region);
        if (old != null) Compositor.Retire(old.Image);
    }

    private volatile LayerDragOverlay? _layerOverlay;

    /// <summary>
    /// 拖曳中、已從合成結果「拆下來」改由 render thread 每幀直接畫的整個圖層。
    /// null＝沒有這回事，一切照舊由合成器負責。
    /// </summary>
    public LayerDragOverlay? LayerOverlay => _layerOverlay;

    /// <summary>合成器要跳過的圖層（拆下來的那個；交還階段就不跳了）＋覆疊層是否已含物件。</summary>
    private (Guid? Id, bool IncludesElements) DetachedLayer =>
        _layerOverlay is { HandingOver: false } o ? (o.Layer.Id, o.IncludesElements) : (null, false);

    /// <summary>
    /// 拖曳（移動工具）期間覆疊層要不要帶著效果堆疊的結果一起走（外框、陰影、漸層在拖曳中看得到）。
    /// 關掉則拖曳中只畫基底像素，放開才看到效果 —— 給效能吃緊的機器用（App 設定）。
    /// </summary>
    public static bool RenderEffectsWhileDragging { get; set; } = true;

    /// <summary>
    /// 把整個圖層從合成結果裡拆下來，改由畫面覆疊（拖曳整個圖層用）。
    ///
    /// 原本每次滑鼠移動都要把圖層涵蓋的每一格重新合成一次（滿版圖層＝整份文件，
    /// 一步十幾毫秒），畫面因此永遠落後滑鼠、看起來像「等合成完才跳過去」。
    /// 拆下來之後拖曳期間**一格都不用重合成**，只有這裡與 <see cref="EndLayerDrag"/>
    /// 各失效一次。條件與浮動內容同一套（<see cref="FloatingSelection.CanOverlay"/>）——
    /// 不成立就回傳 false，呼叫端照舊逐格重合成。
    /// </summary>
    public bool BeginLayerDrag(RasterLayer layer)
    {
        if (_layerOverlay != null) return _layerOverlay.Layer == layer;

        SKRectI region;
        lock (Document.SyncRoot)
        {
            if (!FloatingSelection.CanOverlay(layer)) return false;
            var withEffects = RenderEffectsWhileDragging && layer.HasActiveEffects;
            if (withEffects && layer.FxCache.HasPending)
                LayerEffectRenderer.RenderLayerNow(Document, layer); // 快照要是最新的（通常閒置時早算完了）
            withEffects &= layer.EffectsRendered;
            region = withEffects ? layer.DisplayContentBounds : layer.ContentBounds;
            if (region.Width <= 0 || region.Height <= 0) return false;

            // 覆疊層的像素：效果快取（已含物件與外框／陰影）；否則基底像素＋物件（文字圖層整層拖曳文字要跟著走）
            TileSnapshot snapshot;
            var includesElements = false;
            if (withEffects)
            {
                snapshot = layer.FxCache.Surface.Snapshot();
                includesElements = true;
            }
            else if (layer.HasElements)
            {
                snapshot = SnapshotWithElements(layer);
                includesElements = true;
            }
            else
            {
                snapshot = layer.Surface.Snapshot();
            }
            _layerOverlay = new LayerDragOverlay(layer, snapshot, region, includesElements);
        }

        // 讓合成器把這一層從結果裡拿掉（整趟拖曳只有這一次）
        layer.Invalidate(region);
        return true;
    }

    /// <summary>
    /// 拖曳結束：合成器從現在起把圖層算回去，覆疊層則繼續頂著還沒重畫完的格子
    /// （逐格交接，見 <see cref="LayerDragOverlay.ShouldDraw"/>），全部追上才收掉。
    /// </summary>
    public void EndLayerDrag()
    {
        var overlay = _layerOverlay;
        if (overlay == null || overlay.HandingOver) return;

        SKRectI region;
        lock (Document.SyncRoot) region = SKRectI.Union(overlay.Layer.DisplayContentBounds, overlay.Region);
        overlay.BeginHandover(region);
        // 純平移：效果快取是圖層座標、與 Offset 無關，只要重新合成，不必重算效果
        overlay.Layer.InvalidateComposite(region);
    }

    /// <summary>基底像素（COW 共享）＋物件渲染進去的快照（圖層座標）。在 SyncRoot 內呼叫。</summary>
    private static unsafe TileSnapshot SnapshotWithElements(RasterLayer layer)
    {
        using var temp = new TileSurface();
        foreach (var (idx, tile) in layer.Surface.Tiles)
        {
            var dst = temp.GetTileForWrite(idx);
            new ReadOnlySpan<uint>((uint*)tile.Pixels, Tile.Size * Tile.Size)
                .CopyTo(new Span<uint>((uint*)dst.Pixels, Tile.Size * Tile.Size));
        }
        foreach (var el in layer.Elements)
        {
            if (el.Id == layer.HiddenElementId) continue;
            var b = el.Bounds;
            if (b.IsEmpty) continue;
            var layerRect = new SKRectI(b.Left - layer.Offset.X, b.Top - layer.Offset.Y,
                b.Right - layer.Offset.X, b.Bottom - layer.Offset.Y);
            foreach (var idx in TileIndex.CoveringRect(layerRect))
            {
                var tile = temp.GetTileForWrite(idx);
                using var surface = SKSurface.Create(Tile.Info, tile.Pixels, Tile.RowBytes);
                if (surface == null) continue;
                var tileRect = idx.ToPixelRect();
                var canvas = surface.Canvas;
                canvas.Translate(-tileRect.Left - layer.Offset.X, -tileRect.Top - layer.Offset.Y);
                el.Render(canvas);
                canvas.Flush();
            }
        }
        return temp.Snapshot(); // 快照 AddRef 後 temp 可釋放
    }

    /// <summary>
    /// 一次「整個圖層拖曳」期間的覆疊層：圖層像素的 COW 快照 + 逐格的零拷貝影像包裝。
    ///
    /// 影像刻意在開始時一次建好並留著 —— GPU 以影像的識別碼當貼圖快取的鍵，
    /// 每幀重建等於每幀重傳整層貼圖（正是會讓 FPS 掉到個位數的那種 cache thrash）。
    /// 像素走快照：拖曳期間就算有別的東西寫入該層，COW 會複製一份，覆疊看到的仍是同一批像素。
    /// </summary>
    public sealed class LayerDragOverlay
    {
        private readonly TileSnapshot _snapshot;
        private readonly Dictionary<TileIndex, SKImage> _images;

        internal LayerDragOverlay(RasterLayer layer, TileSnapshot snapshot, SKRectI region, bool includesElements = false)
        {
            Layer = layer;
            _snapshot = snapshot;
            Region = region;
            IncludesElements = includesElements;
            _images = new Dictionary<TileIndex, SKImage>(snapshot.Tiles.Count);
            foreach (var (idx, tile) in snapshot.Tiles)
            {
                using var pixmap = tile.AsPixmap();
                if (SKImage.FromPixels(pixmap) is { } img) _images[idx] = img; // 零拷貝
            }
        }

        public RasterLayer Layer { get; }

        /// <summary>覆疊像素已含這層的物件（合成器拆下這層時連物件也不畫）。</summary>
        public bool IncludesElements { get; }

        /// <summary>交接中：合成器已經把圖層算回去了，只補畫還沒重畫完的格子。</summary>
        public bool HandingOver { get; private set; }

        /// <summary>要等這塊全部合成完，覆疊層才能收掉。</summary>
        public SKRectI Region { get; private set; }

        internal void BeginHandover(SKRectI region)
        {
            Region = region;
            HandingOver = true;
        }

        /// <summary>
        /// 這一格要不要由覆疊層來畫。兩個轉場都靠它逐格切換，畫面才不會閃、也不會疊兩次：
        /// 　拆下來的當下 —— 舊的（髒的）格子裡還有這一層，等它重畫成「不含本層」才輪到覆疊；
        /// 　交還的時候 —— 反過來，重畫完（乾淨）的格子已經含本層了，覆疊就該讓位。
        /// 拖曳穩定期間全部都是乾淨的，等於整片都由覆疊層畫。
        /// </summary>
        public bool ShouldDraw(bool tileIsClean) => HandingOver ? !tileIsClean : tileIsClean;

        /// <summary>畫出圖層內容（doc 座標，呼叫端已套好 viewport 變換）。render thread 呼叫。</summary>
        public void Draw(SKCanvas canvas, SKRectI docRect, SKFilterQuality quality)
        {
            var offset = Layer.Offset;
            using var paint = new SKPaint { FilterQuality = quality };
            foreach (var (idx, image) in _images)
            {
                var r = idx.ToPixelRect();
                var left = r.Left + offset.X;
                var top = r.Top + offset.Y;
                if (left >= docRect.Right || top >= docRect.Bottom ||
                    left + Tile.Size <= docRect.Left || top + Tile.Size <= docRect.Top)
                {
                    continue;
                }
                canvas.DrawImage(image, left, top, paint);
            }
        }

        /// <summary>交給合成器延後釋放 —— render thread 這一幀可能還在畫這些影像。</summary>
        internal void Retire(Compositor compositor)
        {
            foreach (var image in _images.Values) compositor.Retire(image);
            _images.Clear();
            _snapshot.Dispose();
        }
    }

    /// <summary>
    /// 浮動內容要消失了：走覆疊路徑的話先留一張殘影頂著（見 <see cref="OverlayGhost"/>）。
    /// <paramref name="rect"/> 是殘影該出現的位置 —— 落地是新位置，取消是原位置。
    /// </summary>
    private void LeaveGhost(FloatingSelection floating, SKRect rect, bool overlaid)
    {
        if (!overlaid) return;
        var region = SKRectI.Round(rect);
        region.Inflate(2, 2);
        if (_ghost is { } old) Compositor.Retire(old.Image);
        _ghost = new OverlayGhost(floating.DetachPixels(), rect, region);
    }

    /// <summary>短訊息通知（UI 以 toast 呈現）。</summary>
    public event Action<string>? Notified;

    public void Notify(string message) => Notified?.Invoke(message);

    private readonly List<IPendingEdit> _pendingEdits = new();

    /// <summary>
    /// 註冊一種「進行中、尚未進 history 的編輯」。見 <see cref="IPendingEdit"/>。
    /// UI 端的互動（例如畫布內文字編輯）也從這裡加進來。
    /// </summary>
    public void RegisterPendingEdit(IPendingEdit edit) => _pendingEdits.Add(edit);

    /// <summary>是否還有未落地的編輯。</summary>
    public bool HasPendingEdits => _pendingEdits.Any(p => p.IsActive);

    /// <summary>
    /// 落地所有進行中的編輯。這是唯一的落地點 ——
    /// 指令、undo/redo、歷史跳轉、存檔在動手之前都會先走這裡。
    ///
    /// 為什麼必要：浮動中的選取內容像素已從圖層挖走、只存在 FloatingSelection 裡，
    /// 還沒有對應的 history entry。直接 undo 會去動到上一步，
    /// 留下「像素被挖走但沒有對應歷史」的不一致狀態（螞蟻線與把手框各在一邊、undo 像壞掉）。
    /// </summary>
    public void CommitPendingEdits()
    {
        // 保險：拖曳中的圖層覆疊層純粹是呈現最佳化（offset 早就寫進模型了），
        // 但拆下來的狀態不能跨到別的操作去 —— 任何「要動真格」的入口先交還。
        EndLayerDrag();

        // 落地一項可能觸發另一項（例如提交文字編輯會改動選取），所以跑到全部靜止為止。
        for (var pass = 0; pass < 4 && HasPendingEdits; pass++)
        {
            foreach (var edit in _pendingEdits)
            {
                if (edit.IsActive) edit.Commit();
            }
        }
    }

    /// <summary>復原一步（會先落地進行中的編輯）。所有 UI 入口都該走這裡，不要直接用 History。</summary>
    public bool Undo()
    {
        CommitPendingEdits();
        var done = History.Undo();
        RefreshSelectionHandles(); // 純像素的 undo 不經過選取路徑，但圖層內容框可能變了
        return done;
    }

    /// <summary>重做一步（落地進行中的編輯會清掉 redo 堆疊，此時就重做不了 —— 這是正確的）。</summary>
    public bool Redo()
    {
        CommitPendingEdits();
        var done = History.Redo();
        RefreshSelectionHandles();
        return done;
    }

    /// <summary>跳到指定的 undo 深度（歷史面板點擊跳轉）。</summary>
    public void JumpTo(int undoDepth)
    {
        CommitPendingEdits();
        History.JumpTo(undoDepth);
        RefreshSelectionHandles();
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
        if (!floating.IsPasted && floating.TargetRect == new SKRect(
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
        TileDeltaEntry? pixelEntry;
        lock (Document.SyncRoot)
        {
            StampFloating(layer, floating);

            var layerRect = new SKRectI(
                affected.Left - layer.Offset.X, affected.Top - layer.Offset.Y,
                affected.Right - layer.Offset.X, affected.Bottom - layer.Offset.Y);
            pixelEntry = TileDeltaEntry.Capture(label, layer, floating.BeforeSnapshot, layerRect);
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
        LeaveGhost(floating, targetRect, overlaid);
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
                LeaveGhost(floating, new SKRect(src.Left, src.Top, src.Right, src.Bottom), overlaid);
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

        RasterLayer layer;
        switch (Document.ActiveLayer)
        {
            case RasterLayer { IsTextLayer: true } textLayer:
            {
                // 文字圖層不收像素（不變式）：貼到它上方的新圖層，落地時與貼上合成同一步 undo
                var parent = textLayer.Parent ?? Document.Root;
                var index = parent.IndexOf(textLayer) + 1;
                layer = new RasterLayer { Name = "貼上的圖層" };
                var inserted = layer;
                lock (Document.SyncRoot)
                {
                    parent.Insert(index, inserted);
                    Document.ActiveLayer = inserted;
                }
                _pasteLayerEntry = (inserted, new ActionHistoryEntry("新增圖層", Document.Bounds,
                    undo: d =>
                    {
                        if (ReferenceEquals(d.ActiveLayer, inserted)) d.ActiveLayer = textLayer;
                        parent.Remove(inserted);
                    },
                    redo: _ => parent.Insert(Math.Min(index, parent.Children.Count), inserted),
                    onDispose: () =>
                    {
                        if (inserted.Document == null) inserted.Dispose();
                    }));
                Notify("文字圖層不能貼上像素，已貼到新圖層");
                break;
            }
            case RasterLayer raster:
                layer = raster;
                break;
            default:
                Notify("請先選擇一般圖層再貼上");
                pixels.Dispose();
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
            Floating = FloatingSelection.CreatePasted(layer, pixels, bounds, mask);
        }
        ApplySelection(mask);
        layer.Invalidate(bounds);
        return true;
    }

    /// <summary>
    /// 取作用中圖層在選取範圍內的像素（無選取＝整個畫布範圍；只取像素，不含文字物件）。
    /// 呼叫者接手回傳影像的擁有權；沒有內容可複製時回傳 null。
    /// </summary>
    public SKImage? CopyToImage()
    {
        if (Document.ActiveLayer is not RasterLayer layer) return null;
        var selection = Selection is { IsEmpty: false } s ? s : null;
        var bounds = selection != null
            ? SKRectI.Intersect(selection.Bounds, Document.Bounds)
            : Document.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;

        var info = new SKImageInfo(bounds.Width, bounds.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        if (surface == null) return null;
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        lock (Document.SyncRoot)
        {
            canvas.Save();
            canvas.Translate(-bounds.Left, -bounds.Top);
            FloatingSelection.DrawLayerPixels(layer, canvas, bounds);
            canvas.Restore();
            if (selection != null) FloatingSelection.ApplySelectionMask(selection, canvas, bounds);
        }
        canvas.Flush();
        return surface.Snapshot();
    }

    /// <summary>
    /// 取畫面上該點的顏色（滴管用）。
    ///
    /// 合成快取在覆疊路徑下不含浮動內容（那是 render thread 才疊上去的），
    /// 所以要自己把浮動內容那一層補回來 —— 否則滴管會滴到浮動內容「底下」的顏色。
    /// </summary>
    public unsafe SKColor SampleComposite(int x, int y)
    {
        var below = Compositor.SamplePixel(x, y);
        if (FloatingOverlay is not { } floating) return below;

        var rect = floating.TargetRect;
        var px = x + 0.5f;
        var py = y + 0.5f;
        if (rect.Width <= 0 || rect.Height <= 0 || !rect.Contains(px, py)) return below;

        // 目前位置 → 提起時的影像座標（縮放中也對得上）
        var sx = (int)((px - rect.Left) / rect.Width * floating.SourceBounds.Width);
        var sy = (int)((py - rect.Top) / rect.Height * floating.SourceBounds.Height);
        sx = Math.Clamp(sx, 0, floating.Pixels.Width - 1);
        sy = Math.Clamp(sy, 0, floating.Pixels.Height - 1);

        var info = new SKImageInfo(1, 1, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        Span<byte> pixel = stackalloc byte[4];
        fixed (byte* ptr = pixel)
        {
            if (!floating.Pixels.ReadPixels(info, (IntPtr)ptr, 4, sx, sy)) return below;
        }

        var top = new SKColor(pixel[2], pixel[1], pixel[0], pixel[3]);
        if (top.Alpha == 0) return below;
        if (top.Alpha == 255) return top;

        // SrcOver（滴管只在乎 RGB，取回不透明色）
        var a = top.Alpha / 255f;
        return new SKColor(
            (byte)(top.Red * a + below.Red * (1 - a)),
            (byte)(top.Green * a + below.Green * (1 - a)),
            (byte)(top.Blue * a + below.Blue * (1 - a)),
            Math.Max(top.Alpha, below.Alpha));
    }

    /// <summary>套用選取（把手框會自動跟上 —— 兩者是同一個概念）。</summary>
    internal void ApplySelection(SelectionMask? selection)
    {
        if (selection is { IsEmpty: false })
        {
            SelectedElement = null; // 選了範圍就不是在選物件
            Selection = selection;
        }
        else
        {
            Selection = null;
        }
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

    public BrushTool Brush { get; }
    public EraserTool Eraser { get; }
    public EyedropperTool Eyedropper { get; }
    public MoveTool Move { get; }
    public RectangleSelectTool RectSelect { get; }
    public LassoSelectTool Lasso { get; }
    public MagicWandTool Wand { get; }
    public FillTool Fill { get; }
    public TextTool Text { get; }
    public ShapeTool Shape { get; }

    private ITool _activeTool = null!;

    public ITool ActiveTool
    {
        get => _activeTool;
        set
        {
            _activeTool = value;
            RefreshSelectionHandles(); // 圖層內容框只在移動工具下顯示，切工具要重算
        }
    }

    /// <summary>浮動選取內容的 <see cref="IPendingEdit"/> 包裝（見該介面的說明）。</summary>
    private sealed class FloatingPendingEdit(EditorSession session) : IPendingEdit
    {
        public bool IsActive => session.Floating != null;
        public void Commit() => session.CommitFloating();
    }

    /// <summary>變形框 session 的 <see cref="IPendingEdit"/> 包裝。</summary>
    private sealed class TransformPendingEdit(EditorSession session) : IPendingEdit
    {
        public bool IsActive => session.Transform != null;
        public void Commit() => session.CommitTransform();
    }

    public EditorSession(Document document)
    {
        Document = document;
        Compositor = new Compositor(document, StrokeBuffer,
            () => FloatingForCompositor, () => DetachedLayer);
        History = new HistoryManager(document);
        RegisterPendingEdit(new FloatingPendingEdit(this));
        RegisterPendingEdit(new TransformPendingEdit(this));
        History.Changed += ReleaseStaleResumes; // 續接點只在「落地那步仍是最後一步」時有效

        Brush = new BrushTool();
        Eraser = new EraserTool();
        Eyedropper = new EyedropperTool();
        Move = new MoveTool();
        RectSelect = new RectangleSelectTool();
        Lasso = new LassoSelectTool();
        Wand = new MagicWandTool();
        Fill = new FillTool();
        Text = new TextTool();
        Shape = new ShapeTool();
        ActiveTool = Brush;
    }

    public void Dispose()
    {
        Transform?.DisposeDeferred(Compositor); // 退役佇列由 Compositor.Dispose 清掉
        Transform = null;
        Floating?.Dispose();
        Floating = null;
        _transformResume?.Release(Compositor);
        _transformResume = null;
        if (_floatingResume is { } fr) Compositor.Retire(fr.Pixels);
        _floatingResume = null;
        if (_ghost is { } ghost) Compositor.Retire(ghost.Image); // Compositor.Dispose 會清掉退役佇列
        _ghost = null;
        _layerOverlay?.Retire(Compositor);
        _layerOverlay = null;
        Compositor.Dispose();
        History.Dispose();
        Document.Dispose();
    }
}
