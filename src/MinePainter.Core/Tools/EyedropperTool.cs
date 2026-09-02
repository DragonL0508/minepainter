using SkiaSharp;

namespace MinePainter.Core.Tools;

/// <summary>滴管：從合成結果取色設為前景色。</summary>
public sealed class EyedropperTool : ITool
{
    public string Name => "滴管";

    public void OnPointerDown(ToolPointerEvent e, EditorSession session) => Sample(e, session);

    public void OnPointerMove(ToolPointerEvent e, EditorSession session) => Sample(e, session);

    public void OnPointerUp(ToolPointerEvent e, EditorSession session)
    {
    }

    private static void Sample(ToolPointerEvent e, EditorSession session)
    {
        var x = (int)e.DocPosition.X;
        var y = (int)e.DocPosition.Y;
        if (x < 0 || y < 0 || x >= session.Document.Width || y >= session.Document.Height) return;

        var color = session.SampleComposite(x, y); // 含浮動內容（合成快取裡沒有它）
        if (color.Alpha > 0)
            session.Foreground = color.WithAlpha(255);
    }
}
