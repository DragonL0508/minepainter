using MinePainter.Core.History;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using SkiaSharp;

namespace MinePainter.Core.Tools;

/// <summary>
/// 筆刷：dab → StrokeBuffer 預覽，PointerUp 一次 commit 進圖層 + TileDeltaEntry。
/// EraserTool 繼承並改為 DstOut。
/// </summary>
public class BrushTool : ITool
{
    public virtual string Name => "筆刷";
    protected virtual bool IsEraser => false;

    public BrushSettings Settings { get; } = new();

    private readonly BrushEngine _engine = new();
    private TileSnapshot? _beforeSnapshot;
    private RasterLayer? _targetLayer;
    private bool _strokeActive;

    public void OnPointerDown(ToolPointerEvent e, EditorSession session)
    {
        if (session.Document.ActiveLayer is not RasterLayer layer) return;
        if (layer.IsTextLayer)
        {
            session.Notify("文字圖層不能直接繪製；要畫請先「圖層文字平面化」");
            return;
        }

        var doc = session.Document;
        SKRectI dirty;
        lock (doc.SyncRoot)
        {
            _beforeSnapshot = layer.Surface.Snapshot();
            session.StrokeBuffer.Begin(layer.Id, session.Foreground, Settings.Opacity, IsEraser);
            // 平滑都以螢幕像素定義（縮小檢視時手抖與整數座標樓梯都被放大同樣倍數）
            var docPerScreenPx = 1f / Math.Max(e.ViewScale, 1e-3f);
            // 樓梯窗 = 三個螢幕像素，滯後不到一個螢幕像素
            _engine.SmoothingWindow = 3f * docPerScreenPx;
            // 手抖穩定：100% = 16 螢幕像素的繩長/平滑距離
            _engine.Stabilize = Math.Clamp(Settings.Smoothing, 0f, 100f) / 100f * 16f * docPerScreenPx;
            dirty = _engine.BeginStroke(e.DocPosition, session.StrokeBuffer, Settings,
                session.Selection?.Mask, doc.Bounds);
        }
        _targetLayer = layer;
        _strokeActive = true;
        layer.Invalidate(dirty); // 走 layer 失效使祖先群組快取被標髒
    }

    public void OnPointerMove(ToolPointerEvent e, EditorSession session)
    {
        if (!_strokeActive) return;
        var doc = session.Document;

        SKRectI dirty;
        lock (doc.SyncRoot)
        {
            dirty = _engine.ContinueStroke(e.DocPosition, session.StrokeBuffer, Settings,
                session.Selection?.Mask, doc.Bounds);
        }
        if (!dirty.IsEmpty) _targetLayer?.Invalidate(dirty);
    }

    public void OnPointerUp(ToolPointerEvent e, EditorSession session)
    {
        if (!_strokeActive) return;
        _strokeActive = false;

        var doc = session.Document;
        var buffer = session.StrokeBuffer;
        var target = _targetLayer;
        _targetLayer = null;

        TileDeltaEntry? entry = null;
        SKRectI dirtyDoc;
        lock (doc.SyncRoot)
        {
            _engine.EndStroke(e.DocPosition, buffer, Settings, session.Selection?.Mask, doc.Bounds);
            dirtyDoc = buffer.DirtyBounds;
            if (target != null && target.Document == doc && !dirtyDoc.IsEmpty)
            {
                CommitStroke(target, buffer);

                // 圖層座標的受影響範圍
                var affected = new SKRectI(
                    dirtyDoc.Left - target.Offset.X, dirtyDoc.Top - target.Offset.Y,
                    dirtyDoc.Right - target.Offset.X, dirtyDoc.Bottom - target.Offset.Y);
                entry = TileDeltaEntry.Capture(Name, target, _beforeSnapshot!, affected);
            }

            buffer.End();
            _beforeSnapshot?.Dispose();
            _beforeSnapshot = null;
        }

        if (entry != null) session.History.Push(entry);
        if (!dirtyDoc.IsEmpty) target?.Invalidate(dirtyDoc);
    }

    /// <summary>把筆劃遮罩以正確語意烙進圖層（在 SyncRoot 內）。</summary>
    internal static unsafe void CommitStroke(RasterLayer layer, StrokeBuffer buffer)
    {
        var color = buffer.IsEraser
            ? SKColors.White.WithAlpha((byte)(buffer.Opacity * 255))
            : buffer.Color.WithAlpha((byte)(buffer.Color.Alpha * buffer.Opacity));
        var blend = buffer.IsEraser ? SKBlendMode.DstOut : SKBlendMode.SrcOver;

        using var paint = new SKPaint { Color = color, BlendMode = blend };
        var maskInfo = new SKImageInfo(MaskTile.Size, MaskTile.Size, SKColorType.Alpha8, SKAlphaType.Premul);

        // 受影響的「圖層 tile」：stroke 是 doc 座標，先轉圖層座標
        var strokeDoc = buffer.DirtyBounds;
        var strokeLayer = new SKRectI(
            strokeDoc.Left - layer.Offset.X, strokeDoc.Top - layer.Offset.Y,
            strokeDoc.Right - layer.Offset.X, strokeDoc.Bottom - layer.Offset.Y);

        foreach (var layerIdx in TileIndex.CoveringRect(strokeLayer))
        {
            var layerTile = layer.Surface.GetTileForWrite(layerIdx);
            using var surface = SKSurface.Create(Tile.Info, layerTile.Pixels, Tile.RowBytes);
            var canvas = surface.Canvas;

            var layerTileRect = layerIdx.ToPixelRect();
            // canvas 座標 = 圖層 tile 內部；把 doc 座標的 mask 對位過來
            canvas.Translate(-layerTileRect.Left - layer.Offset.X, -layerTileRect.Top - layer.Offset.Y);

            foreach (var (maskIdx, maskTile) in buffer.Mask.Tiles)
            {
                var maskRect = maskIdx.ToPixelRect(); // doc 座標
                fixed (byte* ptr = maskTile.Alpha)
                {
                    using var img = SKImage.FromPixels(maskInfo, (IntPtr)ptr, MaskTile.Size);
                    canvas.DrawImage(img, maskRect.Left, maskRect.Top, paint);
                }
            }
            canvas.Flush();

            if (layerTile.IsBlank())
                layer.Surface.RemoveTile(layerIdx); // 橡皮擦擦光的格回收
        }
    }
}

public sealed class EraserTool : BrushTool
{
    public override string Name => "橡皮擦";
    protected override bool IsEraser => true;
}
