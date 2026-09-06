using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MinePainter.App.Views;
using MinePainter.Core.Documents;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using Xunit;

namespace MinePainter.App.Tests;

/// <summary>
/// 圖層面板的多選：Ctrl 點選、框選、整批拖曳、群組化。
/// 作用中圖層永遠是選取裡的一個（工具要有目標）。
/// </summary>
public class LayersPanelMultiSelectTests
{
    private static (Window Window, LayersPanel Panel, EditorSession Session, RasterLayer A, RasterLayer B, RasterLayer C, RasterLayer D) Open()
    {
        var doc = new Document(64, 64);
        var a = new RasterLayer { Name = "a" };
        var b = new RasterLayer { Name = "b" };
        var c = new RasterLayer { Name = "c" };
        var d = new RasterLayer { Name = "d" };
        lock (doc.SyncRoot)
        {
            doc.Root.Add(a);
            doc.Root.Add(b);
            doc.Root.Add(c);
            doc.Root.Add(d);
            doc.ActiveLayer = d;
        }
        var session = new EditorSession(doc);
        var panel = new LayersPanel();
        var window = new Window { Width = 320, Height = 480, Content = panel };
        window.Show();
        panel.SetSession(session);
        RunJobs();
        return (window, panel, session, a, b, c, d);
    }

    private static void RunJobs() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();

    private static ListBoxItem RowOf(LayersPanel panel, LayerNode node) =>
        panel.GetVisualDescendants().OfType<ListBoxItem>().First(i => ReferenceEquals(i.Tag, node));

    private static Point CenterOf(Window window, Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    private static string[] Names(GroupLayer g) => g.Children.Select(n => n.Name).ToArray();

    [AvaloniaFact]
    public void Ctrl點選_加進選取_作用中圖層是剛點的那層()
    {
        var (window, panel, session, a, b, c, d) = Open();
        Assert.Equal([d], panel.SelectedNodes);

        var p = CenterOf(window, RowOf(panel, b));
        window.MouseDown(p, MouseButton.Left, RawInputModifiers.Control);
        window.MouseUp(p, MouseButton.Left, RawInputModifiers.Control);
        RunJobs();

        Assert.Equal(new LayerNode[] { d, b }, panel.SelectedNodes); // 面板由上到下
        Assert.Same(b, session.Document.ActiveLayer);

        // Ctrl 再點一次 b 取消：作用中圖層退回還在選取裡的 d
        window.MouseDown(p, MouseButton.Left, RawInputModifiers.Control);
        window.MouseUp(p, MouseButton.Left, RawInputModifiers.Control);
        RunJobs();
        Assert.Equal([d], panel.SelectedNodes);
        Assert.Same(d, session.Document.ActiveLayer);
        window.Close();
    }

    [AvaloniaFact]
    public void 群組化_選了幾層就一起進同一個群組_群組成為作用中()
    {
        var (window, panel, session, a, b, c, d) = Open();
        panel.SelectNodes([b, d]);
        RunJobs();

        panel.OnGroupLayer(null, new RoutedEventArgs());
        RunJobs();

        var doc = session.Document;
        Assert.Equal(new[] { "a", "c", "群組" }, Names(doc.Root));
        var group = Assert.IsType<GroupLayer>(doc.Root.Children[2]);
        Assert.Equal(new[] { "b", "d" }, Names(group));
        Assert.Same(group, doc.ActiveLayer);
        Assert.Equal([group], panel.SelectedNodes);

        session.Undo();
        RunJobs();
        Assert.Equal(new[] { "a", "b", "c", "d" }, Names(doc.Root)); // 一步就回來
        window.Close();
    }

    [AvaloniaFact]
    public void 整批拖曳_兩層一起搬到另一層下面_順序不變()
    {
        var (window, panel, session, a, b, c, d) = Open();
        panel.SelectNodes([c, d]);
        RunJobs();

        // 按住已選的 d 拖到 a 的下半部（放在 a 下面）
        var from = CenterOf(window, RowOf(panel, d));
        var rowA = RowOf(panel, a);
        var to = rowA.TranslatePoint(new Point(rowA.Bounds.Width / 2, rowA.Bounds.Height * 0.8), window)!.Value;
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(new Point(from.X, from.Y + 10), RawInputModifiers.LeftMouseButton); // 拖曳中要看得到左鍵還按著
        window.MouseMove(to, RawInputModifiers.LeftMouseButton);
        window.MouseUp(to, MouseButton.Left);
        RunJobs();

        Assert.Equal(new[] { "c", "d", "a", "b" }, Names(session.Document.Root)); // c、d 在 a 下面、順序照舊
        Assert.Equal(new LayerNode[] { d, c }, panel.SelectedNodes);              // 搬完還是選著這兩層
        window.Close();
    }

    [AvaloniaFact]
    public void 空白處拖曳框選_框到的列都被選起來()
    {
        var (window, panel, session, a, b, c, d) = Open();
        var list = panel.GetVisualDescendants().OfType<ListBox>().First();
        var rowA = RowOf(panel, a);
        var rowB = RowOf(panel, b);

        // 從清單最底下的空白處往上拖到 b 的中間：框碰到 a 與 b
        var listOrigin = list.TranslatePoint(default, window)!.Value;
        var start = new Point(listOrigin.X + list.Bounds.Width / 2, listOrigin.Y + list.Bounds.Height - 4);
        var end = CenterOf(window, rowB);
        Assert.True(start.Y > CenterOf(window, rowA).Y, "清單底下要有空白處才能開始框選");

        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(new Point(start.X, start.Y - 10), RawInputModifiers.LeftMouseButton);
        window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        window.MouseUp(end, MouseButton.Left);
        RunJobs();

        Assert.Equal(new LayerNode[] { b, a }, panel.SelectedNodes);
        Assert.Same(b, session.Document.ActiveLayer); // 框裡最上面那層
        window.Close();
    }
}
