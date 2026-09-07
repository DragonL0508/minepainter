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

/// <summary>複製與滴管共用的畫面像素讀取；不擁有編輯狀態，回傳影像由呼叫端接手。</summary>
internal static class SessionPixelReader
{
    /// <summary>
    /// 取作用中圖層在選取範圍內「看得到的樣子」（無選取＝整個畫布範圍）。
    /// 呼叫者接手回傳影像的擁有權；沒有內容可複製時回傳 null。
    ///
    /// 回報取像的左上角文件座標，
    /// 讓貼上能貼回原處（<paramref name="origin"/> 在回傳 null 時無意義）。
    ///
    /// 取的是**算繪後的樣子**：效果堆疊（外框／陰影…）與文字物件都在裡面，群組則是整組合成後的樣子。
    /// 貼到別的程式去要的就是眼睛看到的那張圖，不是圖層底下那份原始像素
    /// （文字圖層根本沒有像素，只取基底的話會複製到一張空白）。
    /// </summary>
    public static SKImage? CopyToImage(EditorSession session, out SKPointI origin)
    {
        origin = default;
        if (session.Document.ActiveLayer is not { CanHaveEffects: true } node) return null;

        // 效果快取要先是最新的（複製是使用者按下去才發生的一次性動作，等得起）
        if (node.HasActiveEffects) LayerEffectRenderer.RenderLayerNow(session.Document, node);

        var selection = session.Selection is { IsEmpty: false } s ? s : null;
        var bounds = selection != null
            ? SKRectI.Intersect(selection.Bounds, session.Document.Bounds)
            : session.Document.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;
        origin = new SKPointI(bounds.Left, bounds.Top);

        var info = new SKImageInfo(bounds.Width, bounds.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        if (surface == null) return null;
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        lock (session.Document.SyncRoot)
        {
            canvas.Save();
            canvas.Translate(-bounds.Left, -bounds.Top);
            DrawNodeAppearanceLocked(node, canvas, bounds);
            canvas.Restore();
            if (selection != null) FloatingSelection.ApplySelectionMask(selection, canvas, bounds);
        }
        canvas.Flush();
        return surface.Snapshot();
    }

    /// <summary>
    /// 把一個節點「畫面上的樣子」畫進 canvas（doc 座標）。在 session.Document.SyncRoot 內呼叫。
    /// 效果快取已算好時它就是最終樣貌（文字物件也已經併在裡面）。
    /// </summary>
    private static void DrawNodeAppearanceLocked(LayerNode node, SKCanvas canvas, SKRectI docRect)
    {
        if (node.EffectsRendered)
        {
            DrawSurface(node.FxCache.Surface, node.EffectOffset, canvas, docRect);
            return;
        }

        switch (node)
        {
            case RasterLayer raster:
                FloatingSelection.DrawLayerPixels(raster, canvas, docRect);
                foreach (var el in raster.Elements)
                {
                    if (raster.IsElementHidden(el.Id)) continue;
                    el.Render(canvas);
                }
                break;

            case GroupLayer group:
                DrawGroupPixels(group, canvas, docRect);
                break;
        }
    }

    private static void DrawSurface(TileSurface source, SKPointI offset, SKCanvas canvas, SKRectI docRect)
    {
        var rect = new SKRectI(
            docRect.Left - offset.X, docRect.Top - offset.Y,
            docRect.Right - offset.X, docRect.Bottom - offset.Y);
        foreach (var idx in TileIndex.CoveringRect(rect))
        {
            var tile = source.GetTileForRead(idx);
            if (tile == null) continue;
            using var pixmap = tile.AsPixmap();
            using var img = SKImage.FromPixels(pixmap);
            var tileRect = idx.ToPixelRect();
            canvas.DrawImage(img, tileRect.Left + offset.X, tileRect.Top + offset.Y);
        }
    }

    private static unsafe void DrawGroupPixels(GroupLayer group, SKCanvas canvas, SKRectI docRect)
    {
        var pixels = Compositing.Compositor.StaticGroupSourceLocked(group, docRect);
        if (pixels.Length < docRect.Width * docRect.Height) return;
        fixed (uint* ptr = pixels)
        {
            var info = new SKImageInfo(docRect.Width, docRect.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var img = SKImage.FromPixels(info, (IntPtr)ptr, docRect.Width * 4);
            canvas.DrawImage(img, docRect.Left, docRect.Top);
        }
    }

    /// <summary>
    /// 取畫面上該點的顏色（滴管用）。
    ///
    /// 合成快取在覆疊路徑下不含浮動內容（那是 render thread 才疊上去的），
    /// 所以要自己把浮動內容那一層補回來 —— 否則滴管會滴到浮動內容「底下」的顏色。
    /// </summary>
    public static unsafe SKColor SampleComposite(EditorSession session, int x, int y)
    {
        var below = session.Compositor.SamplePixel(x, y);
        if (session.FloatingOverlay is not { } floating) return below;

        var rect = floating.TargetRect;
        var px = x + 0.5f;
        var py = y + 0.5f;
        if (rect.Width <= 0 || rect.Height <= 0 || !rect.Contains(px, py)) return below;

        // 目前位置 → 提起時的影像座標（縮放中也對得上）
        var sx = (int)((px - rect.Left) / rect.Width * floating.SourceBounds.Width);
        var sy = (int)((py - rect.Top) / rect.Height * floating.SourceBounds.Height);
        sx = Math.Clamp(sx, 0, floating.Pixels.Width - 1);
        sy = Math.Clamp(sy, 0, floating.Pixels.Height - 1);

        var info = new SKImageInfo(1, 1, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        Span<byte> pixel = stackalloc byte[4];
        fixed (byte* ptr = pixel)
        {
            if (!floating.Pixels.ReadPixels(info, (IntPtr)ptr, 4, sx, sy)) return below;
        }

        var top = new SKColor(pixel[2], pixel[1], pixel[0], pixel[3]);
        if (top.Alpha == 0) return below;
        if (top.Alpha == 255) return top;

        // SrcOver（滴管只在乎 RGB，取回不透明色）
        var a = top.Alpha / 255f;
        return new SKColor(
            (byte)(top.Red * a + below.Red * (1 - a)),
            (byte)(top.Green * a + below.Green * (1 - a)),
            (byte)(top.Blue * a + below.Blue * (1 - a)),
            Math.Max(top.Alpha, below.Alpha));
    }

}
