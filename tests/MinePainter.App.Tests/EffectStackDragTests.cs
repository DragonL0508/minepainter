using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using MinePainter.App.Views;
using MinePainter.Core.Adjustments;
using MinePainter.Core.Effects;
using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.App.Tests;

public class EffectStackDragTests
{
    [AvaloniaFact]
    public void DragCard_ReordersStack()
    {
        var doc = ImageCodec.CreateBlankDocument(64, 64, SKColors.Gray);
        using var session = new EditorSession(doc);
        var layer = (RasterLayer)doc.ActiveLayer!;
        var a = LayerEffect.Create(new AdjustmentEffect(new InvertAdjustment()));
        var b = LayerEffect.Create(new ObjectOutlineEffect());
        var c = LayerEffect.Create(new ObjectGlowEffect());
        LayerEffectCommands.Add(doc, session.History, layer, a);
        LayerEffectCommands.Add(doc, session.History, layer, b);
        LayerEffectCommands.Add(doc, session.History, layer, c);

        var win = new LayerPropertiesWindow(session, layer);
        win.Show();
        Dispatcher();

        // 找出三張卡片（Tag 是 double 的 Border）
        var cards = win.GetVisualDescendants().OfType<Border>().Where(x => x.Tag is double).ToList();
        Assert.Equal(3, cards.Count);
        var top = cards[0];    // 視覺最上 = c
        var bottom = cards[2]; // 視覺最下 = a

        var from = bottom.TranslatePoint(new Point(bottom.Bounds.Width / 2, bottom.Bounds.Height / 2), win)!.Value;
        var to = top.TranslatePoint(new Point(top.Bounds.Width / 2, 2), win)!.Value;

        win.MouseDown(from, MouseButton.Left);
        win.MouseMove(new Point(from.X, from.Y - 10));
        win.MouseMove(to);
        win.MouseMove(new Point(to.X, to.Y - 4));
        win.MouseUp(new Point(to.X, to.Y - 4), MouseButton.Left);
        Dispatcher();

        var ids = layer.Effects.Select(e => e.Id).ToArray();
        Assert.Equal([b.Id, c.Id, a.Id], ids); // a 拖到最上 = 最後套用
        win.Close();
    }

    private static void Dispatcher() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
