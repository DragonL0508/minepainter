using MinePainter.App.Workspace;
using MinePainter.Core.History;
using MinePainter.Core.IO;
using SkiaSharp;
using Xunit;

namespace MinePainter.App.Tests;

/// <summary>
/// OpenDocument 的不變式：dirty 是「變更版本 ≠ 已存版本」的衍生狀態，
/// 存檔期間的編輯不能被存檔完成蓋掉；Dispose 之後不再對外發事件。
/// 這些以前散在 MainWindow 的旗標與 Volatile 讀寫裡，沒有守門。
/// </summary>
public class OpenDocumentTests
{
    private static OpenDocument NewDocument() =>
        new(ImageCodec.CreateBlankDocument(64, 64, SKColors.White));

    /// <summary>推一步什麼都不做的 undo 步驟，只為了讓 History.Changed 發出來。</summary>
    private static void Edit(OpenDocument doc) =>
        doc.Session.History.Push(new ActionHistoryEntry("測試", SKRectI.Empty, _ => { }, _ => { }));

    [Fact]
    public void 剛開啟不dirty_History一變就dirty()
    {
        using var doc = NewDocument();
        Assert.False(doc.IsDirty, "剛開啟的文件不該顯示未儲存");

        var notified = 0;
        doc.StateChanged += () => notified++;
        Edit(doc);
        Assert.True(doc.IsDirty, "History 變了卻沒有變成 dirty");
        Assert.Equal(1, notified);

        Edit(doc);
        Assert.Equal(1, notified); // 已經 dirty 就不用再吵 UI：每一筆都發會塞爆 UI 執行緒
    }

    [Fact]
    public void 存檔成功後乾淨且記住路徑()
    {
        using var doc = NewDocument();
        Edit(doc);
        Edit(doc);

        var version = doc.CaptureSaveVersion();
        doc.CompleteSave(@"C:\x\a.mpp", version);

        Assert.False(doc.IsDirty, "存檔涵蓋了所有變更，卻仍顯示未儲存");
        Assert.Equal("a.mpp", doc.Name);
    }

    [Fact]
    public void 存檔期間又編輯_存完仍dirty()
    {
        using var doc = NewDocument();
        Edit(doc);
        var version = doc.CaptureSaveVersion(); // 背景存檔開始

        Edit(doc); // 使用者在存檔時又畫了一筆

        doc.CompleteSave(@"C:\x\a.mpp", version);
        Assert.True(doc.IsDirty, "存檔期間的編輯被存檔完成蓋掉了 —— 關視窗會不問就丟掉");
    }

    [Fact]
    public void 存檔後編輯再Undo回存檔點_變回乾淨()
    {
        using var doc = NewDocument();
        Edit(doc);
        doc.CompleteSave(@"C:\x\a.mpp", doc.CaptureSaveVersion());

        var notified = 0;
        doc.StateChanged += () => notified++;
        Edit(doc);
        Assert.True(doc.IsDirty);
        Assert.Equal(1, notified);

        doc.Session.Undo();
        Assert.False(doc.IsDirty, "內容已經回到存檔時的樣子，卻還顯示未儲存（關視窗會多問一次）");
        Assert.Equal(2, notified); // 標題的 * 要拿掉，UI 得收到通知

        doc.Session.Redo();
        Assert.True(doc.IsDirty);
    }

    [Fact]
    public void 存檔後Undo再分支_深度相同仍dirty()
    {
        using var doc = NewDocument();
        Edit(doc);
        Edit(doc);
        doc.CompleteSave(@"C:\x\a.mpp", doc.CaptureSaveVersion()); // 存在第二步

        doc.Session.Undo();
        Assert.True(doc.IsDirty);
        Edit(doc); // 從第一步分支出新的第二步：深度與存檔點一樣，內容不一樣
        Assert.True(doc.IsDirty, "分支後深度與存檔點相同就被當成乾淨 —— 改了卻不問就關");
    }

    [Fact]
    public void Dispose之後不再發事件()
    {
        var doc = NewDocument();
        var session = doc.Session;
        var document = session.Document;
        var state = 0;
        var size = 0;
        doc.StateChanged += () => state++;
        doc.DocumentSizeChanged += () => size++;

        DocumentCommands.ResizeCanvas(session, 32, 32);
        Assert.Equal(1, size);
        Assert.Equal(1, state);

        doc.Dispose();

        // HistoryManager.Push 沒有 dispose 守門，事件照發：OpenDocument 的訂閱必須已經解掉，
        // 不然關掉的分頁還會回頭刷新標題（而且拿的是已釋放的文件）
        Edit(doc);
        Assert.Equal(1, state);
    }
}
