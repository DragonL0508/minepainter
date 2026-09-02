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
}

/// <summary>數值滑桿。Decimals = 顯示小數位（0 = 整數）；IsSeed = 亂數種子（UI 加「重新產生」骰子）。</summary>
public sealed record SliderParam(
    string Key, string Label, double Min, double Max,
    Func<object, double> Get, Func<object, double, object> With,
    string Suffix = "", int Decimals = 0) : ParamDef(Key, Label)
{
    public SliderTrack Track { get; init; } = SliderTrack.None;
    public bool IsSeed { get; init; }
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
    Func<object, (float X, float Y)> Get, Func<object, (float X, float Y), object> With) : ParamDef(Key, Label);

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
