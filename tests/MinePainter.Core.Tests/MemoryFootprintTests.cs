using MinePainter.Core.Compositing;
using MinePainter.Core.Documents;
using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 多分頁的記憶體用量：背景分頁不留合成快取、undo 預算所有文件共用、tile 池不無限長大。
/// </summary>
public class MemoryFootprintTests
{
    /// <summary>等到整份文件都合成完（沒有格子在排隊），之後 worker 就是閒著的。</summary>
    private static void WaitForFullComposite(Compositor compositor, Document doc, int timeoutMs = 3000)
    {
        var tiles = TileIndex.CoveringRect(doc.Bounds).ToList();
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var ready = tiles.Count(idx => compositor.TryGetTile(idx, out _));
            if (ready == tiles.Count && compositor.DirtyCount == 0) return;
            Thread.Sleep(10);
        }
        throw new TimeoutException("合成逾時");
    }

    [Fact]
    public void CompositeCache_EvictsBeyondBudget()
    {
        const long budget = 4L * Tile.BytesPerTile;
        using var doc = ImageCodec.CreateBlankDocument(1024, 1024, SKColors.White); // 4×4 = 16 格
        using var compositor = new Compositor(doc) { CacheBudgetBytes = budget };

        // 不用 TryGetTile 等（那會把淘汰掉的格又排回去）：看 worker 自己的計數。
        // 逾時放寬 —— 這條驗的是「會不會淘汰」，不是機器多快。
        var deadline = Environment.TickCount64 + 30000;
        while (compositor.TilesRendered < 16 || compositor.DirtyCount > 0 ||
               compositor.CachedBytes > budget)
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException($"合成/淘汰逾時：已合成 {compositor.TilesRendered} 格、" +
                    $"待處理 {compositor.DirtyCount} 格、快取 {compositor.CachedBytes} bytes");
            Thread.Sleep(10);
        }

        Assert.True(compositor.EvictedTiles > 0, "超出預算應該要淘汰");
        Assert.True(compositor.CachedBytes <= budget, $"快取 {compositor.CachedBytes} bytes 超出預算");
    }

    [Fact]
    public void Suspend_DropsCompositeCache_AndResumeRebuildsIt()
    {
        using var doc = ImageCodec.CreateBlankDocument(512, 512, SKColors.White);
        using var compositor = new Compositor(doc);

        WaitForFullComposite(compositor, doc);
        Assert.Equal(4, compositor.CachedTileCount); // 512×512 = 2×2 格

        compositor.Suspend();
        Assert.True(compositor.IsSuspended);
        Assert.Equal(0, compositor.CachedTileCount); // 背景分頁不留整份文件的合成結果
        Assert.Equal(0, compositor.DirtyCount);      // 也不會自己再排隊算回來

        compositor.Resume();
        Assert.False(compositor.IsSuspended);
        WaitForFullComposite(compositor, doc);
        Assert.Equal(4, compositor.CachedTileCount);
    }

    [Fact]
    public void Suspend_ReleasesGroupCacheTiles()
    {
        using var doc = ImageCodec.CreateBlankDocument(256, 256, SKColors.White);
        var group = new GroupLayer { Name = "群組", Opacity = 0.5f }; // 半透明 = 一定要隔離合成
        var child = new RasterLayer { Name = "內容" };
        child.Surface.Fill(new SKRectI(0, 0, 256, 256), SKColors.Red);
        lock (doc.SyncRoot)
        {
            doc.Root.Add(group);
            group.Add(child);
        }

        using var compositor = new Compositor(doc);
        WaitForFullComposite(compositor, doc);
        Assert.True(group.Cache.Surface.TileCount > 0);

        compositor.Suspend();
        Assert.Equal(0, group.Cache.Surface.TileCount);
    }

    [Fact]
    public void HistoryBudget_IsSharedAcrossOpenDocuments()
    {
        var savedGlobal = HistoryManager.GlobalMemoryLimit;
        try
        {
            HistoryManager.GlobalMemoryLimit = 4 * HistoryManager.MinimumShareBytes;

            using var a = new EditorSession(ImageCodec.CreateBlankDocument(64, 64, SKColors.White));
            using var b = new EditorSession(ImageCodec.CreateBlankDocument(64, 64, SKColors.White));
            using var c = new EditorSession(ImageCodec.CreateBlankDocument(64, 64, SKColors.White));
            using var d = new EditorSession(ImageCodec.CreateBlankDocument(64, 64, SKColors.White));

            // 四份文件平分同一份預算 —— 不是每份都拿到完整的 MemoryLimit
            var share = Math.Max(HistoryManager.MinimumShareBytes, HistoryManager.GlobalMemoryLimit / 4);
            foreach (var s in new[] { a, b, c, d })
            {
                Assert.True(s.History.EffectiveMemoryLimit <= share,
                    $"每份文件的額度 {s.History.EffectiveMemoryLimit} 應不超過 {share}");
                Assert.True(s.History.EffectiveMemoryLimit < s.History.MemoryLimit,
                    "額度應該被全域預算壓下來，而不是各自吃滿");
            }
            var sum = a.History.EffectiveMemoryLimit + b.History.EffectiveMemoryLimit +
                      c.History.EffectiveMemoryLimit + d.History.EffectiveMemoryLimit;
            Assert.True(sum <= HistoryManager.GlobalMemoryLimit, $"總額 {sum} 超出全域預算");
        }
        finally
        {
            HistoryManager.GlobalMemoryLimit = savedGlobal;
        }
    }

    [Fact]
    public void HistoryBudget_KeepsMinimumSharePerDocument()
    {
        var savedGlobal = HistoryManager.GlobalMemoryLimit;
        try
        {
            HistoryManager.GlobalMemoryLimit = HistoryManager.MinimumShareBytes; // 極小預算
            var sessions = Enumerable.Range(0, 8)
                .Select(_ => new EditorSession(ImageCodec.CreateBlankDocument(64, 64, SKColors.White)))
                .ToList();
            try
            {
                // 分頁再多，每一份都還留得住幾步 undo
                foreach (var s in sessions)
                    Assert.Equal(HistoryManager.MinimumShareBytes, s.History.EffectiveMemoryLimit);
            }
            finally
            {
                foreach (var s in sessions) s.Dispose();
            }
        }
        finally
        {
            HistoryManager.GlobalMemoryLimit = savedGlobal;
        }
    }

    [Fact]
    public void TilePool_CapsFreeList_AndTrimReleasesIt()
    {
        using var pool = new TilePool { MaxFreeTiles = 4 };
        var tiles = Enumerable.Range(0, 16).Select(_ => Tile.Rent(pool)).ToList();
        Assert.Equal(16, pool.RentedCount);

        foreach (var t in tiles) t.Release();
        Assert.Equal(0, pool.RentedCount);
        Assert.True(pool.FreeCount <= 4, $"閒置緩衝區 {pool.FreeCount} 應被壓在上限 4 之內");

        pool.Trim();
        Assert.Equal(0, pool.FreeCount);
    }
}
