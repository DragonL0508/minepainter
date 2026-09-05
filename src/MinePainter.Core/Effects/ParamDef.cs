namespace MinePainter.Core.Effects;

/// <summary>
/// 參數描述：讓 UI 不必認識每一種調整／效果就能長出對話框。
/// 調整與效果都是不可變 record，改參數 = <c>With</c> 回傳新實例（undo 只需換參考）。
/// </summary>
public abstract record ParamDef(string Key, string Label);

/// <summary>滑桿底部的視覺軌（提示數值意義）。</summary>
public enum SliderTrack
{
    None,
    /// <summary>色相環（-180..180°）。</summary>
    Hue,
    /// <summary>黑→白（色階的輸入/輸出點）。</summary>
    Gray,
    /// <summary>暗→亮（亮度／明度類）。</summary>
    Brightness,
    /// <summary>冷（藍）→暖（琥珀），白平衡的色溫。</summary>
    Temperature,
    /// <summary>綠→洋紅，白平衡的色調。</summary>
    Tint,
}

/// <summary>數值滑桿。Decimals = 顯示小數位（0 = 整數）；IsSeed = 亂數種子（UI 加「重新產生」骰子）。</summary>
public sealed record SliderParam(
    string Key, string Label, double Min, double Max,
    Func<object, double> Get, Func<object, double, object> With,
    string Suffix = "", int Decimals = 0) : ParamDef(Key, Label)
{
    public SliderTrack Track { get; init; } = SliderTrack.None;
    public bool IsSeed { get; init; }

    /// <summary>
    /// 這個值是「像素長度」（外框寬度、模糊半徑、陰影距離…）。
    /// 整份文件縮放時要跟著縮，不然快速模式輸出成 4K 之後，外框還是 1080p 時的粗細
    /// （見 Documents.OutputRender）。
    /// </summary>
    public bool Geometric { get; init; }
}

/// <summary>角度（度）：UI 用轉盤 + 數值。</summary>
public sealed record AngleParam(
    string Key, string Label, double Min, double Max,
    Func<object, double> Get, Func<object, double, object> With) : ParamDef(Key, Label);

/// <summary>
/// 二維點（正規化 -1..1，0 = 範圍中心）：UI 用「縮圖上拖曳十字」的選點器。
/// 典型用途是各種以中心為準的效果（放射狀模糊、扭轉、暈影…）。
/// </summary>
public sealed record PointParam(
    string Key, string Label,
    Func<object, (float X, float Y)> Get, Func<object, (float X, float Y), object> With) : ParamDef(Key, Label)
{
    /// <summary>
    /// 圍著中心點的範圍導引（聚焦的清楚範圍與過渡帶、暈影的半徑）：選點器把它畫在縮圖上，
    /// 使用者調滑桿時看得到範圍跟著變，不用每次都等預覽。沒有＝只畫十字。
    /// </summary>
    public Func<object, PointGuide?>? Guide { get; init; }

    /// <summary>使用者在選點器上直接拖曳導引圓時回寫參數（沒有＝導引只能看不能拖）。</summary>
    public Func<object, PointGuide, object>? WithGuide { get; init; }
}

/// <summary>
/// 選點器上的範圍導引：兩個以中心為圓心的圓，半徑都是「範圍半對角線」的倍率（與暈影、聚焦的尺度一致）。
/// Inner = 完全不受影響的範圍（實線），Outer = 效果達到最大的位置（虛線）；只有一圈的效果兩個給一樣。
/// Elliptical = 圓跟著範圍長寬比拉成橢圓；Invert = 效果在圓內而不是圓外（畫法反過來提示）。
/// </summary>
public sealed record PointGuide(float Inner, float Outer, bool Elliptical = false, bool Invert = false);

/// <summary>顏色。UsePrimaryByDefault = 從選單新增時先帶入目前主色。</summary>
public sealed record ColorParam(
    string Key, string Label,
    Func<object, SkiaSharp.SKColor> Get, Func<object, SkiaSharp.SKColor, object> With) : ParamDef(Key, Label)
{
    public bool UsePrimaryByDefault { get; init; }
}

/// <summary>
/// 多節點漸層：UI 用漸層條＋節點標記編輯。LegacyStartKey／LegacyEndKey = 舊檔的兩色鍵
/// （沒有節點鍵時用這兩個鍵拼成兩節點漸層）。
/// </summary>
public sealed record GradientParam(
    string Key, string Label,
    Func<object, GradientStops> Get, Func<object, GradientStops, object> With) : ParamDef(Key, Label)
{
    public string? LegacyStartKey { get; init; }
    public string? LegacyEndKey { get; init; }
}

/// <summary>
/// 檔案（LUT 的 .cube）：UI 是「目前檔名＋瀏覽按鈕」。Get 回目前的名字（沒有＝空字串），
/// With 收路徑、由參數物件自己讀檔（格式錯丟 InvalidDataException，UI 接成 toast）。
/// 不進存檔 —— 讀進來的內容由 <c>SaveData</c> 之類的機制另外存。
/// </summary>
public sealed record FileParam(
    string Key, string Label, string[] Patterns,
    Func<object, string> Get, Func<object, string, object> With) : ParamDef(Key, Label);

/// <summary>核取方塊。</summary>
public sealed record BoolParam(
    string Key, string Label,
    Func<object, bool> Get, Func<object, bool, object> With) : ParamDef(Key, Label);

/// <summary>下拉選單（Options 為顯示文字，值為索引）。</summary>
public sealed record ChoiceParam(
    string Key, string Label, string[] Options,
    Func<object, int> Get, Func<object, int, object> With) : ParamDef(Key, Label);

/// <summary>
/// 曲線編輯器（曲線調整專用）。Channels = 各通道名稱；Get/With 操作的是控制點陣列
/// （每通道一組 0..1 的點，至少兩點，依 X 排序）。
/// </summary>
public sealed record CurvesParam(
    string Key, string Label, string[] Channels,
    Func<object, IReadOnlyList<IReadOnlyList<(float X, float Y)>>> Get,
    Func<object, IReadOnlyList<IReadOnlyList<(float X, float Y)>>, object> With) : ParamDef(Key, Label);

/// <summary>有參數描述的物件（調整／效果共用）。</summary>
public interface IParameterized
{
    IReadOnlyList<ParamDef> Parameters { get; }
}
