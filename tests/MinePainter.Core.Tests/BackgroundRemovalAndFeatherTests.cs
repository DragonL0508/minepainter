using MinePainter.Core.AI;
using MinePainter.Core.Adjustments;
using MinePainter.Core.Effects;
using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using MinePainter.Core.Selections;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;
using static MinePainter.Core.Effects.EffectMath;

namespace MinePainter.Core.Tests;

public class BackgroundRemovalAndFeatherTests
{
    private static uint[] Canvas(int w, int h, Func<int, int, uint> f)
    {
        var a = new uint[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            a[y * w + x] = f(x, y);
        return a;
    }

    // ---- 羽化物件 ----

    [Fact]
    public void Feather_SoftensEdge_LeavesInteriorUntouched()
    {
        const int w = 64, h = 64;
        // 內容在 x = 12..51，左邊界線落在 x = 11 與 12 之間
        var src = Canvas(w, h, (x, y) => x is >= 12 and < 52 && y is >= 12 and < 52 ? Premul(0, 0, 255, 255) : 0);
        var fx = new ObjectFeatherEffect { Radius = 8, Strength = 100 };
        var ctx = EffectContext.FromPixels(src, w, h, fx.SourceMargin);
        fx.Render(ctx);

        // 內部完全不動（舊版會從邊緣往內淡出一整個半徑，把物件啃掉一圈）
        Assert.Equal(255, A(ctx.Dst[32 * w + 32]));
        Assert.Equal(255, A(ctx.Dst[32 * w + 20]));
        Assert.Equal(255, A(ctx.Dst[32 * w + 17]));

        // 邊緣兩側對稱地過渡，且是單調的
        Assert.InRange(A(ctx.Dst[32 * w + 12]), 120, 200);
        Assert.InRange(A(ctx.Dst[32 * w + 11]), 55, 135);
        Assert.True(A(ctx.Dst[32 * w + 10]) < A(ctx.Dst[32 * w + 11]));
        Assert.True(A(ctx.Dst[32 * w + 11]) < A(ctx.Dst[32 * w + 12]));
        Assert.True(A(ctx.Dst[32 * w + 12]) < A(ctx.Dst[32 * w + 13]));

        // 軟邊之外仍是空的
        Assert.Equal(0u, ctx.Dst[32 * w + 5]);
    }

    [Fact]
    public void Feather_KeepsObjectSize_EdgeStaysPut()
    {
        const int w = 64, h = 64;
        var src = Canvas(w, h, (x, y) => x is >= 12 and < 52 && y is >= 12 and < 52 ? Premul(0, 0, 255, 255) : 0);
        var fx = new ObjectFeatherEffect { Radius = 10 };
        var ctx = EffectContext.FromPixels(src, w, h, fx.SourceMargin);
        fx.Render(ctx);

        // 往外補的和往內淡掉的互相抵銷：整條掃描線的 alpha 總量幾乎不變（＝物件沒被削瘦）
        var before = 0;
        var after = 0;
        for (var x = 0; x < w; x++)
        {
            before += A(src[32 * w + x]);
            after += A(ctx.Dst[32 * w + x]);
        }
        Assert.InRange(after, before - 255, before + 255);

        // 顏色照抄邊緣色，補出來的邊不會發黑
        var edge = ctx.Dst[32 * w + 10];
        Unpremul(edge, out var b, out var g, out var r, out var a);
        Assert.True(a > 0);
        Assert.InRange(r, 240, 255);
        Assert.InRange(g, 0, 12);
        Assert.InRange(b, 0, 12);
    }

    [Fact]
    public void Feather_SemiTransparentObject_KeepsItsOwnOpacity()
    {
        const int w = 48, h = 48;
        var src = Canvas(w, h, (x, y) => x is >= 12 and < 36 && y is >= 12 and < 36 ? Premul(0, 0, 255, 128) : 0);
        var fx = new ObjectFeatherEffect { Radius = 8 };
        var ctx = EffectContext.FromPixels(src, w, h, fx.SourceMargin);
        fx.Render(ctx);

        Assert.Equal(128, A(ctx.Dst[24 * w + 24]));           // 內部原樣
        Assert.InRange(A(ctx.Dst[24 * w + 11]), 20, 80);      // 外圈不會比物件本身還濃
        Assert.True(A(ctx.Dst[24 * w + 11]) < 128);
    }

    [Fact]
    public void Feather_Strength_BlendsTowardsTheSoftEdge()
    {
        const int w = 32, h = 32;
        var src = Canvas(w, h, (x, y) => x >= 8 && x < 24 && y >= 8 && y < 24 ? Premul(0, 0, 255, 255) : 0);
        var full = new ObjectFeatherEffect { Radius = 6, Strength = 100 };
        var ctx = EffectContext.FromPixels(src, w, h, full.SourceMargin);
        full.Render(ctx);
        var strong = A(ctx.Dst[16 * w + 8]);

        var half = new ObjectFeatherEffect { Radius = 6, Strength = 50 };
        ctx = EffectContext.FromPixels(src, w, h, half.SourceMargin);
        half.Render(ctx);
        var mild = A(ctx.Dst[16 * w + 8]);

        Assert.True(mild > strong);          // 強度越低，邊緣越接近原本的不透明
        Assert.True(mild < 255);

        var none = new ObjectFeatherEffect { Radius = 6, Strength = 0 };
        ctx = EffectContext.FromPixels(src, w, h, none.SourceMargin);
        none.Render(ctx);
        Assert.Equal(src, ctx.Dst);          // 強度 0 = 原封不動
    }

    [Fact]
    public void Feather_CanvasEdgeOption()
    {
        const int w = 32, h = 32;
        var src = Canvas(w, h, (_, _) => Premul(0, 0, 255, 255));
        var keep = new ObjectFeatherEffect { Radius = 6, FeatherCanvasEdge = false };
        var ctx = EffectContext.FromPixels(src, w, h, keep.SourceMargin);
        keep.Render(ctx);
        Assert.Equal(255, A(ctx.Dst[0]));

        var fade = new ObjectFeatherEffect { Radius = 6, FeatherCanvasEdge = true };
        ctx = EffectContext.FromPixels(src, w, h, fade.SourceMargin);
        fade.Render(ctx);
        // 軟邊以畫布邊為中心，看得到的只有內側那一半（角落再少一點）
        Assert.InRange(A(ctx.Dst[0]), 60, 200);
        Assert.Equal(255, A(ctx.Dst[8 * w + 8]));   // 離邊夠遠的地方完全不動
    }

    // ---- 引導濾波：糊掉的遮罩貼回高清邊緣 ----

    [Fact]
    public void GuidedFilter_SnapsBlurryMaskToImageEdge()
    {
        const int w = 128, h = 64;
        // 左半綠、右半紅，邊在 x=64（銳利）
        var src = Canvas(w, h, (x, _) => x < 64 ? Premul(40, 160, 60, 255) : Premul(30, 30, 220, 255));
        // 模型遮罩：同一條邊但糊了 20px（線性漸層 54..74）
        var mask = new byte[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            mask[y * w + x] = (byte)Math.Clamp((x - 54) / 20f * 255f, 0, 255);

        var refined = GuidedFilter.Refine(mask, src, w, h, radius: 16, eps: 1e-3f);

        // 精修後邊界兩側應各自接近 0 / 255（原本 x=58 約 51、x=70 約 204）
        Assert.True(refined[32 * w + 58] < 30, $"left {refined[32 * w + 58]}");
        Assert.True(refined[32 * w + 70] > 225, $"right {refined[32 * w + 70]}");
        Assert.True(refined[32 * w + 10] < 10);
        Assert.True(refined[32 * w + 120] > 245);
    }

    [Fact]
    public void GuidedFilter_LeavesUniformRegionsAlone()
    {
        const int w = 64, h = 64;
        var src = Canvas(w, h, (_, _) => Premul(100, 100, 100, 255));
        var mask = new byte[w * h];
        Array.Fill(mask, (byte)255);
        var refined = GuidedFilter.Refine(mask, src, w, h);
        Assert.All(refined, v => Assert.InRange(v, 250, 255));
    }

    [Fact]
    public void SolidifyCore_FillsInteriorKeepsSoftEdge()
    {
        const int w = 64, h = 64;
        var model = new byte[w * h];
        var soft = new byte[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var inside = x is >= 8 and < 56 && y is >= 8 and < 56;
            model[y * w + x] = inside ? (byte)180 : (byte)20; // 內部只有七成、外部兩成
            soft[y * w + x] = inside ? (byte)170 : (byte)30;
        }
        var result = BackgroundRemover.SolidifyCore(soft, model, w, h, band: 6);
        Assert.Equal(255, result[32 * w + 32]); // 核心填實
        Assert.Equal(0, result[32 * w + 1]);    // 遠處外部清空
        Assert.InRange(result[32 * w + 9], 230, 254); // 邊緣一圈：0.67 被 S 曲線推向 1，但不是硬切
        Assert.InRange(result[32 * w + 6], 0, 20);    // 0.12 推向 0
        var raw = BackgroundRemover.SolidifyCore(soft, model, w, h, band: 6, edgeContrast: 0);
        Assert.Equal(170, raw[32 * w + 9]);     // 關掉 S 曲線 = 原樣保留
    }

    [Fact]
    public void FillSmallHoles_FillsEnclosedOnly()
    {
        const int w = 40, h = 40;
        var bin = new byte[w * h];
        for (var y = 5; y < 35; y++)
        for (var x = 5; x < 35; x++)
            bin[y * w + x] = 255;
        bin[20 * w + 20] = 0; bin[20 * w + 21] = 0;          // 小洞（2px）
        for (var y = 10; y < 15; y++) bin[y * w + 5] = 0;    // 邊界缺口（連到外部）
        BackgroundRemover.FillSmallHoles(bin, w, h, maxArea: 10);
        Assert.Equal(255, bin[20 * w + 20]);
        Assert.Equal(0, bin[12 * w + 5]);
        Assert.Equal(0, bin[0]);
    }

    [Fact]
    public void Shift_ShrinksAndGrows()
    {
        const int w = 32, h = 32;
        var mask = new byte[w * h];
        for (var y = 8; y < 24; y++)
        for (var x = 8; x < 24; x++)
            mask[y * w + x] = 255;
        var shrunk = BackgroundRemover.Shift(mask, w, h, -2);
        Assert.Equal(0, shrunk[16 * w + 8]);
        Assert.Equal(255, shrunk[16 * w + 12]);
        var grown = BackgroundRemover.Shift(mask, w, h, 2);
        Assert.Equal(255, grown[16 * w + 6]);
        Assert.Equal(0, grown[16 * w + 4]);
    }

    // ---- 圖層命令（真的跑模型）----

    /// <summary>MINEPAINTER_TEST_MODELS 資料夾裡第一個可用的輕量模型（u2netp 或 isnet-general-use）；沒有回 null。</summary>
    private static OnnxModelInfo? TestModel()
    {
        var dir = Environment.GetEnvironmentVariable("MINEPAINTER_TEST_MODELS");
        if (string.IsNullOrEmpty(dir)) return null;
        foreach (var name in new[] { "u2netp", "isnet-general-use" })
        {
            var path = Path.Combine(dir, name + ".onnx");
            if (File.Exists(path)) return new OnnxModelInfo(name, path);
        }
        return null;
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

    private static void FillCircle(RasterLayer layer, SKPoint c, float r, SKColor color)
    {
        var rect = new SKRectI((int)(c.X - r) - 1, (int)(c.Y - r) - 1, (int)(c.X + r) + 2, (int)(c.Y + r) + 2);
        foreach (var idx in TileIndex.CoveringRect(rect))
        {
            var tile = layer.Surface.GetTileForWrite(idx);
            using var surface = SKSurface.Create(Tile.Info, tile.Pixels, Tile.RowBytes);
            var tr = idx.ToPixelRect();
            surface.Canvas.Translate(-tr.Left, -tr.Top);
            using var paint = new SKPaint { Color = color, IsAntialias = true };
            surface.Canvas.DrawCircle(c, r, paint);
            surface.Canvas.Flush();
        }
    }

    /// <summary>
    /// 灰底＋深色圓（顯著物件）＋一個效果與一個文字物件：命令要先平面化再去背，
    /// 圓內保留、角落透明；undo 後像素、效果堆疊、文字物件全部回來。
    /// 只在 MINEPAINTER_TEST_MODELS 指到含 u2netp／isnet-general-use 的資料夾時執行。
    /// </summary>
    [Fact]
    public void Command_FlattensThenRemovesBackground_OneUndoStep()
    {
        if (TestModel() is not { } testModel) return;

        var doc = ImageCodec.CreateBlankDocument(256, 256, new SKColor(200, 200, 200));
        using var session = new EditorSession(doc);
        var layer = (RasterLayer)doc.ActiveLayer!;
        lock (doc.SyncRoot)
        {
            FillCircle(layer, new SKPoint(128, 128), 60, new SKColor(30, 20, 200));
            layer.SetEffects([LayerEffect.Create(new AdjustmentEffect(new InvertAdjustment()))]);
            layer.AddElement(new TextElement { Text = "hi", Position = new SKPoint(10, 10), FontSize = 24, Color = SKColors.Black });
        }
        Assert.True(layer.HasActiveEffects);
        Assert.True(layer.HasElements);

        var ok = BackgroundRemovalCommand.Run(session, layer, new BackgroundRemovalOptions
        {
            Model = testModel,
            UseGpu = Environment.GetEnvironmentVariable("MINEPAINTER_TEST_GPU") == "1",
        });
        Assert.True(ok);

        Assert.False(layer.HasActiveEffects);      // 已平面化
        Assert.Empty(layer.Effects);
        Assert.False(layer.HasElements);
        Assert.Equal(255, AlphaAt(layer, 128, 128)); // 內部填實：中心完全不透明
        Assert.True(AlphaAt(layer, 250, 250) < 80, $"corner {AlphaAt(layer, 250, 250)}");
        Assert.Equal("AI 去背", session.History.UndoLabel);

        session.Undo();
        Assert.Equal(255, AlphaAt(layer, 250, 250));
        Assert.True(layer.HasActiveEffects);
        Assert.True(layer.HasElements);
        Assert.False(session.History.CanUndo);

        session.Redo();
        Assert.True(AlphaAt(layer, 250, 250) < 80);
        Assert.False(layer.HasElements);
    }

    /// <summary>
    /// 只處理選取範圍：左右兩個深色圓、只圈住左邊那個，去背後左圓中心保留、
    /// 右圓（選取範圍外）整個被清掉；undo 後右圓回來。
    /// 只在 MINEPAINTER_TEST_MODELS 指到含 u2netp／isnet-general-use 的資料夾時執行。
    /// </summary>
    [Fact]
    public void Command_SelectionOnly_ClearsOutsideAndKeepsSelectedObject()
    {
        if (TestModel() is not { } testModel) return;

        var doc = ImageCodec.CreateBlankDocument(512, 256, new SKColor(200, 200, 200));
        using var session = new EditorSession(doc);
        var layer = (RasterLayer)doc.ActiveLayer!;
        lock (doc.SyncRoot)
        {
            FillCircle(layer, new SKPoint(128, 128), 60, new SKColor(30, 20, 200));
            FillCircle(layer, new SKPoint(384, 128), 60, new SKColor(30, 20, 200));
        }
        using var path = new SKPath();
        path.AddRect(SKRect.Create(20, 20, 216, 216));
        var selection = SelectionMask.FromPath(path, doc.Bounds);

        var ok = BackgroundRemovalCommand.Run(session, layer, new BackgroundRemovalOptions
        {
            Model = testModel,
            UseGpu = Environment.GetEnvironmentVariable("MINEPAINTER_TEST_GPU") == "1",
            Selection = selection,
        });
        Assert.True(ok);

        Assert.Equal(255, AlphaAt(layer, 128, 128)); // 圈住的圓保留
        Assert.Equal(0, AlphaAt(layer, 384, 128));   // 範圍外的圓整個清掉
        Assert.Equal(0, AlphaAt(layer, 500, 250));
        Assert.True(AlphaAt(layer, 30, 30) < 80, $"corner {AlphaAt(layer, 30, 30)}"); // 範圍內的背景照樣去掉

        session.Undo();
        Assert.Equal(255, AlphaAt(layer, 384, 128));
        Assert.Equal(255, AlphaAt(layer, 500, 250));
    }

    /// <summary>選取範圍內完全沒有內容：不動圖層、回傳 false。</summary>
    [Fact]
    public void Command_SelectionWithoutContent_ReturnsFalse()
    {
        var doc = ImageCodec.CreateBlankDocument(256, 256, SKColors.Transparent);
        using var session = new EditorSession(doc);
        var layer = (RasterLayer)doc.ActiveLayer!;
        lock (doc.SyncRoot) FillCircle(layer, new SKPoint(60, 60), 30, SKColors.Red);
        using var path = new SKPath();
        path.AddRect(SKRect.Create(150, 150, 100, 100));

        var ok = BackgroundRemovalCommand.Run(session, layer, new BackgroundRemovalOptions
        {
            Model = new OnnxModelInfo("none", "missing.onnx"),
            Selection = SelectionMask.FromPath(path, doc.Bounds),
        });
        Assert.False(ok);
        Assert.Equal(255, AlphaAt(layer, 60, 60));
        Assert.False(session.History.CanUndo);
    }
}
