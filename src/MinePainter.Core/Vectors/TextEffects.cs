using SkiaSharp;

namespace MinePainter.Core.Vectors;

/// <summary>
/// 兩色漸層（填色與外框共用）。<see cref="Angle"/> 與陰影同一套方向約定：
/// 0＝左→右、90＝上→下（順時針）。<see cref="Radial"/> 時角度無意義（由中心往外）。
/// </summary>
public sealed record TextGradient
{
    public SKColor Start { get; init; } = SKColors.White;
    public SKColor End { get; init; } = new(0x3A, 0x7B, 0xD5);
    public float Angle { get; init; } = 90f;
    public bool Radial { get; init; }

    /// <summary>
    /// 依文字區塊的外框產生著色器。傳入的矩形是「文字自己的座標空間」——
    /// 旋轉/ScaleX 已經在畫布矩陣上了，所以漸層會跟著文字一起轉、一起拉。
    /// </summary>
    public SKShader CreateShader(SKRect block)
    {
        SKColor[] colors = [Start, End];
        if (Radial)
        {
            var radius = Math.Max(1f, Math.Max(block.Width, block.Height) / 2f);
            return SKShader.CreateRadialGradient(
                new SKPoint(block.MidX, block.MidY), radius, colors, SKShaderTileMode.Clamp);
        }

        // 以區塊中心為軸，往角度方向投影出起訖點（對角方向也蓋得滿整個區塊）
        var rad = Angle * MathF.PI / 180f;
        var dx = MathF.Cos(rad);
        var dy = MathF.Sin(rad);
        var half = (MathF.Abs(dx) * block.Width + MathF.Abs(dy) * block.Height) / 2f;
        if (half < 0.5f) half = 0.5f;
        return SKShader.CreateLinearGradient(
            new SKPoint(block.MidX - dx * half, block.MidY - dy * half),
            new SKPoint(block.MidX + dx * half, block.MidY + dy * half),
            colors, SKShaderTileMode.Clamp);
    }
}

/// <summary>
/// 文字外框（Photoshop 的「筆畫」）。一律畫在字身之下、以兩倍寬描邊 ——
/// 內側那一半被字身蓋掉，效果等同 PS 的「位置：外部」。
/// </summary>
public sealed record TextStroke
{
    public SKColor Color { get; init; } = SKColors.White;

    /// <summary>外框寬度（px，指這一層在前一層外側可見的厚度）。</summary>
    public float Width { get; init; } = 3f;

    /// <summary>漸層外框（null＝用 <see cref="Color"/> 的純色）。</summary>
    public TextGradient? Gradient { get; init; }

    /// <summary>
    /// 再往外的下一層外框（null＝這是最外層）。多層外框＝PS 疊多個「筆畫」樣式：
    /// 每一層都包在前一層外面、各自有顏色／寬度／漸層。
    /// 用鏈結而不是清單，是為了讓 record 的值相等語意（Equals）直接涵蓋整條鏈 ——
    /// 「內容有沒有變」的判斷散在 undo 落地各處，清單的參考相等會在那裡出錯。
    /// </summary>
    public TextStroke? Outer { get; init; }

    /// <summary>最多幾層（UI 與讀檔都以此為上限）。</summary>
    public const int MaxLayers = 6;

    /// <summary>由內而外列出所有層（含自己）。</summary>
    public IEnumerable<TextStroke> Layers()
    {
        for (var s = this; s != null; s = s.Outer) yield return s;
    }

    /// <summary>所有層的寬度總和＝外框整體往外長出來的厚度。</summary>
    public float TotalWidth
    {
        get
        {
            var total = 0f;
            foreach (var s in Layers()) total += Math.Max(0f, s.Width);
            return total;
        }
    }

    /// <summary>由「內→外」的清單組回鏈（清單裡各項自己的 Outer 會被忽略）；空清單＝null。</summary>
    public static TextStroke? FromLayers(IReadOnlyList<TextStroke> layers)
    {
        TextStroke? chain = null;
        for (var i = Math.Min(layers.Count, MaxLayers) - 1; i >= 0; i--)
            chain = layers[i] with { Outer = chain };
        return chain;
    }
}

/// <summary>文字陰影（PS 的「陰影」）。</summary>
public sealed record TextShadow
{
    public SKColor Color { get; init; } = new(0, 0, 0, 160);

    /// <summary>陰影投射方向（度；0＝右、90＝下，順時針）。</summary>
    public float Angle { get; init; } = 45f;

    /// <summary>離字身的距離（px）。</summary>
    public float Distance { get; init; } = 6f;

    /// <summary>模糊半徑（px；0＝硬邊）。</summary>
    public float Blur { get; init; } = 6f;

    /// <summary>擴張（px）：模糊之前先把輪廓描粗，陰影因此更厚實（PS 的「展開」）。</summary>
    public float Spread { get; init; }
}

/// <summary>
/// 外光暈（PS 的「外光暈」）：字身外圈的一圈光。畫在所有東西的最底下、
/// 以「描粗 + 模糊」實作（<see cref="Spread"/> 決定實心的厚度、<see cref="Size"/> 決定暈開多遠）。
/// </summary>
public sealed record TextGlow
{
    public SKColor Color { get; init; } = new(0xFF, 0xD3, 0x4A, 210);

    /// <summary>暈開的半徑（px）。</summary>
    public float Size { get; init; } = 12f;

    /// <summary>模糊前先擴張的厚度（px）。</summary>
    public float Spread { get; init; } = 2f;
}

/// <summary>
/// 文字的「外觀」——<see cref="TextElement"/> 扣掉內容與擺放（Text／Position／Rotation）
/// 之後剩下的部分。這是樣式庫（preset）存的東西，也是新文字的預設樣式。
///
/// 分成兩半：
/// 　• 字型半（FontFamily／FontSize／FontWeight／B I U S／對齊）＝工具列在管的東西
/// 　• 外觀半（顏色、漸層、外框、陰影、光暈、字距、行高）＝進階文字設定視窗在管的東西
/// 進階視窗只送外觀半（<see cref="ApplyEffectsTo"/>），這樣調外框才不會把字級一起蓋掉。
/// </summary>
public sealed record TextStyle
{
    public string FontFamily { get; init; } = "Microsoft JhengHei";
    public float FontSize { get; init; } = 48f;
    public int FontWeight { get; init; } = 400;
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public bool Underline { get; init; }
    public bool Strikethrough { get; init; }
    public TextAlign Alignment { get; init; } = TextAlign.Left;

    public SKColor Color { get; init; } = SKColors.Black;
    public TextGradient? Gradient { get; init; }
    public TextStroke? Stroke { get; init; }
    public TextShadow? Shadow { get; init; }
    public TextGlow? Glow { get; init; }

    /// <summary>字距（px，可負）。</summary>
    public float LetterSpacing { get; init; }

    /// <summary>行高倍率（相對字級）。</summary>
    public float LineHeightScale { get; init; } = TextElement.DefaultLineHeightScale;

    public static TextStyle From(TextElement e) => new()
    {
        FontFamily = e.FontFamily,
        FontSize = e.FontSize,
        FontWeight = e.FontWeight,
        Bold = e.Bold,
        Italic = e.Italic,
        Underline = e.Underline,
        Strikethrough = e.Strikethrough,
        Alignment = e.Alignment,
        Color = e.Color,
        Gradient = e.Gradient,
        Stroke = e.Stroke,
        Shadow = e.Shadow,
        Glow = e.Glow,
        LetterSpacing = e.LetterSpacing,
        LineHeightScale = e.LineHeightScale,
    };

    /// <summary>整套套到既有元素上（保留內容與擺放）。</summary>
    public TextElement ApplyTo(TextElement e) => ApplyEffectsTo(e) with
    {
        FontFamily = FontFamily,
        FontSize = FontSize,
        BaseFontSize = null,
        FontWeight = FontWeight,
        Bold = Bold,
        Italic = Italic,
        Underline = Underline,
        Strikethrough = Strikethrough,
        Alignment = Alignment,
    };

    /// <summary>只套外觀半：字型／字級／粗斜體／對齊維持原樣。</summary>
    public TextElement ApplyEffectsTo(TextElement e) => e with
    {
        Color = Color,
        Gradient = Gradient,
        Stroke = Stroke,
        Shadow = Shadow,
        Glow = Glow,
        LetterSpacing = LetterSpacing,
        LineHeightScale = LineHeightScale,
    };
}
