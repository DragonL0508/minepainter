using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// <see cref="HistoryManager.StateId"/> 是「文件狀態的身分」，不是深度也不是變更次數：
/// undo/redo 回到同一步要拿回同一個編號，分支、淘汰、併步都不能讓它說謊。
/// 未儲存標記靠它判斷，錯了就是「明明沒改卻問要不要存」或反過來「改了卻不問就關」。
/// </summary>
public class HistoryStateIdTests
{
    private static EditorSession NewSession() =>
        new(ImageCodec.CreateBlankDocument(32, 32, SKColors.White));

    private static void Push(EditorSession s, long cost = 0) =>
        s.History.Push(new ActionHistoryEntry("步", SKRectI.Empty, _ => { }, _ => { }, memoryCost: cost));

    [Fact]
    public void Undo_Redo_拿回同一個編號()
    {
        using var s = NewSession();
        var h = s.History;
        var start = h.StateId;
        Push(s);
        var a = h.StateId;
        Push(s);
        var b = h.StateId;
        Assert.NotEqual(start, a);
        Assert.NotEqual(a, b);

        s.Undo();
        Assert.Equal(a, h.StateId);
        s.Undo();
        Assert.Equal(start, h.StateId);
        s.Redo();
        s.Redo();
        Assert.Equal(b, h.StateId);
    }

    [Fact]
    public void 分支後深度相同但編號不同()
    {
        using var s = NewSession();
        var h = s.History;
        Push(s);
        Push(s);
        var c = h.StateId; // A→B→C
        s.Undo();          // 回到 B
        Push(s);           // 從 B 做 D，深度與 C 一樣
        Assert.NotEqual(c, h.StateId); // 「深度相同」被當成「狀態相同」＝改了卻不問就關
        Assert.False(h.CanRedo);
    }

    [Fact]
    public void 淘汰最舊步驟不改變目前編號()
    {
        using var s = NewSession();
        var h = s.History;
        h.MemoryLimit = HistoryManager.MinimumShareBytes;
        Push(s, cost: HistoryManager.MinimumShareBytes); // 第一步
        var first = h.StateId;
        Push(s, cost: HistoryManager.MinimumShareBytes); // 超過上限：第一步被淘汰，只剩這步
        var second = h.StateId;
        Assert.Single(h.UndoStack);

        s.Undo(); // 退到堆疊底部＝「做完第一步」的狀態，不是最初的空白
        Assert.Equal(first, h.StateId);
        Assert.NotEqual(second, h.StateId);
    }

    [Fact]
    public void 併步沿用最後一步的編號()
    {
        using var s = NewSession();
        var h = s.History;
        Push(s);
        Push(s);
        Push(s);
        var last = h.StateId;
        h.CollapseLast(2);
        Assert.Equal(last, h.StateId); // 收尾併步後狀態沒變，存檔點不能因此變 dirty
        Assert.Equal(2, h.UndoStack.Count);
        s.Undo();
        s.Redo();
        Assert.Equal(last, h.StateId);
    }
}
