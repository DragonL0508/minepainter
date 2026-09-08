using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using SkiaSharp;

namespace MinePainter.App.Rendering;

public sealed unsafe partial class GpuLayerRenderer
{
    /// <summary>此 viewport 保留的 tile／LOD 像素預算；不含 Skia 自己的貼圖與 mipmap 開銷。</summary>
    public long ImageCacheBudgetBytes { get; set; } = 128L * 1024 * 1024;

    /// <summary>幀末保留的 tile／LOD 像素位元組數；繪製中的工作集可暫時超過預算。</summary>
    public long CachedImageBytes { get; private set; }

    /// <summary>本幀因來源變動或快取未命中而複製的 tile 數。</summary>
    public int LastSourceCopies { get; private set; }

    /// <summary>本幀重建的 LOD 數；熱快取應為零。</summary>
    public int LastLodBuilds { get; private set; }

    private readonly HashSet<Guid> _liveLayers = new();
    private readonly List<Guid> _deadLayers = new();
    private readonly List<CacheVictim> _cacheVictims = new();
    private IntPtr _contextHandle;

    private readonly record struct CacheVictim(LayerImages Cache, TileIndex Tile,
        (int Level, int X, int Y) Lod, bool IsLod, long Used);

    private void SetGpuContext(GRContext? context)
    {
        var handle = context?.Handle ?? IntPtr.Zero;
        if (_contextHandle != handle)
        {
            // LOD Snapshot 綁定建立它的 context，切換裝置不能拿舊貼圖繼續畫。
            foreach (var cache in _images.Values) cache.Dispose();
            _images.Clear();
            CachedImageBytes = 0;
            _contextHandle = handle;
        }
        _gpuContext = context;
    }

    private void SweepImageCaches(EditorSession session)
    {
        _liveLayers.Clear();
        foreach (var layer in session.Document.Descendants()) _liveLayers.Add(layer.Id);
        _deadLayers.Clear();
        foreach (var id in _images.Keys)
            if (!_liveLayers.Contains(id)) _deadLayers.Add(id);
        foreach (var id in _deadLayers)
        {
            _images[id].Dispose();
            _images.Remove(id);
        }
        _deadLayers.Clear();
        foreach (var id in _adjustments.Keys)
            if (!_liveLayers.Contains(id)) _deadLayers.Add(id);
        foreach (var id in _deadLayers)
        {
            _adjustments[id].Filter.Dispose();
            _adjustments.Remove(id);
        }

        long count = 0;
        foreach (var cache in _images.Values) count += cache.Tiles.Count + cache.Lods.Count;
        CachedImageBytes = count * Tile.BytesPerTile;
        var budget = Math.Max(0, ImageCacheBudgetBytes);
        if (CachedImageBytes <= budget) return;

        // 預算不足才排序；幀末才釋放，以免 DrawLod 的待畫清單指到已釋放的影像。
        // 記憶體足夠時保留不同倍率，來回縮放不再每隔三幀丟掉重建。
        _cacheVictims.Clear();
        foreach (var cache in _images.Values)
        {
            foreach (var (key, entry) in cache.Tiles)
                _cacheVictims.Add(new(cache, key, default, false, entry.Used));
            foreach (var (key, entry) in cache.Lods)
                _cacheVictims.Add(new(cache, default, key, true, entry.Used));
        }
        _cacheVictims.Sort(static (a, b) =>
        {
            var age = a.Used.CompareTo(b.Used);
            return age != 0 ? age : a.IsLod.CompareTo(b.IsLod);
        });
        foreach (var victim in _cacheVictims)
        {
            if (CachedImageBytes <= budget) break;
            if (victim.IsLod)
            {
                victim.Cache.Lods[victim.Lod].Image.Dispose();
                victim.Cache.Lods.Remove(victim.Lod);
            }
            else
            {
                victim.Cache.Tiles[victim.Tile].Image.Dispose();
                victim.Cache.Tiles.Remove(victim.Tile);
            }
            CachedImageBytes -= Tile.BytesPerTile;
        }
        _cacheVictims.Clear();
    }
}
