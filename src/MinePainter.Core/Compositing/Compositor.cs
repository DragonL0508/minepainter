using System.Collections.Concurrent;
using MinePainter.Core.Documents;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using SkiaSharp;

namespace MinePainter.Core.Compositing;

/// <summary>
/// 背景合成器：把圖層樹合成為 root 層級的 tile 影像。
///
/// - UI thread 改文件 → Document.Changed → MarkDirty。
/// - render thread 每幀 TryGetTile；miss 的格自動排入佇列。
/// - worker thread 優先合成 viewport 內的 dirty tile，完成後發 TilesReady。
/// - 換下的舊 SKImage 進 retire 佇列，render thread 每幀 CollectRetired 延後釋放。
///
/// M1：只有 root 一層快取；M3 加每群組 GroupCache。
/// </summary>
public sealed class Compositor : IDisposable
{
    private readonly Document _document;
    private readonly StrokeBuffer? _strokeBuffer;
    private readonly Func<Selections.FloatingSelection?>? _floatingProvider;

    /// <summary>拖曳中、已從合成結果拆下來改由畫面覆疊呈現的圖層（見 EditorSession.LayerOverlay）。</summary>
    private readonly Func<(Guid? Id, bool IncludesElements)>? _detachedProvider;

    private readonly Thread _worker;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _signal = new(0);

    private readonly object _dirtyGate = new();
    private readonly HashSet<TileIndex> _dirty = new();

    private readonly ConcurrentDictionary<TileIndex, SKImage?> _cache = new();

    /// <summary>每格最後被用到的時間戳（LRU 淘汰用；全透明的格不計，它們不佔像素記憶體）。</summary>
    private readonly ConcurrentDictionary<TileIndex, long> _touch = new();
    private long _touchClock;

    private readonly ConcurrentQueue<(long Gen, SKImage Image)> _retired = new();
    private long _collectGen;

    // tile 格座標範圍（含端點）；worker 優先處理。畫面還沒報過範圍前是空的
    // —— 預設值 (0,0,0,0) 會被當成「左上那一格看得到」，效果與 tile 都會被排錯優先序。
    private SKRectI _visibleTiles = new(0, 0, -1, -1);

    private long _tilesRendered;
    private long _renderTicks;

    /// <summary>一批 tile 合成完成（worker thread 上發出）。</summary>
    public event Action? TilesReady;

    /// <summary>診斷：累計已合成的 tile 數。</summary>
    public long TilesRendered => Interlocked.Read(ref _tilesRendered);

    /// <summary>診斷：累計合成耗時（worker thread 上的純合成時間）。</summary>
    public TimeSpan RenderTime => TimeSpan.FromSeconds(
        (double)Interlocked.Read(ref _renderTicks) / System.Diagnostics.Stopwatch.Frequency);

    /// <summary>診斷：目前快取著的 tile 數（含全透明格 —— 那些不佔像素記憶體）。</summary>
    public int CachedTileCount => _cache.Count;

    /// <summary>診斷：合成快取實際佔的像素記憶體（bytes）。</summary>
    public long CachedBytes => (long)_imageTiles * Tile.BytesPerTile;

    private int _imageTiles; // 非透明（＝真的佔記憶體）的格數

    /// <summary>診斷：累計因為超出預算被淘汰的格數。</summary>
    public long EvictedTiles => Interlocked.Read(ref _evicted);

    private long _evicted;

    /// <summary>
    /// 合成快取的記憶體預算（bytes）。GEGL 的 tile-cache 預設吃一半的 RAM 並在超出時淘汰／落盤；
    /// 我們的快取是純加速結構（丟了就重算），所以只淘汰、不落盤。
    /// 超出時從「看不到的格」裡挑最久沒用到的丟（可見範圍永遠留著，否則畫面會自己閃）。
    /// </summary>
    public long CacheBudgetBytes { get; set; } = DefaultBudgetBytes;

    /// <summary>預設預算：實體記憶體的 1/8，夾在 64 MB ~ 512 MB 之間。</summary>
    public static long DefaultBudgetBytes { get; } = ComputeDefaultBudget();

    private static long ComputeDefaultBudget()
    {
        long total;
        try
        {
            total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        }
        catch
        {
            total = 0;
        }
        if (total <= 0) total = 8L * 1024 * 1024 * 1024;
        return Math.Clamp(total / 8, 64L * 1024 * 1024, 512L * 1024 * 1024);
    }

    /// <summary>診斷：還在排隊等合成的 tile 數（＝畫面落後多少）。</summary>
    public int DirtyCount
    {
        get { lock (_dirtyGate) return _dirty.Count; }
    }

    /// <summary>
    /// 此範圍的合成結果是否都已經是最新的（沒有格子在排隊）。
    /// 用來判斷「剛落地的內容，合成器追上了沒」。
    /// </summary>
    public bool IsRegionClean(SKRectI docRect)
    {
        lock (_dirtyGate)
        {
            foreach (var idx in TileIndex.CoveringRect(docRect))
            {
                if (_dirty.Contains(idx)) return false;
            }
        }
        return true;
    }

    /// <summary>
    /// 這一格的合成結果是不是最新的。
    /// render thread 用它判斷「這格拿到的是新結果還是舊結果」——
    /// 覆疊路徑的接手／交還就是靠這個逐格切換，畫面才不會閃或疊兩次
    /// （見 EditorSession.LayerDragOverlay）。
    /// </summary>
    public bool IsTileClean(TileIndex idx)
    {
        lock (_dirtyGate) return !_dirty.Contains(idx);
    }

    public Compositor(Document document, StrokeBuffer? strokeBuffer = null,
        Func<Selections.FloatingSelection?>? floatingProvider = null,
        Func<(Guid? Id, bool IncludesElements)>? detachedLayerProvider = null)
    {
        _document = document;
        _strokeBuffer = strokeBuffer;
        _floatingProvider = floatingProvider;
        _detachedProvider = detachedLayerProvider;
        _document.SizeChanged += OnDocumentSizeChanged;
        _document.Changed += MarkDirty;

        // 初始全域 dirty：所有涵蓋文件的 tile
        MarkDirty(document.Bounds);

        _worker = new Thread(WorkerLoop)
        {
            Name = "MinePainter.Compositor",
            IsBackground = true,
        };
        _worker.Start();
    }

    public int TileCols => (_document.Width + Tile.Size - 1) / Tile.Size;
    public int TileRows => (_document.Height + Tile.Size - 1) / Tile.Size;

    /// <summary>render thread：目前可見的 tile 格範圍（含端點），供排程優先。</summary>
    public void SetVisibleTiles(SKRectI tileRange) => _visibleTiles = tileRange;

    /// <summary>目前可見範圍的 doc 座標（tile 對齊）。空的＝還沒有人report過畫面範圍。</summary>
    public SKRectI VisibleDocRect
    {
        get
        {
            var t = _visibleTiles;
            if (t.Right < t.Left || t.Bottom < t.Top) return SKRectI.Empty;
            return new SKRectI(t.Left * Tile.Size, t.Top * Tile.Size,
                (t.Right + 1) * Tile.Size, (t.Bottom + 1) * Tile.Size);
        }
    }

    /// <summary>
    /// render thread：取合成結果。回傳 (found, image)：
    /// found=true 且 image=null 代表該格全透明；found=false 代表尚未合成（已自動排隊）。
    /// </summary>
    public bool TryGetTile(TileIndex idx, out SKImage? image)
    {
        if (_cache.TryGetValue(idx, out image))
        {
            if (image != null) _touch[idx] = Interlocked.Increment(ref _touchClock);
            lock (_dirtyGate)
            {
                if (!_dirty.Contains(idx)) return true;
            }
            return true; // dirty 但有舊圖：先給舊圖，新圖合成完會通知
        }

        lock (_dirtyGate)
        {
            if (_dirty.Add(idx)) _signal.Release();
        }
        image = null;
        return false;
    }

    /// <summary>畫布尺寸變了：舊的合成快取整批作廢（tile 網格範圍也不同了）。</summary>
    private void OnDocumentSizeChanged()
    {
        foreach (var key in _cache.Keys)
        {
            if (_cache.TryRemove(key, out var img) && img != null)
            {
                Interlocked.Decrement(ref _imageTiles);
                _retired.Enqueue((Volatile.Read(ref _collectGen), img));
            }
        }
        _touch.Clear();
        lock (_dirtyGate) _dirty.Clear();
        MarkDirty(_document.Bounds);
    }

    private void MarkDirty(SKRectI docRect)
    {
        var any = false;
        lock (_dirtyGate)
        {
            // 已經整份都髒了就不必再走一遍格子（效果堆疊每寫回一次就會標一次整層，
            // 一秒好幾百次，每次都列舉整張網格是白做的）
            if (_dirty.Count >= TileCols * TileRows) return;

            foreach (var idx in TileIndex.CoveringRect(docRect))
            {
                if (idx.X < 0 || idx.Y < 0 || idx.X >= TileCols || idx.Y >= TileRows) continue;
                any |= _dirty.Add(idx);
            }
        }
        if (any) _signal.Release();
    }

    /// <summary>
    /// 交一張不再使用的影像給退役佇列延後釋放。
    /// render thread 這一幀可能還在畫它 —— 就地 Dispose 會撞上。
    /// </summary>
    public void Retire(SKImage image) => _retired.Enqueue((Volatile.Read(ref _collectGen), image));

    /// <summary>render thread 每幀呼叫：釋放退役超過兩代的影像。</summary>
    public void CollectRetired()
    {
        var gen = Interlocked.Increment(ref _collectGen);
        while (_retired.TryPeek(out var entry) && entry.Gen <= gen - 3)
        {
            if (_retired.TryDequeue(out entry)) entry.Image.Dispose();
        }
    }

    // ---- 全域退役佇列（背景分頁用）----
    //
    // 切到背景的分頁自己沒有幀，收不了自己的退役佇列；但它交出來的影像 render thread
    // 這一幀可能還在畫（切換的瞬間）。所以改交給全域佇列，由「目前正在畫的那個」畫布
    // 每幀順手收 —— 同樣是延後三代才真的 Dispose。

    private static readonly ConcurrentQueue<(long Gen, SKImage Image)> GlobalRetired = new();
    private static long _globalGen;

    private static void RetireGlobal(SKImage image) =>
        GlobalRetired.Enqueue((Interlocked.Read(ref _globalGen), image));

    /// <summary>render thread 每幀呼叫（與 <see cref="CollectRetired"/> 同一處）。</summary>
    public static void CollectGlobalRetired()
    {
        var gen = Interlocked.Increment(ref _globalGen);
        while (GlobalRetired.TryPeek(out var entry) && entry.Gen <= gen - 3)
        {
            if (GlobalRetired.TryDequeue(out entry)) entry.Image.Dispose();
        }
    }

    /// <summary>暫停中（切到背景的分頁）：合成快取已經丟掉，worker 沒事做。</summary>
    public bool IsSuspended { get; private set; }

    /// <summary>
    /// 分頁切到背景：丟掉整份合成快取與群組快取。
    ///
    /// 合成快取涵蓋的是「整份文件」而不是只有看得到的部分（這樣拉動畫面才不會等），
    /// 一份 4000×3000 的文件就是 48 MB —— 開五個分頁，其中四個看不到的白白佔著。
    /// 這些都是純加速結構，切回來時 <see cref="Resume"/> 重新標髒，可見範圍會優先補上。
    /// </summary>
    public void Suspend()
    {
        if (IsSuspended) return;
        IsSuspended = true;

        lock (_dirtyGate) _dirty.Clear();

        foreach (var key in _cache.Keys)
        {
            if (_cache.TryRemove(key, out var img) && img != null)
            {
                Interlocked.Decrement(ref _imageTiles);
                RetireGlobal(img);
            }
        }
        _touch.Clear();
        while (_retired.TryDequeue(out var entry)) RetireGlobal(entry.Image);

        lock (_document.SyncRoot)
        {
            foreach (var node in _document.Descendants())
            {
                if (node is Layers.GroupLayer group) group.Cache.Release();
            }
            _document.Root.Cache.Release();
        }
    }

    /// <summary>分頁切回前景：整份重新排隊合成（可見的先）。</summary>
    public void Resume()
    {
        if (!IsSuspended) return;
        IsSuspended = false;
        MarkDirty(_document.Bounds);
    }

    private void WorkerLoop()
    {
        var token = _cts.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                _signal.Wait(token);

                var rendered = 0;
                var batch = new List<TileIndex>(BatchSize);
                while (!token.IsCancellationRequested)
                {
                    // 效果堆疊先於 tile：有圖層的效果快取髒了就先算（鎖外計算，不擋 UI）
                    try
                    {
                        Effects.LayerEffectRenderer.RenderPending(_document, token, ReadGroupSourceLocked,
                            VisibleDocRect);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        // 效果算壞了不該讓合成器死掉；下一輪會再試
                    }
                    TakeDirtyBatch(batch, BatchSize);
                    if (batch.Count == 0) break;
                    RenderBatch(batch);
                    rendered += batch.Count;

                    // 每完成一小批就通知一次，讓 viewport 邊合成邊顯示
                    if (rendered % 16 < batch.Count)
                    {
                        TrimCache();
                        TilesReady?.Invoke();
                    }
                }
                if (rendered > 0)
                {
                    TrimCache();
                    TilesReady?.Invoke();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// 一次取一批髒格（可見的優先）。批次是為了讓一次取鎖攤在多核上（見 <see cref="RenderBatch"/>）：
    /// 逐格取鎖的話 UI thread 每格都要跟 worker 搶一次，反而更卡。
    /// </summary>
    private void TakeDirtyBatch(List<TileIndex> into, int max)
    {
        into.Clear();
        lock (_dirtyGate)
        {
            if (_dirty.Count == 0) return;

            var visible = _visibleTiles;
            foreach (var idx in _dirty)
            {
                if (idx.X < visible.Left || idx.X > visible.Right ||
                    idx.Y < visible.Top || idx.Y > visible.Bottom) continue;
                into.Add(idx);
                if (into.Count >= max) break;
            }

            // 可見範圍不夠湊滿一批就補畫布其他地方的（之後拉動畫面才不用等）
            if (into.Count < max)
            {
                foreach (var idx in _dirty)
                {
                    if (idx.X >= visible.Left && idx.X <= visible.Right &&
                        idx.Y >= visible.Top && idx.Y <= visible.Bottom) continue;
                    into.Add(idx);
                    if (into.Count >= max) break;
                }
            }

            foreach (var idx in into) _dirty.Remove(idx);
        }
    }

    /// <summary>
    /// 一批格子的合成：取一次 Document.SyncRoot，批次內分派到多個執行緒同時算。
    ///
    /// 合成本身是逐格獨立的（每格自己一張 SKSurface，寫進去的快取也是各自的格），
    /// 唯一的共用資源是「不能同時有人改文件」——所以鎖照舊由這裡整批持有，
    /// 批次內的執行緒一律不再取鎖（取了會等在自己這條 worker 手上的鎖，直接卡死）。
    /// GEGL 的做法也是同一個形狀：圖不變的期間逐 tile 分派執行緒。
    /// </summary>
    private void RenderBatch(List<TileIndex> batch)
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_document.SyncRoot)
        {
            if (ParallelComposite && batch.Count > 1)
            {
                Parallel.ForEach(batch, ParallelOptions, RenderTileLocked);
            }
            else
            {
                foreach (var idx in batch) RenderTileLocked(idx);
            }
        }
        Interlocked.Add(ref _renderTicks, System.Diagnostics.Stopwatch.GetTimestamp() - start);
        Interlocked.Add(ref _tilesRendered, batch.Count);
    }

    /// <summary>
    /// 批次內要不要分派到多執行緒同時合成。預設關閉：多執行緒同時走合成路徑時
    /// 原生層會當掉（根因調查中），確定前不開。批次本身（一次取一次鎖）與它無關，一直是開的。
    /// </summary>
    public static bool ParallelComposite { get; set; }

    /// <summary>一批最多幾格 —— 開了平行合成時也就是最多幾條執行緒同時合成。</summary>
    private static readonly int BatchSize = Math.Clamp(Environment.ProcessorCount, 1, 8);

    private static readonly ParallelOptions ParallelOptions = new() { MaxDegreeOfParallelism = BatchSize };


    /// <summary>
    /// 超出預算就淘汰：從「這一刻看不到的格」裡挑最久沒被用到的丟掉，直到回到預算內。
    /// 可見範圍一律留著 —— 丟了它畫面下一幀就會缺格。
    /// 丟掉的格只是加速結構，之後有人要就重新排隊合成。
    /// </summary>
    private void TrimCache()
    {
        var budget = CacheBudgetBytes;
        if (budget <= 0 || CachedBytes <= budget) return;

        var visible = _visibleTiles;
        var target = (long)(budget * 0.9); // 一次多丟一點，免得每合成一格就淘汰一次

        var candidates = new List<(long Touch, TileIndex Idx)>();
        foreach (var (idx, img) in _cache)
        {
            if (img == null) continue;
            if (idx.X >= visible.Left && idx.X <= visible.Right &&
                idx.Y >= visible.Top && idx.Y <= visible.Bottom) continue;
            candidates.Add((_touch.TryGetValue(idx, out var t) ? t : 0, idx));
        }
        if (candidates.Count == 0) return;

        candidates.Sort(static (a, b) => a.Touch.CompareTo(b.Touch));
        foreach (var (_, idx) in candidates)
        {
            if (CachedBytes <= target) break;
            if (!_cache.TryRemove(idx, out var img)) continue;
            _touch.TryRemove(idx, out _);
            if (img == null) continue;
            Interlocked.Decrement(ref _imageTiles);
            Interlocked.Increment(ref _evicted);
            _retired.Enqueue((Volatile.Read(ref _collectGen), img));
        }
    }

    /// <summary>
    /// 合成結果影像的像素緩衝來自 <see cref="TilePool"/>，由 SKImage 的 release callback 歸還。
    /// （SKSurface.Create(info) 會自己 malloc 一塊 256KB 再被 Clear 清一次 ——
    /// 拖曳大片內容時每秒有數千格走這條路，配置與重複清零都是實打實的成本。）
    /// </summary>
    private static readonly SKImageRasterReleaseDelegate ReleaseTileBuffer =
        static (_, context) => ((Tile)context).Release();

    /// <summary>合成一格。呼叫端須持有 Document.SyncRoot（見 <see cref="RenderBatch"/>）。</summary>
    private void RenderTileLocked(TileIndex idx)
    {
        var tileRect = idx.ToPixelRect();

        var buffer = Tile.Rent(TilePool.Shared, zeroed: false); // 下面的 Clear 會清
        var released = false;
        try
        {
            using var surface = SKSurface.Create(Tile.Info, buffer.Pixels, Tile.RowBytes);
            if (surface == null) return;
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);

            var hasContent = CompositeGroup(_document.Root, surface, tileRect);

            SKImage? image = null;
            if (hasContent)
            {
                canvas.Flush();
                using var pixmap = buffer.AsPixmap();
                image = SKImage.FromPixels(pixmap, ReleaseTileBuffer, buffer); // 零拷貝，接手緩衝
                released = image != null;
            }

            if (_cache.TryGetValue(idx, out var old) && old != null)
            {
                Interlocked.Decrement(ref _imageTiles);
                _retired.Enqueue((Volatile.Read(ref _collectGen), old));
            }
            _cache[idx] = image;
            if (image != null)
            {
                Interlocked.Increment(ref _imageTiles);
                _touch[idx] = Interlocked.Increment(ref _touchClock);
            }
            else
            {
                _touch.TryRemove(idx, out _);
            }
        }
        finally
        {
            if (!released) buffer.Release();
        }
    }

    /// <summary>
    /// 群組效果的來源像素：把這一組合成起來的樣子讀成 doc 座標的緩衝區（未套群組自身的
    /// opacity/blend —— 那是疊到下方時才套的）。進行中的筆劃／浮動內容也要畫進去，
    /// 否則在有效果的群組裡畫畫，畫到一半的筆劃會整個看不見。
    /// 在 Document.SyncRoot 內、compositor 執行緒上呼叫。
    /// </summary>
    private uint[] ReadGroupSourceLocked(GroupLayer group, SKRectI docRect) =>
        ReadGroupPixelsLocked(group, docRect, _strokeBuffer,
            _floatingProvider?.Invoke(), _detachedProvider?.Invoke() ?? (null, false));

    private static unsafe uint[] ReadGroupPixelsLocked(GroupLayer group, SKRectI docRect,
        StrokeBuffer? strokeBuffer, Selections.FloatingSelection? floating,
        (Guid? Id, bool IncludesElements) detachedLayer)
    {
        var pixels = new uint[Math.Max(0, docRect.Width * docRect.Height)];
        if (docRect.Width <= 0 || docRect.Height <= 0) return pixels;

        fixed (uint* ptr = pixels)
        {
            var info = new SKImageInfo(docRect.Width, docRect.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info, (IntPtr)ptr, docRect.Width * 4);
            if (surface == null) return pixels;
            surface.Canvas.Clear(SKColors.Transparent);

            // CompositeGroup 的 canvas 原點＝tileRect 左上，所以一格一格畫；
            // 效果的計算範圍通常比一格大很多，這裡就是「把這塊重新合成一次」的成本。
            foreach (var idx in TileIndex.CoveringRect(docRect))
            {
                var tileRect = idx.ToPixelRect();
                var inter = SKRectI.Intersect(tileRect, docRect);
                if (inter.Width <= 0 || inter.Height <= 0) continue;
                using var tileSurface = SKSurface.Create(Tile.Info);
                if (tileSurface == null) continue;
                tileSurface.Canvas.Clear(SKColors.Transparent);
                if (!CompositeGroup(group, tileSurface, tileRect, strokeBuffer, floating, detachedLayer)) continue;
                tileSurface.Canvas.Flush();
                using var img = tileSurface.Snapshot();
                surface.Canvas.DrawImage(img, tileRect.Left - docRect.Left, tileRect.Top - docRect.Top);
            }
            surface.Canvas.Flush();
        }
        return pixels;
    }

    /// <summary>
    /// 同步合成整份文件（匯出/縮圖用）。在呼叫端執行緒完成，內部自行取 SyncRoot。
    /// </summary>
    public static SKImage RenderComposite(Document doc)
    {
        // 效果堆疊要先是最新的（含 worker 正在算的）；群組效果的來源不含進行中的預覽
        Effects.LayerEffectRenderer.RenderAllNow(doc, StaticGroupSourceLocked);
        var info = new SKImageInfo(doc.Width, doc.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var full = SKSurface.Create(info);
        full.Canvas.Clear(SKColors.Transparent);

        lock (doc.SyncRoot)
        {
            foreach (var idx in TileIndex.CoveringRect(doc.Bounds))
            {
                var tileRect = idx.ToPixelRect();
                using var tileSurface = SKSurface.Create(Tile.Info);
                tileSurface.Canvas.Clear(SKColors.Transparent);
                if (!CompositeGroup(doc.Root, tileSurface, tileRect, null, null, (null, false))) continue;
                tileSurface.Canvas.Flush();
                using var img = tileSurface.Snapshot();
                full.Canvas.DrawImage(img, tileRect.Left, tileRect.Top);
            }
        }

        full.Canvas.Flush();
        return full.Snapshot();
    }

    /// <summary>離線路徑（匯出／縮圖／烙印）用的群組來源：不含進行中的筆劃與浮動內容。</summary>
    internal static uint[] StaticGroupSourceLocked(GroupLayer group, SKRectI docRect) =>
        ReadGroupPixelsLocked(group, docRect, null, null, (null, false));

    private bool CompositeGroup(GroupLayer group, SKSurface surface, SKRectI tileRect) =>
        CompositeGroup(group, surface, tileRect, _strokeBuffer,
            _floatingProvider?.Invoke(), _detachedProvider?.Invoke() ?? (null, false));

    /// <summary>把群組內容合成到 surface（canvas 原點 = tileRect 左上）。回傳是否畫了東西。</summary>
    private static bool CompositeGroup(GroupLayer group, SKSurface surface, SKRectI tileRect,
        StrokeBuffer? strokeBuffer, Selections.FloatingSelection? floating, (Guid? Id, bool IncludesElements) detachedLayer)
    {
        var canvas = surface.Canvas;
        var drew = false;
        foreach (var child in group.Children)
        {
            if (!child.IsVisible || child.Opacity <= 0) continue;

            switch (child)
            {
                case AdjustmentLayer adj:
                {
                    if (!drew) break; // 下方無內容 → 調整無事可做

                    // 套在目前累積的內容（= 同群組內、其下方兄弟的合成結果）上。
                    // Opacity 作為調整強度：filtered 以該 alpha 疊回原圖。
                    canvas.Flush();
                    using var snap = surface.Snapshot();
                    using var filter = adj.Adjustment.CreateColorFilter();
                    var full = adj.Opacity >= 1f;
                    using var paint = new SKPaint
                    {
                        ColorFilter = filter,
                        BlendMode = full ? SKBlendMode.Src : SKBlendMode.SrcOver,
                        Color = SKColors.White.WithAlpha((byte)(adj.Opacity * 255)),
                    };
                    canvas.DrawImage(snap, 0, 0, paint);
                    break;
                }
                case RasterLayer raster:
                {
                    // 拖曳中被拆下來的圖層（EditorSession.LayerOverlay）：像素改由 render thread
                    // 每幀直接覆疊，合成結果裡不能有它（否則畫面上會出現兩份）。
                    // 但物件（文字）不跟著 Offset 走，仍舊由合成器畫 —— 少了這條文字會在拖曳時消失。
                    var detached = detachedLayer.Id is { } d && raster.Id == d;

                    var stroke = strokeBuffer;
                    var strokeHere = !detached && stroke is { IsActive: true } &&
                                     stroke.TargetLayerId == raster.Id &&
                                     !stroke.DirtyBounds.IsEmpty &&
                                     stroke.DirtyBounds.IntersectsWith(tileRect);
                    // 效果堆疊作用中：物件已經併進效果快取（外框／陰影要包住文字），不再另外畫
                    var elementsInFx = raster.HasActiveEffects && raster.FxCache.Rendered;
                    var elementTile = raster.HasElements && !elementsInFx ? RenderElementTile(raster, tileRect) : null;
                    var floatingHere = !detached && floating != null && floating.LayerId == raster.Id &&
                                       floating.TargetBounds.IntersectsWith(tileRect);

                    if (detached)
                    {
                        // 覆疊層已含物件（效果快取快照／文字圖層整層拖曳）：這裡不再畫，否則兩份
                        if (elementTile == null || detachedLayer.IncludesElements) break;
                        using (var pixmap = elementTile.AsPixmap())
                        using (var img = SKImage.FromPixels(pixmap))
                        {
                            canvas.DrawImage(img, 0, 0);
                        }
                        drew = true;
                        break;
                    }

                    if (!strokeHere && elementTile == null && !floatingHere)
                    {
                        drew |= CompositeRaster(raster, canvas, tileRect);
                        break;
                    }

                    // 圖層 = 像素 + 物件（+ 進行中的筆劃／浮動內容預覽）。這些疊加內容
                    // 原則上要先在隔離層合成，圖層的 opacity/blend 才會整體套用一次
                    // （否則重疊處會算兩次），橡皮擦的 DstOut 也才不會擦穿到下方圖層。
                    //
                    // 但 SrcOver 有結合律：正常混合 + 不透明度 100% + 不是橡皮擦時，
                    // 直接畫在同一張 canvas 上的結果完全相同 —— 省下的那張 256KB 離屏緩衝
                    // （配置 + 清空 + 疊回）是拖曳大片浮動內容時每格最貴的一筆。
                    var isolate = raster.Opacity < 1f ||
                                  raster.BlendMode != BlendMode.Normal ||
                                  (strokeHere && stroke!.IsEraser);

                    if (isolate)
                    {
                        using var layerPaint = new SKPaint
                        {
                            Color = new SKColor(255, 255, 255, (byte)(raster.Opacity * 255)),
                            BlendMode = raster.BlendMode.ToSkia(),
                        };
                        canvas.SaveLayer(layerPaint);
                    }

                    CompositeRasterContent(raster, canvas, tileRect, isolate ? 1f : raster.Opacity);
                    if (strokeHere) DrawStrokeOverlay(stroke!, canvas, tileRect);
                    if (floatingHere)
                    {
                        canvas.Save();
                        canvas.Translate(-tileRect.Left, -tileRect.Top);
                        floating!.DrawInto(canvas, preview: true);
                        canvas.Restore();
                    }
                    if (elementTile != null)
                    {
                        using var pixmap = elementTile.AsPixmap();
                        using var img = SKImage.FromPixels(pixmap);
                        canvas.DrawImage(img, 0, 0);
                    }

                    if (isolate) canvas.Restore();
                    drew = true;
                    break;
                }

                case GroupLayer nested:
                {
                    // isolated composite：先拿群組內容的快取 tile，再以群組 opacity/blend 疊上。
                    // 群組有效果堆疊且已算好時，拿的是「整組套過效果」的那份（外框／陰影包住整組，
                    // 而不是每個子層各一份）；還沒算好就先畫原本的內容，不要讓整組消失。
                    var groupIdx = TileIndex.FromPixel(tileRect.Left, tileRect.Top);
                    var contentTile = nested.EffectsRendered
                        ? nested.FxCache.Surface.GetTileForRead(groupIdx)
                        : RenderGroupTile(nested, tileRect, strokeBuffer, floating, detachedLayer);
                    if (contentTile != null)
                    {
                        using var paint = new SKPaint
                        {
                            Color = new SKColor(255, 255, 255, (byte)(nested.Opacity * 255)),
                            BlendMode = nested.BlendMode.ToSkia(),
                        };
                        using var pixmap = contentTile.AsPixmap();
                        using var img = SKImage.FromPixels(pixmap);
                        canvas.DrawImage(img, 0, 0, paint);
                        drew = true;
                    }
                    break;
                }
            }
        }
        return drew;
    }

    /// <summary>
    /// 取群組內容在某 doc tile 的隔離合成結果（未套群組 opacity/blend）。
    /// 快取命中直接回傳；否則重新合成進快取。null = 全透明。
    /// 在 compositor 執行緒、Document.SyncRoot 內呼叫。
    /// </summary>
    private static Tile? RenderGroupTile(GroupLayer group, SKRectI tileRect,
        StrokeBuffer? strokeBuffer, Selections.FloatingSelection? floating, (Guid? Id, bool IncludesElements) detachedLayer)
    {
        var idx = TileIndex.FromPixel(tileRect.Left, tileRect.Top);
        if (group.Cache.IsClean(idx))
            return group.Cache.Surface.GetTileForRead(idx);

        var tile = group.Cache.Surface.GetTileForWrite(idx);
        bool drew;
        using (var surface = SKSurface.Create(Tile.Info, tile.Pixels, Tile.RowBytes))
        {
            surface.Canvas.Clear(SKColors.Transparent);
            drew = CompositeGroup(group, surface, tileRect, strokeBuffer, floating, detachedLayer);
            surface.Canvas.Flush();
        }

        if (!drew)
        {
            group.Cache.Surface.RemoveTile(idx);
            tile = null;
        }

        group.Cache.MarkClean(idx);
        return tile;
    }

    /// <summary>
    /// 取某圖層「物件層」在該 doc tile 的顯示快取。
    /// 物件永不 rasterize 進圖層像素，只進這份快取（保持永遠可再編輯）。
    /// </summary>
    private static Tile? RenderElementTile(RasterLayer layer, SKRectI tileRect)
    {
        var idx = TileIndex.FromPixel(tileRect.Left, tileRect.Top);
        if (layer.ElementCache.IsClean(idx))
            return layer.ElementCache.Surface.GetTileForRead(idx);

        var tile = layer.ElementCache.Surface.GetTileForWrite(idx);
        var drew = false;
        using (var surface = SKSurface.Create(Tile.Info, tile.Pixels, Tile.RowBytes))
        {
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            canvas.Translate(-tileRect.Left, -tileRect.Top);
            foreach (var el in layer.Elements)
            {
                if (layer.IsElementHidden(el.Id)) continue; // 畫布內編輯／手勢快照中，由 overlay 顯示
                if (!el.Bounds.IntersectsWith(tileRect)) continue;
                el.Render(canvas);
                drew = true;
            }
            canvas.Flush();
        }

        if (!drew)
        {
            layer.ElementCache.Surface.RemoveTile(idx);
            tile = null;
        }

        layer.ElementCache.MarkClean(idx);
        return tile;
    }

    private static bool CompositeRaster(RasterLayer layer, SKCanvas canvas, SKRectI tileRect) =>
        CompositeRasterContent(layer, canvas, tileRect, layer.Opacity);

    private static bool CompositeRasterContent(RasterLayer layer, SKCanvas canvas, SKRectI tileRect, float opacity)
    {
        // 圖層座標系中，此輸出 tile 對應的範圍
        var srcRect = new SKRectI(
            tileRect.Left - layer.Offset.X, tileRect.Top - layer.Offset.Y,
            tileRect.Right - layer.Offset.X, tileRect.Bottom - layer.Offset.Y);

        using var paint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, (byte)(opacity * 255)),
            BlendMode = layer.BlendMode.ToSkia(),
        };

        var drew = false;
        var source = layer.DisplaySurface; // 有效果堆疊時拿套用後的快取
        foreach (var srcIdx in TileIndex.CoveringRect(srcRect))
        {
            var tile = source.GetTileForRead(srcIdx);
            if (tile == null) continue;

            // 畫別人的像素之前先佔一份引用：零拷貝的 SKImage 直接指著那塊緩衝，
            // 中途被釋放（寫時複製、undo 還原）就會還進池子被別人借走 —— 那是原生層的當機。
            if (!tile.TryAddRef()) continue;
            try
            {
                using var pixmap = tile.AsPixmap();
                using var img = SKImage.FromPixels(pixmap); // 零拷貝；引用期間有效
                var srcTileRect = srcIdx.ToPixelRect();
                canvas.DrawImage(
                    img,
                    srcTileRect.Left + layer.Offset.X - tileRect.Left,
                    srcTileRect.Top + layer.Offset.Y - tileRect.Top,
                    paint);
            }
            finally
            {
                tile.Release();
            }
            drew = true;
        }
        return drew;
    }

    /// <summary>把進行中筆劃的遮罩畫到隔離層上（doc 座標；canvas 原點 = tileRect 左上）。</summary>
    private static unsafe void DrawStrokeOverlay(StrokeBuffer stroke, SKCanvas canvas, SKRectI tileRect)
    {
        var color = stroke.IsEraser
            ? SKColors.White.WithAlpha((byte)(stroke.Opacity * 255))
            : stroke.Color.WithAlpha((byte)(stroke.Color.Alpha * stroke.Opacity));

        using var paint = new SKPaint
        {
            Color = color,
            BlendMode = stroke.IsEraser ? SKBlendMode.DstOut : SKBlendMode.SrcOver,
        };
        var maskInfo = new SKImageInfo(MaskTile.Size, MaskTile.Size, SKColorType.Alpha8, SKAlphaType.Premul);

        foreach (var (maskIdx, maskTile) in stroke.Mask.Tiles)
        {
            var maskRect = maskIdx.ToPixelRect();
            if (!maskRect.IntersectsWith(tileRect)) continue;

            fixed (byte* ptr = maskTile.Alpha)
            {
                using var img = SKImage.FromPixels(maskInfo, (IntPtr)ptr, MaskTile.Size);
                canvas.DrawImage(img, maskRect.Left - tileRect.Left, maskRect.Top - tileRect.Top, paint);
            }
        }
    }

    /// <summary>從合成快取取單一像素（滴管用）。未合成處回傳透明。</summary>
    public unsafe SKColor SamplePixel(int x, int y)
    {
        var idx = TileIndex.FromPixel(x, y);
        if (!_cache.TryGetValue(idx, out var img) || img == null) return SKColors.Empty;

        var info = new SKImageInfo(1, 1, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        Span<byte> pixel = stackalloc byte[4];
        fixed (byte* ptr = pixel)
        {
            var tileRect = idx.ToPixelRect();
            if (!img.ReadPixels(info, (IntPtr)ptr, 4, x - tileRect.Left, y - tileRect.Top))
                return SKColors.Empty;
        }
        return new SKColor(pixel[2], pixel[1], pixel[0], pixel[3]);
    }

    private int _stopped;

    /// <summary>
    /// 停掉 worker 並等它真的停下來（最多兩秒）。
    ///
    /// 關閉分頁時要**先**做這件事，才輪得到釋放那些「合成器正在畫的東西」——
    /// 浮動內容的影像、殘影、覆疊快照都是 worker 每格都會讀的，
    /// 一邊 Dispose 一邊還在畫就是踩到已釋放的記憶體（原生層直接當掉，攔不到）。
    /// 可重複呼叫。
    /// </summary>
    public void StopRendering()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _document.Changed -= MarkDirty;
        _document.SizeChanged -= OnDocumentSizeChanged;
        _cts.Cancel();
        _signal.Release();
        if (!_worker.Join(TimeSpan.FromSeconds(2)))
        {
            // background thread，程序結束時自然回收
        }
    }

    public void Dispose()
    {
        StopRendering();

        foreach (var img in _cache.Values) img?.Dispose();
        _cache.Clear();
        _touch.Clear();
        Interlocked.Exchange(ref _imageTiles, 0);
        while (_retired.TryDequeue(out var entry)) entry.Image.Dispose();
        _cts.Dispose();
        _signal.Dispose();
    }
}
