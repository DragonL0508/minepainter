using MinePainter.Core.Adjustments;
using MinePainter.Core.Effects;
using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Selections;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;
using static MinePainter.Core.Effects.EffectMath;

namespace MinePainter.Core.Tests;

public class EffectTests
{
    private static uint[] Solid(int w, int h, uint p)
    {
        var a = new uint[w * h];
        Array.Fill(a, p);
        return a;
    }

    [Fact]
    public void PremulRoundTrip()
    {
        var p = Premul(10, 200, 100, 128);
        Unpremul(p, out var b, out var g, out var r, out var a);
        Assert.Equal(128, a);
        Assert.InRange(b, 9, 11);
        Assert.InRange(g, 199, 201);
        Assert.InRange(r, 99, 101);
    }

    [Fact]
    public void GaussianBlur_PreservesSolidColor()
    {
        var src = Solid(64, 64, Pack(30, 60, 90, 255));
        var dst = GaussianBlur(src, 64, 64, 10);
        Assert.All(dst, p => Assert.Equal(Pack(30, 60, 90, 255), p));
    }

    [Fact]
    public void GaussianBlur_SpreadsEdge()
    {
        var src = new uint[64 * 64];
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
            src[y * 64 + x] = x < 32 ? Pack(0, 0, 0, 255) : Pack(255, 255, 255, 255);
        var dst = GaussianBlur(src, 64, 64, 6);
        var mid = B(dst[32 * 64 + 31]);
        Assert.InRange(mid, 60, 195); // 邊界被抹成中間值
        Assert.Equal(0, B(dst[32 * 64 + 2]));
        Assert.Equal(255, B(dst[32 * 64 + 61]));
    }

    [Fact]
    public void AllEffects_RenderDefaultWithoutThrowing()
    {
        var src = new uint[48 * 40];
        var rng = new XorShift(7);
        for (var i = 0; i < src.Length; i++)
        {
            var v = (int)(rng.Next() & 0xFF);
            src[i] = Premul(v, 255 - v, (v * 3) & 0xFF, i % 5 == 0 ? 0 : 255);
        }

        foreach (var entry in EffectRegistry.All)
        {
            var effect = entry.Create();
            var margin = effect.SourceMargin == EffectContext.WholeLayer ? 0 : effect.SourceMargin;
            var ctx = EffectContext.FromPixels(src, 48, 40, margin);
            effect.Render(ctx);
            Assert.Equal(48 * 40, ctx.Dst.Length);
            Assert.NotEmpty(effect.Parameters);
            Assert.Equal(entry.Name, effect.Name);
        }
    }

    [Fact]
    public void Effect_ParamsRoundTripThroughWith()
    {
        foreach (var entry in EffectRegistry.All)
        {
            object effect = entry.Create();
            foreach (var def in effect.GetType() is var _ ? ((IEffect)effect).Parameters : [])
            {
                switch (def)
                {
                    case SliderParam s:
                    {
                        var target = (s.Min + s.Max) / 2;
                        effect = s.With(effect, target);
                        Assert.InRange(s.Get(effect), s.Min, s.Max);
                        break;
                    }
                    case BoolParam b:
                        effect = b.With(effect, !b.Get(effect));
                        break;
                    case ChoiceParam c:
                        effect = c.With(effect, (c.Get(effect) + 1) % c.Options.Length);
                        Assert.InRange(c.Get(effect), 0, c.Options.Length - 1);
                        break;
                    case AngleParam an:
                        effect = an.With(effect, 33);
                        Assert.Equal(33, an.Get(effect), 3);
                        break;
                    case PointParam pt:
                        effect = pt.With(effect, (0.25f, -0.5f));
                        Assert.Equal(0.25f, pt.Get(effect).X, 3);
                        Assert.Equal(-0.5f, pt.Get(effect).Y, 3);
                        break;
                }
            }
            Assert.IsAssignableFrom<IEffect>(effect);
        }
    }

    [Fact]
    public void Pixelate_AveragesCells()
    {
        var src = new uint[8 * 8];
        for (var i = 0; i < src.Length; i++) src[i] = (i % 2 == 0) ? Pack(0, 0, 0, 255) : Pack(200, 200, 200, 255);
        var ctx = EffectContext.FromPixels(src, 8, 8);
        new PixelateEffect { CellSize = 4 }.Render(ctx);
        Assert.All(ctx.Dst, p => Assert.Equal(100, B(p)));
    }

    [Fact]
    public void Invert_And_BlackWhite_Adjustments()
    {
        var src = Solid(4, 4, Pack(10, 20, 30, 255)); // b g r
        var ctx = EffectContext.FromPixels(src, 4, 4);
        new AdjustmentEffect(new InvertAdjustment()).Render(ctx);
        Assert.Equal(Pack(245, 235, 225, 255), ctx.Dst[0]);

        ctx = EffectContext.FromPixels(src, 4, 4);
        new AdjustmentEffect(new BlackWhiteAdjustment()).Render(ctx);
        Assert.Equal(B(ctx.Dst[0]), G(ctx.Dst[0]));
        Assert.Equal(G(ctx.Dst[0]), R(ctx.Dst[0]));
    }

    [Fact]
    public void Levels_TableStretches()
    {
        var table = new LevelsAdjustment(InputLow: 50, InputHigh: 200).BuildTable();
        Assert.Equal(0, table[50]);
        Assert.Equal(255, table[200]);
        Assert.Equal(0, table[10]);
        Assert.InRange(table[125], 125, 130);
    }

    [Fact]
    public void AutoLevel_FromHistogram_StretchesRange()
    {
        var hist = new long[256];
        for (var i = 60; i <= 180; i++) hist[i] = 100;
        var levels = LevelsAdjustment.FromHistogram(hist);
        Assert.InRange(levels.InputLow, 58, 62);
        Assert.InRange(levels.InputHigh, 178, 182);
    }

    [Fact]
    public void Curves_IdentityIsIdentity_AndMonotone()
    {
        var table = CurvesAdjustment.BuildTable(CurvesAdjustment.Identity);
        for (var i = 0; i < 256; i++) Assert.InRange(table[i], i - 1, i + 1);

        var bent = CurvesAdjustment.BuildTable([(0f, 0f), (0.5f, 0.8f), (1f, 1f)]);
        Assert.True(bent[128] > 190);
        for (var i = 1; i < 256; i++) Assert.True(bent[i] >= bent[i - 1], $"非單調於 {i}");
    }

    [Fact]
    public void Posterize_Table()
    {
        var t = PosterizeAdjustment.BuildTable(2);
        Assert.Equal(0, t[100]);
        Assert.Equal(255, t[200]);
    }

    [Fact]
    public void AllAdjustments_SaveLoadRoundTrip()
    {
        foreach (var entry in AdjustmentRegistry.All)
        {
            var adj = entry.CreateDefault();
            var loaded = AdjustmentRegistry.Load(adj.TypeId, adj.SaveParams());
            Assert.Equal(adj.TypeId, loaded.TypeId);
            using var f = loaded.CreateColorFilter();
            Assert.NotNull(f);
        }

        var curves = new CurvesAdjustment { Mode = CurvesAdjustment.ModeRgb, Curves = [[(0f, 0f), (0.3f, 0.6f), (1f, 1f)], CurvesAdjustment.Identity, CurvesAdjustment.Identity] };
        var back = (CurvesAdjustment)AdjustmentRegistry.Load("curves", curves.SaveParams());
        Assert.Equal(CurvesAdjustment.ModeRgb, back.Mode);
        Assert.Equal(3, back.Curves.Count);
        Assert.Equal(0.6f, back.Curves[0][1].Y, 3);
    }

    [Fact]
    public void MppFormat_RoundTripsNewAdjustmentTypes()
    {
        using var doc = ImageCodec.CreateBlankDocument(32, 32, SKColors.Gray);
        var levels = new AdjustmentLayer(new LevelsAdjustment(20, 220, 1.4f, 5, 250));
        var posterize = new AdjustmentLayer(new PosterizeAdjustment(4, 4, 4));
        lock (doc.SyncRoot)
        {
            doc.Root.Add(levels);
            doc.Root.Add(posterize);
        }
        var path = Path.Combine(Path.GetTempPath(), $"mp-{Guid.NewGuid():N}.mpp");
        try
        {
            MppFormat.Save(doc, path);
            using var loaded = MppFormat.Load(path);
            var l = Assert.IsType<LevelsAdjustment>(Assert.IsType<AdjustmentLayer>(loaded.Root.Children[1]).Adjustment);
            Assert.Equal(20, l.InputLow);
            Assert.Equal(1.4f, l.Gamma, 3);
            var p = Assert.IsType<PosterizeAdjustment>(Assert.IsType<AdjustmentLayer>(loaded.Root.Children[2]).Adjustment);
            Assert.Equal(4, p.Red);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- EffectSession：預覽／確定／取消 ----

    private static (EditorSession Session, RasterLayer Layer) NewSession(int w = 64, int h = 64)
    {
        var doc = ImageCodec.CreateBlankDocument(w, h, new SKColor(100, 100, 100));
        var session = new EditorSession(doc);
        var layer = (RasterLayer)doc.ActiveLayer!;
        return (session, layer);
    }

    private static unsafe SKColor PixelAt(RasterLayer layer, int x, int y)
    {
        var tile = layer.Surface.GetTileForRead(Tiles.TileIndex.FromPixel(x, y));
        if (tile == null) return SKColors.Transparent;
        var p = ((uint*)tile.Pixels)[(y % Tiles.Tile.Size) * Tiles.Tile.Size + (x % Tiles.Tile.Size)];
        return new SKColor((byte)R(p), (byte)G(p), (byte)B(p), (byte)A(p));
    }

    [Fact]
    public void EffectSession_CommitPushesUndoableEntry()
    {
        var (session, layer) = NewSession();
        using (var fx = new EffectSession(session, layer))
        {
            fx.RenderAndApply(new AdjustmentEffect(new InvertAdjustment()));
            Assert.True(fx.Commit("負片效果"));
        }
        Assert.Equal(155, PixelAt(layer, 10, 10).Red);
        Assert.True(session.Undo());
        Assert.Equal(100, PixelAt(layer, 10, 10).Red);
        Assert.True(session.Redo());
        Assert.Equal(155, PixelAt(layer, 10, 10).Red);
        session.Dispose();
    }

    [Fact]
    public void EffectSession_CancelRestoresPixels()
    {
        var (session, layer) = NewSession();
        using (var fx = new EffectSession(session, layer))
        {
            fx.RenderAndApply(new AdjustmentEffect(new InvertAdjustment()));
            Assert.Equal(155, PixelAt(layer, 10, 10).Red);
            fx.Cancel();
        }
        Assert.Equal(100, PixelAt(layer, 10, 10).Red);
        Assert.False(session.History.CanUndo);
        session.Dispose();
    }

    [Fact]
    public void EffectSession_RepeatedPreviewsDoNotAccumulate()
    {
        var (session, layer) = NewSession();
        using var fx = new EffectSession(session, layer);
        var bc = new AdjustmentEffect(new BrightnessContrastAdjustment(Brightness: 0.2f));
        fx.RenderAndApply(bc);
        var first = PixelAt(layer, 5, 5).Red;
        fx.RenderAndApply(bc);
        Assert.Equal(first, PixelAt(layer, 5, 5).Red); // 來源永遠是快照
        fx.Cancel();
        session.Dispose();
    }

    [Fact]
    public void EffectSession_RespectsSelection()
    {
        var (session, layer) = NewSession();
        using var path = new SKPath();
        path.AddRect(SKRect.Create(0, 0, 32, 64));
        session.Selection = SelectionMask.FromPath(path, session.Document.Bounds);

        using var fx = new EffectSession(session, layer);
        Assert.Equal(32, fx.Region.Width);
        fx.RenderAndApply(new AdjustmentEffect(new InvertAdjustment()));
        fx.Commit("負片效果");
        Assert.Equal(155, PixelAt(layer, 10, 10).Red);
        Assert.Equal(100, PixelAt(layer, 50, 10).Red);
        session.Dispose();
    }

    [Fact]
    public void EffectSession_HistogramCountsChannels()
    {
        var (session, layer) = NewSession(16, 16);
        using var fx = new EffectSession(session, layer);
        var hist = fx.Histogram();
        Assert.Equal(16 * 16 * 3, hist[100]);
        session.Dispose();
    }
}

public class BrushRenderingTests
{
    private static Tiles.MaskTile StrokeLine(float y, float radius, float hardness, int fromX, int toX, float xJitter = 0.37f)
    {
        var buffer = new StrokeBuffer();
        buffer.Begin(Guid.NewGuid(), SKColors.Black, 1f, false);
        var engine = new BrushEngine();
        var settings = new BrushSettings { Radius = radius, Hardness = hardness };
        engine.BeginStroke(new SKPoint(fromX, y), buffer, settings);
        for (var x = fromX + 1; x <= toX; x++)
            engine.ContinueStroke(new SKPoint(x + xJitter, y), buffer, settings);
        engine.EndStroke(new SKPoint(toX + xJitter, y), buffer, settings);
        return buffer.Mask.GetForRead(new Tiles.TileIndex(0, 0))!;
    }

    [Fact]
    public void Coverage_HardEdgeIsOnePixelRamp()
    {
        Assert.Equal(1f, BrushEngine.Coverage(0f, 6f, 1f));
        Assert.Equal(1f, BrushEngine.Coverage(5.4f, 6f, 1f));
        Assert.Equal(0.5f, BrushEngine.Coverage(6f, 6f, 1f), 3);
        Assert.Equal(0f, BrushEngine.Coverage(6.6f, 6f, 1f));
    }

    [Fact]
    public void Coverage_SoftBrushFallsOff()
    {
        var mid = BrushEngine.Coverage(4f, 6f, 0.5f); // 硬區 3px，4px 處在衰減中段
        Assert.InRange(mid, 0.2f, 0.8f);
        Assert.True(BrushEngine.Coverage(2f, 6f, 0.5f) > mid);
        Assert.True(BrushEngine.Coverage(5.5f, 6f, 0.5f) < mid);
    }

    [Fact]
    public void Stroke_StraightLineHasStableEdge()
    {
        // 沿 y=20.3 畫一條水平線；上緣每一欄的覆蓋度必須完全相同（膠囊解析覆蓋，沒有抖動）
        var tile = StrokeLine(20.3f, 5f, 1f, 10, 90);
        var topRow = 15;
        var reference = tile.Alpha[topRow * Tiles.MaskTile.Size + 40];
        Assert.InRange(reference, 1, 254);
        for (var x = 20; x <= 80; x++)
            Assert.Equal(reference, tile.Alpha[topRow * Tiles.MaskTile.Size + x]);
        for (var x = 20; x <= 80; x++)
            Assert.Equal(255, tile.Alpha[20 * Tiles.MaskTile.Size + x]);
    }

    [Fact]
    public void Stroke_SubpixelPositionShiftsEdge()
    {
        var a = StrokeLine(20.3f, 5f, 1f, 10, 60);
        var b = StrokeLine(20.8f, 5f, 1f, 10, 60);
        // 線往下移 0.5px → 上緣列覆蓋度減少、下緣列增加
        Assert.True(b.Alpha[15 * Tiles.MaskTile.Size + 30] < a.Alpha[15 * Tiles.MaskTile.Size + 30]);
        Assert.True(b.Alpha[25 * Tiles.MaskTile.Size + 30] > a.Alpha[25 * Tiles.MaskTile.Size + 30]);
    }

    [Fact]
    public void Stroke_RoundCapsAtEnds()
    {
        var tile = StrokeLine(30f, 6f, 1f, 20, 40, 0f);
        Assert.Equal(255, tile.Alpha[30 * Tiles.MaskTile.Size + 20]); // 起點中心
        Assert.Equal(0, tile.Alpha[30 * Tiles.MaskTile.Size + 12]);   // 起點外 8px
        Assert.Equal(0, tile.Alpha[23 * Tiles.MaskTile.Size + 14]);   // 圓頭的角落是空的
    }

    /// <summary>
    /// 模擬縮小到 25% 慢慢畫斜線：滑鼠每動 1 螢幕像素就是 4 個文件像素，
    /// 原始輸入是 4px 一階的樓梯（每 xPerY 步 x 才一步 y）。
    /// 回傳筆劃上緣對最佳擬合直線的最大偏差（doc px）。
    /// </summary>
    private static float StaircaseEdgeWobble(float smoothingWindow, int xPerY)
    {
        var buffer = new StrokeBuffer();
        buffer.Begin(Guid.NewGuid(), SKColors.Black, 1f, false);
        var engine = new BrushEngine { SmoothingWindow = smoothingWindow };
        var settings = new BrushSettings { Radius = 3f, Hardness = 1f };
        float x = 8, y = 8;
        engine.BeginStroke(new SKPoint(x, y), buffer, settings);
        for (var i = 0; i < 60; i++)
        {
            for (var j = 0; j < xPerY; j++) { x += 4; engine.ContinueStroke(new SKPoint(x, y), buffer, settings); }
            y += 4;
            engine.ContinueStroke(new SKPoint(x, y), buffer, settings);
        }
        engine.EndStroke(new SKPoint(x, y), buffer, settings);
        var tile = buffer.Mask.GetForRead(new Tiles.TileIndex(0, 0))!;

        // 每欄上緣 = 由上往下第一次跨過 128 的位置（線性內插）
        var xs = new List<float>();
        var ys = new List<float>();
        for (var col = 40; col <= 200; col++)
        {
            for (var row = 1; row < Tiles.MaskTile.Size; row++)
            {
                int a0 = tile.Alpha[(row - 1) * Tiles.MaskTile.Size + col];
                int a1 = tile.Alpha[row * Tiles.MaskTile.Size + col];
                if (a1 >= 128 && a0 < 128)
                {
                    xs.Add(col);
                    ys.Add(row - 1 + (128f - a0) / (a1 - a0));
                    break;
                }
            }
        }
        var mx = xs.Average();
        var my = ys.Average();
        float sxy = 0, sxx = 0;
        for (var i = 0; i < xs.Count; i++)
        {
            sxy += (xs[i] - mx) * (ys[i] - my);
            sxx += (xs[i] - mx) * (xs[i] - mx);
        }
        var slope = sxy / sxx;
        var intercept = my - slope * mx;
        var worst = 0f;
        for (var i = 0; i < xs.Count; i++)
            worst = Math.Max(worst, Math.Abs(ys[i] - (intercept + slope * xs[i])));
        return worst;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Stroke_SmoothingRemovesZoomedOutStaircase(int xPerY)
    {
        var raw = StaircaseEdgeWobble(0f, xPerY);
        var smoothed = StaircaseEdgeWobble(12f, xPerY); // 三個螢幕像素 = 12 doc px
        Assert.True(raw > 0.8f, $"raw={raw}");
        Assert.True(smoothed < 0.25f, $"smoothed={smoothed}");
    }

    /// <summary>
    /// 模擬 25% 縮放慢畫水平線並帶手抖：每採樣前進 1 螢幕像素，垂直方向正弦晃動
    /// （振幅 1.5 螢幕像素、週期 14 個採樣），再取整數螢幕像素。回傳上緣的最高最低差（doc px）。
    /// </summary>
    private static float TremorEdgeRange(float stabilize)
    {
        var buffer = new StrokeBuffer();
        buffer.Begin(Guid.NewGuid(), SKColors.Black, 1f, false);
        var engine = new BrushEngine { SmoothingWindow = 12f, Stabilize = stabilize };
        var settings = new BrushSettings { Radius = 3f, Hardness = 1f };
        SKPoint At(int i) => new(
            (4 + i) * 4,
            MathF.Round(30 + 1.5f * MathF.Sin(2 * MathF.PI * i / 14)) * 4);
        engine.BeginStroke(At(0), buffer, settings);
        for (var i = 1; i < 200; i++) engine.ContinueStroke(At(i), buffer, settings);
        engine.EndStroke(At(199), buffer, settings);
        var tile = buffer.Mask.GetForRead(new Tiles.TileIndex(0, 0))!;

        float lo = float.MaxValue, hi = float.MinValue;
        for (var col = 60; col <= 200; col++)
        {
            for (var row = 1; row < Tiles.MaskTile.Size; row++)
            {
                int a0 = tile.Alpha[(row - 1) * Tiles.MaskTile.Size + col];
                int a1 = tile.Alpha[row * Tiles.MaskTile.Size + col];
                if (a1 >= 128 && a0 < 128)
                {
                    var edge = row - 1 + (128f - a0) / (a1 - a0);
                    lo = Math.Min(lo, edge);
                    hi = Math.Max(hi, edge);
                    break;
                }
            }
        }
        return hi - lo;
    }

    [Fact]
    public void Stroke_StabilizerSuppressesHandTremor()
    {
        var raw = TremorEdgeRange(0f);
        var stabilized = TremorEdgeRange(32f); // 50% 強度 × 16 螢幕像素 × 4 doc px
        Assert.True(raw > 6f, $"raw={raw}");
        Assert.True(stabilized < 1f, $"stabilized={stabilized}");
    }

    [Fact]
    public void Stroke_SmoothingKeepsUpWithFastStrokes()
    {
        // 快速揮筆：每個採樣相距 40px，遠大於平滑窗，控制點應直接等於原始點（不滯後）
        var buffer = new StrokeBuffer();
        buffer.Begin(Guid.NewGuid(), SKColors.Black, 1f, false);
        var engine = new BrushEngine { SmoothingWindow = 12f };
        var settings = new BrushSettings { Radius = 3f, Hardness = 1f };
        engine.BeginStroke(new SKPoint(8, 8), buffer, settings);
        for (var i = 1; i <= 5; i++)
            engine.ContinueStroke(new SKPoint(8 + 40 * i, 8), buffer, settings);
        // 已進來 6 個點，曲線落後一段：最後定形的段落結束在倒數第二點 (168, 8)
        var bounds = buffer.DirtyBounds;
        Assert.InRange(bounds.Right, 168 + 3, 168 + 5);
        engine.EndStroke(new SKPoint(208, 8), buffer, settings);
        Assert.InRange(buffer.DirtyBounds.Right, 208 + 3, 208 + 5);
    }

    [Fact]
    public void ShapeTool_SnapsOddStrokeToPixelCenters()
    {
        var odd = ShapeTool.SnapRect(new SKRect(10.2f, 10.7f, 20.4f, 30.9f), 3f);
        Assert.Equal(10.5f, odd.Left);
        Assert.Equal(10.5f, odd.Top);
        Assert.Equal(20.5f, odd.Right);
        Assert.Equal(30.5f, odd.Bottom);

        var even = ShapeTool.SnapRect(new SKRect(10.2f, 10.7f, 20.4f, 30.9f), 4f);
        Assert.Equal(10f, even.Left);
        Assert.Equal(11f, even.Top);

        var fill = ShapeTool.SnapRect(new SKRect(10.2f, 10.7f, 20.4f, 30.9f), 0f);
        Assert.Equal(10f, fill.Left);
        Assert.Equal(31f, fill.Bottom);
    }
}
