using MinePainter.Core.Tiles;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

public class TileSurfaceTests
{
    [Fact]
    public void GetTileForWrite_CreatesZeroedTile()
    {
        using var surface = new TileSurface();
        var tile = surface.GetTileForWrite(new TileIndex(0, 0));
        Assert.True(tile.IsBlank());
        Assert.Equal(1, surface.TileCount);
    }

    [Fact]
    public void GetTileForRead_MissingTile_ReturnsNull()
    {
        using var surface = new TileSurface();
        Assert.Null(surface.GetTileForRead(new TileIndex(3, 5)));
    }

    [Fact]
    public void Snapshot_IsIsolatedFromLaterWrites()
    {
        using var surface = new TileSurface();
        var idx = new TileIndex(0, 0);

        var tile = surface.GetTileForWrite(idx);
        tile.PixelSpan[0] = 0xAB;

        using var snapshot = surface.Snapshot();

        // 快照後再寫：COW 應該讓快照看不到新值
        var tile2 = surface.GetTileForWrite(idx);
        tile2.PixelSpan[0] = 0xCD;

        Assert.Equal(0xAB, snapshot.GetTile(idx)!.PixelSpan[0]);
        Assert.Equal(0xCD, surface.GetTileForRead(idx)!.PixelSpan[0]);
    }

    [Fact]
    public void Snapshot_SharesUntilWrite()
    {
        using var surface = new TileSurface();
        var idx = new TileIndex(0, 0);
        var tile = surface.GetTileForWrite(idx);

        using var snapshot = surface.Snapshot();
        // 未寫入前：同一塊記憶體（AddRef 即快照，零拷貝）
        Assert.Same(tile, snapshot.GetTile(idx));
        Assert.True(tile.IsShared);
    }

    [Fact]
    public void SnapshotDispose_ReleasesTiles()
    {
        using var surface = new TileSurface();
        var idx = new TileIndex(0, 0);
        var tile = surface.GetTileForWrite(idx);

        var snapshot = surface.Snapshot();
        Assert.True(tile.IsShared);
        snapshot.Dispose();
        Assert.False(tile.IsShared);
        Assert.True(tile.IsAlive);
    }

    [Fact]
    public void RestoreTile_SwapsContent()
    {
        using var surface = new TileSurface();
        var idx = new TileIndex(0, 0);

        surface.GetTileForWrite(idx).PixelSpan[0] = 1;
        using var before = surface.Snapshot();

        surface.GetTileForWrite(idx).PixelSpan[0] = 2;
        Assert.Equal(2, surface.GetTileForRead(idx)!.PixelSpan[0]);

        surface.RestoreTile(idx, before.GetTile(idx));
        Assert.Equal(1, surface.GetTileForRead(idx)!.PixelSpan[0]);
    }

    [Fact]
    public void ContentBounds_TileGranular()
    {
        using var surface = new TileSurface();
        Assert.True(surface.ContentBounds.IsEmpty);

        surface.GetTileForWrite(new TileIndex(1, 2));
        surface.GetTileForWrite(new TileIndex(3, 4));

        Assert.Equal(new SKRectI(256, 512, 1024, 1280), surface.ContentBounds);
    }

    [Fact]
    public void Fill_WritesPremultipliedPixels()
    {
        using var surface = new TileSurface();
        // 50% 透明的純紅
        surface.Fill(new SKRectI(0, 0, 10, 10), new SKColor(255, 0, 0, 128));

        var span = surface.GetTileForRead(new TileIndex(0, 0))!.PixelSpan;
        // BGRA 記憶體序：B=0, G=0, R=premul(255*128/255)=128, A=128
        Assert.Equal(0, span[0]);
        Assert.Equal(0, span[1]);
        Assert.Equal(128, span[2]);
        Assert.Equal(128, span[3]);
    }

    [Fact]
    public void CopyFrom_RoundTripsPixels()
    {
        var info = new SKImageInfo(300, 300, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bmp = new SKBitmap(info);
        using (var c = new SKCanvas(bmp)) c.Clear(new SKColor(10, 20, 30));

        using var surface = new TileSurface();
        using (var pixmap = bmp.PeekPixels())
            surface.CopyFrom(pixmap, SKPointI.Empty);

        // 300×300 跨 2×2 個 tile
        Assert.Equal(4, surface.TileCount);

        // 抽查一個跨 tile 邊界的像素 (299, 299) → tile(1,1) 內 (43, 43)
        var tile = surface.GetTileForRead(new TileIndex(1, 1))!;
        var offset = (43 * Tile.Size + 43) * 4;
        var span = tile.PixelSpan;
        Assert.Equal(30, span[offset + 0]); // B
        Assert.Equal(20, span[offset + 1]); // G
        Assert.Equal(10, span[offset + 2]); // R
    }
}
