using System.Reflection;
using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Selections;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// undo 框架的結構性保證。
///
/// 這組測試守的不是某個功能，而是「未來新增功能時不會再犯同一類錯」：
/// 進行中的編輯（浮動選取、畫布內文字編輯）在任何歷史操作前都必須先落地，
/// 而且不可能有人繞過這件事。
/// </summary>
public class UndoFrameworkTests
{
    /// <summary>
    /// HistoryManager 的 Undo/Redo/JumpTo 必須是 internal —— App 組件碰不到，
    /// 只能走 EditorSession（它會先 CommitPendingEdits）。
    /// 曾經有三個 UI 入口各自直接呼叫 History.Undo()，其中一個漏掉就整組行為壞掉。
    /// </summary>
    [Theory]
    [InlineData(nameof(HistoryManager.Undo))]
    [InlineData("Redo")]
    [InlineData("JumpTo")]
    public void HistoryNavigation_IsNotPubliclyReachable(string methodName)
    {
        var method = typeof(HistoryManager).GetMethod(methodName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.NotNull(method);
        Assert.False(method!.IsPublic,
            $"HistoryManager.{methodName} 不可以是 public —— UI 必須走 EditorSession.{methodName}，" +
            "否則會略過還沒落地的編輯（浮動選取等），undo 就會跳到上一步。");
    }

    /// <summary>把手框只能由 EditorSession 自己推導，外部不可指派（否則會與選取範圍分家）。</summary>
    [Fact]
    public void SelectionHandles_IsNotPubliclyWritable()
    {
        var property = typeof(EditorSession).GetProperty(nameof(EditorSession.SelectionHandles));
        Assert.NotNull(property);
        Assert.False(property!.SetMethod?.IsPublic ?? false,
            "SelectionHandles 不可以是公開可寫 —— 它必須由 RefreshSelectionHandles 從選取狀態推導，" +
            "否則螞蟻線與把手框會分家。");
    }

    /// <summary>註冊進來的 pending edit，在 undo/redo/跳轉時都會被落地。</summary>
    [Theory]
    [InlineData("undo")]
    [InlineData("redo")]
    [InlineData("jump")]
    public void RegisteredPendingEdit_IsCommittedBeforeHistoryNavigation(string operation)
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(256, 256, SKColors.White));
        var edit = new SpyPendingEdit();
        session.RegisterPendingEdit(edit);

        switch (operation)
        {
            case "undo": session.Undo(); break;
            case "redo": session.Redo(); break;
            default: session.JumpTo(0); break;
        }

        Assert.True(edit.Committed, $"{operation} 之前沒有落地已註冊的 pending edit");
    }

    [Fact]
    public void CommitPendingEdits_RunsUntilEverythingIsSettled()
    {
        // 落地一項可能啟動另一項（例如提交文字編輯後又動到選取）
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(256, 256, SKColors.White));
        var second = new SpyPendingEdit { StartsInactive = true };
        var first = new SpyPendingEdit { OnCommit = () => second.Activate() };
        session.RegisterPendingEdit(first);
        session.RegisterPendingEdit(second);

        session.CommitPendingEdits();

        Assert.True(first.Committed);
        Assert.True(second.Committed);
        Assert.False(session.HasPendingEdits);
    }

    /// <summary>
    /// 提起選取內容後沒有真的移動（在選取範圍內點一下），不該留下一步空的 undo。
    /// 空步驟的症狀就是「按了 Ctrl+Z 卻什麼都沒變」。
    /// </summary>
    [Fact]
    public void LiftWithoutMoving_LeavesNoEmptyHistoryStep()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(512, 512, SKColors.White));
        var layer = (RasterLayer)session.Document.ActiveLayer!;
        layer.Surface.Fill(new SKRectI(100, 100, 200, 200), SKColors.Red);

        using var path = new SKPath();
        path.AddRect(SKRect.Create(100, 100, 100, 100));
        SelectionCommands.SetSelection(session, SelectionMask.FromPath(path, session.Document.Bounds), "選取");
        var undoAfterSelect = session.History.UndoStack.Count;

        // 在選取範圍內點一下（提起但沒移動），再落地
        session.Move.OnPointerDown(new ToolPointerEvent(new SKPoint(150, 150), 1f), session);
        session.Move.OnPointerUp(new ToolPointerEvent(new SKPoint(150, 150), 1f), session);
        session.CommitPendingEdits();

        Assert.Equal(undoAfterSelect, session.History.UndoStack.Count);
        Assert.Null(session.Floating);
        Assert.NotNull(session.Selection); // 選取範圍還在
    }

    private sealed class SpyPendingEdit : IPendingEdit
    {
        private bool _active = true;

        public bool StartsInactive
        {
            init { if (value) _active = false; }
        }

        public Action? OnCommit { get; init; }
        public bool Committed { get; private set; }

        public void Activate() => _active = true;

        public bool IsActive => _active;

        public void Commit()
        {
            _active = false;
            Committed = true;
            OnCommit?.Invoke();
        }
    }
}
