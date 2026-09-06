using MinePainter.Core.Effects;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;

namespace MinePainter.App.Rendering;

/// <summary>
/// 直接在 GPU 上把圖層樹畫出來（不經過 CPU 合成器）。
///
/// **現有路徑**：合成器 worker 在 CPU 把所有圖層混成一張張 tile、效果堆疊也在 CPU 逐像素算，
/// 算完才上傳成貼圖給 GPU 貼上去。GPU 幾乎閒著（實測一幀 0.6 ms），CPU 那邊一次上百毫秒 ——
/// 「手勢中畫面跟不上」的根就在這裡。
///
/// **這條路徑**：每幀直接走圖層樹，把每層的 tile 當貼圖畫上去，混合／不透明度交給 GPU。
/// 效果堆疊仍舊由 CPU 算（DisplaySurface）—— 畫面看到的與匯出得到的因此永遠是同一份。
/// CPU 合成器仍然是匯出與離線路徑的唯一真相，也是這條路處理不了時的退路。
///
/// **一律啟用**；真的遇到處理不了的狀態就整份退回（<see cref="TryDraw"/> 回傳 false，
/// 呼叫端改畫合成器的 tile）。目前沒有這樣的狀態 —— 進行中的筆劃、浮動內容、拖曳與變形手勢、
/// 落地殘影、調整圖層都已經接手 —— 但退路留著，之後加新東西時才有地方站。
/// </summary>
public sealed unsafe class GpuLayerRenderer : IDisposable
{
    /// <summary>每一格 tile 的 GPU 貼圖（key＝tile 索引；靠 Tile.Version 判斷要不要重建）。</summary>
    private sealed class LayerImages : IDisposable
    {
        public readonly Dictionary<TileIndex, (long Version, SKImage Image)> Tiles = new();

        /// <summary>縮小檢視時改貼的降取樣貼圖（key＝階數＋區塊座標；見 <see cref="LodLevelFor"/>）。</summary>
        public readonly Dictionary<(int Level, int X, int Y), LodImage> Lods = new();

        public void Dispose()
        {
            foreach (var (_, image) in Tiles.Values) image.Dispose();
            Tiles.Clear();
            foreach (var lod in Lods.Values) lod.Image.Dispose();
            Lods.Clear();
        }
    }

    /// <summary>一張 LOD 貼圖，連同「它是拿哪一份來源建的」。</summary>
    private sealed class LodImage
    {
        public long Version;    // 來源那幾格的 Tile.Version 混出來的數（見 LodVersion）
        public SKImage Image = null!;
        public long Used;       // 最後用到的幀序（LRU／過期回收用）
    }

    /// <summary>縮小檢視要不要走 LOD（設定選單可關；關掉＝一律逐格畫全解析度）。</summary>
    public static bool LodEnabled { get; set; } = true;

    /// <summary>
    /// LOD 最高做到第幾階。第 3 階一張貼圖已經涵蓋 8×8 格（2048×2048 文件像素），
    /// 再往下一張貼圖要讀 256 格來源才建得起來，重建成本反而蓋過省下來的 draw call。
    /// </summary>
    public const int MaxLodLevel = 3;

    /// <summary>
    /// 每個圖層的 LOD 貼圖張數上限。一張 256KB；一個區塊在螢幕上恆為 128～256px
    /// （選階的必然結果，見 <see cref="LodLevelFor"/>），所以一個 4K 視窗的可見範圍
    /// 也在這個數以內 —— 上限只是「別無限長大」的保險，正常情況碰不到。
    /// </summary>
    private const int MaxLodImages = 256;

    /// <summary>連續幾幀沒用到就丟掉：一放大或平移離開，那些區塊立刻失去意義，沒必要佔著 GPU 記憶體。</summary>
    private const int LodKeepFrames = 3;

    private readonly Dictionary<Guid, LayerImages> _images = new();
    private readonly RotatedTextCache _rotatedText = new();
    private readonly Dictionary<Guid, (Core.Adjustments.IAdjustment Adjustment, SKColorFilter Filter)> _adjustments = new();

    /// <summary>這一幀要用第幾階 LOD（0＝照舊逐格畫全解析度）。</summary>
    private int _lodLevel;

    /// <summary>這一幀的 GPU context（建 LOD 貼圖的離屏 surface 用；null＝退回 raster surface）。</summary>
    private GRContext? _gpuContext;

    private long _frame;
    private readonly List<(SKImage Image, float X, float Y)> _lodBatch = new();
    private readonly List<(int Level, int X, int Y)> _lodEvict = new();

    /// <summary>診斷：上一幀畫了幾格。</summary>
    public int LastTiles { get; private set; }

    /// <summary>診斷／測試：上一幀貼了幾張 LOD 貼圖（一張抵 2^L×2^L 格）。</summary>
    public int LastLodTiles { get; private set; }

    /// <summary>診斷／測試：上一幀有幾個文字物件是貼快照畫的（見 <see cref="RotatedTextCache"/>）。</summary>
    public int LastCachedTextDraws { get; private set; }

    /// <summary>
    /// 依檢視縮放比選 LOD 階：scale ≤ 1/2^L 就用第 L 階，最高 <see cref="MaxLodLevel"/> 階。
    ///
    /// 選出來的階數保證「貼圖比目的地大一點點」（texel:螢幕像素落在 0.5～1 之間），
    /// 也就是永遠是降取樣、不會把低解析度的東西放大回去糊掉 —— 縮小檢視原本就該是這個方向。
    /// </summary>
    public static int LodLevelFor(double scale)
    {
        if (!LodEnabled) return 0;
        if (!double.IsFinite(scale) || scale <= 0) return 0; // 算不出縮放比就照舊逐格畫
        var level = 0;
        while (level < MaxLodLevel && scale <= 1.0 / (1 << (level + 1))) level++;
        return level;
    }

    /// <summary>
    /// 一張 LOD 貼圖的「來源版本」：把它涵蓋的每一格 <see cref="Tile.Version"/> 依固定順序混成一個數。
    ///
    /// 缺的格子算 0 而不是跳過 —— 不然「某格被畫出來／被擦掉整格」會混出同一個數，
    /// 貼圖就停在上一份（這正是逐格路徑當年踩過的坑，見 Tile.Version 的註解）。
    /// </summary>
    public static long LodVersion(TileSurface surface, int level, int blockX, int blockY)
    {
        var span = 1 << level;
        var h = unchecked((long)14695981039346656037UL); // FNV-1a
        for (var ty = 0; ty < span; ty++)
        for (var tx = 0; tx < span; tx++)
        {
            var tile = surface.GetTileForRead(new TileIndex(blockX * span + tx, blockY * span + ty));
            h = unchecked((h ^ (tile?.Version ?? 0)) * 1099511628211L);
        }
        return h;
    }

    /// <summary>
    /// 試著畫。回傳 false＝這份文件目前的狀態這條路處理不了，呼叫端請走原本的 tile 路徑。
    /// 必須在 render thread、Document.SyncRoot 內呼叫。
    /// </summary>
    /// <param name="viewScale">文件像素→螢幕像素的實際比例（含顯示器 DPI 縮放），用來選 LOD 階。</param>
    /// <param name="gpuContext">上屏用的 GPU context；給了就在 GPU 上建 LOD 貼圖，null 則退回 raster。</param>
    public bool TryDraw(SKCanvas canvas, EditorSession session, SKRectI visibleDoc,
        double viewScale = 1.0, GRContext? gpuContext = null)
    {
        if (!CanHandle(session)) return false;
        LastTiles = 0;
        LastLodTiles = 0;
        LastCachedTextDraws = 0;
        _frame++;
        _lodLevel = LodLevelFor(viewScale);
        _gpuContext = gpuContext;
        _docBounds = new SKRectI(0, 0, session.Document.Width, session.Document.Height);
        DrawGroup(canvas, session, session.Document.Root, visibleDoc);
        SweepLods();
        return true;
    }

    /// <summary>這條路徑還沒接手的狀態：有任何一個就整份退回原本的合成器。</summary>
    private static bool CanHandle(EditorSession session)
    {
        // 變形手勢的覆疊由 CanvasDrawOperation.DrawTransformOverlay 另外畫（在所有圖層之上，
        // 而覆疊本來就只在「上面沒有看得見的東西」時才成立），這裡照常畫圖層樹即可 ——
        // 被變形的那層此刻沒有像素（手勢開始時已經拆下來），畫出來也是空的。

        // Skia 沒有的混合模式（Photoshop 專有）要逐像素算，GPU 這條路畫不出來：整份退回 CPU 合成器。
        // 逐像素的調整（3D LUT）同理：色彩濾鏡表達不了。
        return !HasCustomBlend(session.Document.Root) && !HasPixelAdjustment(session.Document.Root);
    }

    private static bool HasPixelAdjustment(GroupLayer group)
    {
        foreach (var child in group.Children)
        {
            if (!child.IsVisible) continue;
            if (child is AdjustmentLayer { Adjustment.RequiresPixelPath: true }) return true;
            if (child is GroupLayer nested && HasPixelAdjustment(nested)) return true;
        }
        return false;
    }

    private static bool HasCustomBlend(GroupLayer group)
    {
        foreach (var child in group.Children)
        {
            if (!child.IsVisible) continue;
            if (Core.Compositing.CustomBlend.IsCustom(child.BlendMode)) return true;
            if (child is GroupLayer nested && HasCustomBlend(nested)) return true;
        }
        return false;
    }

    /// <summary>
    /// 手勢中的物件覆疊（「物件＋效果」的快照，跟著滑鼠走／轉／縮）。
    /// 舊路徑把它畫在所有圖層之上；這裡照層序畫在它自己那一層的位置，上面有東西也不會被蓋錯。
    /// </summary>
    private static void DrawElementOverlay(SKCanvas canvas, EditorSession.ElementDragOverlay overlay)
    {
        var rect = overlay.CurrentRect; // 只讀一次：UI thread 正在改它
        var rotation = overlay.Rotation;
        var pivot = overlay.Pivot;      // 物件真正的旋轉軸心，不是這張圖的中心
        var image = overlay.Image!;
        var transformed = rotation != 0 || image.Width != overlay.Bounds.Width ||
                          rect.Width != overlay.Bounds.Width || rect.Height != overlay.Bounds.Height;
        using var paint = new SKPaint
        {
            FilterQuality = transformed ? SKFilterQuality.Low : SKFilterQuality.None,
            IsAntialias = transformed,
        };
        if (rotation != 0)
        {
            canvas.Save();
            canvas.RotateDegrees(rotation, pivot.X, pivot.Y);
        }
        canvas.DrawImage(image, rect, paint);
        if (rotation != 0) canvas.Restore();
    }

    private void DrawGroup(SKCanvas canvas, EditorSession session, GroupLayer group, SKRectI visibleDoc)
        => DrawRange(canvas, session, group.Children, group.Children.Count, visibleDoc);

    /// <summary>
    /// 畫這個群組的前 <paramref name="count"/ > 個子層。
    ///
    /// 調整圖層作用在「同群組內、它下方的合成結果」上（與 CPU 合成器同語意）。GPU 這邊的做法是
    /// 把下方那一段包進一個 SaveLayer、收起來的時候套色彩濾鏡 —— 收起來那一刻濾鏡吃到的正好是
    /// 那一段的合成結果。由最上面那個調整圖層往下遞迴，巢狀的調整層自然就一層層套回去。
    /// </summary>
    private void DrawRange(SKCanvas canvas, EditorSession session, IReadOnlyList<LayerNode> children,
        int count, SKRectI visibleDoc)
    {
        var at = -1;
        for (var i = count - 1; i >= 0; i--)
        {
            if (children[i] is AdjustmentLayer { IsVisible: true } a && a.Opacity > 0) { at = i; break; }
        }

        if (at >= 0)
        {
            var adjustment = (AdjustmentLayer)children[at];
            var full = adjustment.Opacity >= 1f;
            // 不透明度＜1 ＝ 調整強度：先畫一份沒套到的底，再把套過的疊上去
            if (!full) DrawRange(canvas, session, children, at, visibleDoc);
            using var paint = new SKPaint
            {
                ColorFilter = AdjustmentFilter(adjustment),
                Color = SKColors.White.WithAlpha((byte)(adjustment.Opacity * 255)),
            };
            canvas.SaveLayer(paint);
            DrawRange(canvas, session, children, at, visibleDoc);
            canvas.Restore();
        }

        for (var i = at + 1; i < count; i++)
        {
            var child = children[i];
            if (!child.IsVisible || child.Opacity <= 0) continue;
            switch (child)
            {
                case RasterLayer raster:
                    DrawRaster(canvas, session, raster, visibleDoc);
                    break;
                case GroupLayer nested:
                    DrawNestedGroup(canvas, session, nested, visibleDoc);
                    break;
            }
        }
    }

    /// <summary>這個調整圖層的色彩濾鏡（參數沒換就沿用 —— 曲線／色階每次都要建 256 格表）。</summary>
    private SKColorFilter AdjustmentFilter(AdjustmentLayer layer)
    {
        if (_adjustments.TryGetValue(layer.Id, out var cached) &&
            ReferenceEquals(cached.Adjustment, layer.Adjustment))
        {
            return cached.Filter;
        }
        cached.Filter?.Dispose();
        var filter = layer.Adjustment.CreateColorFilter();
        _adjustments[layer.Id] = (layer.Adjustment, filter);
        return filter;
    }

    private void DrawNestedGroup(SKCanvas canvas, EditorSession session, GroupLayer group, SKRectI visibleDoc)
    {
        // 整組套過效果的那份已經算好了就直接畫它（外框／陰影包住整組，而不是每個子層各一份）
        if (group.EffectsRendered)
        {
            DrawSurface(canvas, GroupImages(group), group.FxCache.Surface, SKPointI.Empty, visibleDoc,
                group.Opacity, group.BlendMode);
            return;
        }

        var isolate = group.Opacity < 1f || group.BlendMode != BlendMode.Normal;
        if (isolate)
        {
            using var paint = LayerPaint(group, null);
            canvas.SaveLayer(paint);
        }
        DrawGroup(canvas, session, group, visibleDoc);
        if (isolate) canvas.Restore();
    }

    private void DrawRaster(SKCanvas canvas, EditorSession session, RasterLayer raster, SKRectI visibleDoc)
    {
        // 效果一律拿 CPU 算好的那份（DisplaySurface）——「畫面看到的」與「匯出得到的」是同一份。
        // 曾經試過把效果翻成 Skia 濾鏡交給 GPU 算，但 Skia 的 dilate 是方形核心，
        // 而外框走的是精確歐氏距離場：15px 的外框會把中文筆畫糊成一塊塊方塊（使用者回報）。
        var source = raster.DisplaySurface;
        var elementsInSource = raster.EffectsRendered; // CPU 快取已含物件

        var stroke = session.StrokeBuffer;
        var strokeHere = stroke.ShouldOverlay(raster);   // 含烙進去之後的餘暉（見 StrokeBuffer.IsLingering）
        var floating = session.Floating;
        var floatingHere = floating != null && floating.LayerId == raster.Id;

        // 橡皮擦的 DstOut 一定要在隔離層裡擦，否則會擦穿到下方圖層
        var isolate = raster.Opacity < 1f || raster.BlendMode != BlendMode.Normal ||
                      (strokeHere && stroke.IsEraser);
        if (isolate)
        {
            using var paint = LayerPaint(raster, null);
            canvas.SaveLayer(paint);
        }

        if (GestureItem(session, raster) is { } item)
        {
            // 變形手勢中的這一層：像素已經拆下來了，改用手勢矩陣把那張圖畫在**這個層序位置**
            // （舊路徑只能畫在所有圖層之上，所以上面一有東西就只好退回逐步蓋章）
            DrawGesture(canvas, session.Transform!.Overlay!, item);
        }
        else
        {
            DrawTiles(canvas, raster, source, visibleDoc, isolate ? 1f : raster.Opacity);
        }
        if (strokeHere) DrawStroke(canvas, stroke);
        if (floatingHere) floating!.DrawInto(canvas, preview: true);
        if (session.ElementOverlay is { Image: not null } drag && ReferenceEquals(drag.Layer, raster))
            DrawElementOverlay(canvas, drag);

        if (!elementsInSource)
        {
            // 變形手勢進行中：文字物件每幀都被換成新角度的實例（TransformSession.UpdateElements），
            // 每幀因此都要重排版、重描邊、重模糊一次 —— 走快照那條路（見 RotatedTextCache）。
            var gesture = session.Transform is { GestureActive: true };
            foreach (var element in raster.Elements)
            {
                if (raster.IsElementHidden(element.Id)) continue;
                if (gesture && element is TextElement text && _rotatedText.TryDraw(canvas, text))
                {
                    LastCachedTextDraws++;
                    continue;
                }
                element.Render(canvas);
            }
        }

        if (isolate) canvas.Restore();
    }

    /// <summary>這一層現在是不是變形手勢的一員（交接中的殘影不算 —— 那時像素已經蓋回層裡了）。</summary>
    private static (RasterLayer Layer, SKImage Image, SKRectI SrcBounds, SKMatrix Matrix)? GestureItem(
        EditorSession session, RasterLayer raster)
    {
        if (session.Transform?.Overlay is not { HandingOver: false } overlay) return null;
        foreach (var item in overlay.Items)
        {
            if (ReferenceEquals(item.Layer, raster)) return item;
        }
        return null;
    }

    private static void DrawGesture(SKCanvas canvas, TransformSession.GestureOverlay overlay,
        (RasterLayer Layer, SKImage Image, SKRectI SrcBounds, SKMatrix Matrix) item)
    {
        var m = item.Matrix; // 逐項的矩陣：像素與物件快照的基準時間不同（見 GestureOverlay.Items）
        if (overlay.Warp is { } warp)
        {
            warp.Draw(canvas, item.Image, item.SrcBounds, m, SKFilterQuality.Low);
            return;
        }
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.Low, IsAntialias = true };
        canvas.Save();
        canvas.Concat(ref m);
        canvas.DrawImage(item.Image, item.SrcBounds.Left, item.SrcBounds.Top, paint);
        canvas.Restore();
    }

    /// <summary>進行中的筆劃：遮罩本身就是一張張 Alpha8 的圖，照 doc 座標貼上去即可。</summary>
    private static unsafe void DrawStroke(SKCanvas canvas, StrokeBuffer stroke)
    {
        using var paint = new SKPaint
        {
            Color = stroke.IsEraser
                ? SKColors.White.WithAlpha((byte)(stroke.Opacity * 255))
                : stroke.Color.WithAlpha((byte)(stroke.Color.Alpha * stroke.Opacity)),
            BlendMode = stroke.IsEraser ? SKBlendMode.DstOut : SKBlendMode.SrcOver,
        };
        var info = new SKImageInfo(MaskTile.Size, MaskTile.Size, SKColorType.Alpha8, SKAlphaType.Premul);
        foreach (var (idx, tile) in stroke.Mask.Tiles)
        {
            var rect = idx.ToPixelRect();
            fixed (byte* ptr = tile.Alpha)
            {
                using var image = SKImage.FromPixels(info, (IntPtr)ptr, MaskTile.Size);
                canvas.DrawImage(image, rect.Left, rect.Top, paint);
            }
        }
    }

    private SKPaint LayerPaint(LayerNode node, SKImageFilter? filter) => new()
    {
        Color = new SKColor(255, 255, 255, (byte)(node.Opacity * 255)),
        BlendMode = node.BlendMode.ToSkia(),
        ImageFilter = filter,
    };

    private void DrawTiles(SKCanvas canvas, RasterLayer raster, TileSurface surface, SKRectI visibleDoc, float opacity)
    {
        var offset = surface == raster.DisplaySurface && raster.EffectsRendered
            ? raster.EffectOffset
            : raster.Offset;
        DrawSurface(canvas, Images(raster.Id), surface, offset, visibleDoc, opacity, BlendMode.Normal);
    }

    /// <summary>把一張 tile surface 畫上去（只畫看得到的那幾格；每格一張貼圖，靠 Tile.Version 判斷要不要重傳）。</summary>
    private void DrawSurface(SKCanvas canvas, LayerImages cache, TileSurface surface, SKPointI offset,
        SKRectI visibleDoc, float opacity, BlendMode blend)
    {

        // 只畫看得到的那幾格
        var layerRect = new SKRectI(
            visibleDoc.Left - offset.X, visibleDoc.Top - offset.Y,
            visibleDoc.Right - offset.X, visibleDoc.Bottom - offset.Y);

        // 縮小檢視：改貼涵蓋一整塊的降取樣貼圖（見 DrawLod）。建不出來就照舊逐格畫。
        if (_lodLevel > 0 && DrawLod(canvas, cache, surface, offset, layerRect, opacity, blend)) return;

        using var paint = opacity >= 1f && blend == BlendMode.Normal
            ? null
            : new SKPaint
            {
                Color = new SKColor(255, 255, 255, (byte)(opacity * 255)),
                BlendMode = blend.ToSkia(),
            };

        foreach (var idx in TileIndex.CoveringRect(layerRect))
        {
            var tile = surface.GetTileForRead(idx);
            if (tile == null) continue;
            var image = ImageFor(cache, idx, tile);
            if (image == null) continue;
            var rect = idx.ToPixelRect();
            DrawImageWithinDoc(canvas, image, SKRect.Create(rect.Left + offset.X, rect.Top + offset.Y, Tile.Size, Tile.Size), paint);
            LastTiles++;
        }
    }

    private SKRectI _docBounds;

    /// <summary>
    /// 把貼圖畫到 <paramref name="dst"/>（文件座標），但取樣範圍只限畫布內的那一段。
    ///
    /// 文件尺寸不是 256 的倍數時，最右／最下那格貼圖有一截是「畫布外」的透明區。縮小檢視用雙線性
    /// 取樣，畫布最後一排像素會混到旁邊的透明 —— 底下的白色棋盤格透出來，就是使用者 2026-09-06
    /// 說的「畫布縮小時底邊、右邊有一條很細的白線」。左上邊剛好是貼圖邊緣，Skia 對邊緣是夾住取樣
    /// 所以沒事；這裡把來源框限制在畫布內（Skia 的 strict src rect 同樣夾住框的邊緣），右下就跟左上一樣了。
    /// 整格都在畫布內的照舊整張貼（不必付 strict 的成本）。
    /// </summary>
    private void DrawImageWithinDoc(SKCanvas canvas, SKImage image, SKRect dst, SKPaint? paint)
    {
        var doc = new SKRect(_docBounds.Left, _docBounds.Top, _docBounds.Right, _docBounds.Bottom);
        if (doc.Contains(dst))
        {
            canvas.DrawImage(image, dst, paint);
            return;
        }
        var inter = SKRect.Intersect(dst, doc);
        if (inter.Width <= 0 || inter.Height <= 0) return;
        var sx = image.Width / dst.Width;
        var sy = image.Height / dst.Height;
        var src = new SKRect(
            (inter.Left - dst.Left) * sx, (inter.Top - dst.Top) * sy,
            (inter.Right - dst.Left) * sx, (inter.Bottom - dst.Top) * sy);
        canvas.DrawImage(image, src, inter, paint);
    }

    /// <summary>
    /// 縮小檢視時的貼法（GEGL/GIMP 的 mipmap 金字塔在這裡的對應物）：
    /// 一張 256×256 的貼圖涵蓋 2^L×2^L 格，也就是那塊區域降到 1/2^L。
    ///
    /// 為什麼要這樣：25% 檢視下每層的可見格數是 100% 的 16 倍，圖層一多一幀就是幾千次 draw call；
    /// 而且逐格貼上去是最近鄰取樣，縮小時鋸齒很明顯。改貼 LOD 之後 draw call 與縮放比無關
    /// （一個區塊在螢幕上恆為 128～256px），降取樣也在建貼圖時用雙線性＋mipmap 做好。
    ///
    /// 回傳 false＝這一輪有東西建不出來，呼叫端請照舊逐格畫。**還沒畫任何東西才回 false**：
    /// 所有貼圖都備齊了才開始貼，不然半路退回會把同一塊畫兩次。
    /// </summary>
    private bool DrawLod(SKCanvas canvas, LayerImages cache, TileSurface surface, SKPointI offset,
        SKRectI layerRect, float opacity, BlendMode blend)
    {
        if (layerRect.Width <= 0 || layerRect.Height <= 0) return false;

        var level = _lodLevel;
        var span = 1 << level;              // 一張貼圖涵蓋幾格（每邊）
        var blockPx = span * Tile.Size;     // 對應的文件像素邊長

        var bx0 = FloorDiv(layerRect.Left, blockPx);
        var by0 = FloorDiv(layerRect.Top, blockPx);
        var bx1 = FloorDiv(layerRect.Right - 1, blockPx);
        var by1 = FloorDiv(layerRect.Bottom - 1, blockPx);

        _lodBatch.Clear();
        for (var by = by0; by <= by1; by++)
        for (var bx = bx0; bx <= bx1; bx++)
        {
            var version = LodVersion(surface, level, bx, by);
            var key = (level, bx, by);
            if (cache.Lods.TryGetValue(key, out var lod) && lod.Version == version)
            {
                lod.Used = _frame;
            }
            else if (IsBlockEmpty(surface, level, bx, by))
            {
                // 整塊沒東西：不必建也不必畫（舊的那張留著也沒用了）
                if (lod != null) { lod.Image.Dispose(); cache.Lods.Remove(key); }
                continue;
            }
            else
            {
                var image = BuildLod(surface, level, bx, by);
                if (image == null) return false; // 建不起來 → 整份退回逐格（此時一筆都還沒畫）
                if (lod != null) { lod.Image.Dispose(); cache.Lods.Remove(key); }
                if (!StoreLod(cache, key, new LodImage { Version = version, Image = image, Used = _frame }))
                {
                    image.Dispose();
                    return false;
                }
            }
            _lodBatch.Add((cache.Lods[key].Image, bx * blockPx + offset.X, by * blockPx + offset.Y));
        }

        using var paint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, (byte)(opacity * 255)),
            BlendMode = blend.ToSkia(),
            // 貼上去大約是 1:1（0.5～1 倍），縮放比不是整倍時靠雙線性把鋸齒吃掉
            FilterQuality = SKFilterQuality.Low,
        };
        foreach (var (image, x, y) in _lodBatch)
        {
            DrawImageWithinDoc(canvas, image, SKRect.Create(x, y, blockPx, blockPx), paint);
            LastLodTiles++;
        }
        return true;
    }

    private static int FloorDiv(int a, int b) => (int)Math.Floor(a / (double)b);

    private static bool IsBlockEmpty(TileSurface surface, int level, int blockX, int blockY)
    {
        var span = 1 << level;
        for (var ty = 0; ty < span; ty++)
        for (var tx = 0; tx < span; tx++)
        {
            if (surface.GetTileForRead(new TileIndex(blockX * span + tx, blockY * span + ty)) != null)
                return false;
        }
        return true;
    }

    /// <summary>
    /// 把一塊區域的來源格以 1/2^L 畫進離屏 surface 再 Snapshot 成貼圖。
    /// 拿不到 GPU context 就用 raster surface —— 慢一點，但畫面照樣正確。
    /// </summary>
    private SKImage? BuildLod(TileSurface surface, int level, int blockX, int blockY)
    {
        var span = 1 << level;
        var info = new SKImageInfo(Tile.Size, Tile.Size, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var target = _gpuContext != null
            ? SKSurface.Create(_gpuContext, true, info)
            : SKSurface.Create(info);
        if (target == null) return null;

        var c = target.Canvas;
        c.Clear(SKColors.Transparent);
        c.Scale(1f / span);
        // Medium＝雙線性＋mipmap：降到 1/8 時只用雙線性會跳著取樣、細線閃爍
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium };
        for (var ty = 0; ty < span; ty++)
        for (var tx = 0; tx < span; tx++)
        {
            var tile = surface.GetTileForRead(new TileIndex(blockX * span + tx, blockY * span + ty));
            if (tile == null) continue;
            using var pixmap = tile.AsPixmap();
            // 複製一份：tile 的記憶體會被繼續改寫（這張是一次性的，畫完就丟）
            using var image = SKImage.FromPixelCopy(pixmap);
            if (image == null) return null;
            c.DrawImage(image, tx * Tile.Size, ty * Tile.Size, paint);
        }
        c.Flush();
        return target.Snapshot();
    }

    /// <summary>
    /// 存進快取；滿了就讓最久沒用到的那張出局。回傳 false＝擠不出位子（正常情況碰不到，見
    /// <see cref="MaxLodImages"/>），呼叫端請退回逐格 —— **這一幀已經用到的那幾張絕不能動**，
    /// 它們的貼圖此刻正排在待畫清單裡。
    /// </summary>
    private bool StoreLod(LayerImages cache, (int Level, int X, int Y) key, LodImage lod)
    {
        if (cache.Lods.Count >= MaxLodImages)
        {
            var oldest = key;
            var oldestUsed = _frame; // 這一幀用過的（Used == _frame）不列入候選
            foreach (var (k, v) in cache.Lods)
            {
                if (v.Used >= oldestUsed) continue;
                oldestUsed = v.Used;
                oldest = k;
            }
            if (oldestUsed >= _frame) return false;
            cache.Lods[oldest].Image.Dispose();
            cache.Lods.Remove(oldest);
        }
        cache.Lods[key] = lod;
        return true;
    }

    /// <summary>這一幀沒用到、而且已經連續 <see cref="LodKeepFrames"/> 幀沒用到的 LOD 貼圖就收掉。</summary>
    private void SweepLods()
    {
        foreach (var cache in _images.Values)
        {
            if (cache.Lods.Count == 0) continue;
            _lodEvict.Clear();
            foreach (var (key, lod) in cache.Lods)
            {
                if (_frame - lod.Used > LodKeepFrames) _lodEvict.Add(key);
            }
            foreach (var key in _lodEvict)
            {
                cache.Lods[key].Image.Dispose();
                cache.Lods.Remove(key);
            }
        }
    }

    private LayerImages GroupImages(GroupLayer group) => Images(group.Id);

    private LayerImages Images(Guid layerId)
    {
        if (_images.TryGetValue(layerId, out var cache)) return cache;
        cache = new LayerImages();
        _images[layerId] = cache;
        return cache;
    }

    /// <summary>這一格的貼圖；內容版本變了就重建（Skia 會沿用同一個 SKImage 的貼圖）。</summary>
    private static SKImage? ImageFor(LayerImages cache, TileIndex idx, Tile tile)
    {
        if (cache.Tiles.TryGetValue(idx, out var entry))
        {
            if (entry.Version == tile.Version) return entry.Image;
            entry.Image.Dispose();
            cache.Tiles.Remove(idx);
        }
        using var pixmap = tile.AsPixmap();
        // 複製一份：tile 的記憶體會被繼續改寫，貼圖不能指著它
        var image = SKImage.FromPixelCopy(pixmap);
        if (image == null) return null;
        cache.Tiles[idx] = (tile.Version, image);
        return image;
    }

    public void Dispose()
    {
        foreach (var cache in _images.Values) cache.Dispose();
        _images.Clear();
        foreach (var (_, filter) in _adjustments.Values) filter.Dispose();
        _adjustments.Clear();
        _rotatedText.Dispose();
    }
}

/// <summary>
/// 手勢中「轉起來的文字」的快照快取。
///
/// 旋轉手勢每一幀都是一個新角度，而 Skia 的字形遮罩是按矩陣快取的 —— 換個角度就等於整份
/// 重新點陣化一次，帶外光暈的字還要多描粗一圈再模糊。實測 120px、帶光暈／陰影／兩層外框的
/// 一行字：角度每幀都變是 12.7 ms/幀，角度不變只要 2.1 ms/幀，差的那 10 ms 全是重做遮罩。
///
/// 這裡把「同一個字、只是還沒轉」的那一份點陣化起來，之後每幀只是換個角度貼同一張圖。
/// 幾何是完全等價的：<see cref="TextElement.Render"/> 的外層變換就是
/// 位移(Position) → 旋轉(Rotation) → 其餘，所以「未旋轉、擺在原點的那份圖」
/// 繞 Position 轉 Rotation 度，就是原本會畫出來的東西（差別只在多了一次重取樣）。
///
/// 只在手勢中用：放開之後照舊走 <see cref="TextElement.Render"/> 精算，匯出與合成器更完全碰不到。
/// 只有 render thread 會碰它。
/// </summary>
public sealed class RotatedTextCache : IDisposable
{
    /// <summary>一個文字物件的快照（key＝「未旋轉、擺在原點」的樣子）。</summary>
    private sealed class Entry
    {
        // 連續兩幀看到同一份「未旋轉的樣子」才值得點陣化：旋轉手勢每幀都一樣（只有角度與
        // 位置在變，兩者都不在 key 裡），縮放手勢則每幀都不一樣 —— 後者點陣化只是白做一次工。
        public TextElement? Pending;
        public float PendingScale;

        public TextElement? Key;
        public float Scale;
        public SKImage? Image;
        public SKRectI Bounds;
        public long Used;

        public void Drop()
        {
            Image?.Dispose();
            Image = null;
            Key = null;
        }
    }

    /// <summary>同時最多留幾份（一層有好幾個文字物件一起被變形時，彼此不要互相把對方擠掉）。</summary>
    private const int Capacity = 4;

    /// <summary>單張快照的像素上限（超過就不快取，照舊每幀精算）。</summary>
    private const long MaxPixels = 24L * 1024 * 1024;

    private readonly Dictionary<Guid, Entry> _entries = new();
    private long _clock;

    public bool TryDraw(SKCanvas canvas, TextElement text)
    {
        if (Math.Abs(text.Rotation) < 0.01f) return false;       // 沒轉就沒有這個問題
        if (text.Deform is { IsIdentity: false }) return false;  // 透視／彎曲自己有一份快取
        if (string.IsNullOrEmpty(text.Text)) return false;

        var scale = DeviceScale(canvas);
        if (scale <= 0f) return false;

        // key＝「這個字未旋轉、擺在原點」的樣子：旋轉手勢中它逐幀不變（位置與角度都不在裡面）
        var flat = text with { Rotation = 0f, Position = SKPoint.Empty };
        var entry = EntryFor(text.Id);

        if (entry.Image == null || entry.Key != flat || entry.Scale != scale)
        {
            if (entry.Pending != flat || entry.PendingScale != scale)
            {
                entry.Pending = flat;
                entry.PendingScale = scale;
                return false; // 這一幀先照舊畫；下一幀還是同一份才點陣化
            }
            if (!Rasterize(entry, flat, scale)) return false;
        }

        entry.Used = ++_clock;
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.Low, IsAntialias = true };
        canvas.Save();
        canvas.Translate(text.Position.X, text.Position.Y);
        canvas.RotateDegrees(text.Rotation);
        canvas.Scale(1f / entry.Scale, 1f / entry.Scale);
        canvas.DrawImage(entry.Image, entry.Bounds.Left * entry.Scale, entry.Bounds.Top * entry.Scale, paint);
        canvas.Restore();
        return true;
    }

    private Entry EntryFor(Guid id)
    {
        if (_entries.TryGetValue(id, out var entry)) return entry;
        if (_entries.Count >= Capacity)
        {
            // 最久沒用到的那份讓位（同一層一次轉一堆文字時才會用到）
            var oldest = Guid.Empty;
            var oldestUsed = long.MaxValue;
            foreach (var (key, e) in _entries)
            {
                if (e.Used >= oldestUsed) continue;
                oldestUsed = e.Used;
                oldest = key;
            }
            _entries[oldest].Drop();
            _entries.Remove(oldest);
        }
        entry = new Entry();
        _entries[id] = entry;
        return entry;
    }

    /// <summary>畫布目前的縮放（忽略旋轉），量化成 1/8 —— 縮放值抖一點不要害 key 一直失效。</summary>
    private static float DeviceScale(SKCanvas canvas)
    {
        var m = canvas.TotalMatrix;
        var det = Math.Abs(m.ScaleX * m.ScaleY - m.SkewX * m.SkewY);
        if (det <= 0f || !float.IsFinite(det)) return 0f;
        var scale = MathF.Sqrt(det);
        if (scale < 1f / 16f || scale > 8f) return 0f; // 太小沒必要、太大快照會爆
        return MathF.Round(scale * 8f) / 8f;
    }

    private static bool Rasterize(Entry entry, TextElement flat, float scale)
    {
        var bounds = flat.Bounds; // 未旋轉、擺在原點時的 doc 外框（含效果外擴）
        var w = (int)MathF.Ceiling(bounds.Width * scale);
        var h = (int)MathF.Ceiling(bounds.Height * scale);
        if (w <= 0 || h <= 0 || (long)w * h > MaxPixels) return false;

        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (surface == null) return false;
        var c = surface.Canvas;
        c.Clear(SKColors.Transparent);
        c.Scale(scale);
        c.Translate(-bounds.Left, -bounds.Top);
        flat.Render(c);
        c.Flush();

        entry.Image?.Dispose();
        entry.Image = surface.Snapshot();
        entry.Key = flat;
        entry.Scale = scale;
        entry.Bounds = bounds;
        return true;
    }

    public void Dispose()
    {
        foreach (var entry in _entries.Values) entry.Drop();
        _entries.Clear();
    }
}
