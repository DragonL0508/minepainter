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


public sealed partial class EditorSession
{
    /// <summary>
    /// 落地／取消後的殘影：浮動內容已經沒了，但合成器還沒把那塊重畫完。
    ///
    /// 覆疊路徑下，快取裡的舊 tile 本來就不含浮動內容，少了這層畫面會閃一下
    /// 「東西不見了」再跳出來。合成器追上（<see cref="Compositor.IsRegionClean"/>）就收掉。
    /// </summary>
    public sealed class OverlayGhost(SKImage image, SKRect rect, SKRectI region, float rotation = 0f,
        SKPoint? pivot = null)
    {
        public SKImage Image { get; } = image;

        /// <summary>這張殘影是哪個物件的（沒有＝浮動內容的殘影）。收掉的時機與再利用都要看它。</summary>
        public RasterLayer? Layer { get; init; }

        public Guid? ElementId { get; init; }

        /// <summary>殘影該出現的位置（落地＝新位置，取消＝原位置）。</summary>
        public SKRect Rect { get; } = rect;

        /// <summary>等這塊合成完就可以收掉。</summary>
        public SKRectI Region { get; } = region;

        /// <summary>以 <see cref="Pivot"/> 為軸的角度：旋轉手勢的殘影要跟覆疊圖同一個姿態，
        /// 否則放開的瞬間會閃一下轉回原角度。</summary>
        public float Rotation { get; } = rotation;

        /// <summary>旋轉軸心（doc 座標）＝覆疊圖用的那一個，不是 <see cref="Rect"/> 的中心。</summary>
        public SKPoint Pivot { get; } = pivot ?? new SKPoint(rect.MidX, rect.MidY);
    }


    /// <summary>
    /// 手勢中的文字物件覆疊：手勢開始時把物件（含效果）渲染成一張圖、隱藏原件，
    /// 手勢期間只變換這張圖 —— 不重排版、不逐格重畫、更不重算效果堆疊，放開才真正改物件。
    ///
    /// 移動、旋轉、縮放共用同一張圖：4K 帶外框／陰影的文字，效果堆疊算一次要 0.26 秒，
    /// 每個 pointer-move 都重算就是「怎麼拖都跟不上」。代價是手勢中的效果跟著整張圖轉／縮
    /// （陰影角度、外框粗細會暫時失真），放開重算一次就校正回來 —— PS 的變形預覽也是這樣。
    /// </summary>
    public sealed class ElementDragOverlay(RasterLayer layer, Guid elementId, SKImage? image, SKRectI bounds)
    {
        public RasterLayer Layer { get; } = layer;
        public Guid ElementId { get; } = elementId;

        /// <summary>
        /// 手勢中要貼的那張快照。**GPU 路徑不需要它**（那條路直接把原件套上手勢變換畫出來，
        /// 效果即時算），這時是 null —— 也就省下了「按下去的那一刻先渲染一遍整個物件加效果」
        /// 那筆開場費用（4K 帶效果的大字要 0.2 秒以上，正是「一按下去就頓一下」的來源）。
        /// </summary>
        public SKImage? Image { get; } = image;

        /// <summary>物件原本的（含效果外擴的）外框，doc 座標。</summary>
        public SKRectI Bounds { get; } = bounds;

        // 目前的目標框、角度與旋轉軸心（render thread 讀、UI thread 寫；float 讀寫是原子的，
        // 中間狀態最多讓某一幀的框差一點點，下一幀就對上了）
        private volatile float _left = bounds.Left;
        private volatile float _top = bounds.Top;
        private volatile float _width = bounds.Width;
        private volatile float _height = bounds.Height;
        private volatile float _rotation;
        private volatile float _pivotX = bounds.MidX;
        private volatile float _pivotY = bounds.MidY;

        /// <summary>覆疊圖現在要畫在哪（doc 座標）。</summary>
        public SKRect CurrentRect => new(_left, _top, _left + _width, _top + _height);

        /// <summary>以 <see cref="Pivot"/> 為軸的角度（度）。</summary>
        public float Rotation => _rotation;

        /// <summary>
        /// 旋轉軸心（doc 座標）。**不是覆疊圖的中心** —— 覆疊圖的框是「含效果外擴」的框，
        /// 而物件真正繞著轉的是「使用者看到的框」（著墨範圍）的中心，兩者差了排版框與著墨框的落差
        /// （120px 的字實測差 7–11 px，字級愈大差愈多）。用覆疊圖的中心當軸，手勢中的字就會
        /// 繞錯圓心跑、跟選取框對不起來，放開又跳回正確位置 —— 使用者說的「旋轉時位置會亂跳」。
        /// </summary>
        public SKPoint Pivot => new(_pivotX, _pivotY);

        /// <summary>設定目標框、角度與旋轉軸心（UI thread）；軸心省略＝框的中心。</summary>
        public void SetTarget(SKRect rect, float rotationDeg, SKPoint? pivot = null)
        {
            _left = rect.Left;
            _top = rect.Top;
            _width = rect.Width;
            _height = rect.Height;
            _rotation = rotationDeg;
            var p = pivot ?? new SKPoint(rect.MidX, rect.MidY);
            _pivotX = p.X;
            _pivotY = p.Y;
        }

        /// <summary>
        /// 把「原始框裡的一個框」（例如把手框）依覆疊目前的變換映射過去 ——
        /// 覆疊在縮放時把手要跟著縮，不然框跟畫面上的圖對不起來。
        /// </summary>
        public SKRect MapFrame(SKRect f)
        {
            var cur = CurrentRect;
            var sx = Bounds.Width > 0 ? cur.Width / Bounds.Width : 1f;
            var sy = Bounds.Height > 0 ? cur.Height / Bounds.Height : 1f;
            return new SKRect(
                cur.Left + (f.Left - Bounds.Left) * sx,
                cur.Top + (f.Top - Bounds.Top) * sy,
                cur.Left + (f.Right - Bounds.Left) * sx,
                cur.Top + (f.Bottom - Bounds.Top) * sy);
        }
    }

    /// <summary>
    /// 一次「整個圖層拖曳」期間的覆疊層：圖層像素的 COW 快照 + 逐格的零拷貝影像包裝。
    ///
    /// 影像刻意在開始時一次建好並留著 —— GPU 以影像的識別碼當貼圖快取的鍵，
    /// 每幀重建等於每幀重傳整層貼圖（正是會讓 FPS 掉到個位數的那種 cache thrash）。
    /// 像素走快照：拖曳期間就算有別的東西寫入該層，COW 會複製一份，覆疊看到的仍是同一批像素。
    /// </summary>
    public sealed class LayerDragOverlay
    {
        private readonly TileSnapshot _snapshot;
        private readonly Dictionary<TileIndex, SKImage> _images;

        internal LayerDragOverlay(RasterLayer layer, TileSnapshot snapshot, SKRectI region, bool includesElements = false)
        {
            Layer = layer;
            _snapshot = snapshot;
            Region = region;
            IncludesElements = includesElements;
            _images = new Dictionary<TileIndex, SKImage>(snapshot.Tiles.Count);
            foreach (var (idx, tile) in snapshot.Tiles)
            {
                using var pixmap = tile.AsPixmap();
                if (SKImage.FromPixels(pixmap) is { } img) _images[idx] = img; // 零拷貝
            }
        }

        public RasterLayer Layer { get; }

        /// <summary>覆疊像素已含這層的物件（合成器拆下這層時連物件也不畫）。</summary>
        public bool IncludesElements { get; }

        /// <summary>交接中：合成器已經把圖層算回去了，只補畫還沒重畫完的格子。</summary>
        public bool HandingOver { get; private set; }

        /// <summary>要等這塊全部合成完，覆疊層才能收掉。</summary>
        public SKRectI Region { get; private set; }

        internal void BeginHandover(SKRectI region)
        {
            Region = region;
            HandingOver = true;
        }

        /// <summary>
        /// 這一格要不要由覆疊層來畫。兩個轉場都靠它逐格切換，畫面才不會閃、也不會疊兩次：
        /// 　拆下來的當下 —— 舊的（髒的）格子裡還有這一層，等它重畫成「不含本層」才輪到覆疊；
        /// 　交還的時候 —— 反過來，重畫完（乾淨）的格子已經含本層了，覆疊就該讓位。
        /// 拖曳穩定期間全部都是乾淨的，等於整片都由覆疊層畫。
        /// </summary>
        public bool ShouldDraw(bool tileIsClean) => HandingOver ? !tileIsClean : tileIsClean;

        /// <summary>畫出圖層內容（doc 座標，呼叫端已套好 viewport 變換）。render thread 呼叫。</summary>
        public void Draw(SKCanvas canvas, SKRectI docRect, SKFilterQuality quality)
        {
            var offset = Layer.Offset;
            using var paint = new SKPaint { FilterQuality = quality };
            foreach (var (idx, image) in _images)
            {
                var r = idx.ToPixelRect();
                var left = r.Left + offset.X;
                var top = r.Top + offset.Y;
                if (left >= docRect.Right || top >= docRect.Bottom ||
                    left + Tile.Size <= docRect.Left || top + Tile.Size <= docRect.Top)
                {
                    continue;
                }
                canvas.DrawImage(image, left, top, paint);
            }
        }

        /// <summary>交給合成器延後釋放 —— render thread 這一幀可能還在畫這些影像。</summary>
        internal void Retire(Compositor compositor)
        {
            foreach (var image in _images.Values) compositor.Retire(image);
            _images.Clear();
            _snapshot.Dispose();
        }
    }

}
