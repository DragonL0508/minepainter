using MinePainter.Core.Documents;
using MinePainter.Core.History;
using MinePainter.Core.Layers;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 圖層面板多選後的操作（群組化、整批搬移、上下移、刪除）：相對順序不變、一步 undo。
/// 樹的順序：children index 0 在最下面；面板由上往下看＝index 由大到小。
/// </summary>
public class MultiSelectLayerCommandTests
{
    private static (Document Doc, HistoryManager History, RasterLayer A, RasterLayer B, RasterLayer C, RasterLayer D) NewDoc()
    {
        var doc = new Document(64, 64);
        var history = new HistoryManager(doc);
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
            doc.ActiveLayer = a;
        }
        return (doc, history, a, b, c, d);
    }

    private static string[] Names(GroupLayer g) => g.Children.Select(n => n.Name).ToArray();

    [Fact]
    public void NormalizeSelection_丟掉祖先也被選的子層_並照樹的順序排()
    {
        var (doc, history, a, b, c, d) = NewDoc();
        var group = LayerCommands.WrapInGroup(doc, history, c); // root: a b [c] d
        var picked = LayerCommands.NormalizeSelection(doc, [d, c, group, a, doc.Root]);
        Assert.Equal(new LayerNode[] { a, group, d }, picked); // c 隨群組走、root 沒有父節點
    }

    [Fact]
    public void GroupNodes_多層一起進同一個群組_落在最上面那層的位置_一步undo()
    {
        var (doc, history, a, b, c, d) = NewDoc();

        var group = LayerCommands.GroupNodes(doc, history, [d, b]); // 跨過 c 選了 b 與 d
        Assert.NotNull(group);
        Assert.Equal(new[] { "a", "c", "群組" }, Names(doc.Root));   // 群組在原本 d 的位置（最上）
        Assert.Equal(new[] { "b", "d" }, Names(group!));            // 群組裡上下順序不變

        history.Undo();
        Assert.Equal(new[] { "a", "b", "c", "d" }, Names(doc.Root)); // 一步就整個回來
        Assert.Null(group.Document);

        history.Redo();
        Assert.Equal(new[] { "a", "c", "群組" }, Names(doc.Root));
        Assert.Equal(new[] { "b", "d" }, Names(group));
    }

    [Fact]
    public void GroupNodes_只選一層_等同WrapInGroup()
    {
        var (doc, history, _, b, _, _) = NewDoc();
        var group = LayerCommands.GroupNodes(doc, history, [b]);
        Assert.NotNull(group);
        Assert.Equal(new[] { "a", "群組", "c", "d" }, Names(doc.Root));
        Assert.Same(b, group!.Children[0]);
    }

    [Fact]
    public void MoveNodes_整批搬到目標下方_相對順序不變_一步undo()
    {
        var (doc, history, a, b, c, d) = NewDoc();

        // 把 a 與 c 拖到 d 的上面（面板語意 Above：index = IndexOf(d)+1）
        Assert.True(LayerCommands.MoveNodes(doc, history, [c, a], doc.Root, doc.Root.IndexOf(d) + 1));
        Assert.Equal(new[] { "b", "d", "a", "c" }, Names(doc.Root));

        history.Undo();
        Assert.Equal(new[] { "a", "b", "c", "d" }, Names(doc.Root));
    }

    [Fact]
    public void MoveNodes_拖到某層下面_落在它的正下方()
    {
        var (doc, history, a, b, c, d) = NewDoc();

        // Below d：index = IndexOf(d) → 整批插在 d 下面、c 上面
        Assert.True(LayerCommands.MoveNodes(doc, history, [a, b], doc.Root, doc.Root.IndexOf(d)));
        Assert.Equal(new[] { "c", "a", "b", "d" }, Names(doc.Root));
    }

    [Fact]
    public void MoveNodes_已在位子上_不留空步驟()
    {
        var (doc, history, a, b, c, d) = NewDoc();
        var before = history.CanUndo;
        Assert.False(LayerCommands.MoveNodes(doc, history, [c, d], doc.Root, doc.Root.Children.Count));
        Assert.Equal(new[] { "a", "b", "c", "d" }, Names(doc.Root));
        Assert.Equal(before, history.CanUndo);
    }

    [Fact]
    public void MoveNodes_放進群組_與放進自己的子孫會被拒絕()
    {
        var (doc, history, a, b, c, d) = NewDoc();
        var group = LayerCommands.WrapInGroup(doc, history, d); // root: a b c [d]

        Assert.True(LayerCommands.MoveNodes(doc, history, [a, c], group, group.Children.Count));
        Assert.Equal(new[] { "b", "群組" }, Names(doc.Root));
        Assert.Equal(new[] { "d", "a", "c" }, Names(group)); // 放到群組最上層，a 在 c 下面

        // 群組自己加上一層一起搬進群組裡：不行
        Assert.False(LayerCommands.MoveNodes(doc, history, [group, b], group, 0));
        Assert.Equal(new[] { "b", "群組" }, Names(doc.Root));
    }

    [Fact]
    public void ShiftNodes_整批上移_頂到頭的留在原位_其餘跟上()
    {
        var (doc, history, a, b, c, d) = NewDoc();

        Assert.True(LayerCommands.ShiftNodes(doc, history, [a, d], +1)); // d 已在最上，a 往上一格
        Assert.Equal(new[] { "b", "a", "c", "d" }, Names(doc.Root));

        Assert.True(LayerCommands.ShiftNodes(doc, history, [c, d], -1)); // 整塊往下擠一格
        Assert.Equal(new[] { "b", "c", "d", "a" }, Names(doc.Root));

        history.Undo();
        Assert.Equal(new[] { "b", "a", "c", "d" }, Names(doc.Root)); // 兩層一起下移是一步

        Assert.False(LayerCommands.ShiftNodes(doc, history, [c, d], +1)); // 全部頂到頭：沒事可做、不留空步驟
    }

    [Fact]
    public void RemoveNodes_一起刪_一步undo()
    {
        var (doc, history, a, b, c, d) = NewDoc();
        lock (doc.SyncRoot) doc.ActiveLayer = b;

        Assert.Equal(2, LayerCommands.RemoveNodes(doc, history, [b, d]));
        Assert.Equal(new[] { "a", "c" }, Names(doc.Root));
        Assert.Null(doc.ActiveLayer); // 作用中那層被刪了，呼叫端再挑鄰居

        history.Undo();
        Assert.Equal(new[] { "a", "b", "c", "d" }, Names(doc.Root));
        Assert.Same(doc, b.Document);
        Assert.Same(doc, d.Document);
    }
}
