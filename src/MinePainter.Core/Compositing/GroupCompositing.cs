using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using SkiaSharp;

namespace MinePainter.Core.Compositing;

internal static partial class TileCompositing
{
    /// <summary>
    /// 遮色片在這一格 tile 上的樣子。「整格 255」與「整格 0」是絕大多數格子的情況
    /// （遮色片只框住畫面一小塊，其餘全是預設值），這兩種不用逐像素算：
    /// 前者當沒有遮色片、後者這個子層在這格根本不用畫。
    /// </summary>
    private enum MaskCoverage { Full, Empty, Partial }

    private static MaskCoverage ClassifyMask(LayerMask? mask, SKRectI tileRect)
    {
        if (mask == null) return MaskCoverage.Full;
        var rendered = mask.Rendered;
        if (rendered.Bounds.IntersectsWith(tileRect)) return MaskCoverage.Partial;
        return rendered.DefaultValue switch
        {
            255 => MaskCoverage.Full,
            0 => MaskCoverage.Empty,
            _ => MaskCoverage.Partial,
        };
    }

    /// <summary>把遮色片在 tile 第 <paramref name="row"/> 列的 256 個覆蓋值填進 <paramref name="into"/>。</summary>
    private static void FillCoverageRow(LayerMask? mask, SKRectI tileRect, int row, Span<byte> into)
    {
        var rendered = mask?.Rendered;
        if (rendered == null)
        {
            into.Fill(255);
            return;
        }
        into.Fill(rendered.DefaultValue);
        var bounds = rendered.Bounds;
        var y = tileRect.Top + row;
        if (y < bounds.Top || y >= bounds.Bottom) return;
        var x0 = Math.Max(bounds.Left, tileRect.Left);
        var x1 = Math.Min(bounds.Right, tileRect.Right);
        if (x1 <= x0) return;
        var src = rendered.Alpha.AsSpan((y - bounds.Top) * bounds.Width + (x0 - bounds.Left), x1 - x0);
        src.CopyTo(into.Slice(x0 - tileRect.Left));
    }

    /// <summary>
    /// 把 surface 目前的像素讀進一塊池子借來的 tile（呼叫端 Release）。
    /// 遞迴合成裡不能共用 Snapshot：SkiaSharp 對沒變的 surface 會回同一個 SKImage，
    /// 內層 Dispose 掉外層還握著的那份。自己的緩衝就沒這問題，也不必每格 malloc 256KB。
    /// </summary>
    private static Tile ReadCompositePixels(SKSurface surface)
    {
        var tile = Tile.Rent(TilePool.Shared, zeroed: false);
        surface.ReadPixels(Tile.Info, tile.Pixels, Tile.RowBytes, 0, 0);
        return tile;
    }

    private static void WriteCompositePixels(SKSurface surface, Tile tile)
    {
        using var pixmap = tile.AsPixmap();
        using var image = SKImage.FromPixels(pixmap);
        using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
        surface.Canvas.DrawImage(image, 0, 0, paint);
        surface.Canvas.Flush();
    }

    /// <summary>
    /// 子層畫完之後，把「限制通道」與「遮色片」套回去：限制的通道還原成畫之前的值，
    /// 遮色片則在畫之前／之後兩份之間依覆蓋值內插（W3C 的合成公式對來源 alpha 是線性的，
    /// 所以這等於把子層的 alpha 乘上覆蓋值，任何混合模式都成立）。
    ///
    /// 2026-09-10：原本每個像素每個通道都呼叫一次 <see cref="LayerMask.At"/>（一格 26 萬次虛擬呼叫，
    /// 每次還要走 Rendered 的快取判斷），一格要 6～20 ms；使用者開 50 層的 PSD 只剩 20 fps。
    /// 現在整列一次取覆蓋值、無遮色片時只做通道搬運。
    /// </summary>
    private static unsafe void RestoreChannels(SKSurface surface, Tile before, int channels, LayerMask? mask, SKRectI tileRect)
    {
        var coverage = mask == null ? MaskCoverage.Full : ClassifyMask(mask, tileRect);
        if (coverage == MaskCoverage.Full && channels == 0) return;

        var afterTile = ReadCompositePixels(surface);
        try
        {
            var src = (byte*)before.Pixels;
            var dst = (byte*)afterTile.Pixels;

            // 通道位元：bit0＝R、bit1＝G、bit2＝B；像素在記憶體裡是 B,G,R,A
            var keepB = (channels & 4) != 0;
            var keepG = (channels & 2) != 0;
            var keepR = (channels & 1) != 0;

            if (coverage == MaskCoverage.Full)
            {
                var keep = (keepB ? 0x000000FFu : 0) | (keepG ? 0x0000FF00u : 0) | (keepR ? 0x00FF0000u : 0);
                var s = (uint*)src;
                var d = (uint*)dst;
                for (var i = 0; i < Tile.Size * Tile.Size; i++)
                    d[i] = (d[i] & ~keep) | (s[i] & keep);
        }
        else
        {
            Span<byte> row = stackalloc byte[Tile.Size];
            for (var y = 0; y < Tile.Size; y++)
            {
                FillCoverageRow(mask, tileRect, y, row);
                var offset = y * Tile.RowBytes;
                for (var x = 0; x < Tile.Size; x++)
                {
                    int c = row[x];
                    var i = offset + x * 4;
                    if (c == 0)
                    {
                        *(uint*)(dst + i) = *(uint*)(src + i);
                        continue;
                    }
                    var inv = 255 - c;
                    dst[i] = keepB ? src[i] : (byte)((src[i] * inv + dst[i] * c + 127) / 255);
                    dst[i + 1] = keepG ? src[i + 1] : (byte)((src[i + 1] * inv + dst[i + 1] * c + 127) / 255);
                    dst[i + 2] = keepR ? src[i + 2] : (byte)((src[i + 2] * inv + dst[i + 2] * c + 127) / 255);
                    dst[i + 3] = (byte)((src[i + 3] * inv + dst[i + 3] * c + 127) / 255);
                }
            }
        }
        WriteCompositePixels(surface, afterTile);
        }
        finally
        {
            afterTile.Release();
        }
    }

    /// <summary>
    /// 帶遮色片或直通（pass-through）的群組：直通群組的子層直接看得到下方的內容（混合模式吃到的是
    /// 群組外的背景），群組遮色片則作用在「整組畫完之後」與「畫之前」的差異上。
    /// 遮色片在這格整格 255 且不必縮 opacity 時，直接把子層畫在 surface 上，一份複製都不用。
    /// </summary>
    private static unsafe bool CompositeMaskedGroup(GroupLayer group, SKSurface destination, SKRectI rect,
        StrokeBuffer? stroke, Selections.FloatingSelection? floating,
        (Guid? Id, bool IncludesElements) detached, bool hasBackdrop)
    {
        var pass = group.IsPassThrough && group.BlendMode == BlendMode.Normal && !group.HasActiveEffects;
        var coverage = ClassifyMask(group.Mask, rect);
        if (coverage == MaskCoverage.Empty) return false; // 整格被遮掉：畫了也等於沒畫

        var plain = coverage == MaskCoverage.Full && (!pass || group.Opacity >= 1f);
        if (plain && pass)
            return CompositeGroup(group, destination, rect, stroke, floating, detached, hasBackdrop);

        var original = plain ? null : ReadCompositePixels(destination);
        Tile? result = null;
        try
        {
            using var scratch = plain ? null : SKSurface.Create(Tile.Info);
            var target = scratch ?? destination;
            if (scratch != null)
            {
                using var pixmap = original!.AsPixmap();
                using var image = SKImage.FromPixels(pixmap);
                scratch.Canvas.DrawImage(image, 0, 0);
        }

        if (pass)
        {
            if (!CompositeGroup(group, target, rect, stroke, floating, detached, hasBackdrop)) return false;
        }
        else
        {
            var tile = group.EffectsRendered
                ? group.FxCache.Surface.GetTileForRead(TileIndex.FromPixel(rect.Left, rect.Top))
                : RenderGroupTile(group, rect, stroke, floating, detached);
            if (tile == null) return false;
            using var pixmap = tile.AsPixmap();
            using var image = SKImage.FromPixels(pixmap);
            if (CustomBlend.IsCustom(group.BlendMode))
                CustomBlend.DrawImage(target, image, 0, 0, group.Opacity, group.BlendMode);
            else
            {
                using var paint = new SKPaint { BlendMode = group.BlendMode.ToSkia(),
                    Color = SKColors.White.WithAlpha((byte)(group.Opacity * 255)) };
                target.Canvas.DrawImage(image, 0, 0, paint);
            }
        }
        if (plain) return true;

        result = ReadCompositePixels(scratch!);
        var a = (byte*)original!.Pixels;
        var b = (byte*)result.Pixels;
        var opacity = pass ? group.Opacity : 1f;
        Span<byte> row = stackalloc byte[Tile.Size];
        for (var y = 0; y < Tile.Size; y++)
        {
            FillCoverageRow(group.Mask, rect, y, row);
            var offset = y * Tile.RowBytes;
            for (var x = 0; x < Tile.Size; x++)
            {
                var c = opacity >= 1f ? row[x] : (int)MathF.Round(row[x] * opacity);
                var i = offset + x * 4;
                if (c == 255) continue;
                if (c == 0)
                {
                    *(uint*)(b + i) = *(uint*)(a + i);
                    continue;
                }
                var inv = 255 - c;
                b[i] = (byte)((a[i] * inv + b[i] * c + 127) / 255);
                b[i + 1] = (byte)((a[i + 1] * inv + b[i + 1] * c + 127) / 255);
                b[i + 2] = (byte)((a[i + 2] * inv + b[i + 2] * c + 127) / 255);
                b[i + 3] = (byte)((a[i + 3] * inv + b[i + 3] * c + 127) / 255);
            }
        }
        WriteCompositePixels(destination, result);
        return true;
        }
        finally
        {
            original?.Release();
            result?.Release();
        }
    }
}
