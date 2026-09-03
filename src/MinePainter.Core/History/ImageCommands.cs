using MinePainter.Core.Documents;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;

namespace MinePainter.Core.History;

/// <summary>
/// paint.net「影像」與「圖層」選單裡的幾何類指令：調整影像大小（重新取樣）、
/// 調整畫布大小（含錨點）、單一圖層翻轉、從檔案匯入圖層。
/// </summary>
/// <summary>調整影像大小的重新取樣方式（paint.net：最佳品質／雙線性／最接近像素）。</summary>
public enum ResampleMode
{
    /// <summary>雙三次（放大平滑、縮小走 mipmap）；照片與一般插畫的預設。</summary>
    Bicubic,
    /// <summary>雙線性：較軟、沒有雙三次的輕微過衝。</summary>
    Bilinear,
    /// <summary>最接近像素：不混色，像素圖／點陣風整數倍縮放用。</summary>
    Nearest,
}

public static class ImageCommands
{
    // ---- 調整影像大小 ----

    /// <summary>所有點陣圖層以高品質重新取樣到新尺寸；文字物件等比縮放；選取範圍丟棄。</summary>
    public static void ResizeImage(EditorSession session, int width, int height, string label = "調整影像大小",
        ResampleMode resample = ResampleMode.Bicubic)
    {
        var doc = session.Document;
        var oldW = doc.Width;
        var oldH = doc.Height;
        if (width < 1 || height < 1 || (width == oldW && height == oldH)) return;

        var sx = width / (float)oldW;
        var sy = height / (float)oldH;
        var states = new List<(RasterLayer Layer, TileSurface Before, SKPointI BeforeOffset, List<VectorElement> BeforeElements)>();

        lock (doc.SyncRoot)
        {
            foreach (var layer in DocumentCommands.RasterLayers(doc.Root))
            {
                states.Add((layer, layer.Surface, layer.Offset, layer.Elements.ToList()));
                ScaleLayerCore(layer, sx, sy, resample);
            }
            doc.SetSize(width, height);
        }

        var oldSelection = session.Selection;
        session.ApplySelection(null);
        InvalidateAll(doc);

        session.History.Push(new ActionHistoryEntry(label, SKRectI.Empty,
            undo: d =>
            {
                lock (d.SyncRoot)
                {
                    foreach (var (layer, before, offset, elements) in states)
                    {
                        layer.ReplaceSurface(before, disposeOld: true);
                        layer.Offset = offset;
                        RestoreElements(layer, elements);
                    }
                    d.SetSize(oldW, oldH);
                }
                session.ApplySelection(oldSelection);
                InvalidateAll(d);
            },
            redo: d =>
            {
                lock (d.SyncRoot)
                {
                    foreach (var (layer, _, _, _) in states) ScaleLayerCore(layer, sx, sy, resample);
                    d.SetSize(width, height);
                }
                session.ApplySelection(null);
                InvalidateAll(d);
            }));
    }

    private static void ScaleLayerCore(RasterLayer layer, float sx, float sy, ResampleMode resample)
    {
        var scaled = ScaleSurface(layer, sx, sy, resample);
        layer.ReplaceSurface(scaled, disposeOld: false); // 舊的留給 undo
        layer.Offset = SKPointI.Empty;
        foreach (var element in layer.Elements.ToList())
        {
            if (element is not TextElement text) continue;
            layer.ReplaceElement(text with
            {
                Position = new SKPoint(text.Position.X * sx, text.Position.Y * sy),
                FontSize = Math.Max(1f, text.FontSize * (sx + sy) / 2f),
            });
        }
        layer.ElementCache.MarkAllDirty();
    }

    /// <summary>圖層內容（含畫布外像素）整體縮放到 doc 座標的新表面（offset 併入）。</summary>
    private static TileSurface ScaleSurface(RasterLayer layer, float sx, float sy, ResampleMode resample)
    {
        var result = new TileSurface();
        var bounds = layer.Surface.ExactContentBounds();
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return result;

        using var src = ReadRegion(layer.Surface, bounds);
        var docRect = new SKRect(
            (bounds.Left + layer.Offset.X) * sx, (bounds.Top + layer.Offset.Y) * sy,
            (bounds.Right + layer.Offset.X) * sx, (bounds.Bottom + layer.Offset.Y) * sy);
        var dstRect = SKRectI.Round(docRect);
        if (dstRect.Width < 1) dstRect.Right = dstRect.Left + 1;
        if (dstRect.Height < 1) dstRect.Bottom = dstRect.Top + 1;

        using var dst = new SKBitmap(new SKImageInfo(dstRect.Width, dstRect.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(dst))
        using (var paint = new SKPaint
               {
                   FilterQuality = resample switch
                   {
                       ResampleMode.Nearest => SKFilterQuality.None,
                       ResampleMode.Bilinear => SKFilterQuality.Low,
                       _ => SKFilterQuality.High, // 雙三次（含縮小時的 mipmap）
                   },
                   IsAntialias = resample != ResampleMode.Nearest,
               })
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(src, SKRect.Create(0, 0, dstRect.Width, dstRect.Height), paint);
            canvas.Flush();
        }
        using var pixmap = dst.PeekPixels();
        result.CopyFrom(pixmap, new SKPointI(dstRect.Left, dstRect.Top));
        return result;
    }

    /// <summary>把表面某範圍（圖層座標）讀成 SKBitmap（premul BGRA）。</summary>
    public static unsafe SKBitmap ReadRegion(TileSurface surface, SKRectI rect)
    {
        var bitmap = new SKBitmap(new SKImageInfo(rect.Width, rect.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var dst = (uint*)bitmap.GetPixels();
        new Span<uint>(dst, rect.Width * rect.Height).Clear();

        foreach (var idx in TileIndex.CoveringRect(rect))
        {
            var tile = surface.GetTileForRead(idx);
            if (tile == null) continue;
            var tileRect = idx.ToPixelRect();
            var inter = SKRectI.Intersect(tileRect, rect);
            if (inter.Width <= 0 || inter.Height <= 0) continue;
            var src = (uint*)tile.Pixels;
            for (var y = inter.Top; y < inter.Bottom; y++)
            {
                var srcRow = src + (y - tileRect.Top) * Tile.Size + (inter.Left - tileRect.Left);
                var dstRow = dst + (y - rect.Top) * rect.Width + (inter.Left - rect.Left);
                new ReadOnlySpan<uint>(srcRow, inter.Width).CopyTo(new Span<uint>(dstRow, inter.Width));
            }
        }
        return bitmap;
    }

    // ---- 調整畫布大小（錨點） ----

    /// <summary>
    /// 改變畫布大小，anchorX/anchorY 為 0、0.5、1（左/中/右、上/中/下）決定原內容貼在哪一邊；
    /// 圖層像素不重取樣，只平移 Offset。
    /// </summary>
    public static void ResizeCanvas(EditorSession session, int width, int height, float anchorX, float anchorY,
        string label = "調整畫布大小")
    {
        var doc = session.Document;
        var oldW = doc.Width;
        var oldH = doc.Height;
        if (width < 1 || height < 1 || (width == oldW && height == oldH)) return;

        var dx = (int)Math.Round((width - oldW) * Math.Clamp(anchorX, 0f, 1f));
        var dy = (int)Math.Round((height - oldH) * Math.Clamp(anchorY, 0f, 1f));

        var oldSelection = session.Selection;
        Apply(doc, width, height, dx, dy);
        if (dx != 0 || dy != 0) session.ApplySelection(null);

        session.History.Push(new ActionHistoryEntry(label, SKRectI.Empty,
            undo: d =>
            {
                Apply(d, oldW, oldH, -dx, -dy);
                session.ApplySelection(oldSelection);
            },
            redo: d =>
            {
                Apply(d, width, height, dx, dy);
                if (dx != 0 || dy != 0) session.ApplySelection(null);
            }));

        static void Apply(Document doc, int w, int h, int dx, int dy)
        {
            lock (doc.SyncRoot)
            {
                doc.SetSize(w, h);
                if (dx != 0 || dy != 0)
                {
                    foreach (var layer in DocumentCommands.RasterLayers(doc.Root))
                    {
                        layer.Offset = new SKPointI(layer.Offset.X + dx, layer.Offset.Y + dy);
                        foreach (var element in layer.Elements.ToList())
                            layer.ReplaceElement(element.Translated(dx, dy));
                        layer.ElementCache.MarkAllDirty();
                    }
                }
            }
            InvalidateAll(doc);
        }
    }

    // ---- 單一圖層翻轉 ----

    /// <summary>只翻轉一個圖層（以畫布為軸）；翻轉互為反操作，undo 不存像素。</summary>
    public static void FlipLayer(EditorSession session, RasterLayer layer, GeometryOp op, string label)
    {
        var doc = session.Document;
        ApplyToLayer(doc, layer, op);
        session.History.Push(new ActionHistoryEntry(label, doc.Bounds,
            undo: d => ApplyToLayer(d, layer, GeometryTransform.Inverse(op)),
            redo: d => ApplyToLayer(d, layer, op)));
    }

    private static void ApplyToLayer(Document doc, RasterLayer layer, GeometryOp op)
    {
        var srcSize = new SKSizeI(doc.Width, doc.Height);
        lock (doc.SyncRoot)
        {
            var transformed = GeometryTransform.Transform(layer.Surface, op, srcSize, layer.Offset);
            layer.ReplaceSurface(transformed);
            layer.Offset = SKPointI.Empty;
            foreach (var element in layer.Elements.ToList())
            {
                if (element is not TextElement text) continue;
                layer.ReplaceElement(text with { Position = GeometryTransform.MapForward(op, text.Position, srcSize) });
            }
            layer.ElementCache.MarkAllDirty();
        }
        layer.InvalidateAll();
    }

    // ---- 從檔案匯入圖層 ----

    /// <summary>把影像放進新圖層（插在作用中圖層上方）並設為作用中；可 undo。</summary>
    public static RasterLayer ImportImageLayer(EditorSession session, SKBitmap bitmap, string name)
    {
        var doc = session.Document;
        var layer = new RasterLayer { Name = name };
        using (var pixmap = bitmap.PeekPixels())
        {
            layer.Surface.CopyFrom(pixmap, SKPointI.Empty);
        }

        var active = doc.ActiveLayer;
        var parent = active?.Parent ?? doc.Root;
        var index = active?.Parent != null ? parent.IndexOf(active) + 1 : parent.Children.Count;
        LayerCommands.InsertLayer(doc, session.History, parent, index, layer, "匯入圖層");
        lock (doc.SyncRoot) doc.ActiveLayer = layer;
        return layer;
    }

    // ---- 共用 ----

    private static void RestoreElements(RasterLayer layer, List<VectorElement> elements)
    {
        foreach (var current in layer.Elements.ToList()) layer.RemoveElement(current.Id);
        foreach (var element in elements) layer.AddElement(element);
        layer.ElementCache.MarkAllDirty();
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
        doc.NotifyChanged(doc.Bounds);
    }
}
