using MinePainter.Core.AI;
using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Selections;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

public class GrabCutTests
{
    private static uint Px(byte r, byte g, byte b) => 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;

    /// <summary>雜訊背景（偏綠）上一個雜訊的圓（偏紅）；回傳 premul BGRA。</summary>
    private static uint[] NoisyScene(int w, int h, SKPoint c, float r, int seed = 7)
    {
        var rnd = new Random(seed);
        var px = new uint[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var inside = (x - c.X) * (x - c.X) + (y - c.Y) * (y - c.Y) <= r * r;
            var n = rnd.Next(-25, 26);
            px[y * w + x] = inside
                ? Px((byte)Math.Clamp(190 + n, 0, 255), (byte)Math.Clamp(60 + n, 0, 255), (byte)Math.Clamp(50 + n, 0, 255))
                : Px((byte)Math.Clamp(70 + n, 0, 255), (byte)Math.Clamp(150 + n, 0, 255), (byte)Math.Clamp(90 + n, 0, 255));
        }
        return px;
    }

    [Fact]
    public void MaxFlow_SimpleCut()
    {
        // 0-1 強邊、1-2 弱邊、2-3 強邊；0 接 source、3 接 sink → 割在 1-2
        var g = new GrabCut.MaxFlow(4, 3);
        g.AddTerminal(0, 100, 0);
        g.AddTerminal(3, 0, 100);
        g.AddEdge(0, 1, 50);
        g.AddEdge(1, 2, 1);
        g.AddEdge(2, 3, 50);
        g.Compute();
        Assert.True(g.IsSource(0));
        Assert.True(g.IsSource(1));
        Assert.False(g.IsSource(2));
        Assert.False(g.IsSource(3));
    }

    /// <summary>簡單的 Edmonds–Karp 當參考。</summary>
    private static double RefMaxFlow(int n, float[] tsrc, float[] tsink, List<(int a, int b, float c)> edges)
    {
        var N = n + 2; var S = n; var T = n + 1;
        var cap = new double[N, N];
        for (var i = 0; i < n; i++) { cap[S, i] += tsrc[i]; cap[i, T] += tsink[i]; }
        foreach (var (a, b, c) in edges) { cap[a, b] += c; cap[b, a] += c; }
        double flow = 0;
        while (true)
        {
            var parent = new int[N]; Array.Fill(parent, -1); parent[S] = S;
            var q = new Queue<int>(); q.Enqueue(S);
            while (q.Count > 0 && parent[T] == -1)
            {
                var u = q.Dequeue();
                for (var v = 0; v < N; v++)
                    if (parent[v] == -1 && cap[u, v] > 1e-9) { parent[v] = u; q.Enqueue(v); }
            }
            if (parent[T] == -1) break;
            var b = double.MaxValue;
            for (var v = T; v != S; v = parent[v]) b = Math.Min(b, cap[parent[v], v]);
            for (var v = T; v != S; v = parent[v]) { cap[parent[v], v] -= b; cap[v, parent[v]] += b; }
            flow += b;
        }
        return flow;
    }

    /// <summary>Boykov–Kolmogorov 的流量與 Edmonds–Karp 在隨機 8 鄰域格狀圖上一致。</summary>
    [Fact]
    public void MaxFlow_MatchesReference_OnRandomGrids()
    {
        var rnd = new Random(3);
        var bad = new List<string>();
        for (var trial = 0; trial < 200; trial++)
        {
            var w = rnd.Next(2, 7); var h = rnd.Next(2, 7); var n = w * h;
            var tsrc = new float[n]; var tsink = new float[n];
            var edges = new List<(int, int, float)>();
            var g = new GrabCut.MaxFlow(n, n * 4);
            for (var i = 0; i < n; i++)
            {
                tsrc[i] = rnd.Next(0, 3) == 0 ? 0 : (float)rnd.NextDouble() * 10;
                tsink[i] = rnd.Next(0, 3) == 0 ? 0 : (float)rnd.NextDouble() * 10;
                g.AddTerminal(i, tsrc[i], tsink[i]);
            }
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                void E(int j) { var c = (float)rnd.NextDouble() * 5; edges.Add((i, j, c)); g.AddEdge(i, j, c); }
                if (x > 0) E(i - 1);
                if (y > 0) E(i - w);
                if (x > 0 && y > 0) E(i - w - 1);
                if (x < w - 1 && y > 0) E(i - w + 1);
            }
            g.Compute();
            var rs = new float[n]; var rk = new float[n];
            for (var i = 0; i < n; i++) { var d = tsrc[i] - tsink[i]; rs[i] = Math.Max(0, d); rk[i] = Math.Max(0, -d); }
            var expected = RefMaxFlow(n, rs, rk, edges);
            if (Math.Abs(expected - g.Flow) > 1e-3)
                bad.Add($"trial {trial} {w}x{h}: mine={g.Flow:0.###} ref={expected:0.###}");
        }
        Assert.True(bad.Count == 0, string.Join("; ", bad.Take(5)));
    }

    /// <summary>矩形框住雜訊圓（框比圓大一截）：圓內是前景、框內圓外的背景被切掉。</summary>
    [Fact]
    public void Run_RectAroundNoisyCircle_FindsCircle()
    {
        const int w = 200, h = 160;
        var px = NoisyScene(w, h, new SKPoint(100, 80), 40);
        var trimap = new byte[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            trimap[y * w + x] = x >= 40 && x < 160 && y >= 25 && y < 135 ? GrabCut.ProbableForeground : GrabCut.Background;

        var mask = GrabCut.Run(px, w, h, trimap);

        Assert.Equal(255, mask[80 * w + 100]);
        Assert.Equal(255, mask[80 * w + 70]);
        Assert.Equal(0, mask[80 * w + 150]);   // 框內、圓外
        Assert.Equal(0, mask[30 * w + 100]);
        Assert.Equal(0, mask[10 * w + 10]);    // 框外
        // 整體：圓內 ≥ 95% 選到、框內圓外 ≤ 5% 誤選
        int inHit = 0, inTotal = 0, outHit = 0, outTotal = 0;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var inside = (x - 100) * (x - 100) + (y - 80) * (y - 80) <= 36 * 36;
            var ring = !inside && (x - 100) * (x - 100) + (y - 80) * (y - 80) > 44 * 44 && trimap[y * w + x] != 0;
            if (inside) { inTotal++; if (mask[y * w + x] != 0) inHit++; }
            if (ring) { outTotal++; if (mask[y * w + x] != 0) outHit++; }
        }
        Assert.True(inHit >= inTotal * 0.95, $"inside {inHit}/{inTotal}");
        Assert.True(outHit <= outTotal * 0.05, $"outside {outHit}/{outTotal}");
    }

    /// <summary>大圖會先縮到 320 再算，結果放回原尺寸仍要對。</summary>
    [Fact]
    public void Run_LargeImage_DownscalesAndStillFinds()
    {
        const int w = 900, h = 700;
        var px = NoisyScene(w, h, new SKPoint(450, 350), 180);
        var trimap = new byte[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            trimap[y * w + x] = x >= 200 && x < 700 && y >= 120 && y < 580 ? GrabCut.ProbableForeground : GrabCut.Background;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var mask = GrabCut.Run(px, w, h, trimap);
        sw.Stop();

        Assert.Equal(255, mask[350 * w + 450]);
        Assert.Equal(0, mask[350 * w + 680]);
        Assert.Equal(0, mask[150 * w + 450]);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"took {sw.ElapsedMilliseconds}ms");
    }

    private static byte AlphaAt(RasterLayer layer, int x, int y)
    {
        var lx = x - layer.Offset.X;
        var ly = y - layer.Offset.Y;
        var tile = layer.Surface.GetTileForRead(TileIndex.FromPixel(lx, ly));
        if (tile == null) return 0;
        var rect = TileIndex.FromPixel(lx, ly).ToPixelRect();
        return tile.PixelSpan[((ly - rect.Top) * Tile.Size + (lx - rect.Left)) * 4 + 3];
    }

    private static unsafe void WritePixels(RasterLayer layer, uint[] px, int w, int h)
    {
        foreach (var idx in TileIndex.CoveringRect(new SKRectI(0, 0, w, h)))
        {
            var tile = layer.Surface.GetTileForWrite(idx);
            var tr = idx.ToPixelRect();
            var dst = (uint*)tile.Pixels;
            for (var y = tr.Top; y < Math.Min(tr.Bottom, h); y++)
            for (var x = tr.Left; x < Math.Min(tr.Right, w); x++)
                dst[(y - tr.Top) * Tile.Size + (x - tr.Left)] = px[y * w + x];
        }
    }

    /// <summary>矩形選取工具開「物件選取」：框住雜訊圓，選取貼著圓、不含框內的背景。</summary>
    [Fact]
    public void RectSelect_ObjectSelect_SelectsCircleOnly()
    {
        const int w = 256, h = 256;
        var doc = ImageCodec.CreateBlankDocument(w, h, SKColors.Transparent);
        using var session = new EditorSession(doc);
        var layer = (RasterLayer)doc.ActiveLayer!;
        lock (doc.SyncRoot) WritePixels(layer, NoisyScene(w, h, new SKPoint(128, 128), 50), w, h);

        session.ObjectSelect = true;
        session.RectSelect.OnPointerDown(new ToolPointerEvent(new SKPoint(50, 50), 1f), session);
        session.RectSelect.OnPointerMove(new ToolPointerEvent(new SKPoint(210, 210), 1f), session);
        session.RectSelect.OnPointerUp(new ToolPointerEvent(new SKPoint(210, 210), 1f), session);

        var sel = session.Selection;
        Assert.NotNull(sel);
        Assert.Equal(255, sel!.CoverageAt(128, 128));
        Assert.Equal(255, sel.CoverageAt(100, 128));
        Assert.Equal(0, sel.CoverageAt(60, 60));    // 框內、圓外
        Assert.Equal(0, sel.CoverageAt(200, 128));
        Assert.Equal(0, sel.CoverageAt(20, 20));    // 框外
        Assert.Equal("物件選取", session.History.UndoLabel);

        // 關掉就是普通矩形
        session.ObjectSelect = false;
        session.RectSelect.OnPointerDown(new ToolPointerEvent(new SKPoint(50, 50), 1f), session);
        session.RectSelect.OnPointerUp(new ToolPointerEvent(new SKPoint(210, 210), 1f), session);
        Assert.Equal(255, session.Selection!.CoverageAt(60, 60));
    }

    /// <summary>框到全透明的地方：沒有物件，選取維持原狀（不會留下空框）。</summary>
    [Fact]
    public void RectSelect_ObjectSelect_NothingThere_KeepsNoSelection()
    {
        var doc = ImageCodec.CreateBlankDocument(128, 128, SKColors.Transparent);
        using var session = new EditorSession(doc);
        session.ObjectSelect = true;
        session.RectSelect.OnPointerDown(new ToolPointerEvent(new SKPoint(10, 10), 1f), session);
        session.RectSelect.OnPointerUp(new ToolPointerEvent(new SKPoint(100, 100), 1f), session);
        Assert.Null(session.Selection);
    }

    /// <summary>演算去背：不上網，圓留下、背景清掉，一步 undo。</summary>
    [Fact]
    public void Command_LocalRemoval_KeepsCircleClearsBackground()
    {
        const int w = 256, h = 256;
        var doc = ImageCodec.CreateBlankDocument(w, h, SKColors.Transparent);
        using var session = new EditorSession(doc);
        var layer = (RasterLayer)doc.ActiveLayer!;
        lock (doc.SyncRoot) WritePixels(layer, NoisyScene(w, h, new SKPoint(128, 128), 60), w, h);

        var ok = BackgroundRemovalCommand.Run(session, layer, new BackgroundRemovalOptions { RemoveBg = null });
        Assert.True(ok);
        Assert.Equal(255, AlphaAt(layer, 128, 128));
        Assert.Equal(255, AlphaAt(layer, 90, 128));
        Assert.Equal(0, AlphaAt(layer, 20, 20));
        Assert.Equal(0, AlphaAt(layer, 128, 30));
        Assert.Equal("AI 去背", session.History.UndoLabel);

        session.Undo();
        Assert.Equal(255, AlphaAt(layer, 20, 20));
    }
}
