using Avalonia.Input;

namespace MinePainter.App.Services;

/// <summary>
/// 可自訂的指令定義。每個指令有兩格手勢：主鍵與副鍵（DefaultAlt）——
/// 「Ctrl+Shift+Z 也是重做」「0 也是最適大小」這種本來寫死的別名就是靠副鍵表達的。
/// </summary>
public sealed record ShortcutDef(
    string Id, string Category, string Name, KeyGesture? Default, KeyGesture? DefaultAlt = null);

/// <summary>
/// 快捷鍵表：指令 id ↔ 手勢。預設值 = 原本硬編碼在 MainWindow.OnKeyDown 的那套
/// （抄 Pinta、與 paint.net 相容）；使用者覆寫存進 <see cref="AppSettings.Shortcuts"/>。
/// MainWindow 與 CanvasView 的按鍵處理都查這張表，改一處兩邊同步。
/// 每個指令有主鍵與副鍵兩格；滾輪手勢是另一張表（見 <see cref="WheelMap"/>）。
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
        new("file.copyImage", "檔案", "複製這張圖片", null),
        new("file.closeTab", "檔案", "關閉分頁", new KeyGesture(Key.W, KeyModifiers.Control)),

        new("edit.undo", "編輯", "復原", new KeyGesture(Key.Z, KeyModifiers.Control)),
        // 副鍵 Ctrl+Shift+Z：paint.net 的重做別名，本來寫死在按鍵處理裡
        new("edit.redo", "編輯", "重做", new KeyGesture(Key.Y, KeyModifiers.Control),
            new KeyGesture(Key.Z, KeyModifiers.Control | KeyModifiers.Shift)),
        new("edit.cut", "編輯", "剪下", new KeyGesture(Key.X, KeyModifiers.Control)),
        new("edit.copy", "編輯", "複製", new KeyGesture(Key.C, KeyModifiers.Control)),
        new("edit.paste", "編輯", "貼上", new KeyGesture(Key.V, KeyModifiers.Control)),
        new("edit.selectAll", "編輯", "全選", new KeyGesture(Key.A, KeyModifiers.Control)),
        new("edit.deselect", "編輯", "取消選取", new KeyGesture(Key.D, KeyModifiers.Control)),
        new("edit.invertSelection", "編輯", "反轉選取", new KeyGesture(Key.I, KeyModifiers.Control)),
        // 選中物件時同一組鍵＝刪除那個物件（情境判斷，見 CanvasView）
        new("edit.erase", "編輯", "清除選取範圍／刪除選中的物件", new KeyGesture(Key.Delete)),
        new("edit.fill", "編輯", "填滿選取範圍", new KeyGesture(Key.Back)),
        new("edit.commitEdit", "編輯", "套用（變形／浮動內容／鋼筆路徑轉選取）", new KeyGesture(Key.Enter)),
        new("edit.cancelEdit", "編輯", "取消（變形／浮動內容／路徑）", new KeyGesture(Key.Escape)),

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
        new("adjust.exposure", "調整", "曝光度", null),
        new("adjust.hueSaturation", "調整", "色相 / 飽和度", new KeyGesture(Key.U, KeyModifiers.Control)),
        new("adjust.invert", "調整", "負片效果", new KeyGesture(Key.I, KeyModifiers.Control | KeyModifiers.Shift)),
        new("adjust.levels", "調整", "色階", new KeyGesture(Key.L, KeyModifiers.Control)),
        new("adjust.posterize", "調整", "色調分離", new KeyGesture(Key.P, KeyModifiers.Control | KeyModifiers.Shift)),
        new("adjust.sepia", "調整", "懷舊", new KeyGesture(Key.E, KeyModifiers.Control | KeyModifiers.Shift)),
        new("adjust.temperatureTint", "調整", "色溫 / 色調", null),
        new("adjust.colorBalance", "調整", "色彩平衡", null),
        new("adjust.photoFilter", "調整", "相片濾鏡", null),
        new("adjust.channelMixer", "調整", "通道混合器", null),
        new("adjust.threshold", "調整", "臨界值", null),
        new("effect.repeat", "效果", "重複上次效果", new KeyGesture(Key.F, KeyModifiers.Control)),

        new("layer.add", "圖層", "新增圖層", new KeyGesture(Key.N, KeyModifiers.Control | KeyModifiers.Shift)),
        new("layer.duplicate", "圖層", "複製圖層", new KeyGesture(Key.D, KeyModifiers.Control | KeyModifiers.Shift)),
        new("layer.mergeDown", "圖層", "向下合併", new KeyGesture(Key.M, KeyModifiers.Control)),
        new("layer.flattenText", "圖層", "圖層文字平面化", null),
        new("layer.import", "圖層", "從檔案匯入圖層", null),
        new("layer.flipH", "圖層", "水平翻轉圖層", null),
        new("layer.flipV", "圖層", "垂直翻轉圖層", null),
        new("layer.properties", "圖層", "圖層屬性", new KeyGesture(Key.F4)),
        new("layer.removeBackground", "圖層", "AI 去背", null),
        new("layer.removeBackgroundLocal", "圖層", "演算去背", null),
        new("layer.transformFree", "圖層", "自由變形", new KeyGesture(Key.T, KeyModifiers.Control)),
        new("layer.transformPerspective", "圖層", "透視變形", null),
        new("layer.transformDistort", "圖層", "扭曲變形", null),

        new("gadget.youtubePreview", "小工具", "YouTube 縮圖預覽", null),

        new("view.zoomIn", "檢視", "放大", new KeyGesture(Key.OemPlus, KeyModifiers.Control)),
        new("view.zoomOut", "檢視", "縮小", new KeyGesture(Key.OemMinus, KeyModifiers.Control)),
        new("view.actualSize", "檢視", "實際大小", new KeyGesture(Key.D0, KeyModifiers.Control),
            new KeyGesture(Key.D1)),
        new("view.bestFit", "檢視", "最適大小", new KeyGesture(Key.B, KeyModifiers.Control),
            new KeyGesture(Key.D0)),
        // 按住型：壓著就是暫時切成「拖曳畫面」
        new("view.panHold", "檢視", "平移檢視（按住）", new KeyGesture(Key.Space)),

        new("nudge.left", "微調", "往左一格", new KeyGesture(Key.Left)),
        new("nudge.right", "微調", "往右一格", new KeyGesture(Key.Right)),
        new("nudge.up", "微調", "往上一格", new KeyGesture(Key.Up)),
        new("nudge.down", "微調", "往下一格", new KeyGesture(Key.Down)),

        new("tool.brush", "工具", "筆刷", new KeyGesture(Key.B)),
        new("tool.pencil", "工具", "鉛筆", new KeyGesture(Key.N)),
        new("tool.eraser", "工具", "橡皮擦", new KeyGesture(Key.E)),
        new("tool.bgeraser", "工具", "去背筆", new KeyGesture(Key.E, KeyModifiers.Shift)),
        new("tool.eyedropper", "工具", "滴管", new KeyGesture(Key.I)),
        new("tool.move", "工具", "移動", new KeyGesture(Key.M)),
        new("tool.rectselect", "工具", "矩形選取", new KeyGesture(Key.S)),
        new("tool.ellipseselect", "工具", "橢圓選取", new KeyGesture(Key.C)),
        new("tool.lasso", "工具", "套索選取", new KeyGesture(Key.L)),
        new("tool.wand", "工具", "魔術棒", new KeyGesture(Key.W)),
        new("tool.fill", "工具", "油漆桶", new KeyGesture(Key.F)),
        new("tool.text", "工具", "文字", new KeyGesture(Key.T)),
        new("tool.shape", "工具", "形狀", new KeyGesture(Key.O)),
        new("tool.line", "工具", "直線", new KeyGesture(Key.U)),
        new("tool.pen", "工具", "鋼筆", new KeyGesture(Key.P)),
        new("pen.removeLastPoint", "工具", "鋼筆：退回上一個錨點", new KeyGesture(Key.Back)),

        // 按住型：壓著進入對齊模式（移動框時吸附畫布四邊與中線），放開即退出
        new("tool.alignHold", "工具", "對齊模式（按住）", new KeyGesture(Key.Tab)),
    ];

    /// <summary>手勢的格數：0 = 主鍵、1 = 副鍵。</summary>
    public const int Slots = 2;

    /// <summary>副鍵在 settings.json 裡的鍵字尾（主鍵就是 id 本身，舊設定檔照樣讀得進來）。</summary>
    private const string AltSuffix = "#alt";

    private static readonly Dictionary<string, KeyGesture?[]> Current = new();

    /// <summary>快捷鍵表變更後發出（選單顯示文字要跟著換）。</summary>
    public static event Action? Changed;

    static ShortcutMap()
    {
        foreach (var def in Defs) Current[def.Id] = [def.Default, def.DefaultAlt];
        LoadOverrides();
    }

    private static void LoadOverrides()
    {
        foreach (var (key, text) in AppSettings.Instance.Shortcuts)
        {
            var slot = key.EndsWith(AltSuffix, StringComparison.Ordinal) ? 1 : 0;
            var id = slot == 1 ? key[..^AltSuffix.Length] : key;
            if (!Current.TryGetValue(id, out var gestures)) continue;
            if (string.IsNullOrEmpty(text))
            {
                gestures[slot] = null;
                continue;
            }
            try
            {
                gestures[slot] = KeyGesture.Parse(text);
            }
            catch
            {
                // 壞掉的覆寫忽略，維持預設
            }
        }
    }

    /// <summary>某一格的手勢（slot 0 = 主鍵、1 = 副鍵）。</summary>
    public static KeyGesture? GetGesture(string id, int slot = 0) =>
        Current.GetValueOrDefault(id) is { } g && slot >= 0 && slot < Slots ? g[slot] : null;

    /// <summary>NumPad 與主鍵盤的等價鍵折疊成同一個鍵（Ctrl+0 用數字鍵區也要通）。</summary>
    public static Key NormalizeKey(Key key) => key switch
    {
        Key.NumPad0 => Key.D0,
        Key.Add => Key.OemPlus,
        Key.Subtract => Key.OemMinus,
        _ => key,
    };

    /// <summary>
    /// 找出這組按鍵對應的指令 id（兩格都比；modifier 要完全相等）。
    ///
    /// 依 <see cref="Defs"/> 的宣告順序找，不是照字典的雜湊順序 —— 少數幾組鍵預設會被兩個指令
    /// 共用（Backspace＝填滿選取範圍，鋼筆進行中時＝退一個錨點），那種情境鍵在呼叫這裡之前
    /// 就先被攔掉了；走到這裡時要穩定地回同一個答案，不能每次執行不一樣。
    /// </summary>
    public static string? Match(Key key, KeyModifiers modifiers)
    {
        key = NormalizeKey(key);
        foreach (var def in Defs)
        {
            if (Current.GetValueOrDefault(def.Id) is not { } gestures) continue;
            foreach (var gesture in gestures)
            {
                if (gesture != null && gesture.Key == key && gesture.KeyModifiers == modifiers)
                    return def.Id;
            }
        }
        return null;
    }

    /// <summary>這組按鍵是這個指令嗎（主鍵或副鍵任一格命中就算）。</summary>
    public static bool Matches(string id, Key key, KeyModifiers modifiers)
    {
        if (Current.GetValueOrDefault(id) is not { } gestures) return false;
        key = NormalizeKey(key);
        foreach (var gesture in gestures)
        {
            if (gesture != null && gesture.Key == key && gesture.KeyModifiers == modifiers) return true;
        }
        return false;
    }

    /// <summary>
    /// 只比按鍵本身、不管修飾鍵。按住型的指令放開時要用這個 ——
    /// 按住期間修飾鍵可能已經變了，比完整手勢會漏掉 KeyUp。
    /// </summary>
    public static bool MatchesKey(string id, Key key)
    {
        if (Current.GetValueOrDefault(id) is not { } gestures) return false;
        key = NormalizeKey(key);
        foreach (var gesture in gestures)
        {
            if (gesture != null && gesture.Key == key) return true;
        }
        return false;
    }

    /// <summary>
    /// 重新綁定（null = 清除）。同手勢的其他指令自動解除（回傳被解除的定義，供 UI 提示）。
    /// 覆寫寫進 AppSettings（呼叫端負責 Save）。
    /// </summary>
    public static ShortcutDef? SetGesture(string id, int slot, KeyGesture? gesture)
    {
        if (!Current.ContainsKey(id) || slot < 0 || slot >= Slots) return null;

        ShortcutDef? displaced = null;
        if (gesture != null)
        {
            // 撞到別的指令（不管撞在它的哪一格）就把那一格解除：一組鍵只會做一件事
            foreach (var def in Defs)
            {
                if (def.Id == id) continue;
                if (Current.GetValueOrDefault(def.Id) is not { } g) continue;
                for (var i = 0; i < Slots; i++)
                {
                    if (g[i] is not { } other ||
                        other.Key != gesture.Key || other.KeyModifiers != gesture.KeyModifiers)
                    {
                        continue;
                    }
                    displaced = def;
                    StoreOverride(def.Id, i, null);
                    break;
                }
                if (displaced != null) break;
            }
        }

        StoreOverride(id, slot, gesture);
        Changed?.Invoke();
        return displaced;
    }

    private static void StoreOverride(string id, int slot, KeyGesture? gesture)
    {
        Current[id][slot] = gesture;
        var def = Defs.First(d => d.Id == id);
        var key = slot == 0 ? id : id + AltSuffix;
        var fallback = slot == 0 ? def.Default : def.DefaultAlt;
        if (Equals(gesture?.ToString(), fallback?.ToString()))
            AppSettings.Instance.Shortcuts.Remove(key); // 回到預設就不用記
        else
            AppSettings.Instance.Shortcuts[key] = gesture?.ToString() ?? "";
    }

    public static void ResetAll()
    {
        foreach (var def in Defs) Current[def.Id] = [def.Default, def.DefaultAlt];
        AppSettings.Instance.Shortcuts.Clear();
        Changed?.Invoke();
    }
}
