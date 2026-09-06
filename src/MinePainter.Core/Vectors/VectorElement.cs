using SkiaSharp;

namespace MinePainter.Core.Vectors;
/// <summary>
/// 向量物件基底：不可變 record —— 編輯 = 以 with 產生新實例替換，undo 換參考。
/// 座標一律為 doc 空間。
/// </summary>
public abstract record VectorElement
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>粗略邊界（含描邊/行高/效果的保守外擴），只給失效與重繪用。</summary>
    public abstract SKRectI Bounds { get; }

    /// <summary>
    /// 「使用者看到的框」：把手框、命中、對齊吸附都用這個。
    /// 與 <see cref="Bounds"/> 分開 —— 失效範圍必須保守（少算會拖殘影），
    /// 但顯示的框必須貼著實際內容（多算會讓對齊完全不準）。
    /// </summary>
    public virtual SKRect FrameBounds
    {
        get
        {
            var b = Bounds;
            return new SKRect(b.Left, b.Top, b.Right, b.Bottom);
        }
    }

    public abstract void Render(SKCanvas canvas);

    public virtual bool HitTest(SKPoint p) => Bounds.Contains((int)p.X, (int)p.Y);

    /// <summary>平移後的新實例。</summary>
    public abstract VectorElement Translated(float dx, float dy);

    /// <summary>
    /// 整體變形（移動工具的變形框）：<paramref name="matrix"/> 是 doc 空間的完整映射，
    /// <paramref name="sx"/>/<paramref name="sy"/>/<paramref name="rotationDeg"/> 是它的分解
    /// （軸對齊縮放在前、旋轉在後），供文字這類「以參數表達外形」的元素套用。
    /// </summary>
    public abstract VectorElement TransformedBy(SKMatrix matrix, float sx, float sy, float rotationDeg);
}

/// <summary>多行文字的水平對齊（在區塊寬度＝最寬行之內對齊）。</summary>
public enum TextAlign
{
    Left,
    Center,
    Right,
}

/// <summary>
/// 文字的非仿射變形（透視／彎曲），套在 Position／Rotation／ScaleX 之後（doc → doc）：
/// 先 <see cref="Projective"/>（單應矩陣，可含透視）、再 <see cref="Warp"/>（貝茲網格，null＝無）。
/// 文字本身的排版參數完全不動 —— 改字之後照樣套同一套變形，文字永遠可編輯（使用者明示）。
