using Avalonia.Input;

namespace MinePainter.App.Services;

/// <summary>可自訂的滾輪手勢定義（<paramref name="Default"/> = null 代表預設沒綁）。</summary>
public sealed record WheelDef(string Id, string Name, string Hint, KeyModifiers? Default);

/// <summary>
/// 滾輪手勢表：修飾鍵組合 ↔ 滾輪要做的事。
///
/// 快捷鍵表（<see cref="ShortcutMap"/>）記的是「按鍵」，錄不到「Alt + 滾輪」這種手勢 ——
/// 滾輪沒有 Key 可以填。所以滾輪另外一張表，一個動作綁一組修飾鍵（含「不按」），
/// 設定頁在按鈕上直接滾一下就完成綁定（使用者 2026-09-05 要求能自己設）。
///
/// 一組修飾鍵只會做一件事：綁到別人在用的那組，對方會被解除（同快捷鍵表）。
/// 方向的約定見 <see cref="Controls.WheelInput"/>：改數值的一律「往上＝變小」，
/// 捲動與縮放維持各自的直覺方向。
/// </summary>
public static class WheelMap
{
    public static readonly WheelDef[] Defs =
    [
        new("wheel.zoom", "縮放畫布", "往上滾＝放大", KeyModifiers.Control),
        new("wheel.panVertical", "上下捲動畫面", "往上滾＝看上面的內容", KeyModifiers.None),
        new("wheel.panHorizontal", "左右捲動畫面", "往上滾＝看左邊的內容", KeyModifiers.Shift),
        new("wheel.brushSize", "筆刷大小", "往上滾＝變小", KeyModifiers.Alt),
        new("wheel.brushOpacity", "筆刷不透明度", "往上滾＝變小", null),
    ];

    private static readonly Dictionary<string, KeyModifiers?> Current = new();

    /// <summary>滾輪表變更後發出。</summary>
    public static event Action? Changed;

    static WheelMap()
    {
        foreach (var def in Defs) Current[def.Id] = def.Default;
        LoadOverrides();
    }

    private static void LoadOverrides()
    {
        foreach (var (id, text) in AppSettings.Instance.WheelGestures)
        {
            if (!Current.ContainsKey(id)) continue;
            if (string.IsNullOrEmpty(text))
            {
                Current[id] = null; // 明確的「沒綁」
                continue;
            }
            if (Enum.TryParse<KeyModifiers>(text, out var mods)) Current[id] = mods;
        }
    }

    public static KeyModifiers? Get(string id) => Current.GetValueOrDefault(id);

    /// <summary>這組修飾鍵對應的動作 id（完全相等；沒綁回 null）。依宣告順序找，答案才穩定。</summary>
    public static string? Match(KeyModifiers modifiers)
    {
        foreach (var def in Defs)
        {
            if (Current.GetValueOrDefault(def.Id) == modifiers) return def.Id;
        }
        return null;
    }

    /// <summary>
    /// 重新綁定（null = 不綁）。原本用同一組修飾鍵的動作會自動解除（回傳它，供 UI 提示）。
    /// 覆寫寫進 AppSettings（呼叫端負責 Save）。
    /// </summary>
    public static WheelDef? Set(string id, KeyModifiers? modifiers)
    {
        if (!Current.ContainsKey(id)) return null;

        WheelDef? displaced = null;
        if (modifiers != null)
        {
            foreach (var def in Defs)
            {
                if (def.Id == id || Current.GetValueOrDefault(def.Id) != modifiers) continue;
                displaced = def;
                Store(def.Id, null);
                break;
            }
        }

        Store(id, modifiers);
        Changed?.Invoke();
        return displaced;
    }

    private static void Store(string id, KeyModifiers? modifiers)
    {
        Current[id] = modifiers;
        var def = Defs.First(d => d.Id == id);
        if (modifiers == def.Default)
            AppSettings.Instance.WheelGestures.Remove(id); // 回到預設就不用記
        else
            AppSettings.Instance.WheelGestures[id] = modifiers?.ToString() ?? "";
    }

    public static void ResetAll()
    {
        foreach (var def in Defs) Current[def.Id] = def.Default;
        AppSettings.Instance.WheelGestures.Clear();
        Changed?.Invoke();
    }

    /// <summary>顯示用文字：「Ctrl + 滾輪」「滾輪」「未綁定」。</summary>
    public static string Describe(KeyModifiers? modifiers) => modifiers switch
    {
        null => "未綁定",
        KeyModifiers.None => "滾輪",
        _ => $"{modifiers.ToString()!.Replace(", ", " + ")} + 滾輪",
    };
}
