using MinePainter.Core.Effects;
using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 把一個圖層的效果複製到另一個圖層（圖層面板 Alt 拖曳的底層指令）：
/// 來源不會被清空、複本是獨立實體（各改各的）、疊加與取代各一步 undo。
/// </summary>
public class EffectCopyBetweenLayersTests
{
    private static (EditorSession Session, RasterLayer Source, RasterLayer Target) Setup()
    {
        var doc = ImageCodec.CreateBlankDocument(64, 64, SKColors.White);
        var source = (RasterLayer)doc.ActiveLayer!;
        source.Name = "來源";
        var target = new RasterLayer { Name = "目標" };
        lock (doc.SyncRoot)
        {
            source.SetEffects(
            [
                LayerEffect.Create(new ObjectOutlineEffect { Width = 4, Color = SKColors.Red }),
                LayerEffect.Create(new ObjectShadowEffect { Blur = 7 }),
            ]);
            target.SetEffects([LayerEffect.Create(new ObjectGlowEffect())]);
            doc.Root.Add(target);
        }
        return (new EditorSession(doc), source, target);
    }

    [Fact]
    public void 疊加_加在目標原有的效果後面()
    {
        var (session, source, target) = Setup();
        using var _ = session;

        Assert.True(LayerEffectCommands.CopyEffectsTo(
            session.Document, session.History, source.Effects, target, EffectDropMode.Append));

        Assert.Equal(3, target.Effects.Count);
        Assert.IsType<ObjectGlowEffect>(target.Effects[0].Effect);   // 目標原本的留在前面
        Assert.IsType<ObjectOutlineEffect>(target.Effects[1].Effect);
        Assert.IsType<ObjectShadowEffect>(target.Effects[2].Effect);
        Assert.Equal(2, source.Effects.Count);                        // 來源不會被清空
    }

    [Fact]
    public void 取代_整份換掉目標原有的效果()
    {
        var (session, source, target) = Setup();
        using var _ = session;

        LayerEffectCommands.CopyEffectsTo(
            session.Document, session.History, source.Effects, target, EffectDropMode.Replace);

        Assert.Equal(2, target.Effects.Count);
        Assert.IsType<ObjectOutlineEffect>(target.Effects[0].Effect);
        Assert.DoesNotContain(target.Effects, fx => fx.Effect is ObjectGlowEffect);
    }

    [Fact]
    public void 複本是獨立實體_改一邊不影響另一邊()
    {
        var (session, source, target) = Setup();
        using var _ = session;
        LayerEffectCommands.CopyEffectsTo(
            session.Document, session.History, source.Effects, target, EffectDropMode.Replace);

        var copy = target.Effects[0];
        Assert.NotEqual(source.Effects[0].Id, copy.Id); // Id 一樣的話兩層會被當成同一道效果
        Assert.Equal(source.Effects[0].Effect, copy.Effect);

        LayerEffectCommands.Replace(session.Document, session.History, target,
            copy with { Effect = new ObjectOutlineEffect { Width = 20 } });

        Assert.Equal(20, ((ObjectOutlineEffect)target.Effects[0].Effect).Width);
        Assert.Equal(4, ((ObjectOutlineEffect)source.Effects[0].Effect).Width);
    }

    [Fact]
    public void 一步undo還原目標原本的效果()
    {
        var (session, source, target) = Setup();
        using var _ = session;
        var undoBefore = session.History.UndoStack.Count;

        LayerEffectCommands.CopyEffectsTo(
            session.Document, session.History, source.Effects, target, EffectDropMode.Replace);
        Assert.Equal(undoBefore + 1, session.History.UndoStack.Count);

        session.Undo();
        Assert.Single(target.Effects);
        Assert.IsType<ObjectGlowEffect>(target.Effects[0].Effect);
    }

    [Fact]
    public void 來源沒有效果就沒事可做_不留下空的undo步驟()
    {
        var (session, _, target) = Setup();
        using var __ = session;
        var undoBefore = session.History.UndoStack.Count;

        Assert.False(LayerEffectCommands.CopyEffectsTo(
            session.Document, session.History, [], target, EffectDropMode.Replace));
        Assert.Equal(undoBefore, session.History.UndoStack.Count);
        Assert.Single(target.Effects);
    }

    [Fact]
    public void 群組也可以是目標()
    {
        var (session, source, _) = Setup();
        using var _ = session;
        var group = new GroupLayer { Name = "群組" };
        lock (session.Document.SyncRoot) session.Document.Root.Add(group);

        Assert.True(LayerEffectCommands.CopyEffectsTo(
            session.Document, session.History, source.Effects, group, EffectDropMode.Append));
        Assert.Equal(2, group.Effects.Count);
    }
}
