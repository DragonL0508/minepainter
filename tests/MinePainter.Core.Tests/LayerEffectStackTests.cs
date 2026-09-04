using MinePainter.Core.Adjustments;
using MinePainter.Core.Effects;
using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Selections;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

public class LayerEffectStackTests
{
    private static unsafe SKColor DisplayPixel(RasterLayer layer, int x, int y)
    {
        var tile = layer.DisplaySurface.GetTileForRead(TileIndex.FromPixel(x, y));
        if (tile == null) return SKColors.Transparent;
        var p = ((uint*)tile.Pixels)[(y & 255) * Tile.Size + (x & 255)];
        return new SKColor((byte)((p >> 16) & 0xFF), (byte)((p >> 8) & 0xFF), (byte)(p & 0xFF), (byte)(p >> 24));
    }

    private static unsafe SKColor BasePixel(RasterLayer layer, int x, int y)
    {
        var tile = layer.Surface.GetTileForRead(TileIndex.FromPixel(x, y));
        if (tile == null) return SKColors.Transparent;
        var p = ((uint*)tile.Pixels)[(y & 255) * Tile.Size + (x & 255)];
        return new SKColor((byte)((p >> 16) & 0xFF), (byte)((p >> 8) & 0xFF), (byte)(p & 0xFF), (byte)(p >> 24));
    }

    private static (EditorSession Session, RasterLayer Layer) NewSession()
    {
        var doc = ImageCodec.CreateBlankDocument(64, 64, new SKColor(100, 100, 100));
        var session = new EditorSession(doc);
        return (session, (RasterLayer)doc.ActiveLayer!);
    }

    /// <summary>
    /// 看得到的圖層先算（GIMP 的 priority rect）：畫面外的圖層照樣會算，只是排在後面。
    /// </summary>
    [Fact]
    public void RenderPending_ComputesLayersInsideThePriorityRectFirst()
    {
        using var doc = ImageCodec.CreateBlankDocument(1024, 512, SKColors.Transparent);

        var far = new RasterLayer { Name = "遠" };
        far.Surface.Fill(new SKRectI(800, 100, 1000, 300), SKColors.Red);
        var near = new RasterLayer { Name = "近" };
        near.Surface.Fill(new SKRectI(0, 100, 200, 300), SKColors.Blue);
        lock (doc.SyncRoot)
        {
            doc.Root.Add(far);  // 先加的在後序裡排前面：沒有優先範圍時它會先算
            doc.Root.Add(near);
            far.SetEffects([LayerEffect.Create(new GaussianBlurEffect())]);
            near.SetEffects([LayerEffect.Create(new GaussianBlurEffect())]);
        }

        var order = new List<string>();
        void OnRendered(LayerNode layer)
        {
            if (ReferenceEquals(layer, far) || ReferenceEquals(layer, near))
                lock (order) order.Add(layer.Name);
        }

        LayerEffectRenderer.LayerRendered += OnRendered;
        try
        {
            LayerEffectRenderer.RenderPending(doc, priority: new SKRectI(0, 0, 256, 512));
        }
        finally
        {
            LayerEffectRenderer.LayerRendered -= OnRendered;
        }

        Assert.Equal(["近", "遠"], order); // 兩層都算完，看得到的那層先
    }

    [Fact]
    public void AddEffect_RendersIntoCache_BaseUntouched()
    {
        var (session, layer) = NewSession();
        LayerEffectCommands.Add(session.Document, session.History, layer,
            LayerEffect.Create(new AdjustmentEffect(new InvertAdjustment())));
        LayerEffectRenderer.RenderLayerNow(session.Document, layer);

        Assert.Equal(155, DisplayPixel(layer, 10, 10).Red);
        Assert.Equal(100, BasePixel(layer, 10, 10).Red);

        Assert.True(session.Undo());
        Assert.Empty(layer.Effects);
        Assert.Equal(100, DisplayPixel(layer, 10, 10).Red);
        session.Dispose();
    }

    [Fact]
    public void Stack_AppliesInOrder_AndDisabledIsSkipped()
    {
        var (session, layer) = NewSession();
        var invert = LayerEffect.Create(new AdjustmentEffect(new InvertAdjustment()));
        var bright = LayerEffect.Create(new AdjustmentEffect(new BrightnessContrastAdjustment(Brightness: 0.2f)));
        lock (session.Document.SyncRoot) layer.SetEffects([invert, bright]);
        LayerEffectRenderer.RenderLayerNow(session.Document, layer);
        Assert.Equal(206, DisplayPixel(layer, 5, 5).Red); // 155 + 51

        LayerEffectCommands.SetEnabled(session.Document, session.History, layer, bright.Id, false);
        LayerEffectRenderer.RenderLayerNow(session.Document, layer);
        Assert.Equal(155, DisplayPixel(layer, 5, 5).Red);

        LayerEffectCommands.Move(session.Document, session.History, layer, invert.Id, +1);
        Assert.Equal(invert.Id, layer.Effects[1].Id);
        session.Dispose();
    }

    [Fact]
    public void Mask_LimitsEffectToSelection()
    {
        var (session, layer) = NewSession();
        using var path = new SKPath();
        path.AddRect(SKRect.Create(0, 0, 32, 64));
        var mask = SelectionMask.FromPath(path, session.Document.Bounds).Mask;
        lock (session.Document.SyncRoot)
            layer.SetEffects([LayerEffect.Create(new AdjustmentEffect(new InvertAdjustment()), mask)]);
        LayerEffectRenderer.RenderLayerNow(session.Document, layer);
        Assert.Equal(155, DisplayPixel(layer, 10, 10).Red);
        Assert.Equal(100, DisplayPixel(layer, 50, 10).Red);
        session.Dispose();
    }

    [Fact]
    public void BasePixelChange_MarksCacheDirty_PartialRecompute()
    {
        var (session, layer) = NewSession();
        lock (session.Document.SyncRoot)
            layer.SetEffects([LayerEffect.Create(new AdjustmentEffect(new InvertAdjustment()))]);
        LayerEffectRenderer.RenderLayerNow(session.Document, layer);
        Assert.False(layer.FxCache.HasPending);

        lock (session.Document.SyncRoot) layer.Surface.Fill(new SKRectI(0, 0, 8, 8), SKColors.White);
        layer.Invalidate(new SKRectI(0, 0, 8, 8));
        Assert.True(layer.FxCache.HasPending);
        LayerEffectRenderer.RenderLayerNow(session.Document, layer);
        Assert.Equal(0, DisplayPixel(layer, 2, 2).Red);    // 白 → 反相 = 黑
        Assert.Equal(155, DisplayPixel(layer, 40, 40).Red); // 其他不變
        session.Dispose();
    }

    [Fact]
    public void Bake_WritesPixelsAndClearsStack_Undoable()
    {
        var (session, layer) = NewSession();
        LayerEffectCommands.Add(session.Document, session.History, layer,
            LayerEffect.Create(new AdjustmentEffect(new InvertAdjustment())));
        Assert.True(LayerEffectCommands.Bake(session, layer));
        Assert.Empty(layer.Effects);
        Assert.Equal(155, BasePixel(layer, 10, 10).Red);

        Assert.True(session.Undo());
        Assert.Single(layer.Effects);
        Assert.Equal(100, BasePixel(layer, 10, 10).Red);
        session.Dispose();
    }

    [Fact]
    public void Preview_CommitAndCancel()
    {
        var (session, layer) = NewSession();
        var entry = LayerEffect.Create(new GaussianBlurEffect { Radius = 3 });
        var preview = new LayerEffectPreview(session, layer, entry, isNew: true);
        Assert.Single(layer.Effects);
        preview.Preview(new GaussianBlurEffect { Radius = 9 }, CancellationToken.None);
        Assert.Equal(9, ((GaussianBlurEffect)layer.Effects[0].Effect).Radius);
        preview.Cancel();
        Assert.Empty(layer.Effects);
        preview.Dispose();

        var preview2 = new LayerEffectPreview(session, layer, entry, isNew: true);
        preview2.Commit(new GaussianBlurEffect { Radius = 5 });
        Assert.Single(layer.Effects);
        Assert.True(session.History.CanUndo);
        preview2.Dispose();
        session.Dispose();
    }

    [Fact]
    public void Serializer_RoundTripsEveryEffect()
    {
        foreach (var entry in EffectRegistry.All)
        {
            object effect = entry.Create();
            foreach (var def in ((IEffect)effect).Parameters)
            {
                effect = def switch
                {
                    SliderParam s => s.With(effect, s.Min + (s.Max - s.Min) * 0.3),
                    AngleParam a => a.With(effect, 77),
                    BoolParam b => b.With(effect, !b.Get(effect)),
                    ChoiceParam c => c.With(effect, (c.Get(effect) + 1) % c.Options.Length),
                    PointParam p => p.With(effect, (0.4f, -0.2f)),
                    _ => effect,
                };
            }
            var typeId = EffectSerializer.TypeIdOf((IEffect)effect);
            Assert.Equal(entry.Id, typeId);
            var loaded = EffectSerializer.Load(typeId, EffectSerializer.Save((IEffect)effect));
            Assert.Equal(EffectSerializer.Save((IEffect)effect), EffectSerializer.Save(loaded));
        }

        var adj = new AdjustmentEffect(new LevelsAdjustment(10, 200, 1.3f));
        var back = (AdjustmentEffect)EffectSerializer.Load(EffectSerializer.TypeIdOf(adj), EffectSerializer.Save(adj));
        Assert.Equal(1.3f, ((LevelsAdjustment)back.Adjustment).Gamma, 3);
    }

    [Fact]
    public void Mpp_RoundTripsEffectStackWithMask()
    {
        using var doc = ImageCodec.CreateBlankDocument(48, 48, SKColors.Gray);
        var layer = (RasterLayer)doc.ActiveLayer!;
        using var path = new SKPath();
        path.AddRect(SKRect.Create(0, 0, 24, 48));
        var mask = SelectionMask.FromPath(path, doc.Bounds).Mask;
        lock (doc.SyncRoot)
        {
            layer.SetEffects([
                LayerEffect.Create(new GaussianBlurEffect { Radius = 7 }, mask, SKColors.Red),
                LayerEffect.Create(new AdjustmentEffect(new InvertAdjustment())) with { Enabled = false },
            ]);
        }
        var file = Path.Combine(Path.GetTempPath(), $"fx-{Guid.NewGuid():N}.mpp");
        try
        {
            MppFormat.Save(doc, file);
            using var loaded = MppFormat.Load(file);
            var l = Assert.IsType<RasterLayer>(loaded.Root.Children[0]);
            Assert.Equal(2, l.Effects.Count);
            Assert.Equal(7, ((GaussianBlurEffect)l.Effects[0].Effect).Radius);
            Assert.Equal(SKColors.Red, l.Effects[0].Color);
            Assert.NotNull(l.Effects[0].Mask);
            Assert.Equal(255, l.Effects[0].Mask!.GetForRead(new TileIndex(0, 0))!.Alpha[5 * MaskTile.Size + 5]);
            Assert.Equal(0, l.Effects[0].Mask!.GetForRead(new TileIndex(0, 0))!.Alpha[5 * MaskTile.Size + 40]);
            Assert.False(l.Effects[1].Enabled);
            Assert.IsType<InvertAdjustment>(((AdjustmentEffect)l.Effects[1].Effect).Adjustment);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Composite_UsesEffectStack()
    {
        var (session, layer) = NewSession();
        lock (session.Document.SyncRoot)
            layer.SetEffects([LayerEffect.Create(new AdjustmentEffect(new InvertAdjustment()))]);
        using var image = Compositing.Compositor.RenderComposite(session.Document);
        using var bmp = SKBitmap.FromImage(image);
        Assert.Equal(155, bmp.GetPixel(10, 10).Red);
        session.Dispose();
    }
}
