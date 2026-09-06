using MinePainter.Core.Adjustments;
using MinePainter.Core.Compositing;
using MinePainter.Core.Documents;
using MinePainter.Core.Effects;
using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 群組的效果堆疊：作用對象是「整組合成起來的樣子」，組內每一層都吃得到，
/// 而且只套一次（不是每層各套一份）。
/// </summary>
public class GroupEffectTests
{
    private static unsafe SKColor CachePixel(LayerNode node, int x, int y)
    {
        var tile = node.FxCache.Surface.GetTileForRead(TileIndex.FromPixel(x, y));
        if (tile == null) return SKColors.Transparent;
        var p = ((uint*)tile.Pixels)[(y & 255) * Tile.Size + (x & 255)];
        return new SKColor((byte)((p >> 16) & 0xFF), (byte)((p >> 8) & 0xFF), (byte)(p & 0xFF), (byte)(p >> 24));
    }

    private static unsafe SKColor CompositePixel(Document doc, int x, int y)
    {
        using var image = Compositor.RenderComposite(doc);
        using var bmp = SKBitmap.FromImage(image);
        return bmp.GetPixel(x, y);
    }

    /// <summary>群組裡兩層：下面整片灰、上面一塊紅。</summary>
    private static (EditorSession Session, GroupLayer Group, RasterLayer Bottom, RasterLayer Top) NewGroupDoc()
    {
        var doc = new Document(128, 128);
        var session = new EditorSession(doc);
        var group = new GroupLayer { Name = "群組" };
        var bottom = new RasterLayer { Name = "底" };
        var top = new RasterLayer { Name = "上" };
        lock (doc.SyncRoot)
        {
            doc.Root.Add(group);
            group.Add(bottom);
            group.Add(top);
            bottom.Surface.Fill(new SKRectI(0, 0, 128, 128), new SKColor(100, 100, 100));
            top.Surface.Fill(new SKRectI(0, 0, 64, 64), new SKColor(200, 0, 0));
            doc.ActiveLayer = top;
        }
        group.Cache.MarkAllDirty();
        bottom.InvalidateAll();
        top.InvalidateAll();
        return (session, group, bottom, top);
    }

    [Fact]
    public void GroupEffect_AppliesToWholeGroup_LayersUntouched()
    {
        var (session, group, bottom, top) = NewGroupDoc();
        LayerEffectCommands.Add(session.Document, session.History, group,
            LayerEffect.Create(new AdjustmentEffect(new InvertAdjustment())));
        Assert.True(group.HasActiveEffects);

        LayerEffectRenderer.RenderLayerNow(session.Document, group);
        Assert.True(group.EffectsRendered);

        // 反相套在「整組合成後」：上層紅 (200,0,0) → (55,255,255)，下層灰 100 → 155
        Assert.Equal(new SKColor(55, 255, 255), CachePixel(group, 10, 10));
        Assert.Equal(155, CachePixel(group, 100, 100).Red);

        // 子層本身完全沒被動到（非破壞性）
        Assert.Empty(top.Effects);
        Assert.Empty(bottom.Effects);
        Assert.False(top.EffectsRendered);

        session.Dispose();
    }

    [Fact]
    public void GroupEffect_ShowsInComposite_AndUndoRestores()
    {
        var (session, group, _, _) = NewGroupDoc();
        Assert.Equal(new SKColor(200, 0, 0), CompositePixel(session.Document, 10, 10));

        LayerEffectCommands.Add(session.Document, session.History, group,
            LayerEffect.Create(new AdjustmentEffect(new InvertAdjustment())));
        Assert.Equal(new SKColor(55, 255, 255), CompositePixel(session.Document, 10, 10));

        Assert.True(session.Undo());
        Assert.Empty(group.Effects);
        Assert.Equal(new SKColor(200, 0, 0), CompositePixel(session.Document, 10, 10));
        session.Dispose();
    }

    [Fact]
    public void EditingAChild_InvalidatesGroupEffect()
    {
        var (session, group, _, top) = NewGroupDoc();
        LayerEffectCommands.Add(session.Document, session.History, group,
            LayerEffect.Create(new AdjustmentEffect(new InvertAdjustment())));
        LayerEffectRenderer.RenderLayerNow(session.Document, group);
        Assert.False(group.FxCache.HasPending);

        lock (session.Document.SyncRoot) top.Surface.Fill(new SKRectI(0, 0, 64, 64), new SKColor(0, 0, 255));
        top.Invalidate(new SKRectI(0, 0, 64, 64));
        Assert.True(group.FxCache.HasPending); // 子層改了，整組的效果要重算

        LayerEffectRenderer.RenderLayerNow(session.Document, group);
        Assert.Equal(new SKColor(255, 255, 0), CachePixel(group, 10, 10)); // 藍反相 = 黃
        session.Dispose();
    }

    [Fact]
    public void NestedGroupEffects_ComputeInnerFirst()
    {
        var (session, group, _, _) = NewGroupDoc();
        var outer = new GroupLayer { Name = "外層群組" };
        lock (session.Document.SyncRoot)
        {
            session.Document.Root.Remove(group);
            session.Document.Root.Add(outer);
            outer.Add(group);
        }
        LayerEffectCommands.Add(session.Document, session.History, group,
            LayerEffect.Create(new AdjustmentEffect(new InvertAdjustment())));
        LayerEffectCommands.Add(session.Document, session.History, outer,
            LayerEffect.Create(new AdjustmentEffect(new InvertAdjustment())));

        LayerEffectRenderer.RenderAllNow(session.Document);
        // 反相兩次 = 回到原色（內層先算完，外層才吃得到正確的來源）
        Assert.Equal(new SKColor(200, 0, 0), CachePixel(outer, 10, 10));
        session.Dispose();
    }

    [Fact]
    public void GroupEffects_SurviveMppRoundTrip()
    {
        var (session, group, _, _) = NewGroupDoc();
        LayerEffectCommands.Add(session.Document, session.History, group,
            LayerEffect.Create(new AdjustmentEffect(new InvertAdjustment())));
        LayerEffectCommands.Add(session.Document, session.History, group,
            LayerEffect.Create(new GaussianBlurEffect { Radius = 4 }));
        LayerEffectCommands.SetEnabled(session.Document, session.History, group, group.Effects[1].Id, false);

        var path = Path.Combine(Path.GetTempPath(), $"mp-group-fx-{Guid.NewGuid():N}.mpp");
        try
        {
            MppFormat.Save(session.Document, path);
            using var loaded = MppFormat.Load(path);
            var reloaded = Assert.IsType<GroupLayer>(loaded.Root.Children[0]);

            Assert.Equal(2, reloaded.Effects.Count);
            Assert.IsType<AdjustmentEffect>(reloaded.Effects[0].Effect);
            Assert.True(reloaded.Effects[0].Enabled);
            Assert.Equal(4, ((GaussianBlurEffect)reloaded.Effects[1].Effect).Radius);
            Assert.False(reloaded.Effects[1].Enabled); // 停用狀態也要留著
        }
        finally
        {
            File.Delete(path);
            session.Dispose();
        }
    }

    [Fact]
    public void AdjustmentLayer_CannotHaveEffects()
    {
        var adjustment = new AdjustmentLayer(new InvertAdjustment());
        Assert.False(adjustment.CanHaveEffects);
        Assert.True(new GroupLayer().CanHaveEffects);
        Assert.True(new RasterLayer().CanHaveEffects);
    }

    /// <summary>
    /// 使用者 2026-09-06 回報：群組套「聚焦 - 亮度」後，關掉群組顯示再打開，效果變深了。
    /// 位置相關的效果（圓心、半對角線看的是計算範圍）宣告要整層重算，可是顯示切換只把畫布範圍標髒，
    /// 局部重算的範圍（畫布）跟第一次整份算的範圍（內容 tile 對齊 ∪ 畫布）不一樣，圓就換了位置與大小。
    /// </summary>
    [Fact]
    public void PositionDependentGroupEffect_SameAfterVisibilityToggle()
    {
        var (session, group, _, _) = NewGroupDoc();
        var doc = session.Document;
        LayerEffectCommands.Add(doc, session.History, group,
            LayerEffect.Create(new FocusEffect { Mode = FocusEffect.ModeBrightness, Brightness = -80, Radius = 10, Feather = 30 }));
        LayerEffectRenderer.RenderLayerNow(doc, group);
        var before = new[] { CachePixel(group, 5, 5), CachePixel(group, 64, 64), CachePixel(group, 120, 120), CachePixel(group, 100, 30) };

        LayerCommands.SetVisible(doc, session.History, group, false);
        LayerCommands.SetVisible(doc, session.History, group, true);
        LayerEffectRenderer.RenderLayerNow(doc, group);
        var after = new[] { CachePixel(group, 5, 5), CachePixel(group, 64, 64), CachePixel(group, 120, 120), CachePixel(group, 100, 30) };

        for (var i = 0; i < before.Length; i++)
            Assert.True(Math.Abs(before[i].Red - after[i].Red) <= 1 && Math.Abs(before[i].Green - after[i].Green) <= 1,
                $"第 {i} 點顯示切換前 {before[i]} 後 {after[i]}：位置相關效果的局部重算改變了幾何");

        session.Dispose();
    }
}
