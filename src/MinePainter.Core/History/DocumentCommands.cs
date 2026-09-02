using MinePainter.Core.Documents;
using MinePainter.Core.Layers;
using MinePainter.Core.Selections;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;

namespace MinePainter.Core.History;

/// <summary>畫布層級的操作：翻轉、旋轉、裁切、平面化。</summary>
public static class DocumentCommands
{
    /// <summary>
    /// 翻轉／旋轉整份文件。這些操作互為反操作，所以 undo 不必存任何像素 ——
    /// 直接套用反操作即可（記憶體成本為零）。
    /// </summary>
    public static void ApplyGeometry(EditorSession session, GeometryOp op, string label)
    {
        Apply(session, op);
        session.History.Push(new ActionHistoryEntry(label, SKRectI.Empty,
            undo: _ => Apply(session, GeometryTransform.Inverse(op)),
            redo: _ => Apply(session, op)));
    }

    private static void Apply(EditorSession session, GeometryOp op)
    {
        var doc = session.Document;
        var srcSize = new SKSizeI(doc.Width, doc.Height);
        var dstSize = GeometryTransform.ResultSize(op, srcSize);

        lock (doc.SyncRoot)
        {
            foreach (var layer in RasterLayers(doc.Root))
            {
                var transformed = GeometryTransform.Transform(layer.Surface, op, srcSize, layer.Offset);
                layer.ReplaceSurface(transformed);
                layer.Offset = SKPointI.Empty; // 內容已重新對齊到文件原點

                // 物件跟著搬位置（文字本身不旋轉，維持可讀）
                foreach (var element in layer.Elements.ToList())
                {
                    if (element is not TextElement text) continue;
                    var p = GeometryTransform.MapForward(op, text.Position, srcSize);
                    layer.ReplaceElement(text with { Position = p });
                }
                layer.ElementCache.MarkAllDirty();
            }

            // 選取範圍跟著轉（Pinta 是直接丟棄；保留體驗更好）
            if (session.Selection is { IsEmpty: false } selection)
            {
                var mask = GeometryTransform.TransformMask(selection.Mask, op, srcSize);
                session.ApplySelection(SelectionMask.FromMaskSurface(mask));
            }

            doc.SetSize(dstSize.Width, dstSize.Height);
        }

        InvalidateAll(doc);
    }

    /// <summary>
    /// 改變畫布大小（錨定左上，不動任何圖層像素 —— 圖層本來就可持有畫布外像素，
    /// 縮小只是看不到，放大就自然露出來）。貼上超出畫布時的「延展畫布」走這裡。
    /// </summary>
    public static void ResizeCanvas(EditorSession session, int width, int height, string label = "調整畫布大小")
    {
        var doc = session.Document;
        var oldW = doc.Width;
        var oldH = doc.Height;
        if (width == oldW && height == oldH) return;
        if (width < 1 || height < 1) return;

        lock (doc.SyncRoot) doc.SetSize(width, height);
        InvalidateAll(doc);

        session.History.Push(new ActionHistoryEntry(label, SKRectI.Empty,
            undo: d =>
            {
                lock (d.SyncRoot) d.SetSize(oldW, oldH);
                InvalidateAll(d);
            },
            redo: d =>
            {
                lock (d.SyncRoot) d.SetSize(width, height);
                InvalidateAll(d);
            }));
    }

    /// <summary>裁切到選取範圍：文件縮成選取的外接矩形，範圍外的像素清掉。</summary>
    public static void CropToSelection(EditorSession session)
    {
        var doc = session.Document;
        if (session.Selection is not { IsEmpty: false } selection)
        {
            session.Notify("請先建立選取範圍");
            return;
        }

        var rect = SKRectI.Intersect(selection.Bounds, doc.Bounds);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        var oldSize = new SKSizeI(doc.Width, doc.Height);
        var states = new List<(RasterLayer Layer, TileSurface Before, SKPointI BeforeOffset)>();

        lock (doc.SyncRoot)
        {
            foreach (var layer in RasterLayers(doc.Root))
            {
                var before = layer.Surface;
                var cropped = CropSurface(layer, rect, selection);
                states.Add((layer, before, layer.Offset));
                layer.ReplaceSurface(cropped, disposeOld: false); // 舊的留給 undo
                layer.Offset = SKPointI.Empty;

                foreach (var element in layer.Elements.ToList())
                {
                    if (element is not TextElement text) continue;
                    layer.ReplaceElement(text.Translated(-rect.Left, -rect.Top));
                }
                layer.ElementCache.MarkAllDirty();
            }

            doc.SetSize(rect.Width, rect.Height);
        }

        var oldSelection = session.Selection;
        session.ApplySelection(null);
        InvalidateAll(doc);

        session.History.Push(new ActionHistoryEntry("裁切至選取範圍", SKRectI.Empty,
            undo: d =>
            {
                lock (d.SyncRoot)
                {
                    foreach (var (layer, before, offset) in states)
                    {
                        layer.ReplaceSurface(before, disposeOld: true);
                        layer.Offset = offset;
                        foreach (var element in layer.Elements.ToList())
                        {
                            if (element is TextElement text)
                                layer.ReplaceElement(text.Translated(rect.Left, rect.Top));
                        }
                        layer.ElementCache.MarkAllDirty();
                    }
                    d.SetSize(oldSize.Width, oldSize.Height);
                }
                session.ApplySelection(oldSelection);
                InvalidateAll(d);
            },
            redo: _ => CropToSelectionCore(session, rect, selection, states)));
    }

    private static void CropToSelectionCore(EditorSession session, SKRectI rect,
        SelectionMask selection, List<(RasterLayer Layer, TileSurface Before, SKPointI BeforeOffset)> states)
    {
        var doc = session.Document;
        lock (doc.SyncRoot)
        {
            foreach (var (layer, _, _) in states)
            {
                var cropped = CropSurface(layer, rect, selection);
                layer.ReplaceSurface(cropped, disposeOld: false);
                layer.Offset = SKPointI.Empty;
                foreach (var element in layer.Elements.ToList())
                {
                    if (element is TextElement text)
                        layer.ReplaceElement(text.Translated(-rect.Left, -rect.Top));
                }
                layer.ElementCache.MarkAllDirty();
            }
            doc.SetSize(rect.Width, rect.Height);
        }
        session.ApplySelection(null);
        InvalidateAll(doc);
    }

    /// <summary>把圖層在 rect 內的像素搬到新表面的原點，並以選取形狀裁切。</summary>
    private static unsafe TileSurface CropSurface(RasterLayer layer, SKRectI rect, SelectionMask selection)
    {
        var result = new TileSurface();
        var buffer = new uint[Tile.Size * Tile.Size];
        var dstRect = SKRectI.Create(0, 0, rect.Width, rect.Height);

        foreach (var idx in TileIndex.CoveringRect(dstRect))
        {
            var tileRect = idx.ToPixelRect();
            var inter = SKRectI.Intersect(tileRect, dstRect);
            if (inter.Width <= 0 || inter.Height <= 0) continue;

            Array.Clear(buffer);
            var any = false;

            for (var y = inter.Top; y < inter.Bottom; y++)
            {
                for (var x = inter.Left; x < inter.Right; x++)
                {
                    var docX = x + rect.Left;
                    var docY = y + rect.Top;
                    var coverage = selection.CoverageAt(docX, docY);
                    if (coverage == 0) continue; // 選取形狀外的清掉

                    var srcIdx = TileIndex.FromPixel(docX - layer.Offset.X, docY - layer.Offset.Y);
                    var srcTile = layer.Surface.GetTileForRead(srcIdx);
                    if (srcTile == null) continue;

                    var srcRect = srcIdx.ToPixelRect();
                    var value = ((uint*)srcTile.Pixels)[
                        ((docY - layer.Offset.Y - srcRect.Top) << 8) | (docX - layer.Offset.X - srcRect.Left)];
                    if (value == 0) continue;

                    if (coverage < 255) value = ScalePremultiplied(value, coverage);
                    buffer[((y - tileRect.Top) << 8) | (x - tileRect.Left)] = value;
                    any = true;
                }
            }

            if (!any) continue;
            var dstTile = result.GetTileForWrite(idx);
            fixed (uint* srcBuf = buffer)
            {
                Buffer.MemoryCopy(srcBuf, (void*)dstTile.Pixels, Tile.BytesPerTile, Tile.BytesPerTile);
            }
        }
        return result;
    }

    /// <summary>premultiplied BGRA 整體乘上覆蓋度（軟選取邊界）。</summary>
    private static uint ScalePremultiplied(uint premul, byte coverage)
    {
        var b = (premul & 0xFF) * coverage / 255;
        var g = ((premul >> 8) & 0xFF) * coverage / 255;
        var r = ((premul >> 16) & 0xFF) * coverage / 255;
        var a = ((premul >> 24) & 0xFF) * coverage / 255;
        return (a << 24) | (r << 16) | (g << 8) | b;
    }

    public static IEnumerable<RasterLayer> RasterLayers(GroupLayer group)
    {
        foreach (var child in group.Children)
        {
            if (child is RasterLayer raster) yield return raster;
            else if (child is GroupLayer nested)
            {
                foreach (var inner in RasterLayers(nested)) yield return inner;
            }
        }
    }

    private static void InvalidateAll(Document doc)
    {
        foreach (var node in doc.Descendants())
        {
            if (node is GroupLayer g) g.Cache.MarkAllDirty();
            if (node is RasterLayer r)
            {
                r.ElementCache.MarkAllDirty();
                r.FxCache.MarkAllDirty();
            }
        }
        doc.Root.Cache.MarkAllDirty();
        doc.NotifyChanged(doc.Bounds);
    }
}
