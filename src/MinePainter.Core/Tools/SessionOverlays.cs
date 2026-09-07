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

using OverlayGhost = MinePainter.Core.Tools.EditorSession.OverlayGhost;
using ElementDragOverlay = MinePainter.Core.Tools.EditorSession.ElementDragOverlay;
using LayerDragOverlay = MinePainter.Core.Tools.EditorSession.LayerDragOverlay;

/// <summary>
/// 擁有手勢覆疊與交接殘影；所有快照都經合成器退役，避免 render thread 使用已釋放的影像。
/// Session 只轉送操作，不直接改這裡的覆疊狀態。
/// </summary>
internal sealed class SessionOverlays(EditorSession session)
{
    private Document Document => session.Document;
    private Compositor Compositor => session.Compositor;
    private TransformSession? Transform => session.Transform;
    private bool LiveElementRendering => session.LiveElementRendering;
    private static bool RenderEffectsWhileDragging => EditorSession.RenderEffectsWhileDragging;
    private void RefreshSelectionHandles() => session.RefreshSelectionHandles();

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
    public (Guid? Id, bool IncludesElements) DetachedLayer =>
        _layerOverlay is { HandingOver: false } o ? (o.Layer.Id, o.IncludesElements) : (null, false);

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
    /// 浮動內容要消失了：走覆疊路徑的話先留一張殘影頂著（見 <see cref="OverlayGhost"/>）。
    /// <paramref name="rect"/> 是殘影該出現的位置 —— 落地是新位置，取消是原位置。
    /// </summary>
    public void LeaveGhost(FloatingSelection floating, SKRect rect, bool overlaid)
    {
        if (!overlaid) return;
        var region = SKRectI.Round(rect);
        region.Inflate(2, 2);
        if (_ghost is { } old) Compositor.Retire(old.Image);
        _ghost = new OverlayGhost(floating.DetachPixels(), rect, region);
    }

    /// <summary>合成器停止後，依原順序退役仍待交接的影像。</summary>
    public void RetireAfterRenderingStopped()
    {
        if (_ghost is { } ghost) Compositor.Retire(ghost.Image);
        _ghost = null;
        _layerOverlay?.Retire(Compositor);
        _layerOverlay = null;
    }
}
