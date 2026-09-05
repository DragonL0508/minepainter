using Avalonia.Input;
using MinePainter.App.Services;
using Xunit;

namespace MinePainter.App.Tests;

/// <summary>
/// 快捷鍵表：每個指令兩格手勢（主鍵／副鍵）。
/// 「Ctrl+Shift+Z 也是重做」「0 也是最適大小」這些本來寫死的別名，現在就是副鍵，
/// 所以使用者改得到；一組鍵仍然只會做一件事（撞到就解除對方）。
/// </summary>
[Collection("ShortcutMap")]
public class ShortcutMapTests : IDisposable
{
    public void Dispose() => ShortcutMap.ResetAll(); // 靜態狀態：每個測試後還原

    [Fact]
    public void 主鍵與副鍵都會命中()
    {
        Assert.True(ShortcutMap.Matches("edit.redo", Key.Y, KeyModifiers.Control));
        Assert.True(ShortcutMap.Matches("edit.redo", Key.Z, KeyModifiers.Control | KeyModifiers.Shift));
        Assert.Equal("edit.redo", ShortcutMap.Match(Key.Z, KeyModifiers.Control | KeyModifiers.Shift));
    }

    [Fact]
    public void 本來寫死的縮放別名變成副鍵()
    {
        Assert.Equal("view.bestFit", ShortcutMap.Match(Key.D0, KeyModifiers.None));
        Assert.Equal("view.actualSize", ShortcutMap.Match(Key.D1, KeyModifiers.None));
        // 數字鍵區也通（NormalizeKey）
        Assert.True(ShortcutMap.Matches("view.bestFit", Key.NumPad0, KeyModifiers.None));
    }

    [Fact]
    public void 本來寫死的按住鍵也進表了()
    {
        Assert.True(ShortcutMap.Matches("view.panHold", Key.Space, KeyModifiers.None));
        Assert.True(ShortcutMap.Matches("nudge.left", Key.Left, KeyModifiers.None));
        Assert.True(ShortcutMap.Matches("edit.cancelEdit", Key.Escape, KeyModifiers.None));
        Assert.True(ShortcutMap.Matches("pen.removeLastPoint", Key.Back, KeyModifiers.None));
    }

    [Fact]
    public void 按住型放開時只比按鍵本身()
    {
        // 按住期間又按了 Shift，放開時的修飾鍵已經不是當初那組
        Assert.False(ShortcutMap.Matches("view.panHold", Key.Space, KeyModifiers.Shift));
        Assert.True(ShortcutMap.MatchesKey("view.panHold", Key.Space));
    }

    [Fact]
    public void 綁到別人已經在用的鍵_對方那一格會被解除()
    {
        // 把「新增圖層」綁成 Ctrl+Shift+Z —— 那是「重做」的副鍵
        var displaced = ShortcutMap.SetGesture("layer.add", 0,
            new KeyGesture(Key.Z, KeyModifiers.Control | KeyModifiers.Shift));

        Assert.Equal("edit.redo", displaced?.Id);
        Assert.Equal("layer.add", ShortcutMap.Match(Key.Z, KeyModifiers.Control | KeyModifiers.Shift));
        Assert.Null(ShortcutMap.GetGesture("edit.redo", 1));      // 副鍵被解除
        Assert.NotNull(ShortcutMap.GetGesture("edit.redo"));      // 主鍵還在
    }

    [Fact]
    public void 副鍵的覆寫分開存_舊設定檔照樣是主鍵()
    {
        var settings = AppSettings.Instance;
        settings.Shortcuts.Remove("tool.brush");
        settings.Shortcuts.Remove("tool.brush#alt");

        ShortcutMap.SetGesture("tool.brush", 1, new KeyGesture(Key.F9));

        Assert.Equal("F9", settings.Shortcuts["tool.brush#alt"]);
        Assert.False(settings.Shortcuts.ContainsKey("tool.brush")); // 主鍵沒動就不記
        Assert.True(ShortcutMap.Matches("tool.brush", Key.F9, KeyModifiers.None));
        Assert.True(ShortcutMap.Matches("tool.brush", Key.B, KeyModifiers.None));
    }

    [Fact]
    public void 清除某一格不影響另一格()
    {
        ShortcutMap.SetGesture("edit.redo", 1, null);

        Assert.Null(ShortcutMap.GetGesture("edit.redo", 1));
        Assert.Equal(new KeyGesture(Key.Y, KeyModifiers.Control).ToString(),
            ShortcutMap.GetGesture("edit.redo")?.ToString());
    }

    [Fact]
    public void 全部重設會把兩格都放回預設()
    {
        ShortcutMap.SetGesture("edit.redo", 0, new KeyGesture(Key.F8));
        ShortcutMap.SetGesture("edit.redo", 1, null);

        ShortcutMap.ResetAll();

        Assert.True(ShortcutMap.Matches("edit.redo", Key.Y, KeyModifiers.Control));
        Assert.True(ShortcutMap.Matches("edit.redo", Key.Z, KeyModifiers.Control | KeyModifiers.Shift));
    }

    /// <summary>
    /// 預設值裡只允許「情境鍵」共用同一組鍵（那種鍵在查表之前就被攔掉了），
    /// 而且 Match 必須穩定地回宣告順序在前的那個 —— 不然一般狀態下按這組鍵會時好時壞。
    /// </summary>
    [Fact]
    public void 預設值只有情境鍵可以共用同一組鍵()
    {
        // 鋼筆進行中時 Backspace＝退一個錨點，其餘時候＝填滿選取範圍
        var allowed = new HashSet<string> { "pen.removeLastPoint" };

        var seen = new Dictionary<string, ShortcutDef>();
        foreach (var def in ShortcutMap.Defs)
        {
            foreach (var gesture in new[] { def.Default, def.DefaultAlt })
            {
                if (gesture == null) continue;
                var text = gesture.ToString();
                if (seen.TryGetValue(text, out var first))
                {
                    Assert.True(allowed.Contains(def.Id),
                        $"「{def.Name}」與「{first.Name}」都預設綁 {text}");
                    Assert.Equal(first.Id, ShortcutMap.Match(gesture.Key, gesture.KeyModifiers));
                    continue;
                }
                seen[text] = def;
            }
        }
    }
}
