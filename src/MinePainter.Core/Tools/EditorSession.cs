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

    /// <summary>
    /// 對齊模式（按住 Tab）：移動框時吸附畫布、其他圖層內容、其他文字物件、選取範圍的邊與中線。
    /// UI 依按鍵狀態設定。
    /// </summary>
    public bool SnapToCanvas { get; set; }

    /// <summary>吸附距離（doc 像素；UI 依縮放換算，約螢幕 8px）。</summary>
    public float SnapTolerance { get; set; } = 8f;

    private List<SnapTarget>? _snapTargets;
    private readonly HashSet<Guid> _snapExclude = new();

    /// <summary>
    /// 這次拖曳的吸附參考框（第一次用到才蒐集，整趟拖曳沿用 —— 中途按 Tab 也拿得到）。
    /// </summary>
    internal IReadOnlyList<SnapTarget> SnapTargets => _snapTargets ??= CanvasSnap.Collect(this, _snapExclude);

    /// <summary>
    /// 拖曳開始：重設吸附參考框，並記下「正在被拖的東西」（圖層／物件 Id）——
    /// 自己不能當自己的參考，否則會被原地吸住。
    /// </summary>
    public void BeginSnapDrag(params Guid[] exclude)
    {
        _snapTargets = null;
        _snapExclude.Clear();
        foreach (var id in exclude) _snapExclude.Add(id);
    }

    /// <summary>拖曳結束：收掉導線與參考框快取（下一趟重新蒐集，才看得到這趟的新位置）。</summary>
    public void EndSnapDrag()
    {
        SnapGuides = null;
        _snapTargets = null;
        _snapExclude.Clear();
    }

    private volatile SnapGuides? _snapGuides;

    /// <summary>吸附中的導線（render thread 讀，畫在畫布上；null = 沒吸到）。</summary>
    public SnapGuides? SnapGuides
    {
        get => _snapGuides;
        set => _snapGuides = value;
    }

    /// <summary>魔術棒 / 油漆桶容差（0..255）。</summary>
    public byte Tolerance { get; set; } = 32;

    /// <summary>矩形／橢圓／套索選取的「物件選取」：圈完自動在圈內找主體（見 <see cref="Selections.ObjectSelector"/>）。</summary>
    public bool ObjectSelect { get; set; }

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
    /// 四角模式（透視／扭曲）的把手框：四個角的實際位置（render thread 直接讀，陣列 immutable）。
    /// 非 null 時 <see cref="SelectionHandles"/> 是它的外接矩形、旋轉為 0。
    /// </summary>
    public SKPoint[]? SelectionHandlesQuad { get; private set; }

    /// <summary>彎曲模式（扭曲）的把手框：4×4 控制點網格（render thread 直接讀）。</summary>
    public WarpMesh? SelectionHandlesWarp { get; private set; }

    /// <summary>
    /// 把手框現在框住的是什麼（與 <see cref="SelectionHandles"/> 同一次推導）。
    /// 繪製端靠它決定畫法：框住的是像素選取時，螞蟻線已經圈出邊界了，
    /// 再描一圈藍框只會變成同一條線畫兩次。
    /// </summary>
    public HandleDragController.TargetKind SelectionHandlesKind { get; private set; }

    private bool _layerFrameDismissed;

    /// <summary>
    /// 「圖層內容框」已被使用者點掉。移動工具下沒有別的東西被框住時會自動框住整個圖層內容，
    /// 但那個框以前點空白處也清不掉（清掉後立刻又從圖層內容推導回來），畫面上永遠有一個框。
    /// 現在點一次空白處就把它收起來，直到下一次點到圖層內容、或換作用中圖層才自動框回來
    /// —— 「點空白處一定清得掉」對所有框都成立。
    /// </summary>
    public bool LayerFrameDismissed
    {
        get => _layerFrameDismissed;
        set
        {
            if (_layerFrameDismissed == value) return;
            _layerFrameDismissed = value;
            RefreshSelectionHandles();
        }
    }

    private bool _selectionGestureActive;

    /// <summary>
    /// 選取工具正在拖曳中。拖的時候畫面上只該有「正在框出來的那條線」（工具預覽），
    /// 把手框一律收掉 —— 加選／減選時舊選取的把手還留著會讓人以為那是可以拖的東西。
    /// 放開＝選取區確定，把手才出現。
    /// </summary>
    public bool SelectionGestureActive
    {
        get => _selectionGestureActive;
        set
        {
            if (_selectionGestureActive == value) return;
            _selectionGestureActive = value;
            RefreshSelectionHandles();
        }
    }

    /// <summary>
    /// 依模式開始（或切換）變形：Free＝一般變形框；Perspective＝四角模式；Warp＝彎曲模式。
    /// 目標含文字物件時先自動「圖層文字平面化」再框（PS 也是先柵格化；Esc 取消會連平面化一起還原）。
    /// 已在對應模式就回傳現有 session；做不到回 null。
    /// </summary>
    public TransformSession? EnterTransformMode(TransformMode mode)
    {
        var t = Transform ?? BeginTransform();
        if (t == null) return null;
        if (mode == TransformMode.Free) return t;
        if (mode == TransformMode.Perspective && t.Quad != null && t.Warp == null) return t;
        if (mode == TransformMode.Warp && t.Warp != null) return t;

        // 文字物件不必平面化：透視／彎曲疊在文字的輸出端（TextElement.Deform），改字照樣套（使用者明示）
        var ok = mode == TransformMode.Warp ? t.EnterWarpMode() : t.EnterQuadMode();
        if (!ok) Notify("此圖層無法進入這種變形");
        RefreshSelectionHandles();
        return t;
    }

    /// <summary>鋼筆工具的工作路徑（render thread 直接讀；immutable，每次改動換新實例）。null＝沒有路徑。</summary>
    public Vectors.PenPath? PenPath
    {
        get => _penPath;
        set => _penPath = value;
    }

    private volatile Vectors.PenPath? _penPath;

    /// <summary>
    /// 依目前狀態重算把手框。改變選取／選中物件的路徑會自動呼叫；
    /// 拖曳浮動內容時因為改的是 FloatingSelection 內部的 TargetRect，需要手動呼叫一次。
    /// </summary>
    public void RefreshSelectionHandles()
    {
        lock (Document.SyncRoot)
        {
            var frame = HandleDragController.GetFrame(this, out var frameKind);
            SelectionHandlesKind = frameKind;
            // 物件手勢覆疊中：把手跟著覆疊圖的變換走（原件還在原位、還沒改）
            var overlayRotation = 0f;
            if (frame is { } f && _elementOverlay is { } overlay && SelectedElement?.ElementId == overlay.ElementId)
            {
                frame = overlay.MapFrame(f);
                overlayRotation = overlay.Rotation;
            }
            SelectionHandles = frame;
            SelectionHandlesRotation = Transform?.DisplayRotation ?? overlayRotation;
            SelectionHandlesWarp = Transform?.Warp;
            SelectionHandlesQuad = SelectionHandlesWarp == null ? Transform?.Quad : null;

            // 還沒開始變形、移動工具在透視／扭曲模式：框就先畫成該模式的把手（4 角／16 控制點），
            // 不必先拖一下才換（使用者明示）。拖任一把手時才真的開 session（HandleDragController）。
            if (Transform == null && frame is { } pf && ActiveTool == Move &&
                Floating == null && Selection is not { IsEmpty: false })
            {
                switch (Move.TransformMode)
                {
                    case TransformMode.Perspective: SelectionHandlesQuad = QuadGeometry.Corners(pf); break;
                    case TransformMode.Warp: SelectionHandlesWarp = WarpMesh.Flat(pf); break;
                }
            }
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
            if (Transform is { } t) return t.CanReset;
            // 沒在變形、但剛落地的變形還能續接（點出去再點回來）：重設要能回到最原始
            if (ActiveTool == Move && Floating == null && Document.ActiveLayer is { } node && HasResumeFor(node))
                return true;
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
            // 回到「最原始」：退出四角／彎曲、角度 0、尺寸回原始（含續接的上一輪也一起丟掉），位置留在原地
            t.ResetAll();
            t.Apply(preview: false);
            RefreshSelectionHandles();
            return true;
        }

        // 沒在變形、剛落地的變形還能續接：用續接點開一輪、重設、直接落地（單一步 undo）
        if (ActiveTool == Move && Floating == null && Document.ActiveLayer is { } node && HasResumeFor(node))
        {
            var resumed = BeginTransform();
            if (resumed == null) return false;
            resumed.ResetAll();
            resumed.Apply(preview: false);
            CommitTransform();
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

        // 這些圖層還留著原始高清那份 → 從它續接（縮小落地後再拉大不糊，隔多久都一樣）
        TransformSession? session = null;
        if (BuildResumeFromLayers(target) is { } resume)
            session = TransformSession.Resume(Document, target, resume);
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

    private FloatingResume? _floatingResume;

    /// <summary>目標底下所有點陣圖層（群組＝所有子孫）。</summary>
    private static List<RasterLayer>? RasterLayersOf(LayerNode target)
    {
        var layers = new List<RasterLayer>();
        switch (target)
        {
            case RasterLayer r: layers.Add(r); break;
            case GroupLayer g: CollectRasters(g, layers); break;
            default: return null;
        }
        return layers;

        static void CollectRasters(GroupLayer group, List<RasterLayer> into)
        {
            foreach (var child in group.Children)
            {
                switch (child)
                {
                    case RasterLayer r: into.Add(r); break;
                    case GroupLayer g: CollectRasters(g, into); break;
                }
            }
        }
    }

    /// <summary>
    /// <paramref name="target"/> 底下每一層都還留著原始高清那份（<see cref="RasterLayer.ValidPixelSource"/>）
    /// —— 下一輪變形可以從原始重取樣，而不是從已經縮小的像素再放大。
    /// </summary>
    public bool HasResumeFor(LayerNode target) => BuildResumeFromLayers(target) != null;

    /// <summary>
    /// 從各圖層留著的原始高清來源組出續接資料；有一層沒有就整組不續接（回 null）。
    /// 像素的擁有權留在圖層那邊，session 只是借用。
    /// </summary>
    private TransformResume? BuildResumeFromLayers(LayerNode target)
    {
        if (RasterLayersOf(target) is not { Count: > 0 } layers) return null;

        var items = new (RasterLayer, SKImage, SKRectI)[layers.Count];
        LayerPixelSource? first = null;
        SKPointI delta = default;
        for (var i = 0; i < layers.Count; i++)
        {
            var layer = layers[i];
            if (layer.ValidPixelSource is not { } src) return null;
            if (first == null)
            {
                first = src;
                // 來源建立之後圖層被整層平移過：差值疊到框與映射上（像素還是同一份）
                delta = new SKPointI(layer.Offset.X - src.BaseOffset.X, layer.Offset.Y - src.BaseOffset.Y);
            }
            items[i] = (layer, src.Pixels, src.Bounds);
        }
        if (first == null) return null;

        var matrix = first.Matrix;
        var rect = first.TargetRect;
        if (delta != SKPointI.Empty)
        {
            matrix = SKMatrix.Concat(SKMatrix.CreateTranslation(delta.X, delta.Y), matrix);
            rect = new SKRect(rect.Left + delta.X, rect.Top + delta.Y,
                rect.Right + delta.X, rect.Bottom + delta.Y);
        }
        return new TransformResume(target, items, matrix, rect, first.RotationDeg, first.OriginalSize);
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

    /// <summary>把變形結果烙進圖層並記單一步 undo；恰好回到原狀時無損還原、不記步驟。</summary>
    public void CommitTransform()
    {
        var t = Transform;
        if (t == null) return;
        Transform = null;

        if (t.IsIdentity)
        {
            t.RestoreOriginal();          // 蓋章期間的 Low/High 重取樣通通不留下 —— 逐位元回到原狀
            t.RepublishBorrowedSources(); // 還原動到像素，借來的原始那份要重新掛回去
        }
        else
        {
            var entry = t.BuildCommit(t.IsGroup ? "變形群組" : "變形圖層");
            if (entry != null) History.Push(entry);
            // 把原始高清掛回各圖層：之後對同一目標再變形就從它重取樣（存檔也存這一份）
            t.PublishPixelSources();
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
        t.RepublishBorrowedSources(); // 還原動到像素，借來的原始那份要重新掛回去
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
    public sealed class OverlayGhost(SKImage image, SKRect rect, SKRectI region, float rotation = 0f,
        SKPoint? pivot = null)
    {
        public SKImage Image { get; } = image;

        /// <summary>這張殘影是哪個物件的（沒有＝浮動內容的殘影）。收掉的時機與再利用都要看它。</summary>
        public RasterLayer? Layer { get; init; }

        public Guid? ElementId { get; init; }

        /// <summary>殘影該出現的位置（落地＝新位置，取消＝原位置）。</summary>
        public SKRect Rect { get; } = rect;

        /// <summary>等這塊合成完就可以收掉。</summary>
        public SKRectI Region { get; } = region;

        /// <summary>以 <see cref="Pivot"/> 為軸的角度：旋轉手勢的殘影要跟覆疊圖同一個姿態，
        /// 否則放開的瞬間會閃一下轉回原角度。</summary>
        public float Rotation { get; } = rotation;

        /// <summary>旋轉軸心（doc 座標）＝覆疊圖用的那一個，不是 <see cref="Rect"/> 的中心。</summary>
        public SKPoint Pivot { get; } = pivot ?? new SKPoint(rect.MidX, rect.MidY);
    }

    private volatile OverlayGhost? _ghost;

    /// <summary>render thread 讀：等合成器追上前要繼續顯示的殘影。</summary>
    public OverlayGhost? Ghost => _ghost;

    /// <summary>UI thread 每幀呼叫：合成器追上了就把殘影／圖層覆疊收掉。</summary>
    public void CollectOverlayGhost()
    {
        var ghost = _ghost;
        // 合成器「畫完了」不等於「畫對了」：效果堆疊還在背景重算時，合成結果裡的物件是沒有效果的，
        // 這時收掉殘影，畫面就會閃一下（外框／陰影消失再出現）——放開的瞬間閃爍就是這個。
        // 「算過」不等於「算的是現在這份」：物件搬走之後快取仍舊 Rendered，畫的卻是舊位置
        // （拖曳中原件是藏起來的，那份快取甚至是空的）。這裡要的是「已經是最新的」。
        var effectsBehind = ghost?.Layer is { HasActiveEffects: true } gl && !gl.FxCache.UpToDate;
        if (ghost != null && !effectsBehind && CompositeCaughtUp(ghost.Region))
        {
            _ghost = null;
            Compositor.Retire(ghost.Image); // render thread 這一幀可能還在畫它，不能就地 Dispose
        }

        if (_layerOverlay is { HandingOver: true } overlay &&
            !(overlay.Layer is { HasActiveEffects: true } ol && !ol.FxCache.UpToDate) &&
            CompositeCaughtUp(overlay.Region))
        {
            _layerOverlay = null;
            overlay.Retire(Compositor);
        }

        Transform?.CollectOverlay(Compositor, LiveElementRendering); // 變形手勢覆疊的殘影
    }

    /// <summary>
    /// 這塊區域可以交還給「畫面自己畫」了嗎。
    ///
    /// GPU 路徑（<see cref="LiveElementRendering"/>）每幀直接走圖層樹，畫面根本不看合成結果 ——
    /// 還要等合成器追上的話，殘影／覆疊會在畫面上多留幾百毫秒到幾秒（4K、一堆效果的檔案），
    /// 那期間圖層自己也已經畫得出來，看起來就是同一個東西疊了兩份或「卡在舊的樣子」。
    /// 走 tile 路徑時畫面吃的就是合成結果，那就非等不可。
    /// </summary>
    private bool CompositeCaughtUp(SKRectI region) =>
        LiveElementRendering || Compositor.IsRegionClean(region);

    /// <summary>
    /// 手勢中的文字物件覆疊：手勢開始時把物件（含效果）渲染成一張圖、隱藏原件，
    /// 手勢期間只變換這張圖 —— 不重排版、不逐格重畫、更不重算效果堆疊，放開才真正改物件。
    ///
    /// 移動、旋轉、縮放共用同一張圖：4K 帶外框／陰影的文字，效果堆疊算一次要 0.26 秒，
    /// 每個 pointer-move 都重算就是「怎麼拖都跟不上」。代價是手勢中的效果跟著整張圖轉／縮
    /// （陰影角度、外框粗細會暫時失真），放開重算一次就校正回來 —— PS 的變形預覽也是這樣。
    /// </summary>
    public sealed class ElementDragOverlay(RasterLayer layer, Guid elementId, SKImage? image, SKRectI bounds)
    {
        public RasterLayer Layer { get; } = layer;
        public Guid ElementId { get; } = elementId;

        /// <summary>
        /// 手勢中要貼的那張快照。**GPU 路徑不需要它**（那條路直接把原件套上手勢變換畫出來，
        /// 效果即時算），這時是 null —— 也就省下了「按下去的那一刻先渲染一遍整個物件加效果」
        /// 那筆開場費用（4K 帶效果的大字要 0.2 秒以上，正是「一按下去就頓一下」的來源）。
        /// </summary>
        public SKImage? Image { get; } = image;

        /// <summary>物件原本的（含效果外擴的）外框，doc 座標。</summary>
        public SKRectI Bounds { get; } = bounds;

        // 目前的目標框、角度與旋轉軸心（render thread 讀、UI thread 寫；float 讀寫是原子的，
        // 中間狀態最多讓某一幀的框差一點點，下一幀就對上了）
        private volatile float _left = bounds.Left;
        private volatile float _top = bounds.Top;
        private volatile float _width = bounds.Width;
        private volatile float _height = bounds.Height;
        private volatile float _rotation;
        private volatile float _pivotX = bounds.MidX;
        private volatile float _pivotY = bounds.MidY;

        /// <summary>覆疊圖現在要畫在哪（doc 座標）。</summary>
        public SKRect CurrentRect => new(_left, _top, _left + _width, _top + _height);

        /// <summary>以 <see cref="Pivot"/> 為軸的角度（度）。</summary>
        public float Rotation => _rotation;

        /// <summary>
        /// 旋轉軸心（doc 座標）。**不是覆疊圖的中心** —— 覆疊圖的框是「含效果外擴」的框，
        /// 而物件真正繞著轉的是「使用者看到的框」（著墨範圍）的中心，兩者差了排版框與著墨框的落差
        /// （120px 的字實測差 7–11 px，字級愈大差愈多）。用覆疊圖的中心當軸，手勢中的字就會
        /// 繞錯圓心跑、跟選取框對不起來，放開又跳回正確位置 —— 使用者說的「旋轉時位置會亂跳」。
        /// </summary>
        public SKPoint Pivot => new(_pivotX, _pivotY);

        /// <summary>設定目標框、角度與旋轉軸心（UI thread）；軸心省略＝框的中心。</summary>
        public void SetTarget(SKRect rect, float rotationDeg, SKPoint? pivot = null)
        {
            _left = rect.Left;
            _top = rect.Top;
            _width = rect.Width;
            _height = rect.Height;
            _rotation = rotationDeg;
            var p = pivot ?? new SKPoint(rect.MidX, rect.MidY);
            _pivotX = p.X;
            _pivotY = p.Y;
        }

        /// <summary>
        /// 把「原始框裡的一個框」（例如把手框）依覆疊目前的變換映射過去 ——
        /// 覆疊在縮放時把手要跟著縮，不然框跟畫面上的圖對不起來。
        /// </summary>
        public SKRect MapFrame(SKRect f)
        {
            var cur = CurrentRect;
            var sx = Bounds.Width > 0 ? cur.Width / Bounds.Width : 1f;
            var sy = Bounds.Height > 0 ? cur.Height / Bounds.Height : 1f;
            return new SKRect(
                cur.Left + (f.Left - Bounds.Left) * sx,
                cur.Top + (f.Top - Bounds.Top) * sy,
                cur.Left + (f.Right - Bounds.Left) * sx,
                cur.Top + (f.Bottom - Bounds.Top) * sy);
        }
    }

    private volatile ElementDragOverlay? _elementOverlay;

    /// <summary>render thread 讀：拖曳中的文字物件覆疊。</summary>
    public ElementDragOverlay? ElementOverlay => _elementOverlay;

    /// <summary>開始物件拖曳覆疊（在 Document.SyncRoot 內呼叫）。</summary>
    /// <summary>覆疊範圍在效果邊界之外多留的一圈（重取樣的邊緣餘裕）。</summary>
    private const int Slack = 1;

    /// <summary>診斷／測試用：上一次的物件覆疊有沒有沿用效果快取（沒沿用＝整個物件重算一遍）。</summary>
    internal bool OverlayReusedCache { get; private set; }

    public unsafe void BeginElementOverlayLocked(RasterLayer layer, Vectors.VectorElement element)
    {
        EndElementOverlayLocked(discardGhost: true);
        var bounds = element.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var withEffects = RenderEffectsWhileDragging && layer.HasActiveEffects;
        var margin = withEffects ? LayerEffectRenderer.TotalMargin(layer) : 0;
        bounds.Inflate(margin + Slack, margin + Slack);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        // 上一趟手勢剛落地、效果還在背景重算（那扇窗大約 0.2–0.3 秒）：這時再按下去，
        // 效果快取不是最新的，本來就得整個物件重算一遍 —— 使用者感受到的就是「頭幾次不順、
        // 多做幾次才變順」。而剛落地的那張殘影，畫的正好就是這個物件現在的樣子，直接接手來用。
        if (_ghost is { Rotation: 0f } ghost && ghost.ElementId == element.Id &&
            ReferenceEquals(ghost.Layer, layer) && SKRectI.Round(ghost.Rect) == bounds)
        {
            _ghost = null; // 影像的擁有權轉給覆疊
            _elementOverlay = new ElementDragOverlay(layer, element.Id, ghost.Image, bounds);
            layer.HiddenElementId = element.Id;
            OverlayReusedCache = true;
            return;
        }

        SKImage? image = null;
        var scale = OverlayScale(bounds);
        OverlayReusedCache = false;
        if (withEffects && scale >= 1f)
        {
            // 帶效果拖曳：物件單獨跑一遍這層的效果堆疊（外框／陰影／漸層跟著走）。
            // 快取剛好蓋得到就直接裁一塊（省下重跑一遍）。
            var cached = TryReadEffectCache(layer, element, bounds);
            OverlayReusedCache = cached != null;
            image = ImageFrom(cached ?? LayerEffectRenderer.RenderElementPreview(layer, element, out _), bounds);
        }

        // 太大時（見 OverlayScale）降解析度、也不跑效果堆疊：整張當一張貼圖畫不出來，
        // 低解析度的預覽總比手勢中整個物件消失好。
        image ??= RenderElementOnly(element, bounds, scale);
        if (image == null) return;

        _elementOverlay = new ElementDragOverlay(layer, element.Id, image, bounds);
        layer.HiddenElementId = element.Id; // 原件先藏起來（合成器重畫一次少了它的樣子）
    }

    /// <summary>
    /// 手勢覆疊圖的解析度上限。
    ///
    /// 覆疊是「一張圖」，要當成 GPU 貼圖畫出來；貼圖有尺寸上限（常見 16384），超過就整張
    /// **靜靜地畫不出來** —— 畫面上看起來就是「拖曳／旋轉大物件時，物件整個消失」
    /// （使用者 2026-09-04 回報）。而且一張 27000×4500 的圖也要 466 MB。
    /// 超過就縮小畫、之後照樣拉回原本的框顯示：手勢中糊一點，總比看不到好。
    /// </summary>
    private const int MaxOverlaySide = 4096;

    private const long MaxOverlayPixels = 8L * 1024 * 1024; // 8 MPx ＝ 32 MB

    /// <summary>覆疊快照要縮多少（1 ＝ 原尺寸）。</summary>
    private static float OverlayScale(SKRectI bounds)
    {
        var longest = Math.Max(bounds.Width, bounds.Height);
        if (longest <= 0) return 1f;
        var scale = longest > MaxOverlaySide ? MaxOverlaySide / (float)longest : 1f;
        var pixels = (long)MathF.Ceiling(bounds.Width * scale) * (long)MathF.Ceiling(bounds.Height * scale);
        if (pixels > MaxOverlayPixels) scale *= MathF.Sqrt(MaxOverlayPixels / (float)pixels);
        return Math.Min(1f, scale);
    }

    /// <summary>只畫物件本身（不跑效果堆疊）到指定範圍；太大時縮小解析度。</summary>
    private static SKImage? RenderElementOnly(Vectors.VectorElement element, SKRectI bounds, float scale = 1f)
    {
        var w = Math.Max(1, (int)MathF.Ceiling(bounds.Width * scale));
        var h = Math.Max(1, (int)MathF.Ceiling(bounds.Height * scale));
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        if (surface == null) return null;
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        if (scale != 1f) canvas.Scale(scale);
        canvas.Translate(-bounds.Left, -bounds.Top);
        element.Render(canvas);
        canvas.Flush();
        return surface.Snapshot();
    }

    private static unsafe SKImage? ImageFrom(uint[] pixels, SKRectI bounds)
    {
        var info = new SKImageInfo(bounds.Width, bounds.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        fixed (uint* ptr = pixels)
        {
            return SKImage.FromPixelCopy(info, (IntPtr)ptr, bounds.Width * 4);
        }
    }

    /// <summary>
    /// 從效果快取裁出這個物件那一塊（圖層座標 → doc 座標）。
    /// 快取不是最新的、或這層還有別的物件（裁出來會夾帶到）就回 null，交給完整重算那條路。
    /// </summary>
    private static uint[]? TryReadEffectCache(RasterLayer layer, Vectors.VectorElement element, SKRectI docRect)
    {
        if (!layer.FxCache.Rendered || layer.Elements.Count != 1) return null;


        var layerRect = new SKRectI(
            docRect.Left - layer.Offset.X, docRect.Top - layer.Offset.Y,
            docRect.Right - layer.Offset.X, docRect.Bottom - layer.Offset.Y);

        // 快取只算「畫布看得到的那塊」時（見 LayerEffectRenderer 的裁切）蓋不到整個物件，
        // 直接裁出來的話拖曳中把畫布外那段拉進畫面會是一片空白 —— 那種情況乖乖整份現算。
        // 直接問「這塊在不在上次算的範圍裡」，不靠旗標推論。
        // 要的範圍比效果邊界多留了一圈安全餘裕（見 BeginElementOverlayLocked 的 margin + 1）。
        // 那一圈本來就是空的，卻會讓「快取蓋得到嗎」永遠不成立 —— 於是每次按下去都整個重算一遍
        // （4K 帶漸層／外框／光暈的大字實測 200–350 ms，就是使用者說的「點下去卡死」）。
        // 判斷時把餘裕還回去：真正要問的是「效果算過的那塊蓋不蓋得到物件」。
        var wanted = layerRect;
        wanted.Inflate(-Slack, -Slack);
        if (layer.FxCache.LastClipped || !layer.FxCache.LastRegion.Contains(wanted)) return null;

        return LayerEffectRenderer.ReadPixels(layer.FxCache.Surface, layerRect);
    }

    public void MoveElementOverlay(float dx, float dy)
    {
        var overlay = _elementOverlay;
        if (overlay == null) return;
        overlay.SetTarget(SKRect.Create(overlay.Bounds.Left + dx, overlay.Bounds.Top + dy,
            overlay.Bounds.Width, overlay.Bounds.Height), 0f);
        RefreshSelectionHandles();
    }

    /// <summary>
    /// 手勢中的旋轉預覽：只轉覆疊圖，原件放開才改。
    /// <paramref name="pivot"/> 要與原件真正的旋轉軸心是同一點（見 <see cref="ElementDragOverlay.Pivot"/>）。
    /// </summary>
    public void RotateElementOverlay(float degrees, SKPoint pivot)
    {
        var overlay = _elementOverlay;
        if (overlay == null) return;
        overlay.SetTarget(overlay.CurrentRect, degrees, pivot);
        RefreshSelectionHandles();
    }

    /// <summary>
    /// 手勢中的縮放預覽：物件的框從 <paramref name="oldFrame"/> 變成 <paramref name="newFrame"/>，
    /// 覆疊圖（比框大一圈的效果外擴）依同一個仿射一起走。
    /// </summary>
    public void ScaleElementOverlay(SKRect oldFrame, SKRect newFrame)
    {
        var overlay = _elementOverlay;
        if (overlay == null || oldFrame.Width <= 0 || oldFrame.Height <= 0) return;
        var sx = newFrame.Width / oldFrame.Width;
        var sy = newFrame.Height / oldFrame.Height;
        var b = overlay.Bounds;
        var pivot = overlay.Pivot;
        overlay.SetTarget(new SKRect(
            newFrame.Left + (b.Left - oldFrame.Left) * sx,
            newFrame.Top + (b.Top - oldFrame.Top) * sy,
            newFrame.Left + (b.Right - oldFrame.Left) * sx,
            newFrame.Top + (b.Bottom - oldFrame.Top) * sy), overlay.Rotation,
            new SKPoint(newFrame.Left + (pivot.X - oldFrame.Left) * sx,
                newFrame.Top + (pivot.Y - oldFrame.Top) * sy));
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

        if (discardGhost || overlay.Image == null)
        {
            // 沒有快照＝走的是即時渲染那條路：原件解除隱藏後畫面上馬上就是它，不需要殘影
            if (overlay.Image != null) Compositor.Retire(overlay.Image);
            return;
        }
        var final = overlay.CurrentRect;
        var pivot = overlay.Pivot;
        // 旋轉中的殘影範圍要用轉過之後的外接框，不然合成器判斷「這塊乾淨了」會少算一塊
        var region = SKRectI.Union(overlay.Bounds,
            SKRectI.Ceiling(RotatedBounds(final, overlay.Rotation, pivot)));
        var old = _ghost;
        _ghost = new OverlayGhost(overlay.Image, final, region, overlay.Rotation, pivot)
        {
            Layer = overlay.Layer,
            ElementId = overlay.ElementId,
        };
        if (old != null) Compositor.Retire(old.Image);
    }

    /// <summary>矩形繞 <paramref name="pivot"/>（省略＝自己的中心）旋轉後的外接框。</summary>
    private static SKRect RotatedBounds(SKRect rect, float degrees, SKPoint? pivot = null)
    {
        if (degrees == 0f) return rect;
        var c = pivot ?? new SKPoint(rect.MidX, rect.MidY);
        var m = SKMatrix.CreateRotationDegrees(degrees, c.X, c.Y);
        Span<SKPoint> pts =
        [
            m.MapPoint(rect.Left, rect.Top), m.MapPoint(rect.Right, rect.Top),
            m.MapPoint(rect.Right, rect.Bottom), m.MapPoint(rect.Left, rect.Bottom),
        ];
        float l = pts[0].X, t = pts[0].Y, r = pts[0].X, b = pts[0].Y;
        for (var i = 1; i < 4; i++)
        {
            l = Math.Min(l, pts[i].X); t = Math.Min(t, pts[i].Y);
            r = Math.Max(r, pts[i].X); b = Math.Max(b, pts[i].Y);
        }
        return new SKRect(l, t, r, b);
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
    /// 畫面端能不能「照層序即時畫出手勢中的內容」（＝GPU 圖層渲染開著）。
    ///
    /// 開著時變形手勢不必再要求「這層上面沒有看得見的東西」——覆疊畫得到對的位置，
    /// 就不用退回逐步蓋章（見 TransformSession.BeginGesturePreview）。
    /// </summary>
    public bool LiveElementRendering { get; set; }

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
            // 快照要是最新的（通常閒置時早算完了）。只看 HasPending 不夠：worker 可能剛取走工作、
            // 正在鎖外計算 —— 髒區已清空但 Rendered 還是 false，RenderLayerNow 會等它寫回。
            if (withEffects && (layer.FxCache.HasPending || !layer.EffectsRendered))
                LayerEffectRenderer.RenderLayerNow(Document, layer);
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
            if (layer.IsElementHidden(el.Id)) continue;
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

    /// <summary>
    /// 取作用中圖層在選取範圍內「看得到的樣子」（無選取＝整個畫布範圍）。
    /// 呼叫者接手回傳影像的擁有權；沒有內容可複製時回傳 null。
    /// </summary>
    public SKImage? CopyToImage() => CopyToImage(out _);

    /// <summary>
    /// 同 <see cref="CopyToImage()"/>，另外回報取像的左上角文件座標，
    /// 讓貼上能貼回原處（<paramref name="origin"/> 在回傳 null 時無意義）。
    ///
    /// 取的是**算繪後的樣子**：效果堆疊（外框／陰影…）與文字物件都在裡面，群組則是整組合成後的樣子。
    /// 貼到別的程式去要的就是眼睛看到的那張圖，不是圖層底下那份原始像素
    /// （文字圖層根本沒有像素，只取基底的話會複製到一張空白）。
    /// </summary>
    public SKImage? CopyToImage(out SKPointI origin)
    {
        origin = default;
        if (Document.ActiveLayer is not { CanHaveEffects: true } node) return null;

        // 效果快取要先是最新的（複製是使用者按下去才發生的一次性動作，等得起）
        if (node.HasActiveEffects) LayerEffectRenderer.RenderLayerNow(Document, node);

        var selection = Selection is { IsEmpty: false } s ? s : null;
        var bounds = selection != null
            ? SKRectI.Intersect(selection.Bounds, Document.Bounds)
            : Document.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;
        origin = new SKPointI(bounds.Left, bounds.Top);

        var info = new SKImageInfo(bounds.Width, bounds.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        if (surface == null) return null;
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        lock (Document.SyncRoot)
        {
            canvas.Save();
            canvas.Translate(-bounds.Left, -bounds.Top);
            DrawNodeAppearanceLocked(node, canvas, bounds);
            canvas.Restore();
            if (selection != null) FloatingSelection.ApplySelectionMask(selection, canvas, bounds);
        }
        canvas.Flush();
        return surface.Snapshot();
    }

    /// <summary>
    /// 把一個節點「畫面上的樣子」畫進 canvas（doc 座標）。在 Document.SyncRoot 內呼叫。
    /// 效果快取已算好時它就是最終樣貌（文字物件也已經併在裡面）。
    /// </summary>
    private static void DrawNodeAppearanceLocked(LayerNode node, SKCanvas canvas, SKRectI docRect)
    {
        if (node.EffectsRendered)
        {
            DrawSurface(node.FxCache.Surface, node.EffectOffset, canvas, docRect);
            return;
        }

        switch (node)
        {
            case RasterLayer raster:
                FloatingSelection.DrawLayerPixels(raster, canvas, docRect);
                foreach (var el in raster.Elements)
                {
                    if (raster.IsElementHidden(el.Id)) continue;
                    el.Render(canvas);
                }
                break;

            case GroupLayer group:
                DrawGroupPixels(group, canvas, docRect);
                break;
        }
    }

    private static void DrawSurface(TileSurface source, SKPointI offset, SKCanvas canvas, SKRectI docRect)
    {
        var rect = new SKRectI(
            docRect.Left - offset.X, docRect.Top - offset.Y,
            docRect.Right - offset.X, docRect.Bottom - offset.Y);
        foreach (var idx in TileIndex.CoveringRect(rect))
        {
            var tile = source.GetTileForRead(idx);
            if (tile == null) continue;
            using var pixmap = tile.AsPixmap();
            using var img = SKImage.FromPixels(pixmap);
            var tileRect = idx.ToPixelRect();
            canvas.DrawImage(img, tileRect.Left + offset.X, tileRect.Top + offset.Y);
        }
    }

    private static unsafe void DrawGroupPixels(GroupLayer group, SKCanvas canvas, SKRectI docRect)
    {
        var pixels = Compositing.Compositor.StaticGroupSourceLocked(group, docRect);
        if (pixels.Length < docRect.Width * docRect.Height) return;
        fixed (uint* ptr = pixels)
        {
            var info = new SKImageInfo(docRect.Width, docRect.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var img = SKImage.FromPixels(info, (IntPtr)ptr, docRect.Width * 4);
            canvas.DrawImage(img, docRect.Left, docRect.Top);
        }
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
    public PencilTool Pencil { get; }
    public EraserTool Eraser { get; }
    public BackgroundEraserTool BackgroundEraser { get; }
    public EyedropperTool Eyedropper { get; }
    public MoveTool Move { get; }
    public RectangleSelectTool RectSelect { get; }
    public EllipseSelectTool EllipseSelect { get; }
    public LassoSelectTool Lasso { get; }
    public MagicWandTool Wand { get; }
    public FillTool Fill { get; }
    public TextTool Text { get; }
    public ShapeTool Shape { get; }
    public PenTool Pen { get; }

    /// <summary>
    /// 反向橡皮擦（Alt）的還原基準：這一輪擦除開始前，那一層的樣子。
    /// 由橡皮擦／去背筆在落筆時維護（見 <see cref="EraseBaseline"/>）。
    /// </summary>
    public EraseBaseline EraseBaseline { get; } = new();

    private ITool _activeTool = null!;

    public ITool ActiveTool
    {
        get => _activeTool;
        set
        {
            var changed = !ReferenceEquals(_activeTool, value);
            _activeTool = value;
            // 手勢旗標不跨工具（拖到一半被切走時放開事件收不到，框會永遠藏著）
            if (changed) _selectionGestureActive = false;
            // 「收掉的框」跨工具維持收著：使用者已經明示不要那個框了，
            // 去畫個筆刷再切回來又冒出來只會覺得自己白清了。要它回來就點一下圖層內容。
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
        Document.ActiveLayerChanged += OnActiveLayerChanged;

        Brush = new BrushTool();
        Pencil = new PencilTool();
        Eraser = new EraserTool();
        BackgroundEraser = new BackgroundEraserTool();
        Eyedropper = new EyedropperTool();
        Move = new MoveTool();
        RectSelect = new RectangleSelectTool();
        EllipseSelect = new EllipseSelectTool();
        Lasso = new LassoSelectTool();
        Wand = new MagicWandTool();
        Fill = new FillTool();
        Text = new TextTool();
        Shape = new ShapeTool();
        Pen = new PenTool();
        ActiveTool = Brush;
    }

    /// <summary>
    /// 換到文字圖層時放掉像素選取。文字圖層沒有可選的像素（有物件就沒有像素），
    /// 留著的話畫面上就是一圈沒有任何操作會理它的螞蟻線 —— 而且移動工具在文字圖層
    /// 走的是整層平移，連「在框外點一下取消選取」都碰不到，看起來就是清不掉。
    /// 不推 history：換圖層不是一步編輯。
    /// </summary>
    private void DropSelectionOnTextLayer()
    {
        if (Selection == null) return;
        if (Document.ActiveLayer is RasterLayer { IsTextLayer: true }) ApplySelection(null);
    }

    /// <summary>
    /// 換作用中圖層（UI thread，鎖外呼叫）：先把上一層還浮著的東西落地，再切過去。
    ///
    /// 變形框與浮動內容都綁在原本那一層。留著不落地的話，把手框會一直優先顯示那個舊的變形框
    /// （見 <see cref="HandleDragController.GetFrame"/> 的優先序），新圖層的內容框就長不出來
    /// —— 使用者回報的「縮放過之後再點到另一個圖層，不會自動框住那層的東西」就是這個。
    /// 切走工具時本來就會落地（MainWindow.SelectTool），換圖層是同一件事。
    /// </summary>
    public void SetActiveLayer(LayerNode node)
    {
        if (ReferenceEquals(Document.ActiveLayer, node)) return;
        if (Transform != null && !ReferenceEquals(Transform.Target, node)) CommitTransform();
        CommitFloating();
        lock (Document.SyncRoot) Document.ActiveLayer = node;
    }

    /// <summary>
    /// 換作用中圖層：放掉文字圖層上的像素選取，並讓圖層內容框重新自動出現一次
    /// （「點進圖層時永遠先框一次」；上一層被點掉的狀態不帶到新圖層）。
    /// </summary>
    private void OnActiveLayerChanged()
    {
        DropSelectionOnTextLayer();
        LayerFrameDismissed = false;
    }

    public void Dispose()
    {
        Document.ActiveLayerChanged -= OnActiveLayerChanged;
        // 先讓合成器停下來：下面要釋放的浮動影像／殘影／覆疊快照，worker 正在畫的就是它們
        Compositor.StopRendering();
        Transform?.DisposeDeferred(Compositor); // 退役佇列由 Compositor.Dispose 清掉
        Transform = null;
        Floating?.Dispose();
        Floating = null;
        if (_floatingResume is { } fr) Compositor.Retire(fr.Pixels);
        _floatingResume = null;
        if (_ghost is { } ghost) Compositor.Retire(ghost.Image); // Compositor.Dispose 會清掉退役佇列
        _ghost = null;
        _layerOverlay?.Retire(Compositor);
        _layerOverlay = null;
        Compositor.Dispose();
        EraseBaseline.Dispose();
        History.Dispose();
        Document.Dispose();
    }
}
