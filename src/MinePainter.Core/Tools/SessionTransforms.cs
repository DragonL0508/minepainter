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
/// 擁有進行中的變形，集中開始／續接／重設／提交與取消。
/// 浮動內容與把手框仍交由 Session 協調，保留互斥與單一步 Undo 的順序。
/// </summary>
internal sealed class SessionTransforms(EditorSession session)
{
    private Document Document => session.Document;
    private HistoryManager History => session.History;
    private Compositor Compositor => session.Compositor;
    private FloatingSelection? Floating => session.Floating;
    private ITool ActiveTool => session.ActiveTool;
    private MoveTool Move => session.Move;
    private (Guid LayerId, Guid ElementId)? SelectedElement => session.SelectedElement;
    private void CommitFloating() => session.CommitFloating();
    private void Notify(string message) => session.Notify(message);
    private void RefreshSelectionHandles() => session.RefreshSelectionHandles();

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

    /// <summary>合成器停止後釋放變形，仍沿用延後退役影像的路徑。</summary>
    public void DisposeAfterRenderingStopped()
    {
        Transform?.DisposeDeferred(Compositor);
        Transform = null;
    }
}
