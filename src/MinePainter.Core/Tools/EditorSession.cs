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
public sealed partial class EditorSession : IDisposable
{
    private readonly SessionOverlays _overlays;
    private readonly SessionTransforms _transforms;
    private readonly FloatingEditor _floatingEditor;

    public Document Document { get; }
    public Compositor Compositor { get; }
    public HistoryManager History { get; }
    public StrokeBuffer StrokeBuffer { get; } = new();

    public SKColor Foreground { get; set; } = SKColors.Black;

    private SelectionMask? _selection;
    private (Guid LayerId, Guid ElementId)? _selectedElement;

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
            if (ElementOverlay is { } overlay && value?.ElementId != overlay.ElementId)
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

    /// <summary>開始或切換變形模式。</summary>
    public TransformSession? EnterTransformMode(TransformMode mode) => _transforms.EnterTransformMode(mode);

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
            if (frame is { } f && ElementOverlay is { } overlay && SelectedElement?.ElementId == overlay.ElementId)
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

    /// <summary>目前框住物件的可重設旋轉角度。</summary>
    public float? FrameRotation => _transforms.FrameRotation;

    /// <summary>目前框住的物件是否有可重設的變形。</summary>
    public bool CanResetTransform => _transforms.CanResetTransform;

    /// <summary>回到原始角度與比例，維持單一步 Undo。</summary>
    public bool ResetTransform() => _transforms.ResetTransform();

    /// <summary>文字工具剛建立的元素（UI 應立即開啟畫布內編輯）。</summary>
    public (Guid LayerId, Guid ElementId)? PendingTextEdit { get; set; }

    /// <summary>目前浮動中的選取內容。</summary>
    public FloatingSelection? Floating => _floatingEditor.Floating;
    /// <summary>進行中的變形框 session。</summary>
    public TransformSession? Transform => _transforms.Transform;

    /// <summary>開始變形，先落地互斥的浮動內容。</summary>
    public TransformSession? BeginTransform() => _transforms.BeginTransform();

    /// <summary>目標是否仍有可續接的原始高清來源。</summary>
    public bool HasResumeFor(LayerNode target) => _transforms.HasResumeFor(target);

    /// <summary>落地變形並記單一步 Undo；原狀不記空步驟。</summary>
    public void CommitTransform() => _transforms.CommitTransform();

    /// <summary>取消變形，還原原始像素與物件。</summary>
    public void CancelTransform() => _transforms.CancelTransform();

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
            var floating = Floating;
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
    private FloatingSelection? FloatingForCompositor => FloatingOverlay == null ? Floating : null;

    /// <summary>浮動內容目前是否走覆疊路徑（工具用來決定要不要通知合成器重畫）。</summary>
    public bool IsFloatingOverlaid => FloatingOverlay != null;

    /// <summary>render thread 讀取的交接殘影。</summary>
    public OverlayGhost? Ghost => _overlays.Ghost;

    /// <summary>合成器追上後收掉覆疊與殘影。</summary>
    public void CollectOverlayGhost() => _overlays.CollectOverlayGhost();

    /// <summary>render thread 讀取的物件手勢覆疊。</summary>
    public ElementDragOverlay? ElementOverlay => _overlays.ElementOverlay;

    internal bool OverlayReusedCache => _overlays.OverlayReusedCache;

    /// <summary>在 Document.SyncRoot 內建立物件拖曳覆疊。</summary>
    public void BeginElementOverlayLocked(RasterLayer layer, VectorElement element) =>
        _overlays.BeginElementOverlayLocked(layer, element);

    /// <summary>平移物件預覽並同步把手框。</summary>
    public void MoveElementOverlay(float dx, float dy) => _overlays.MoveElementOverlay(dx, dy);

    /// <summary>依物件軸心旋轉預覽。</summary>
    public void RotateElementOverlay(float degrees, SKPoint pivot) => _overlays.RotateElementOverlay(degrees, pivot);

    /// <summary>依把手框的變換縮放預覽。</summary>
    public void ScaleElementOverlay(SKRect oldFrame, SKRect newFrame) => _overlays.ScaleElementOverlay(oldFrame, newFrame);

    /// <summary>在 Document.SyncRoot 內交還物件；必要時留下殘影。</summary>
    public void EndElementOverlayLocked(bool discardGhost = false) => _overlays.EndElementOverlayLocked(discardGhost);

    /// <summary>render thread 讀取的整層拖曳覆疊。</summary>
    public LayerDragOverlay? LayerOverlay => _overlays.LayerOverlay;

    private (Guid? Id, bool IncludesElements) DetachedLayer => _overlays.DetachedLayer;

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


    /// <summary>開始整層覆疊；不能維持相同合成結果時回傳 false。</summary>
    public bool BeginLayerDrag(RasterLayer layer) => _overlays.BeginLayerDrag(layer);

    /// <summary>把整層覆疊交還合成器。</summary>
    public void EndLayerDrag() => _overlays.EndLayerDrag();

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

    /// <summary>提起選取像素；已在浮動中時回傳現有內容。</summary>
    public FloatingSelection? LiftSelection() => _floatingEditor.LiftSelection();

    /// <summary>提起整層內容，包含畫布外像素。</summary>
    public FloatingSelection? LiftLayerContent() => _floatingEditor.LiftLayerContent();

    /// <summary>落地浮動內容與選取，記錄單一步 Undo。</summary>
    public void CommitFloating() => _floatingEditor.CommitFloating();

    /// <summary>還原提起前的內容，或取消尚未落地的貼上。</summary>
    public void CancelFloating() => _floatingEditor.CancelFloating();

    /// <summary>把選取複製到暫定新圖層；取消時一併移除。</summary>
    public FloatingSelection? LiftSelectionAsCopy() => _floatingEditor.LiftSelectionAsCopy();

    /// <summary>接手外部影像，貼成浮動內容。</summary>
    public bool PasteImage(SKImage pixels, SKPointI position) => _floatingEditor.PasteImage(pixels, position);

    /// <summary>依快速模式比例取得貼上尺寸。</summary>
    public (int Width, int Height) PastedSize(int width, int height) => _floatingEditor.PastedSize(width, height);
    /// <summary>取作用中圖層在選取內的樣貌；呼叫者接手影像。</summary>
    public SKImage? CopyToImage() => CopyToImage(out _);

    /// <summary>取作用中圖層的樣貌，同時回報文件座標原點。</summary>
    public SKImage? CopyToImage(out SKPointI origin) => SessionPixelReader.CopyToImage(this, out origin);

    /// <summary>讀取合成像素，補上直接覆疊顯示的浮動內容。</summary>
    public SKColor SampleComposite(int x, int y) => SessionPixelReader.SampleComposite(this, x, y);

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
        _overlays = new SessionOverlays(this);
        _transforms = new SessionTransforms(this);
        _floatingEditor = new FloatingEditor(this, _overlays.LeaveGhost);
        Compositor = new Compositor(document, StrokeBuffer,
            () => FloatingForCompositor, () => DetachedLayer);
        History = new HistoryManager(document);
        RegisterPendingEdit(new FloatingPendingEdit(this));
        RegisterPendingEdit(new TransformPendingEdit(this));
        History.Changed += _floatingEditor.ReleaseStaleResumes; // 續接點只在「落地那步仍是最後一步」時有效
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
        _transforms.DisposeAfterRenderingStopped();
        _floatingEditor.DisposeAfterRenderingStopped();
        _overlays.RetireAfterRenderingStopped();
        Compositor.Dispose();
        EraseBaseline.Dispose();
        History.Dispose();
        Document.Dispose();
    }
}
