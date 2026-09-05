using MinePainter.Core.Tiles;
using SkiaSharp;

namespace MinePainter.Core.Tools;

/// <summary>
/// 一劃筆劃的暫存：覆蓋度遮罩（doc 座標）+ 顏色/不透明度。
/// 筆劃期間 compositor 把它疊在目標圖層上預覽；PointerUp 才 commit 進圖層。
/// 「整劃不透明度」語意：重疊 dab 取 max 覆蓋度，不會自我加深。
/// 存取一律在 Document.SyncRoot 內。
///
/// 餘暉（<see cref="IsLingering"/>）：目標圖層有效果堆疊時，筆劃烙進圖層後效果快取要在背景重算，
/// 這段時間畫面拿的還是舊的快取 —— 預覆疊一收掉，剛擦掉的東西就會「閃回來」一下再消失
/// （使用者 2026-09-06 回報「橡皮擦擦掉的瞬間會回溯」）。所以烙進去之後不立刻收，
/// 繼續疊在舊快取上，等快取追上（或這層又被別人改了）才收。
/// </summary>
public sealed class StrokeBuffer
{
    public MaskSurface Mask { get; } = new();

    public bool IsActive { get; private set; }

    /// <summary>已烙進圖層、只是還留在畫面上等效果快取追上。</summary>
    public bool IsLingering { get; private set; }

    private int _lingerRevision;
    public Guid TargetLayerId { get; private set; }
    public SKColor Color { get; private set; }
    public float Opacity { get; private set; } = 1f;
    public bool IsEraser { get; private set; }

    public SKRectI DirtyBounds => Mask.Bounds;

    public void Begin(Guid targetLayerId, SKColor color, float opacity, bool isEraser)
    {
        if (IsLingering) End();
        if (IsActive) throw new InvalidOperationException("前一劃尚未結束。");
        IsActive = true;
        TargetLayerId = targetLayerId;
        Color = color;
        Opacity = Math.Clamp(opacity, 0f, 1f);
        IsEraser = isEraser;
        Mask.Clear();
    }

    public void End()
    {
        IsActive = false;
        IsLingering = false;
        Mask.Clear();
    }

    /// <summary>筆劃已烙進圖層（表面版本 <paramref name="surfaceRevision"/>），先別收，留成餘暉。</summary>
    public void Linger(int surfaceRevision)
    {
        if (!IsActive) return;
        IsLingering = true;
        _lingerRevision = surfaceRevision;
    }

    /// <summary>
    /// 這一層現在要不要把筆劃疊上去（在 Document.SyncRoot 內）。
    /// 餘暉在「效果快取已追上」或「這層又被別人改了」時過期，順手收掉。
    /// </summary>
    public bool ShouldOverlay(Layers.RasterLayer layer)
    {
        if (!IsActive || TargetLayerId != layer.Id || DirtyBounds.IsEmpty) return false;
        if (!IsLingering) return true;
        if (layer.Surface.Revision != _lingerRevision || !layer.HasActiveEffects || layer.FxCache.UpToDate)
        {
            End();
            return false;
        }
        return true;
    }
}
