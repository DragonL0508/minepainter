using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using MinePainter.App.Views;
using MinePainter.Core.Documents;
using MinePainter.Core.Effects;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.App.Tests;

/// <summary>
/// 圖層面板按住 Alt 拖曳＝把效果複製到另一層（Alt 在這個 App 一律是「複製」），
/// 再加 Shift＝取代那一層原本的效果。沒按 Alt 的拖曳仍然是搬圖層。
/// </summary>
public class LayersPanelEffectDragTests
{
    private static (Window Window, LayersPanel Panel, EditorSession Session, RasterLayer Source, RasterLayer Target)
        Open()
    {
        var doc = new Document(64, 64);
        var source = new RasterLayer { Name = "來源" };
        var target = new RasterLayer { Name = "目標" };
        lock (doc.SyncRoot)
        {
            source.SetEffects(
            [
                LayerEffect.Create(new ObjectOutlineEffect { Width = 4, Color = SKColors.Red }),
                LayerEffect.Create(new ObjectShadowEffect { Blur = 7 }),
            ]);
            target.SetEffects([LayerEffect.Create(new ObjectGlowEffect())]);
            doc.Root.Add(source);
            doc.Root.Add(target);
            doc.ActiveLayer = target;
        }

        var session = new EditorSession(doc);
        var panel = new LayersPanel();
        var window = new Window { Width = 320, Height = 480, Content = panel };
        window.Show();
        panel.SetSession(session);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return (window, panel, session, source, target);
    }

    private static Point CenterOf(Window window, LayersPanel panel, LayerNode node)
    {
        var item = panel.GetVisualDescendants().OfType<ListBoxItem>().First(i => ReferenceEquals(i.Tag, node));
        return item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), window)!.Value;
    }

    /// <summary>從一列拖到另一列；<paramref name="extra"/> 是整段拖曳期間按著的修飾鍵。</summary>
    private static void Drag(Window window, Point from, Point to, RawInputModifiers extra)
    {
        window.MouseDown(from, MouseButton.Left, extra);
        // 拖曳中要看得到左鍵還按著，不然面板會以為手放開了
        window.MouseMove(new Point(from.X, from.Y + 10), RawInputModifiers.LeftMouseButton | extra);
        window.MouseMove(to, RawInputModifiers.LeftMouseButton | extra);
        window.MouseUp(to, MouseButton.Left, extra);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Alt拖曳_效果疊加到目標圖層_來源保留()
    {
        var (window, panel, session, source, target) = Open();
        using var _ = session;

        Drag(window, CenterOf(window, panel, source), CenterOf(window, panel, target), RawInputModifiers.Alt);

        Assert.Equal(3, target.Effects.Count);
        Assert.IsType<ObjectGlowEffect>(target.Effects[0].Effect); // 原本的留在前面
        Assert.IsType<ObjectOutlineEffect>(target.Effects[1].Effect);
        Assert.Equal(2, source.Effects.Count);                     // 來源不會被搬空
        Assert.Equal(2, session.Document.Root.Children.Count);     // 圖層沒有被搬動
    }

    [AvaloniaFact]
    public void AltShift拖曳_取代目標原本的效果()
    {
        var (window, panel, session, source, target) = Open();
        using var _ = session;

        Drag(window, CenterOf(window, panel, source), CenterOf(window, panel, target),
            RawInputModifiers.Alt | RawInputModifiers.Shift);

        Assert.Equal(2, target.Effects.Count);
        Assert.DoesNotContain(target.Effects, fx => fx.Effect is ObjectGlowEffect);
        Assert.Equal(2, source.Effects.Count);
    }

    [AvaloniaFact]
    public void 沒按Alt的拖曳照舊搬圖層_不動效果()
    {
        var (window, panel, session, source, target) = Open();
        using var _ = session;
        var order = () => session.Document.Root.Children.Select(n => n.Name).ToArray();
        Assert.Equal(["來源", "目標"], order());

        Drag(window, CenterOf(window, panel, target), CenterOf(window, panel, source), RawInputModifiers.None);

        Assert.Equal(["目標", "來源"], order()); // 搬過去了
        Assert.Single(target.Effects);           // 效果沒被動到
        Assert.Equal(2, source.Effects.Count);
    }

    /// <summary>
    /// 落點框走覆疊層，不去改 ListBoxItem.Background —— 那個屬性有 160ms 的漸變
    /// （Animations.axaml），改它的話掃過一串圖層會留下一整排還在褪色的紫色殘影
    /// （使用者 2026-09-07 回報：「一堆圖層都會變成紫色」）。
    /// </summary>
    [AvaloniaFact]
    public void 落點框只有一個_不去染ListBoxItem的背景()
    {
        var (window, panel, session, source, target) = Open();
        using var _ = session;

        var highlight = panel.GetVisualDescendants().OfType<Border>().First(b => b.Name == "DropRowHighlight");
        Assert.False(highlight.IsVisible);

        var from = CenterOf(window, panel, source);
        window.MouseDown(from, MouseButton.Left, RawInputModifiers.Alt);
        window.MouseMove(new Point(from.X, from.Y + 10), RawInputModifiers.LeftMouseButton | RawInputModifiers.Alt);
        window.MouseMove(CenterOf(window, panel, target), RawInputModifiers.LeftMouseButton | RawInputModifiers.Alt);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.True(highlight.IsVisible, "指到目標圖層時要看得到落點框");
        var targetItem = panel.GetVisualDescendants().OfType<ListBoxItem>().First(i => ReferenceEquals(i.Tag, target));
        var top = targetItem.TranslatePoint(default, panel.GetVisualDescendants().OfType<ListBox>().First())!.Value.Y;
        Assert.Equal(top, Canvas.GetTop(highlight), 1);
        Assert.All(panel.GetVisualDescendants().OfType<ListBoxItem>(),
            i => Assert.NotEqual(highlight.Background, i.Background));

        window.MouseUp(CenterOf(window, panel, target), MouseButton.Left, RawInputModifiers.Alt);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.False(highlight.IsVisible, "放開之後落點框要收掉");
    }

    [AvaloniaFact]
    public void Alt拖曳到自己身上不做事()
    {
        var (window, panel, session, source, _) = Open();
        using var __ = session;
        var p = CenterOf(window, panel, source);

        Drag(window, p, p, RawInputModifiers.Alt);

        Assert.Equal(2, source.Effects.Count); // 不能把自己的效果再疊自己一次
    }
}
