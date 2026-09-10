using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using MinePainter.App.Controls;
using MinePainter.App.Views;
using MinePainter.Core.Documents;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.App.Tests;

public class LayerMaskPropertiesTests
{
    [AvaloniaFact]
    public void AdvancedTogglesUndoRedoAndSyncWithoutAddingHistory()
    {
        using var doc = new Document(8, 8);
        var group = new GroupLayer { Mask = new LayerMask(doc.Bounds, new byte[64], 255) };
        doc.Root.Add(group);
        using var session = new EditorSession(doc);
        session.Compositor.StopRendering();
        var window = Open(session, group);
        try
        {
            var checks = window.GetVisualDescendants().OfType<CheckBox>().ToArray();
            var pass = checks.Single(c => c.Name == "PassThrough");
            var red = checks.Single(c => Equals(c.Content, "R"));
            var inverted = checks.Single(c => c.Name == "MaskInverted");
            pass.IsChecked = true;
            red.IsChecked = false;
            inverted.IsChecked = true;
            Assert.True(group.IsPassThrough);
            Assert.Equal(1, group.RestrictedChannels);
            Assert.True(group.Mask!.Inverted);
            session.Undo();
            Assert.False(group.Mask.Inverted);
            session.Undo();
            Assert.Equal(0, group.RestrictedChannels);
            session.Undo();
            Assert.False(group.IsPassThrough);
            var state = session.History.StateId;
            window.SyncFromModel();
            Assert.Equal(state, session.History.StateId);
            Assert.False(pass.IsChecked);
            Assert.True(red.IsChecked);
            session.Redo();
            session.Redo();
            session.Redo();
            Assert.True(group.IsPassThrough);
            Assert.Equal(1, group.RestrictedChannels);
            Assert.True(group.Mask.Inverted);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void MaskDensityDragCreatesOneUndoAndPreservesOtherSettings()
    {
        using var doc = new Document(8, 8);
        var raster = new RasterLayer { Mask = new LayerMask(doc.Bounds, new byte[64], 255) { Feather = 2, Inverted = true } };
        doc.Root.Add(raster);
        using var session = new EditorSession(doc);
        session.Compositor.StopRendering();
        var window = Open(session, raster);
        try
        {
            var slider = window.GetVisualDescendants().OfType<BarSlider>().Single(c => c.Name == "MaskDensity");
            Point At(double fraction) => slider.TranslatePoint(new Point(slider.Bounds.Width * fraction, slider.Bounds.Height / 2), window)!.Value;
            window.MouseDown(At(.8), MouseButton.Left);
            window.MouseMove(At(.6));
            window.MouseMove(At(.4));
            Assert.Equal(0, session.History.StateId);
            window.MouseUp(At(.4), MouseButton.Left);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var final = raster.Mask!.Density;
            Assert.InRange(final, .2f, .6f);
            Assert.Equal(2, raster.Mask.Feather);
            Assert.True(raster.Mask.Inverted);
            Assert.Equal(1, session.History.StateId);
            session.Undo();
            Assert.Equal(1, raster.Mask.Density);
            Assert.Equal(0, session.History.StateId);
            session.Redo();
            Assert.Equal(final, raster.Mask.Density);
        }
        finally { window.Close(); }
    }

    private static LayerPropertiesWindow Open(EditorSession session, LayerNode node)
    {
        var window = new LayerPropertiesWindow(session, node);
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.GetVisualDescendants().OfType<Expander>().First().IsExpanded = true;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return window;
    }
}
