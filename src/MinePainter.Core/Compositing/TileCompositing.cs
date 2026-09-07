using MinePainter.Core.Documents;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using SkiaSharp;

namespace MinePainter.Core.Compositing;

/// <summary>同步圖層像素合成；呼叫端持有文件鎖，排程與影像回收由 Compositor 管理。</summary>
internal static class TileCompositing
{
    internal static unsafe uint[] ReadGroupPixelsLocked(GroupLayer group, SKRectI docRect,
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
        Effects.LayerEffectRenderer.RenderAllNow(doc, Compositor.StaticGroupSourceLocked);
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

    /// <summary>走像素路徑的調整：快照讀成 premul BGRA、就地套、包成新影像（呼叫端 Dispose）。</summary>
    private static unsafe SKImage ApplyPixelAdjustment(SKImage snap, Adjustments.IAdjustment adjustment)
    {
        var info = new SKImageInfo(snap.Width, snap.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var pixels = new uint[snap.Width * snap.Height];
        fixed (uint* ptr = pixels)
        {
            snap.ReadPixels(info, (IntPtr)ptr, info.RowBytes, 0, 0);
            adjustment.ApplyPixels(pixels, pixels.Length);
            return SKImage.FromPixelCopy(info, (IntPtr)ptr, info.RowBytes);
        }
    }

    /// <summary>把群組內容合成到 surface（canvas 原點 = tileRect 左上）。回傳是否畫了東西。</summary>
    internal static bool CompositeGroup(GroupLayer group, SKSurface surface, SKRectI tileRect,
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
                    var full = adj.Opacity >= 1f;
                    using var paint = new SKPaint
                    {
                        BlendMode = full ? SKBlendMode.Src : SKBlendMode.SrcOver,
                        Color = SKColors.White.WithAlpha((byte)(adj.Opacity * 255)),
                    };
                    if (adj.Adjustment.RequiresPixelPath)
                    {
                        // 色彩濾鏡做不到的調整（3D LUT）：讀出來逐像素算，再當一張圖畫回去
                        using var filtered = ApplyPixelAdjustment(snap, adj.Adjustment);
                        canvas.DrawImage(filtered, 0, 0, paint);
                        break;
                    }
                    using var filter = adj.Adjustment.CreateColorFilter();
                    paint.ColorFilter = filter;
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
                    var strokeHere = !detached && stroke != null && stroke.ShouldOverlay(raster) &&
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

                    if (!strokeHere && elementTile == null && !floatingHere && !CustomBlend.IsCustom(raster.BlendMode))
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
                    var custom = CustomBlend.IsCustom(raster.BlendMode);
                    var isolate = raster.Opacity < 1f ||
                                  raster.BlendMode != BlendMode.Normal ||
                                  (strokeHere && stroke!.IsEraser);

                    // Skia 沒有的混合模式：內容先隔離畫到一格暫存 tile，再由 CustomBlend 逐像素疊上去
                    using var scratch = custom ? SKSurface.Create(Tile.Info) : null;
                    var target = scratch?.Canvas ?? canvas;
                    if (custom) target.Clear(SKColors.Transparent);
                    else if (isolate)
                    {
                        using var layerPaint = new SKPaint
                        {
                            Color = new SKColor(255, 255, 255, (byte)(raster.Opacity * 255)),
                            BlendMode = raster.BlendMode.ToSkia(),
                        };
                        canvas.SaveLayer(layerPaint);
                    }

                    CompositeRasterContent(raster, target, tileRect, isolate ? 1f : raster.Opacity);
                    if (strokeHere) DrawStrokeOverlay(stroke!, target, tileRect);
                    if (floatingHere)
                    {
                        target.Save();
                        target.Translate(-tileRect.Left, -tileRect.Top);
                        floating!.DrawInto(target, preview: true);
                        target.Restore();
                    }
                    if (elementTile != null)
                    {
                        using var pixmap = elementTile.AsPixmap();
                        using var img = SKImage.FromPixels(pixmap);
                        target.DrawImage(img, 0, 0);
                    }

                    if (custom)
                    {
                        target.Flush();
                        using var content = scratch!.Snapshot();
                        CustomBlend.DrawImage(surface, content, 0, 0, raster.Opacity, raster.BlendMode);
                    }
                    else if (isolate) canvas.Restore();
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
                        using var pixmap = contentTile.AsPixmap();
                        using var img = SKImage.FromPixels(pixmap);
                        if (CustomBlend.IsCustom(nested.BlendMode))
                        {
                            CustomBlend.DrawImage(surface, img, 0, 0, nested.Opacity, nested.BlendMode);
                        }
                        else
                        {
                            using var paint = new SKPaint
                            {
                                Color = new SKColor(255, 255, 255, (byte)(nested.Opacity * 255)),
                                BlendMode = nested.BlendMode.ToSkia(),
                            };
                            canvas.DrawImage(img, 0, 0, paint);
                        }
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
}
