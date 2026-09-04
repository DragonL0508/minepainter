using MinePainter.Core.Compositing;
using MinePainter.Core.Documents;
using MinePainter.Core.History;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Vectors;
using SkiaSharp;

namespace MinePainter.Core.Tools;

/// <summary>
/// 變形框 session（移動工具）：把作用中圖層或整個群組的像素與文字物件框起來
/// 移動／縮放／旋轉。與浮動選取同級的「進行中編輯」（IPendingEdit）。
///
/// 核心不變量：**整段 session 期間永遠以「開始時提起的原始像素 × 單一累積矩陣」重取樣** ——
/// 縮小再拉大不會糊；回到恰好原狀（identity）時直接還原快照，一個位元都不差。
/// 只有落地（<see cref="EditorSession.CommitTransform"/>）那一次才真正烙進圖層。
///
/// 效能分兩條路：
/// - 縮放/旋轉改變 → 全量重蓋章（拖曳中 Low、手勢結束補 High）。
/// - **純平移（尺寸與角度沒變）→ 不重蓋章**，位移放進各層的 Offset（蓋章的像素不動）——
///   大圖放大後拖著走的成本從「整片重取樣」變成「改一個位移值」；單一圖層時
///   呼叫端還能再接上拖曳覆疊快路徑（BeginLayerDrag，render thread 直接畫，零重合成）。
///
/// 開始時不動任何像素（零成本）；第一次偏離 identity 才清掉原像素改為蓋章。
/// 只在 UI thread 操作；像素/元素的讀寫都在 Document.SyncRoot 內。
/// </summary>
public sealed class TransformSession : IDisposable
{
    private sealed class Item
    {
        public required RasterLayer Layer;
        public required SKImage? Pixels;         // null = 該層沒有像素（可能只有文字）
        public required SKRectI SrcBounds;       // 像素內容的 doc 範圍（Pixels 的位置；Offset=Base 時）
        public required SKPointI BaseOffset;     // 開始時的圖層 Offset（平移位移疊在它之上）
        public required TileSnapshot Before;
        public required VectorElement[] StartElements;
        public SKRectI LastStamp;                // 目前蓋章的 doc 範圍（Offset=Base 基準；呈現位置再加 OffsetDelta）

        /// <summary>
        /// Pixels 的擁有權在本 session 手上。false＝借用圖層的 <see cref="LayerPixelSource"/>
        /// （續接時），釋放由圖層負責，session 結束不能動它。
        /// </summary>
        public bool OwnsPixels = true;

        /// <summary>進入四角／彎曲模式時的文字物件（已含矩形模式的變形）：網格變形疊在它們的輸出端。</summary>
        public Dictionary<Guid, VectorElement>? MeshStartElements;
    }

    /// <summary>
    /// 文字物件在目前狀態下該長什麼樣：矩形模式走參數式 TransformedBy；
    /// 四角／彎曲模式把單應／網格疊在進入時的物件輸出端（排版參數不動 → 文字永遠可編輯）。
    /// </summary>
    private VectorElement TransformedElement(Item item, VectorElement start, SKMatrix m, float sx, float sy)
    {
        if (IsMeshMode && item.MeshStartElements != null &&
            item.MeshStartElements.TryGetValue(start.Id, out var meshStart) && meshStart is TextElement text)
        {
            if (_warp != null) return IsWarpChanged ? text.Warped(_warp) : text;
            if (_quad != null && _quadStart != null)
                return IsQuadChanged ? text.Deformed(QuadGeometry.QuadToQuad(_quadStart, _quad)) : text;
            return text;
        }
        if (_stripDeform && start is TextElement t) start = t.WithoutDeform();
        return start.TransformedBy(m, sx, sy, DeltaRotation);
    }

    /// <summary>進入網格模式：把此刻的文字物件記下來當網格變形的輸入端。</summary>
    private void CaptureMeshStartElements()
    {
        lock (_doc.SyncRoot)
        {
            foreach (var item in _items)
            {
                if (item.StartElements.Length == 0) continue;
                var dict = new Dictionary<Guid, VectorElement>();
                foreach (var start in item.StartElements)
                {
                    if (item.Layer.FindElement(start.Id) is { } current) dict[start.Id] = current;
                }
                item.MeshStartElements = dict;
            }
        }
    }

    /// <summary>有沒有帶著透視／彎曲的文字（重設鈕要亮：拿掉才是最原始）。</summary>
    public bool HasDeformedElements =>
        _items.Any(i => i.StartElements.Any(e => e is TextElement { Deform: not null }));

    /// <summary>單層內容的尺寸上限（單邊），與整層提起相同的保險。</summary>
    private const int MaxContentSide = 16384;

    private readonly Document _doc;
    private readonly List<Item> _items;
    private bool _disposed;

    // 續接（見 TransformResume）：原始像素 → 上一輪落地結果 的映射（doc 座標）。
    // 像素一律以「原始像素 × Pre × 本輪矩陣」重取樣；文字物件與框只吃本輪矩陣（它們本來就在落地結果上）。
    private SKMatrix _preMatrix = SKMatrix.Identity;
    private bool _preIsIdentity = true;
    private float _baseRotation;     // 續接時開始的角度（IsIdentity / 相對旋轉的基準）

    // ---- 四角模式（透視／扭曲，Photoshop「編輯 → 變形 → 透視／扭曲」）----
    // 進入四角模式時把矩形模式的狀態凍結：之後的映射 = 單應(起始四角 → 目前四角) × 凍結時的矩陣。
    // 像素照常以「原始像素 × 單一累積矩陣」重取樣（矩陣含透視項，Skia 直接吃），不變量不變。
    private SKPoint[]? _quad;         // 目前四角（doc 座標；0 左上 1 右上 2 右下 3 左下）
    private SKPoint[]? _quadStart;    // 進入時的框四角（＝四角模式的 identity）
    private SKMatrix _quadBase = SKMatrix.Identity; // 進入時的矩形模式矩陣
    private SKPoint[]? _stampedQuad;  // 蓋章時的四角（Offset=Base 基準；純平移判斷用）

    /// <summary>四角模式中的四角（null＝矩形模式）。陣列視為 immutable，每次改動換新實例（render thread 直接讀）。</summary>
    public SKPoint[]? Quad => _quad;

    /// <summary>TargetRect 往外加效果外擴（外框／陰影／光暈畫在像素之外，框要包住它們）。</summary>
    private SKRect PaddedTargetRect
    {
        get
        {
            var pad = HandleDragController.EffectPad(Target);
            var r = TargetRect;
            if (pad > 0) r.Inflate(pad, pad);
            return r;
        }
    }

    /// <summary>目前框在畫面上的外接矩形：四角模式取四角外框，矩形模式就是 TargetRect。</summary>
    public SKRect FrameRect => _warp != null ? _warp.Bounds : _quad != null ? QuadGeometry.Bounds(_quad) : TargetRect;

    /// <summary>把手框該畫的旋轉角：四角／彎曲模式的框本身就是網格，不再另外轉。</summary>
    public float DisplayRotation => IsMeshMode ? 0f : RotationDeg;

    /// <summary>文字物件也能透視／彎曲（變形疊在輸出端，文字仍可編輯），任何目標都能進網格模式。</summary>
    public bool CanUseQuad => true;

    /// <summary>
    /// 進入四角模式：以目前框（含旋轉）的四角為起點。已在四角模式回 true；有文字物件回 false。
    /// 矩形模式的 TargetRect／RotationDeg 之後凍結不再變（呼叫端一律改四角）。
    /// </summary>
    public bool EnterQuadMode()
    {
        if (_disposed) return false;
        if (_quad != null) return true;
        if (!CanUseQuad) return false;

        _quadBase = Matrix; // 矩形模式的累積矩陣（此時 _quad 仍為 null）
        // 起始四角用「含效果外擴」的框：把手才會框在外框／陰影之外。單應映射定義在整個平面上，
        // 參考矩形取哪個都一樣，像素照樣對得上。
        var padded = PaddedTargetRect;
        var center = new SKPoint(padded.MidX, padded.MidY);
        _quadStart = QuadGeometry.Rotated(QuadGeometry.Corners(padded), center, RotationDeg);
        _quad = (SKPoint[])_quadStart.Clone();
        // 目前蓋章的像素位置 = 現在的框 − 純平移位移（蓋章一律在 Offset=Base 基準）
        _stampedQuad = QuadGeometry.Translated(_quadStart, -OffsetDelta.X, -OffsetDelta.Y);
        CaptureMeshStartElements();
        return true;
    }

    /// <summary>設定四角（拒絕凹／翻面／退化的四邊形，回 false 表示沒改）。之後要呼叫 Apply。</summary>
    public bool SetQuad(SKPoint[] quad)
    {
        if (_disposed || _quad == null || quad.Length != 4) return false;
        if (!QuadGeometry.IsConvex(quad)) return false;
        if (QuadGeometry.NearlyEqual(quad, _quad, 0.001f)) return false;
        _quad = (SKPoint[])quad.Clone();
        return true;
    }

    /// <summary>四角回到進入四角模式時的位置（「重設」鈕）。</summary>
    public void ResetQuad()
    {
        if (_quad == null || _quadStart == null) return;
        _quad = (SKPoint[])_quadStart.Clone();
    }

    /// <summary>四角模式且四角已偏離起點。</summary>
    public bool IsQuadChanged => _quad != null && _quadStart != null && !QuadGeometry.NearlyEqual(_quad, _quadStart);

    // ---- 彎曲模式（Photoshop「彎曲」；使用者稱「扭曲」）----
    // 疊在目前狀態之上：像素先走 PixelMatrix（含矩形／四角模式的累積映射）落在平的框裡，
    // 再由 4×4 貝茲曲面把框映射到曲面（WarpMesh.Draw 以三角網格貼圖）。彎曲不是矩陣，落地後不留續接點。
    private WarpMesh? _warp;
    private WarpMesh? _warpStart;
    private SKMatrix _warpBase = SKMatrix.Identity;
    private WarpMesh? _stampedWarp;

    /// <summary>彎曲模式中的網格（null＝不在彎曲模式）。</summary>
    public WarpMesh? Warp => _warp;

    public bool IsWarpChanged => _warp != null && _warpStart != null &&
                                 !QuadGeometry.NearlyEqual(_warp.Points, _warpStart.Points, 0.01f);

    /// <summary>進入彎曲模式：以目前框的外接矩形鋪平網格。有文字物件回 false（呼叫端先平面化）。</summary>
    public bool EnterWarpMode()
    {
        if (_disposed) return false;
        if (_warp != null) return true;
        if (!CanUseQuad) return false;

        _warpBase = Matrix; // 目前（矩形或四角模式）的累積矩陣
        // 網格框同樣含效果外擴（貝茲映射的參考框取哪個都一樣）
        var paddedRect = PaddedTargetRect;
        var frame = _quad != null
            ? QuadGeometry.Bounds(_quad)
            : QuadGeometry.Bounds(QuadGeometry.Rotated(QuadGeometry.Corners(paddedRect),
                new SKPoint(paddedRect.MidX, paddedRect.MidY), RotationDeg));
        _warpStart = WarpMesh.Flat(frame);
        _warp = _warpStart;
        _stampedWarp = _warpStart.Translated(-OffsetDelta.X, -OffsetDelta.Y);
        CaptureMeshStartElements();
        return true;
    }

    public bool SetWarp(WarpMesh mesh)
    {
        if (_disposed || _warp == null) return false;
        if (QuadGeometry.NearlyEqual(mesh.Points, _warp.Points, 0.001f)) return false;
        _warp = mesh;
        return true;
    }

    public void ResetWarp()
    {
        if (_warp == null || _warpStart == null) return;
        _warp = _warpStart;
    }

    /// <summary>四角或彎曲模式（把手框不是矩形）。</summary>
    public bool IsMeshMode => _quad != null || _warp != null;

    // 蓋章狀態：目前圖層裡的像素是用哪組參數蓋出來的
    private (float Sx, float Sy, float Rot, float W, float H) _stampedParams;
    private SKPoint _stampedOrigin;  // 蓋章時 TargetRect 的左上（呈現位置 = 這裡 + OffsetDelta）
    private bool _stampedHigh;       // 蓋章已是最終品質（純平移的 None 視為無損）
    private bool _pixelsStamped;     // 已經動過像素（false = 圖層裡還是原始像素）

    public bool IsGroup { get; }

    /// <summary>開始時的內容外框（像素 ∪ 文字物件；doc 座標，可超出畫布）。</summary>
    public SKRect SourceRect { get; private set; }

    /// <summary>「重設」把續接的前段也丟掉（回到最初的原始像素）後，identity 不再等於開始時的快照。</summary>
    private bool _resetFromResume;

    /// <summary>
    /// 有沒有東西可以重設：任何角度／尺寸／四角／網格偏離原始，或這是續接的 session
    /// （上一輪的變形也算，重設要回到「最原始」的狀態）。
    /// </summary>
    /// <summary>重設後文字物件拿掉透視／彎曲（回到排版本身）。</summary>
    private bool _stripDeform;

    public bool CanReset =>
        IsResumed && !_resetFromResume ||
        HasDeformedElements && !_stripDeform ||
        IsQuadChanged || IsWarpChanged ||
        Math.Abs(RotationDeg) > 0.01f ||
        Math.Abs(TargetRect.Width - ResetSize.Width) > 0.5f ||
        Math.Abs(TargetRect.Height - ResetSize.Height) > 0.5f;

    /// <summary>
    /// 重設回最原始的狀態與比例：退出四角／彎曲模式、角度 0、尺寸回 ResetSize（維持目前中心）；
    /// 續接的 session 連上一輪落地的縮放／旋轉／透視一起丟掉 —— 像素本來就是最初提起的那份，
    /// 改成只做平移重蓋一次即可（無損）。使用者只要求「回到最原始」，位置留在原地。
    /// </summary>
    public void ResetAll()
    {
        if (_disposed) return;
        var frame = FrameRect;
        var center = new SKPoint(frame.MidX, frame.MidY);

        _quad = null; _quadStart = null; _stampedQuad = null;
        _warp = null; _warpStart = null; _stampedWarp = null;
        RotationDeg = 0f;
        if (HasDeformedElements) _stripDeform = true; // 文字的透視／彎曲一併拿掉
        // 蓋章狀態標成未知：強制下一次 Apply 走全量路徑（純平移的捷徑在位移為 0 時會直接略過，
        // 文字物件就不會被重新算一次、透視／彎曲拿不掉）
        _stampedParams = (float.NaN, float.NaN, float.NaN, float.NaN, float.NaN);

        if (IsResumed)
        {
            // 丟掉前段：原始像素 × 純平移 就是「最原始」。SourceRect 改成原始像素的外框，
            // 蓋章狀態標成未知（強制重蓋），identity 也不再成立（開始時的快照是上一輪的結果）。
            SKRect? src = null;
            foreach (var item in _items)
            {
                if (item.Pixels == null) continue;
                var r = new SKRect(item.SrcBounds.Left, item.SrcBounds.Top, item.SrcBounds.Right, item.SrcBounds.Bottom);
                src = src is { } a
                    ? new SKRect(Math.Min(a.Left, r.Left), Math.Min(a.Top, r.Top), Math.Max(a.Right, r.Right), Math.Max(a.Bottom, r.Bottom))
                    : r;
            }
            if (src is { } original)
            {
                _preMatrix = SKMatrix.Identity;
                _preIsIdentity = true;
                _baseRotation = 0f;
                SourceRect = original;
                ResetSize = original.Size;
                _resetFromResume = true;
                _stampedParams = (float.NaN, float.NaN, float.NaN, float.NaN, float.NaN); // 強制重蓋章
            }
        }

        // 位移取整：純平移的蓋章才是無損（None 取樣）
        var left = MathF.Round(center.X - ResetSize.Width / 2f);
        var top = MathF.Round(center.Y - ResetSize.Height / 2f);
        TargetRect = SKRect.Create(left, top, ResetSize.Width, ResetSize.Height);
    }

    /// <summary>目前的目標矩形（軸對齊；移動/縮放都改這個）。</summary>
    public SKRect TargetRect { get; set; }

    /// <summary>順時針旋轉角度（度），以 TargetRect 中心為軸。</summary>
    public float RotationDeg { get; set; }

    /// <summary>純平移累積的位移（各層 Offset = BaseOffset + 這個值）。</summary>
    public SKPointI OffsetDelta { get; private set; }

    /// <summary>單一圖層的 session 才能走拖曳覆疊快路徑。</summary>
    public RasterLayer? SoleLayer => _items.Count == 1 ? _items[0].Layer : null;

    /// <summary>
    /// 縮放/旋轉手勢期間 render thread 直接畫的預覽（null = 無）。
    /// 拖曳中只換 Matrix，一格 tile 都不重寫、不重合成 —— 大圖的縮放/旋轉才跟得上滑鼠。
    /// </summary>
    public sealed class GestureOverlay
    {
        /// <summary>每一項附上它屬於哪一層 —— GPU 路徑要照層序把它畫在對的位置。</summary>
        public required (RasterLayer Layer, SKImage Image, SKRectI SrcBounds)[] Items { get; init; }
        public required SKMatrix Matrix { get; init; }

        /// <summary>彎曲模式：矩陣之後再套這張網格（WarpMesh.Draw）；null＝只有矩陣。</summary>
        public WarpMesh? Warp { get; init; }

        /// <summary>手勢已結束、蓋章已寫入：等合成器把 <see cref="HandoverRegion"/> 畫完才收掉（不閃）。</summary>
        public required bool HandingOver { get; init; }

        public required SKRectI HandoverRegion { get; init; }
    }

    private volatile GestureOverlay? _overlay;
    private bool _gestureOverlay;      // 手勢覆疊進行中（像素已從合成結果拿掉）
    private bool _overlayEverPublished;

    /// <summary>render thread 每幀讀。</summary>
    public GestureOverlay? Overlay => _overlay;

    /// <summary>本輪相對於開始時的旋轉（續接時以上一輪落地的角度為 0）。</summary>
    private float DeltaRotation
    {
        get
        {
            var d = RotationDeg - _baseRotation;
            return Math.Abs(d) < 0.01f ? 0f : d;
        }
    }

    private bool RectIsIdentity => TargetRect == SourceRect && DeltaRotation == 0f;

    public bool IsIdentity => !_resetFromResume && !_stripDeform && RectIsIdentity && !IsQuadChanged && !IsWarpChanged;

    /// <summary>
    /// 「重設角度與比例」該回到的尺寸：第一輪＝SourceRect；續接時＝最初提起時的原始尺寸
    /// （不是上一輪落地的尺寸，重設才真的是重設）。
    /// </summary>
    public SKSize ResetSize { get; private set; }

    /// <summary>本 session 是從上一輪落地結果續接的（像素仍以最初的原始像素重取樣）。</summary>
    public bool IsResumed => !_preIsIdentity;

    /// <summary>變形的目標（作用中圖層或群組；續接點要對得上同一個）。</summary>
    public LayerNode Target { get; }

    private TransformSession(Document doc, LayerNode target, List<Item> items, SKRect sourceRect)
    {
        _doc = doc;
        Target = target;
        _items = items;
        SourceRect = sourceRect;
        TargetRect = sourceRect;
        ResetSize = sourceRect.Size;
        IsGroup = target is GroupLayer;
        ResetStampStateToOriginal();
    }

    /// <summary>「原始像素」本身就是 identity 參數的無損蓋章 —— 純移動的 session 從頭到尾不必重蓋。</summary>
    private void ResetStampStateToOriginal()
    {
        _stampedParams = (1f, 1f, 0f, SourceRect.Width, SourceRect.Height);
        _stampedOrigin = new SKPoint(SourceRect.Left, SourceRect.Top);
        _stampedHigh = true;
        _pixelsStamped = false;
        OffsetDelta = SKPointI.Empty;
        // 四角模式：原始像素 = 起始四角（identity 時 _quadBase 也是 identity）
        _stampedQuad = _quadStart == null ? null : (SKPoint[])_quadStart.Clone();
        _stampedWarp = _warpStart;
    }

    /// <summary>SourceRect → 目前狀態 的完整映射（縮放平移在前、旋轉在後）。</summary>
    public SKMatrix Matrix
    {
        get
        {
            // 彎曲模式：矩陣部分凍結（彎曲本身在 Stamp／覆疊時以網格套在矩陣之後）
            if (_warp != null) return _warpBase;

            // 四角模式：單應(起始四角 → 目前四角) 疊在凍結的矩形模式矩陣上
            if (_quad != null && _quadStart != null)
            {
                if (!IsQuadChanged) return _quadBase;
                return SKMatrix.Concat(QuadGeometry.QuadToQuad(_quadStart, _quad), _quadBase);
            }

            var (sx, sy) = Scales;
            var m = SKMatrix.CreateScaleTranslation(sx, sy,
                TargetRect.Left - SourceRect.Left * sx,
                TargetRect.Top - SourceRect.Top * sy);
            var rot = DeltaRotation;
            if (rot != 0f)
            {
                m = SKMatrix.Concat(
                    SKMatrix.CreateRotationDegrees(rot, TargetRect.MidX, TargetRect.MidY), m);
            }
            return m;
        }
    }

    /// <summary>原始像素（Item.SrcBounds）→ 目前狀態 的映射：續接時多乘一段上一輪的結果。</summary>
    private SKMatrix PixelMatrix => _preIsIdentity ? Matrix : SKMatrix.Concat(Matrix, _preMatrix);

    /// <summary>像素矩陣是不是整數平移（蓋章可用 None 取樣，逐位元無損）。</summary>
    private static bool IsIntegerTranslation(SKMatrix m) =>
        Math.Abs(m.ScaleX - 1f) < 0.0001f && Math.Abs(m.ScaleY - 1f) < 0.0001f &&
        Math.Abs(m.SkewX) < 0.0001f && Math.Abs(m.SkewY) < 0.0001f &&
        Math.Abs(m.Persp0) < 1e-7f && Math.Abs(m.Persp1) < 1e-7f && Math.Abs(m.Persp2 - 1f) < 0.0001f &&
        Math.Abs(m.TransX - MathF.Round(m.TransX)) < 0.001f &&
        Math.Abs(m.TransY - MathF.Round(m.TransY)) < 0.001f;

    private (float Sx, float Sy) Scales => (
        SourceRect.Width > 0.5f ? TargetRect.Width / SourceRect.Width : 1f,
        SourceRect.Height > 0.5f ? TargetRect.Height / SourceRect.Height : 1f);

    /// <summary>
    /// 對作用中圖層（或群組 = 所有子孫點陣圖層）開始變形。
    /// 不動像素，只做快照；無內容或內容過大時回傳 null（reason 帶原因）。
    /// </summary>
    public static TransformSession? Begin(Document doc, LayerNode target, out string? reason)
    {
        reason = null;
        var layers = new List<RasterLayer>();
        switch (target)
        {
            case RasterLayer r: layers.Add(r); break;
            case GroupLayer g: Collect(g, layers); break;
            default:
                reason = "此圖層類型無法變形";
                return null;
        }

        var items = new List<Item>();
        SKRect? source = null;
        void Accumulate(SKRect r) =>
            source = source is { } a
                ? new SKRect(Math.Min(a.Left, r.Left), Math.Min(a.Top, r.Top),
                    Math.Max(a.Right, r.Right), Math.Max(a.Bottom, r.Bottom))
                : r;

        lock (doc.SyncRoot)
        {
            foreach (var layer in layers)
            {
                var content = layer.Surface.ExactContentBounds();
                var hasPixels = content.Width > 0 && content.Height > 0;
                if (hasPixels && (content.Width > MaxContentSide || content.Height > MaxContentSide))
                {
                    reason = "圖層內容過大，無法變形";
                    DisposeItems(items);
                    return null;
                }

                SKImage? pixels = null;
                var docRect = SKRectI.Empty;
                if (hasPixels)
                {
                    docRect = new SKRectI(
                        content.Left + layer.Offset.X, content.Top + layer.Offset.Y,
                        content.Right + layer.Offset.X, content.Bottom + layer.Offset.Y);
                    var info = new SKImageInfo(docRect.Width, docRect.Height,
                        SKColorType.Bgra8888, SKAlphaType.Premul);
                    using var surface = SKSurface.Create(info);
                    if (surface == null) continue;
                    surface.Canvas.Clear(SKColors.Transparent);
                    surface.Canvas.Save();
                    surface.Canvas.Translate(-docRect.Left, -docRect.Top);
                    Selections.FloatingSelection.DrawLayerPixels(layer, surface.Canvas, docRect);
                    surface.Canvas.Restore();
                    surface.Canvas.Flush();
                    pixels = surface.Snapshot();
                    Accumulate(new SKRect(docRect.Left, docRect.Top, docRect.Right, docRect.Bottom));
                }

                var elements = layer.HasElements ? layer.Elements.ToArray() : Array.Empty<VectorElement>();
                foreach (var el in elements)
                {
                    // 使用者看到的框：FrameBounds（貼著字），不是 Bounds（失效用的保守外擴，含效果邊、行高餘裕）
                    var b = el.FrameBounds;
                    if (b.IsEmpty)
                    {
                        var pb = el.Bounds;
                        b = new SKRect(pb.Left, pb.Top, pb.Right, pb.Bottom);
                    }
                    Accumulate(b);
                }

                if (pixels == null && elements.Length == 0) continue;
                items.Add(new Item
                {
                    Layer = layer,
                    Pixels = pixels,
                    SrcBounds = docRect,
                    BaseOffset = layer.Offset,
                    Before = layer.Surface.Snapshot(),
                    StartElements = elements,
                    LastStamp = docRect,
                });
            }
        }

        if (items.Count == 0 || source is not { } src || src.Width < 1 || src.Height < 1)
        {
            reason ??= "沒有可變形的內容";
            DisposeItems(items);
            return null;
        }
        return new TransformSession(doc, target, items, src);
    }

    /// <summary>
    /// 從上一輪落地結果續接（使用者縮小、落地、之後又把它拉大 —— 不能糊）：
    /// 像素改以 <paramref name="resume"/> 保留的最初原始像素重取樣，框與文字物件則從目前狀態出發，
    /// 所以 identity（沒動）＝上一輪結果、undo 也只退回上一輪結果。
    /// 目標圖層集合對不上（結構變了）時回傳 null，呼叫端退回 <see cref="Begin"/>。
    /// 成功時接手 resume 內像素的擁有權。
    /// </summary>
    public static TransformSession? Resume(Document doc, LayerNode target, TransformResume resume)
    {
        if (!ReferenceEquals(resume.Target, target)) return null;
        var layers = new List<RasterLayer>();
        switch (target)
        {
            case RasterLayer r: layers.Add(r); break;
            case GroupLayer g: Collect(g, layers); break;
            default: return null;
        }
        if (layers.Count != resume.Items.Length) return null;
        for (var i = 0; i < layers.Count; i++)
        {
            if (!ReferenceEquals(layers[i], resume.Items[i].Layer)) return null;
        }

        var items = new List<Item>();
        lock (doc.SyncRoot)
        {
            foreach (var (layer, pixels, srcBounds) in resume.Items)
            {
                if (layer.Document == null) { DisposeItems(items); return null; }
                var content = layer.Surface.ExactContentBounds();
                var current = content.Width > 0 && content.Height > 0
                    ? new SKRectI(
                        content.Left + layer.Offset.X, content.Top + layer.Offset.Y,
                        content.Right + layer.Offset.X, content.Bottom + layer.Offset.Y)
                    : SKRectI.Empty;
                items.Add(new Item
                {
                    Layer = layer,
                    Pixels = pixels,
                    SrcBounds = srcBounds,
                    BaseOffset = layer.Offset,
                    Before = layer.Surface.Snapshot(),
                    StartElements = layer.HasElements ? layer.Elements.ToArray() : Array.Empty<VectorElement>(),
                    LastStamp = current,
                    OwnsPixels = false, // 像素是圖層 LayerPixelSource 那份，session 只是借用
                });
            }
        }

        var session = new TransformSession(doc, target, items, resume.TargetRect)
        {
            _preMatrix = resume.PreMatrix,
            _preIsIdentity = false,
            _baseRotation = resume.RotationDeg,
            RotationDeg = resume.RotationDeg,
            ResetSize = resume.OriginalSize,
        };
        return session;
    }

    /// <summary>
    /// 落地後把「最初的原始像素 × 到目前為止的累積映射」掛回各個圖層（<see cref="LayerPixelSource"/>）：
    /// 之後不論隔多久、做過多少別的事、甚至存檔重開，只要沒有直接改到這層像素，
    /// 再變形都是從原始高清重取樣 —— 縮小落地後再拉大不會糊。
    /// 須在 <see cref="BuildCommit"/> 之後呼叫；像素的擁有權移交給圖層。
    /// </summary>
    internal void PublishPixelSources()
    {
        if (_disposed) return;
        if (!_pixelsStamped) return; // 像素沒被重取樣過（純平移）：原有的來源照舊有效

        // 彎曲不是矩陣、整數平移本來就無損 —— 兩種都沒有「保留原始」的價值，
        // 但像素已經被重蓋過，舊的來源對不上了，得清掉。
        var pm = PixelMatrix;
        if (_warp != null && IsWarpChanged || IsIntegerTranslation(pm))
        {
            foreach (var item in _items) item.Layer.SetPixelSource(null);
            return;
        }

        // 落地後 layer.Offset = BaseOffset + OffsetDelta，而 Matrix 是從（含位移的）TargetRect 算的，
        // 兩者指向同一個 doc 位置 —— 下一輪以 BaseOffset = 目前 Offset 蓋章，Pre 直接用 PixelMatrix 即可。
        // 四角模式落地後，下一輪的框是變形結果的外接矩形（PS 也一樣），角度視為 0（已烙進像素矩陣）
        var rect = _quad != null ? FrameRect : TargetRect;
        var rotation = _quad != null ? 0f : RotationDeg;

        foreach (var item in _items)
        {
            var layer = item.Layer;
            if (item.Pixels is not { } pixels)
            {
                layer.SetPixelSource(null);
                continue;
            }
            // 借用中的那份就是同一張影像：先 Detach 讓舊來源別把它釋放掉
            layer.PixelSource?.Detach();
            layer.SetPixelSource(new LayerPixelSource(pixels, item.SrcBounds, pm, layer.Offset,
                rect, rotation, ResetSize, layer.Surface.Revision));
            item.Pixels = null;      // 擁有權移交給圖層
            item.OwnsPixels = false;
        }
    }

    /// <summary>
    /// 沒有落地（Esc 取消、或恰好回到原狀無損還原）時，把借來的原始像素原封不動掛回圖層。
    /// 還原會動到像素（版本號變了），不重掛的話原本的來源會被判定失效、之後拉大就糊了。
    /// </summary>
    internal void RepublishBorrowedSources()
    {
        if (_disposed || _preIsIdentity) return;
        foreach (var item in _items)
        {
            var layer = item.Layer;
            if (item.OwnsPixels || item.Pixels is not { } pixels) continue;
            layer.PixelSource?.Detach();
            layer.SetPixelSource(new LayerPixelSource(pixels, item.SrcBounds, _preMatrix, item.BaseOffset,
                SourceRect, _baseRotation, ResetSize, layer.Surface.Revision));
            item.Pixels = null;
        }
    }

    private static void Collect(GroupLayer group, List<RasterLayer> into)
    {
        foreach (var child in group.Children)
        {
            switch (child)
            {
                case RasterLayer r: into.Add(r); break;
                case GroupLayer g: Collect(g, into); break;
            }
        }
    }

    /// <summary>
    /// 縮放/旋轉手勢開始：符合覆疊條件（各層上方都沒有看得見的東西）時，
    /// 把像素從合成結果拿掉一次，改由 render thread 每幀以目前矩陣直接畫。
    /// 條件不成立就維持逐步蓋章的合成器路徑（畫面正確優先於流暢）。
    /// </summary>
    public void BeginGesturePreview(bool live = false)
    {
        if (_disposed || _gestureOverlay) return;

        // 覆疊畫在所有圖層之上，所以舊路徑要求「這層上面沒有看得見的東西」，否則只好逐步蓋章
        // —— 那就是大圖旋轉時「手勢中完全沒有畫面、放開才跳出來」的來源。
        // 畫面端能照層序把覆疊畫在對的位置時（GPU 路徑），這個限制就不需要了。
        if (!live)
        {
            lock (_doc.SyncRoot)
            {
                foreach (var item in _items)
                {
                    if (!Selections.FloatingSelection.CanOverlay(item.Layer)) return;
                }
            }
        }

        _gestureOverlay = true;
        _pixelsStamped = true; // 像素被拿掉了，就算手勢回到 identity 也得還原快照
        foreach (var item in _items)
        {
            lock (_doc.SyncRoot)
            {
                ClearPixelTiles(item.Layer);
            }
            var display = OffsetRect(item.LastStamp, OffsetDelta);
            if (!display.IsEmpty) item.Layer.Invalidate(display);
            item.LastStamp = SKRectI.Empty;
        }
        PublishOverlay(handingOver: false);
    }

    /// <summary>
    /// 手勢結束：走覆疊時補一次 High 蓋章（覆疊殘影等合成器追上才收，不閃）；
    /// 沒走覆疊就照舊補 High。
    /// </summary>
    public void EndGesture()
    {
        if (_disposed) return;
        if (!_gestureOverlay)
        {
            Apply(preview: false);
            return;
        }
        _gestureOverlay = false;

        if (IsIdentity)
        {
            RestoreOriginal();
        }
        else
        {
            var (sx, sy) = Scales;
            var rot = DeltaRotation;
            StampAll(preview: false, sx, sy, rot);
        }
        PublishOverlay(handingOver: true);
    }

    private void PublishOverlay(bool handingOver)
    {
        var items = _items.Where(i => i.Pixels != null)
            .Select(i => (i.Layer, i.Pixels!, i.SrcBounds)).ToArray();
        if (items.Length == 0)
        {
            _overlay = null;
            return;
        }

        var region = SKRectI.Empty;
        if (handingOver)
        {
            foreach (var item in _items)
            {
                var display = OffsetRect(item.LastStamp, OffsetDelta);
                if (display.IsEmpty) continue;
                region = region.IsEmpty ? display : SKRectI.Union(region, display);
            }
        }

        _overlayEverPublished = true;
        _overlay = new GestureOverlay
        {
            Items = items,
            Matrix = PixelMatrix,
            Warp = _warp,
            HandingOver = handingOver,
            HandoverRegion = region,
        };
    }

    /// <summary>UI thread 每幀：合成器把蓋章區域畫完了，就收掉手勢覆疊的殘影。</summary>
    internal void CollectOverlay(Compositor compositor)
    {
        var state = _overlay;
        if (state is { HandingOver: true } &&
            (state.HandoverRegion.IsEmpty || compositor.IsRegionClean(state.HandoverRegion)))
        {
            _overlay = null;
        }
    }

    public void Apply(bool preview) => Apply(preview, null);

    /// <summary>
    /// 把目前的 TargetRect/RotationDeg 套到畫面上。
    /// <paramref name="pixelsHandledExternally"/>：回傳 true 的圖層像素改由外部呈現
    /// （拖曳覆疊快路徑），純平移時就不失效它的像素區域。
    /// </summary>
    public void Apply(bool preview, Func<RasterLayer, bool>? pixelsHandledExternally)
    {
        if (_disposed) return;

        // 手勢覆疊中：像素由 render thread 以目前矩陣直接畫，這裡只發布新矩陣、更新文字物件
        if (_gestureOverlay)
        {
            PublishOverlay(handingOver: false);
            UpdateElements();
            return;
        }

        if (IsIdentity)
        {
            if (_pixelsStamped || OffsetDelta != SKPointI.Empty) RestoreOriginal();
            return;
        }

        var (sx, sy) = Scales;
        var rot = Math.Abs(RotationDeg) < 0.01f ? 0f : RotationDeg;

        // 彎曲模式：網格只是整體平移了整數向量 → 純平移；否則全量重蓋章
        if (_warp != null)
        {
            if (_stampedWarp != null && (preview || _stampedHigh) &&
                QuadGeometry.IsIntegerTranslationOf(_warp.Points, _stampedWarp.Points, out var warpDelta))
            {
                TranslateTo(warpDelta, pixelsHandledExternally);
                return;
            }
            StampAll(preview, sx, sy, rot);
            return;
        }

        // 四角模式：四角只是整體平移了整數向量 → 純平移；否則全量重蓋章
        if (_quad != null)
        {
            if (_stampedQuad != null && (preview || _stampedHigh) &&
                QuadGeometry.IsIntegerTranslationOf(_quad, _stampedQuad, out var quadDelta))
            {
                TranslateTo(quadDelta, pixelsHandledExternally);
                return;
            }
            StampAll(preview, sx, sy, rot);
            return;
        }

        // 尺寸與角度沒變 → 純平移：不重蓋章（不重取樣），位移放進各層 Offset
        var s = _stampedParams;
        if (Math.Abs(s.Sx - sx) < 0.0001f && Math.Abs(s.Sy - sy) < 0.0001f &&
            Math.Abs(s.Rot - rot) < 0.01f &&
            Math.Abs(s.W - TargetRect.Width) < 0.5f && Math.Abs(s.H - TargetRect.Height) < 0.5f &&
            (preview || _stampedHigh))
        {
            var delta = new SKPointI(
                (int)MathF.Round(TargetRect.Left - _stampedOrigin.X),
                (int)MathF.Round(TargetRect.Top - _stampedOrigin.Y));
            TranslateTo(delta, pixelsHandledExternally);
            return;
        }

        StampAll(preview, sx, sy, rot);
    }

    /// <summary>純平移：只改各層 Offset 與文字物件位置，像素蓋章原地不動。</summary>
    private void TranslateTo(SKPointI delta, Func<RasterLayer, bool>? pixelsHandledExternally)
    {
        var old = OffsetDelta;
        if (old == delta) return;
        OffsetDelta = delta;

        var m = Matrix;
        var (sx, sy) = Scales;
        foreach (var item in _items)
        {
            var external = pixelsHandledExternally?.Invoke(item.Layer) == true;
            lock (_doc.SyncRoot)
            {
                item.Layer.Offset = new SKPointI(
                    item.BaseOffset.X + delta.X, item.BaseOffset.Y + delta.Y);
                foreach (var start in item.StartElements)
                {
                    if (item.Layer.FindElement(start.Id) != null)
                        item.Layer.ReplaceElement(TransformedElement(item, start, m, sx, sy));
                }
            }

            if (!external && !item.LastStamp.IsEmpty)
            {
                // 純平移只改 Offset：效果快取是圖層座標、不必重算，只重新合成（含效果外擴）
                var pad = Effects.LayerEffectRenderer.TotalMargin(item.Layer);
                var dirty = SKRectI.Union(OffsetRect(item.LastStamp, old), OffsetRect(item.LastStamp, delta));
                dirty.Inflate(pad, pad);
                item.Layer.InvalidateComposite(dirty);
            }
        }
    }

    /// <summary>縮放/旋轉變了：位移歸零、以累積矩陣全量重蓋章。</summary>
    private void StampAll(bool preview, float sx, float sy, float rot)
    {
        var oldDelta = OffsetDelta;
        OffsetDelta = SKPointI.Empty;
        _stampedParams = (sx, sy, rot, TargetRect.Width, TargetRect.Height);
        _stampedOrigin = new SKPoint(TargetRect.Left, TargetRect.Top);
        _stampedQuad = _quad == null ? null : (SKPoint[])_quad.Clone();
        _stampedWarp = _warp;
        var m = Matrix;
        var pm = PixelMatrix;
        // 無損＝像素矩陣（含續接的前段）是整數平移：None 取樣、逐位元不變。
        // 續接時本輪就算是純平移，前段仍帶縮放/旋轉，得照常重取樣。彎曲一律重取樣。
        var lossless = _warp == null && IsIntegerTranslation(pm);
        _stampedHigh = lossless || !preview;
        _pixelsStamped = true;

        foreach (var item in _items)
        {
            var newStamp = SKRectI.Empty;
            lock (_doc.SyncRoot)
            {
                item.Layer.Offset = item.BaseOffset; // 蓋章一律以基準位移為準
                ClearPixelTiles(item.Layer);

                if (item.Pixels != null)
                {
                    var mapped = _warp != null
                        ? _warp.Bounds // 曲面落在控制點凸包內
                        : pm.MapRect(new SKRect(
                            item.SrcBounds.Left, item.SrcBounds.Top,
                            item.SrcBounds.Right, item.SrcBounds.Bottom));
                    newStamp = SKRectI.Ceiling(mapped);
                    newStamp.Inflate(2, 2); // 重取樣的邊緣餘裕
                    Stamp(item, pm, newStamp, lossless, preview);
                }

                foreach (var start in item.StartElements)
                {
                    if (item.Layer.FindElement(start.Id) != null)
                        item.Layer.ReplaceElement(TransformedElement(item, start, m, sx, sy));
                }
            }

            var oldDisplay = OffsetRect(item.LastStamp, oldDelta);
            var dirty = oldDisplay.IsEmpty ? newStamp
                : newStamp.IsEmpty ? oldDisplay : SKRectI.Union(oldDisplay, newStamp);
            if (!dirty.IsEmpty) item.Layer.Invalidate(dirty);
            item.LastStamp = newStamp;
        }
    }

    private static SKRectI OffsetRect(SKRectI r, SKPointI d) =>
        r.IsEmpty ? r : new SKRectI(r.Left + d.X, r.Top + d.Y, r.Right + d.X, r.Bottom + d.Y);

    /// <summary>把文字物件更新到目前的累積矩陣（一律從起始快照換算）。</summary>
    private void UpdateElements()
    {
        var m = Matrix;
        var (sx, sy) = Scales;
        foreach (var item in _items)
        {
            lock (_doc.SyncRoot)
            {
                foreach (var start in item.StartElements)
                {
                    if (item.Layer.FindElement(start.Id) != null)
                        item.Layer.ReplaceElement(TransformedElement(item, start, m, sx, sy));
                }
            }
        }
    }

    private void Stamp(Item item, SKMatrix m, SKRectI docStamp, bool lossless, bool preview)
    {
        var layer = item.Layer;
        var layerRect = new SKRectI(
            docStamp.Left - item.BaseOffset.X, docStamp.Top - item.BaseOffset.Y,
            docStamp.Right - item.BaseOffset.X, docStamp.Bottom - item.BaseOffset.Y);

        using var paint = new SKPaint
        {
            FilterQuality = lossless ? SKFilterQuality.None
                : preview ? SKFilterQuality.Low
                : SKFilterQuality.High,
            IsAntialias = !lossless,
        };

        foreach (var idx in TileIndex.CoveringRect(layerRect))
        {
            var tile = layer.Surface.GetTileForWrite(idx);
            using var surface = SKSurface.Create(Tile.Info, tile.Pixels, Tile.RowBytes);
            var canvas = surface.Canvas;
            var tileRect = idx.ToPixelRect();
            canvas.Translate(-tileRect.Left - item.BaseOffset.X, -tileRect.Top - item.BaseOffset.Y);
            if (_warp != null)
            {
                _warp.Draw(canvas, item.Pixels!, item.SrcBounds, m, paint.FilterQuality);
            }
            else
            {
                canvas.Concat(ref m);
                canvas.DrawImage(item.Pixels, item.SrcBounds.Left, item.SrcBounds.Top, paint);
            }
            canvas.Flush();

            if (tile.IsBlank()) layer.Surface.RemoveTile(idx);
        }
    }

    /// <summary>回到開始時的原狀：還原像素快照、位移與文字物件（無損）。</summary>
    public void RestoreOriginal()
    {
        if (_disposed) return;
        foreach (var item in _items)
        {
            var touchedPixels = _pixelsStamped;
            lock (_doc.SyncRoot)
            {
                item.Layer.Offset = item.BaseOffset;
                if (touchedPixels)
                {
                    ClearPixelTiles(item.Layer);
                    foreach (var (idx, tile) in item.Before.Tiles)
                        item.Layer.Surface.RestoreTile(idx, tile);
                }
                foreach (var start in item.StartElements)
                {
                    if (item.Layer.FindElement(start.Id) != null)
                        item.Layer.ReplaceElement(start);
                }
            }

            if (touchedPixels || OffsetDelta != SKPointI.Empty)
            {
                var display = OffsetRect(item.LastStamp, OffsetDelta);
                var dirty = display.IsEmpty ? item.SrcBounds
                    : item.SrcBounds.IsEmpty ? display : SKRectI.Union(display, item.SrcBounds);
                if (!dirty.IsEmpty) item.Layer.Invalidate(dirty);
            }
            item.LastStamp = item.SrcBounds;
        }
        ResetStampStateToOriginal();
    }

    /// <summary>
    /// session 期間圖層的像素完全歸本 session 管（開始時的內容已全部提起），
    /// 清空 = 移除所有 tile。
    /// </summary>
    private static void ClearPixelTiles(RasterLayer layer)
    {
        if (layer.Surface.TileCount == 0) return;
        foreach (var idx in layer.Surface.Tiles.Keys.ToList())
            layer.Surface.RemoveTile(idx);
    }

    /// <summary>
    /// 落地：以 High 品質蓋最後一章（純平移已無損則不重蓋），回傳單一 undo 步驟
    /// （各層像素差異 + 位移 + 文字物件變更）。identity 時回傳 null（呼叫端直接還原）。
    /// </summary>
    internal IHistoryEntry? BuildCommit(string label)
    {
        if (IsIdentity && OffsetDelta == SKPointI.Empty) return null;
        Apply(preview: false);

        var entries = new List<IHistoryEntry>();
        foreach (var item in _items)
        {
            var layer = item.Layer;

            if (_pixelsStamped)
            {
                TileDeltaEntry? pixelEntry;
                lock (_doc.SyncRoot)
                {
                    var affected = item.SrcBounds.IsEmpty ? item.LastStamp
                        : item.LastStamp.IsEmpty ? item.SrcBounds
                        : SKRectI.Union(item.SrcBounds, item.LastStamp);
                    var layerRect = new SKRectI(
                        affected.Left - item.BaseOffset.X, affected.Top - item.BaseOffset.Y,
                        affected.Right - item.BaseOffset.X, affected.Bottom - item.BaseOffset.Y);
                    pixelEntry = TileDeltaEntry.Capture(label, layer, item.Before, layerRect);
                }
                if (pixelEntry != null) entries.Add(pixelEntry);
            }

            // 純平移落在 Offset：記位移變更
            if (OffsetDelta != SKPointI.Empty)
            {
                var oldOffset = item.BaseOffset;
                var newOffset = new SKPointI(
                    item.BaseOffset.X + OffsetDelta.X, item.BaseOffset.Y + OffsetDelta.Y);
                entries.Add(new ActionHistoryEntry(label, SKRectI.Empty,
                    undo: _ => { layer.Offset = oldOffset; layer.InvalidateAll(); },
                    redo: _ => { layer.Offset = newOffset; layer.InvalidateAll(); }));
            }

            // 文字物件：舊/新成對記錄（同 Id 替換）
            var olds = item.StartElements;
            if (olds.Length > 0)
            {
                var news = new VectorElement?[olds.Length];
                var changed = false;
                lock (_doc.SyncRoot)
                {
                    for (var i = 0; i < olds.Length; i++)
                    {
                        news[i] = layer.FindElement(olds[i].Id);
                        changed |= news[i] != null && !Equals(news[i], olds[i]);
                    }
                }
                if (changed)
                {
                    var pairs = olds.Zip(news).Where(p => p.Second != null)
                        .Select(p => (Old: p.First, New: p.Second!)).ToArray();
                    entries.Add(new ActionHistoryEntry(label, SKRectI.Empty,
                        undo: _ =>
                        {
                            foreach (var (o, _) in pairs) layer.ReplaceElement(o);
                        },
                        redo: _ =>
                        {
                            foreach (var (_, n) in pairs) layer.ReplaceElement(n);
                        }));
                }
            }
        }

        return entries.Count switch
        {
            0 => null,
            1 => entries[0],
            _ => new CompositeHistoryEntry(label, entries.ToArray()),
        };
    }

    public void Dispose() => DisposeCore(null);

    /// <summary>
    /// session 結束時用這個：發布過覆疊的話 render thread 可能還在畫那些影像，
    /// 交給退役佇列延後釋放，不能就地 Dispose。
    /// </summary>
    internal void DisposeDeferred(Compositor compositor) => DisposeCore(compositor);

    private void DisposeCore(Compositor? compositor)
    {
        if (_disposed) return;
        _disposed = true;
        _overlay = null;
        foreach (var item in _items)
        {
            // 借來的原始像素屬於圖層的 LayerPixelSource，session 不能釋放它
            if (item.Pixels != null && item.OwnsPixels)
            {
                if (_overlayEverPublished && compositor != null) compositor.Retire(item.Pixels);
                else item.Pixels.Dispose();
            }
            item.Before.Dispose();
        }
        _items.Clear();
    }

    private static void DisposeItems(List<Item> items)
    {
        foreach (var item in items)
        {
            item.Pixels?.Dispose();
            item.Before.Dispose();
        }
        items.Clear();
    }
}

/// <summary>
/// 開變形時交給 <see cref="TransformSession.Resume"/> 的「續接資料」：各層的原始高清像素
/// ＋累積映射＋目前的框。內容由各圖層的 <see cref="LayerPixelSource"/> 組出來
/// （見 <see cref="EditorSession.BuildResumeFromLayers"/>），像素的擁有權一直在圖層那邊，
/// 本物件只是借過來用。</summary>
public sealed class TransformResume
{
    internal LayerNode Target { get; }
    internal (RasterLayer Layer, SKImage Pixels, SKRectI SrcBounds)[] Items { get; }
    internal SKMatrix PreMatrix { get; }
    internal SKRect TargetRect { get; }
    internal float RotationDeg { get; }
    internal SKSize OriginalSize { get; }

    internal TransformResume(LayerNode target,
        (RasterLayer Layer, SKImage Pixels, SKRectI SrcBounds)[] items,
        SKMatrix preMatrix, SKRect targetRect, float rotationDeg, SKSize originalSize)
    {
        Target = target;
        Items = items;
        PreMatrix = preMatrix;
        TargetRect = targetRect;
        RotationDeg = rotationDeg;
        OriginalSize = originalSize;
    }
}
