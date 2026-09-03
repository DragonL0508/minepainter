using MinePainter.Core.Tiles;
using SkiaSharp;

namespace MinePainter.Core.Compositing;

/// <summary>
/// 一個群組的隔離合成快取：群組內容（未套群組 opacity/blend）的 tile 圖。
/// 以「clean 集」追蹤有效格：不在集合內 = 髒（含從未合成過）。
/// Surface 由 compositor 執行緒在 Document.SyncRoot 內讀寫；
/// MarkDirty 可能來自 UI thread（同樣持 SyncRoot），clean 集另用內部 gate 保護。
/// </summary>
public sealed class GroupCache : IDisposable
{
    private readonly object _gate = new();
    private readonly HashSet<TileIndex> _clean = new();

    public TileSurface Surface { get; } = new();

    public void MarkDirty(SKRectI docRect)
    {
        lock (_gate)
        {
            foreach (var idx in TileIndex.CoveringRect(docRect))
                _clean.Remove(idx);
        }
    }

    public void MarkAllDirty()
    {
        lock (_gate)
        {
            _clean.Clear();
        }
    }

    public bool IsClean(TileIndex idx)
    {
        lock (_gate)
        {
            return _clean.Contains(idx);
        }
    }

    public void MarkClean(TileIndex idx)
    {
        lock (_gate)
        {
            _clean.Add(idx);
        }
    }

    /// <summary>
    /// 丟掉快取的像素（整批標髒 + 歸還 tile）。分頁切到背景時呼叫 ——
    /// 群組快取是純粹的加速結構，重算便宜，但一格 256 KB 放著很貴。
    /// 呼叫端須持有 Document.SyncRoot。
    /// </summary>
    public void Release()
    {
        MarkAllDirty();
        foreach (var idx in Surface.Tiles.Keys.ToList()) Surface.RemoveTile(idx);
    }

    public void Dispose() => Surface.Dispose();
}
