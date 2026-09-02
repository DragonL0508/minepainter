using Avalonia.Input;

namespace MinePainter.App.Services;

/// <summary>可自訂的指令定義。</summary>
public sealed record ShortcutDef(string Id, string Category, string Name, KeyGesture? Default);

/// <summary>
/// 快捷鍵表：指令 id ↔ 手勢。預設值 = 原本硬編碼在 MainWindow.OnKeyDown 的那套
/// （抄 Pinta、與 paint.net 相容）；使用者覆寫存進 <see cref="AppSettings.Shortcuts"/>。
/// MainWindow 與 CanvasView 的按鍵處理都查這張表，改一處兩邊同步。
/// 不參與自訂的特殊鍵：Ctrl+Shift+Z（重做別名）、浮動內容的 Enter/Esc、空白鍵平移、
/// 滾輪縮放與 Shift/Caps Lock + 滾輪平移。
/// </summary>
public static class ShortcutMap
{
    public static readonly ShortcutDef[] Defs =
    [
        new("file.new", "檔案", "新增", new KeyGesture(Key.N, KeyModifiers.Control)),
        new("file.open", "檔案", "開啟", new KeyGesture(Key.O, KeyModifiers.Control)),
        new("file.save", "檔案", "儲存", new KeyGesture(Key.S, KeyModifiers.Control)),
        new("file.saveAs", "檔案", "另存新檔", null),
        new("file.export", "檔案", "匯出影像", null),
        new("file.closeTab", "檔案", "關閉分頁", new KeyGesture(Key.W, KeyModifiers.Control)),

        new("edit.undo", "編輯", "復原", new KeyGesture(Key.Z, KeyModifiers.Control)),
        new("edit.redo", "編輯", "重做", new KeyGesture(Key.Y, KeyModifiers.Control)),
        new("edit.cut", "編輯", "剪下", new KeyGesture(Key.X, KeyModifiers.Control)),
        new("edit.copy", "編輯", "複製", new KeyGesture(Key.C, KeyModifiers.Control)),
        new("edit.paste", "編輯", "貼上", new KeyGesture(Key.V, KeyModifiers.Control)),
        new("edit.selectAll", "編輯", "全選", new KeyGesture(Key.A, KeyModifiers.Control)),
        new("edit.deselect", "編輯", "取消選取", new KeyGesture(Key.D, KeyModifiers.Control)),
        new("edit.invertSelection", "編輯", "反轉選取", new KeyGesture(Key.I, KeyModifiers.Control)),
        new("edit.erase", "編輯", "清除選取範圍", new KeyGesture(Key.Delete)),
        new("edit.fill", "編輯", "填滿選取範圍", new KeyGesture(Key.Back)),

        new("image.crop", "影像", "裁切至選取範圍", new KeyGesture(Key.X, KeyModifiers.Control | KeyModifiers.Shift)),
        new("image.rotateCw", "影像", "順時針旋轉 90°", new KeyGesture(Key.H, KeyModifiers.Control)),
        new("image.rotateCcw", "影像", "逆時針旋轉 90°", new KeyGesture(Key.G, KeyModifiers.Control)),
        new("image.rotate180", "影像", "旋轉 180°", new KeyGesture(Key.J, KeyModifiers.Control)),
        new("image.resize", "影像", "調整影像大小", new KeyGesture(Key.R, KeyModifiers.Control)),
        new("image.canvasSize", "影像", "調整畫布大小", new KeyGesture(Key.R, KeyModifiers.Control | KeyModifiers.Shift)),
        new("image.flatten", "影像", "平面化", new KeyGesture(Key.F, KeyModifiers.Control | KeyModifiers.Shift)),

        new("adjust.autoLevel", "調整", "自動色階", new KeyGesture(Key.L, KeyModifiers.Control | KeyModifiers.Shift)),
        new("adjust.blackWhite", "調整", "黑白", new KeyGesture(Key.G, KeyModifiers.Control | KeyModifiers.Shift)),
        new("adjust.brightnessContrast", "調整", "亮度 / 對比", new KeyGesture(Key.T, KeyModifiers.Control | KeyModifiers.Shift)),
        new("adjust.curves", "調整", "曲線", new KeyGesture(Key.M, KeyModifiers.Control | KeyModifiers.Shift)),
        new("adjust.hueSaturation", "調整", "色相 / 飽和度", new KeyGesture(Key.U, KeyModifiers.Control)),
        new("adjust.invert", "調整", "負片效果", new KeyGesture(Key.I, KeyModifiers.Control | KeyModifiers.Shift)),
        new("adjust.levels", "調整", "色階", new KeyGesture(Key.L, KeyModifiers.Control)),
        new("adjust.posterize", "調整", "色調分離", new KeyGesture(Key.P, KeyModifiers.Control | KeyModifiers.Shift)),
        new("adjust.sepia", "調整", "懷舊", new KeyGesture(Key.E, KeyModifiers.Control | KeyModifiers.Shift)),
        new("effect.repeat", "效果", "重複上次效果", new KeyGesture(Key.F, KeyModifiers.Control)),

        new("layer.add", "圖層", "新增圖層", new KeyGesture(Key.N, KeyModifiers.Control | KeyModifiers.Shift)),
        new("layer.duplicate", "圖層", "複製圖層", new KeyGesture(Key.D, KeyModifiers.Control | KeyModifiers.Shift)),
        new("layer.mergeDown", "圖層", "向下合併", new KeyGesture(Key.M, KeyModifiers.Control)),
        new("layer.flattenText", "圖層", "圖層文字平面化", null),
        new("layer.import", "圖層", "從檔案匯入圖層", null),
        new("layer.flipH", "圖層", "水平翻轉圖層", null),
        new("layer.flipV", "圖層", "垂直翻轉圖層", null),
        new("layer.properties", "圖層", "圖層屬性", new KeyGesture(Key.F4)),

        new("view.zoomIn", "檢視", "放大", new KeyGesture(Key.OemPlus, KeyModifiers.Control)),
        new("view.zoomOut", "檢視", "縮小", new KeyGesture(Key.OemMinus, KeyModifiers.Control)),
        new("view.actualSize", "檢視", "實際大小", new KeyGesture(Key.D0, KeyModifiers.Control)),
        new("view.bestFit", "檢視", "最適大小", new KeyGesture(Key.B, KeyModifiers.Control)),

        new("tool.brush", "工具", "筆刷", new KeyGesture(Key.B)),
        new("tool.eraser", "工具", "橡皮擦", new KeyGesture(Key.E)),
        new("tool.eyedropper", "工具", "滴管", new KeyGesture(Key.I)),
        new("tool.move", "工具", "移動", new KeyGesture(Key.M)),
        new("tool.rectselect", "工具", "矩形選取", new KeyGesture(Key.S)),
        new("tool.lasso", "工具", "套索選取", new KeyGesture(Key.L)),
        new("tool.wand", "工具", "魔術棒", new KeyGesture(Key.W)),
        new("tool.fill", "工具", "油漆桶", new KeyGesture(Key.F)),
        new("tool.text", "工具", "文字", new KeyGesture(Key.T)),
        new("tool.shape", "工具", "形狀", new KeyGesture(Key.O)),

        // 按住型：壓著進入對齊模式（移動框時吸附畫布四邊與中線），放開即退出
        new("tool.alignHold", "工具", "對齊模式（按住）", new KeyGesture(Key.Tab)),
    ];

    private static readonly Dictionary<string, KeyGesture?> Current = new();

    /// <summary>快捷鍵表變更後發出（選單顯示文字要跟著換）。</summary>
    public static event Action? Changed;

    static ShortcutMap()
    {
        foreach (var def in Defs) Current[def.Id] = def.Default;
        LoadOverrides();
    }

    private static void LoadOverrides()
    {
        foreach (var (id, text) in AppSettings.Instance.Shortcuts)
        {
            if (!Current.ContainsKey(id)) continue;
            if (string.IsNullOrEmpty(text))
            {
                Current[id] = null;
                continue;
            }
            try
            {
                Current[id] = KeyGesture.Parse(text);
            }
            catch
            {
                // 壞掉的覆寫忽略，維持預設
            }
        }
    }

    public static KeyGesture? GetGesture(string id) => Current.GetValueOrDefault(id);

    /// <summary>NumPad 與主鍵盤的等價鍵折疊成同一個鍵（Ctrl+0 用數字鍵區也要通）。</summary>
    public static Key NormalizeKey(Key key) => key switch
    {
        Key.NumPad0 => Key.D0,
        Key.Add => Key.OemPlus,
        Key.Subtract => Key.OemMinus,
        _ => key,
    };

    /// <summary>找出這組按鍵對應的指令 id（modifier 要完全相等）。</summary>
    public static string? Match(Key key, KeyModifiers modifiers)
    {
        key = NormalizeKey(key);
        foreach (var (id, gesture) in Current)
        {
            if (gesture != null && gesture.Key == key && gesture.KeyModifiers == modifiers)
                return id;
        }
        return null;
    }

    public static bool Matches(string id, Key key, KeyModifiers modifiers) =>
        Current.GetValueOrDefault(id) is { } g &&
        g.Key == NormalizeKey(key) && g.KeyModifiers == modifiers;

    /// <summary>
    /// 重新綁定（null = 清除）。同手勢的其他指令自動解除（回傳被解除的定義，供 UI 提示）。
    /// 覆寫寫進 AppSettings（呼叫端負責 Save）。
    /// </summary>
    public static ShortcutDef? SetGesture(string id, KeyGesture? gesture)
    {
        if (!Current.ContainsKey(id)) return null;

        ShortcutDef? displaced = null;
        if (gesture != null)
        {
            foreach (var def in Defs)
            {
                if (def.Id == id) continue;
                if (Current.GetValueOrDefault(def.Id) is { } g &&
                    g.Key == gesture.Key && g.KeyModifiers == gesture.KeyModifiers)
                {
                    displaced = def;
                    StoreOverride(def.Id, null);
                    break;
                }
            }
        }

        StoreOverride(id, gesture);
        Changed?.Invoke();
        return displaced;
    }

    private static void StoreOverride(string id, KeyGesture? gesture)
    {
        Current[id] = gesture;
        var def = Defs.First(d => d.Id == id);
        if (Equals(gesture?.ToString(), def.Default?.ToString()))
            AppSettings.Instance.Shortcuts.Remove(id); // 回到預設就不用記
        else
            AppSettings.Instance.Shortcuts[id] = gesture?.ToString() ?? "";
    }

    public static void ResetAll()
    {
        foreach (var def in Defs) Current[def.Id] = def.Default;
        AppSettings.Instance.Shortcuts.Clear();
        Changed?.Invoke();
    }
}
