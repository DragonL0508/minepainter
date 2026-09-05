using Avalonia.Input;
using MinePainter.App.Services;
using Xunit;

namespace MinePainter.App.Tests;

/// <summary>
/// 滾輪手勢表：滾輪錄不進按鍵表（沒有 Key 可以填），所以一個動作綁一組修飾鍵。
/// 一組修飾鍵只做一件事；「不按修飾鍵」也是一組合法的綁定。
/// </summary>
[Collection("ShortcutMap")]
public class WheelMapTests : IDisposable
{
    public void Dispose() => WheelMap.ResetAll(); // 靜態狀態：每個測試後還原

    [Fact]
    public void 預設的四組手勢()
    {
        Assert.Equal("wheel.zoom", WheelMap.Match(KeyModifiers.Control));
        Assert.Equal("wheel.panVertical", WheelMap.Match(KeyModifiers.None));
        Assert.Equal("wheel.panHorizontal", WheelMap.Match(KeyModifiers.Shift));
        Assert.Equal("wheel.brushSize", WheelMap.Match(KeyModifiers.Alt));
    }

    [Fact]
    public void 預設沒綁的動作不會被誤判()
    {
        Assert.Null(WheelMap.Get("wheel.brushOpacity"));
        Assert.Null(WheelMap.Match(KeyModifiers.Control | KeyModifiers.Alt));
    }

    [Fact]
    public void 綁到別人在用的修飾鍵_對方會被解除()
    {
        var displaced = WheelMap.Set("wheel.brushOpacity", KeyModifiers.Alt);

        Assert.Equal("wheel.brushSize", displaced?.Id);
        Assert.Equal("wheel.brushOpacity", WheelMap.Match(KeyModifiers.Alt));
        Assert.Null(WheelMap.Get("wheel.brushSize"));
    }

    [Fact]
    public void 覆寫會記進設定_回到預設就不記()
    {
        var settings = AppSettings.Instance;

        WheelMap.Set("wheel.brushSize", KeyModifiers.Control | KeyModifiers.Shift);
        Assert.Equal("Control, Shift", settings.WheelGestures["wheel.brushSize"]);

        WheelMap.Set("wheel.brushSize", KeyModifiers.Alt); // 回到預設
        Assert.False(settings.WheelGestures.ContainsKey("wheel.brushSize"));
    }

    [Fact]
    public void 取消綁定之後那組修飾鍵就沒人接()
    {
        WheelMap.Set("wheel.zoom", null);

        Assert.Null(WheelMap.Get("wheel.zoom"));
        Assert.Null(WheelMap.Match(KeyModifiers.Control));
        Assert.Equal("", AppSettings.Instance.WheelGestures["wheel.zoom"]);
    }

    [Theory]
    [InlineData(null, "未綁定")]
    [InlineData(KeyModifiers.None, "滾輪")]
    [InlineData(KeyModifiers.Alt, "Alt + 滾輪")]
    [InlineData(KeyModifiers.Control | KeyModifiers.Shift, "Control + Shift + 滾輪")]
    public void 顯示文字(KeyModifiers? modifiers, string expected)
        => Assert.Equal(expected, WheelMap.Describe(modifiers));

    /// <summary>預設值裡不能有兩個動作綁同一組修飾鍵（Match 只會回一個）。</summary>
    [Fact]
    public void 預設值沒有重複()
    {
        var seen = new HashSet<KeyModifiers>();
        foreach (var def in WheelMap.Defs)
        {
            if (def.Default is not { } mods) continue;
            Assert.True(seen.Add(mods), $"「{def.Name}」的預設修飾鍵與別人重複");
        }
    }
}
