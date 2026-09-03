using System.Text.Json;

namespace MinePainter.App.Services;

/// <summary>
/// 使用者設定（主題／畫布背景圖／快捷鍵覆寫），存在
/// %APPDATA%\MinePainter\settings.json。載入失敗一律用預設值（不擋啟動）。
/// </summary>
public sealed class AppSettings
{
    public string Theme { get; set; } = "dark";

    /// <summary>畫布外圍背景圖的路徑（null = 無）。</summary>
    public string? BackdropPath { get; set; }

    /// <summary>背景圖不透明度（%），預設 10。</summary>
    public int BackdropOpacity { get; set; } = 10;

    /// <summary>快捷鍵覆寫：指令 id → 手勢字串（"" = 已清除）。沒列的用預設值。</summary>
    public Dictionary<string, string> Shortcuts { get; set; } = new();

    /// <summary>調色盤「最近使用」（RRGGBB，最新在前）。</summary>
    public List<string> RecentColors { get; set; } = new();

    /// <summary>效果／調整套用時記錄在圖層的效果堆疊（非破壞性），而不是直接改像素。</summary>
    public bool NonDestructiveEffects { get; set; } = true;

    /// <summary>檢視 → 放大時平滑取樣（預設關：放大顯示真實像素，同 paint.net）。</summary>
    public bool SmoothZoom { get; set; }

    /// <summary>啟動時靜默檢查 GitHub 有沒有新版（開發建置不檢查）。</summary>
    public bool CheckUpdatesOnStartup { get; set; } = true;

    /// <summary>使用者按「略過此版」的版本 tag；同一版不再提醒（手動檢查仍會顯示）。</summary>
    public string? SkippedUpdateTag { get; set; }

    /// <summary>每種效果上次確定套用的參數（效果 id → 參數字典），下次開同一個效果沿用。</summary>
    public Dictionary<string, Dictionary<string, string>> EffectParams { get; set; } = new();

    /// <summary>移動工具拖曳時覆疊層帶著效果堆疊結果（外框／陰影／漸層跟著走）；關掉省效能。</summary>
    public bool RenderEffectsWhileDragging { get; set; } = true;

    /// <summary>啟動音效（啟動畫面出現／載入完成／主視窗現身）。</summary>
    public bool StartupSounds { get; set; } = true;

    /// <summary>
    /// 第一次執行時自動安裝到 %LocalAppData%\Programs\MinePainter（檔案關聯需要一個
    /// 不會被搬走的落點）。想純綠色使用可以在 settings.json 把它關掉。
    /// </summary>
    public bool AutoInstall { get; set; } = true;

    /// <summary>已經自動登記過檔案關聯（只做一次；之後使用者自己清掉就不再自動塞回去）。</summary>
    public bool FileAssociationsRegistered { get; set; }

    /// <summary>使用者在「檔案關聯」按過「全部移除」；啟動時不再自動登記。</summary>
    public bool FileAssociationsOptOut { get; set; }

    /// <summary>主視窗啟動時最大化（預設是）。</summary>
    public bool WindowMaximized { get; set; } = true;

    /// <summary>浮動面板的位置／大小／開關（key = 面板 id）。空的＝用內建預設排法。</summary>
    public Dictionary<string, PanelLayout> Panels { get; set; } = new();

    /// <summary>
    /// 一個浮動面板記住的東西：貼哪一組邊、與那條邊的距離（螢幕像素，相對主視窗工作區）、
    /// 大小，以及開關有沒有打開。與 <see cref="Controls.PanelAnchor"/> 一一對應。
    /// </summary>
    public sealed class PanelLayout
    {
        public bool Right { get; set; }
        public bool Bottom { get; set; }
        public int OffsetX { get; set; }
        public int OffsetY { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool Visible { get; set; } = true;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string FilePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MinePainter", "settings.json");

    private static AppSettings? _instance;

    public static AppSettings Instance => _instance ??= Load();

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath))
                    ?? new AppSettings();
            }
        }
        catch
        {
            // 壞掉的設定檔不該擋啟動，直接用預設值
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // 存不進去（權限/磁碟）就算了，下次再試
        }
    }
}
