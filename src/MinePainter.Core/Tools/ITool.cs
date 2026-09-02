using SkiaSharp;

namespace MinePainter.Core.Tools;

[Flags]
public enum ToolModifiers
{
    None = 0,
    Shift = 1,
    Ctrl = 2,
    Alt = 4,
}

/// <summary>
/// UI 框架無關的 pointer 事件（座標已轉為 doc 空間）。
/// ClickCount 為此次按下的連擊數（1 = 單擊、2 = 雙擊），move/up 沿用該次按下的值。
/// </summary>
public readonly record struct ToolPointerEvent(
    SKPoint DocPosition,
    float Pressure,
    ToolModifiers Modifiers = ToolModifiers.None,
    int ClickCount = 1);

/// <summary>工具進行中的幾何預覽（doc 座標折線；immutable，render thread 直接讀）。</summary>
/// <summary>
/// 工具幾何預覽：Element 非 null 時用元素本身的渲染畫（形狀工具的所見即所得預覽），
/// 否則畫 Points 折線（選取框／套索軌跡）。
/// </summary>
public sealed record OverlayPreview(IReadOnlyList<SKPoint> Points, bool Closed, Vectors.VectorElement? Element = null);

/// <summary>
/// 工具：接收 doc 空間的 pointer 事件流，透過 EditorSession 改動文件。
/// 實作不持 UI 依賴，可 headless 測試。
/// </summary>
public interface ITool
{
    string Name { get; }

    void OnPointerDown(ToolPointerEvent e, EditorSession session);
    void OnPointerMove(ToolPointerEvent e, EditorSession session);
    void OnPointerUp(ToolPointerEvent e, EditorSession session);
}
