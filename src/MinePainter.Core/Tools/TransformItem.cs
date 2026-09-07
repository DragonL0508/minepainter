using MinePainter.Core.Documents;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Vectors;
using SkiaSharp;

namespace MinePainter.Core.Tools;

internal sealed class TransformItem
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

    /// <summary>
    /// 手勢期間代替「物件（＋圖層效果）」呈現的快照。只給覆疊用 ——
    /// 永遠不會被蓋回圖層像素（文字必須維持可再編輯）。
    /// </summary>
    public SKImage? ElementPreview;

    /// <summary>ElementPreview 的 doc 範圍。</summary>
    public SKRectI ElementPreviewBounds;

    /// <summary>這一層的物件是本 session 藏起來的（放回來時只放自己藏的那些）。</summary>
    public bool ElementsWereHidden;

    /// <summary>拍下物件快照那一刻的手勢矩陣的反矩陣：畫的時候要把「已經含在快照裡」的那段扣掉。</summary>
    public SKMatrix PreviewInverse = SKMatrix.Identity;
}
