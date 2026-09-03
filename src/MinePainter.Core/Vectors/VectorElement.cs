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
/// </summary>
public sealed record TextDeform(SKMatrix Projective, Tools.WarpMesh? Warp)
{
    public static readonly TextDeform None = new(SKMatrix.Identity, null);

    public bool IsIdentity => Warp == null && IsIdentityMatrix(Projective);

    private static bool IsIdentityMatrix(SKMatrix m) =>
        Math.Abs(m.ScaleX - 1) < 1e-5f && Math.Abs(m.ScaleY - 1) < 1e-5f &&
        Math.Abs(m.SkewX) < 1e-5f && Math.Abs(m.SkewY) < 1e-5f &&
        Math.Abs(m.TransX) < 1e-3f && Math.Abs(m.TransY) < 1e-3f &&
        Math.Abs(m.Persp0) < 1e-9f && Math.Abs(m.Persp1) < 1e-9f && Math.Abs(m.Persp2 - 1) < 1e-5f;

    public SKPoint MapPoint(SKPoint p)
    {
        var q = Projective.MapPoint(p);
        return Warp?.MapPoint(q) ?? q;
    }

    /// <summary>矩形經整套變形後的外接矩形。</summary>
    public SKRect MapBounds(SKRect r)
    {
        var q = Projective.MapRect(r);
        return Warp?.MapBounds(q) ?? q;
    }

    /// <summary>輸入端平移 d（文字搬家）：輸出也跟著平移。</summary>
    public TextDeform Translated(float dx, float dy)
    {
        var t = SKMatrix.CreateTranslation(dx, dy);
        var p = SKMatrix.Concat(t, SKMatrix.Concat(Projective, SKMatrix.CreateTranslation(-dx, -dy)));
        return new TextDeform(p, Warp?.TranslatedWithFrame(dx, dy));
    }

    /// <summary>輸出端再套一個矩陣（仿射精確；透視在有網格時是控制點近似）。</summary>
    public TextDeform Then(SKMatrix m) => Warp == null
        ? new TextDeform(SKMatrix.Concat(m, Projective), null)
        : new TextDeform(Projective, Warp.Transformed(m));

    /// <summary>輸出端再套一張網格。</summary>
    public TextDeform Then(Tools.WarpMesh mesh) => Warp == null
        ? new TextDeform(Projective, mesh)
        : new TextDeform(Projective, Warp.Then(mesh));

    public bool Equals(TextDeform? other) =>
        other != null && Projective == other.Projective &&
        (Warp == null ? other.Warp == null
            : other.Warp != null && Warp.Frame == other.Warp.Frame &&
              Tools.QuadGeometry.NearlyEqual(Warp.Points, other.Warp.Points, 0f));

    public override int GetHashCode() => Projective.GetHashCode();
}

public sealed record TextElement : VectorElement
{
    public string Text { get; init; } = "";

    /// <summary>透視／彎曲變形（null＝無）；套在排版之後，改字不影響。</summary>
    public TextDeform? Deform { get; init; }

    private bool HasDeform => Deform is { IsIdentity: false };
    public string FontFamily { get; init; } = "Microsoft JhengHei";
    public float FontSize { get; init; } = 48f;
    public SKColor Color { get; init; } = SKColors.Black;

    /// <summary>字重（100–1000；400 = Regular）。家族的命名變種（Black/Light…）就是選不同字重。</summary>
    public int FontWeight { get; init; } = 400;

    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public bool Underline { get; init; }
    public bool Strikethrough { get; init; }
    public TextAlign Alignment { get; init; } = TextAlign.Left;

    /// <summary>第一行文字的左上角。</summary>
    public SKPoint Position { get; init; }

    /// <summary>水平額外縮放（1 = 原比例）；讓文字能被自由拉寬拉窄。</summary>
    public float ScaleX { get; init; } = 1f;

    /// <summary>
    /// 被把手／變形框縮放前的原始字級（null = 沒被縮放過）。
    /// 「重設角度與比例」會把字級退回這裡；工具列直接改字級則視為新的原始值（清掉）。
    /// </summary>
    public float? BaseFontSize { get; init; }

    /// <summary>使用者明確指定字級（工具列／樣式）：成為新的原始值。</summary>
    public TextElement WithFontSize(float size) => this with { FontSize = Math.Max(1f, size), BaseFontSize = null };

    /// <summary>順時針旋轉角度（度），以 <see cref="Position"/> 為軸心。</summary>
    public float Rotation { get; init; }

    /// <summary>外框（null = 無）。</summary>
    public TextStroke? Stroke { get; init; }

    /// <summary>陰影（null = 無）。</summary>
    public TextShadow? Shadow { get; init; }

    /// <summary>外光暈（null = 無）。</summary>
    public TextGlow? Glow { get; init; }

    /// <summary>字身漸層（null = 用 <see cref="Color"/> 的純色）。</summary>
    public TextGradient? Gradient { get; init; }

    /// <summary>字距（px，可負）：每個字之後多／少留的水平距離。</summary>
    public float LetterSpacing { get; init; }

    public const float DefaultLineHeightScale = 1.25f;

    /// <summary>行高倍率（相對字級）。</summary>
    public float LineHeightScale { get; init; } = DefaultLineHeightScale;

    public float LineHeight => FontSize * LineHeightScale;

    /// <summary>未套 ScaleX 的原始排版寬度。</summary>
    public float UnscaledWidth
    {
        get
        {
            if (Text.Length == 0) return 0;
            using var shaper = CreateShaper();
            var maxWidth = 0f;
            foreach (var line in Text.Split('\n'))
                maxWidth = Math.Max(maxWidth, shaper.MeasureLine(line));
            return maxWidth;
        }
    }

    /// <summary>未旋轉時、相對 <see cref="Position"/> 的外框。</summary>
    private SKRect LocalBounds
    {
        get
        {
            var lineCount = Text.Split('\n').Length;
            // 合成斜體以 SkewX 把字面往右上斜，右緣保守外擴；
            // 末行的降部/底線可能超出 LineHeight 區塊，底緣也外擴 0.25em
            var italicPad = Italic ? FontSize * 0.35f : 0f;
            return SKRect.Create(-2, -2,
                UnscaledWidth * ScaleX + italicPad + 4,
                lineCount * LineHeight + FontSize * 0.25f + 4);
        }
    }

    /// <summary>外框／陰影／光暈往外長出來的量（對稱取最大值，保守）。</summary>
    private float EffectPad
    {
        get
        {
            var stroke = Stroke?.TotalWidth ?? 0f;
            var shadow = Shadow is { } s ? s.Distance + s.Spread + s.Blur * 1.5f : 0f;
            var glow = Glow is { } g ? g.Spread + g.Size * 1.5f : 0f;
            return stroke + Math.Max(shadow, glow);
        }
    }

    public override SKRectI Bounds
    {
        get
        {
            var doc = MapLocalToDoc(PaddedLocalBounds);
            if (!HasDeform) return SKRectI.Ceiling(doc);
            // 彎曲網格在框外是貝茲外插（三次成長），把含效果外擴的大框整個送進去會爆成離譜的範圍；
            // 只映射排版框，效果外擴在變形後再往外加（效果本來就是在算繪結果上長出去的）
            var deformed = Deform!.MapBounds(MapLocalToDoc(LocalBounds));
            var pad = EffectPad + 2;
            deformed.Inflate(pad, pad);
            return SKRectI.Ceiling(deformed);
        }
    }

    /// <summary>沒有變形時的 doc 外框（透視／彎曲前；離線算繪文字用）。</summary>
    internal SKRect UndeformedPaddedBounds => MapLocalToDoc(PaddedLocalBounds);

    /// <summary>套用透視／彎曲前的框（把手／對齊用的「使用者看到的框」，變形前版本）。</summary>
    public SKRect UndeformedFrameBounds => MapLocalToDoc(MeasureInkBounds() ?? LocalBounds);

    /// <summary>輸出端再疊一個 doc→doc 單應矩陣（變形框的透視）。</summary>
    public TextElement Deformed(SKMatrix h) => this with { Deform = (Deform ?? TextDeform.None).Then(h) };

    /// <summary>輸出端再疊一張彎曲網格。</summary>
    public TextElement Warped(Tools.WarpMesh mesh) => this with { Deform = (Deform ?? TextDeform.None).Then(mesh) };

    public TextElement WithoutDeform() => Deform == null ? this : this with { Deform = null };

    private SKRect PaddedLocalBounds
    {
        get
        {
            var local = LocalBounds;
            var pad = EffectPad;
            if (pad > 0) local.Inflate(pad, pad);
            return local;
        }
    }

    /// <summary>
    /// 使用者看到的框＝實際著墨範圍（逐行量測 ink bounds，含底線/刪除線），
    /// 不含效果外擴與保守 padding —— 把手貼著字，對齊吸附才有意義。
    /// 空字串／全空白退回排版框（LocalBounds），編輯中還是有框可看。
    /// </summary>
    public override SKRect FrameBounds
    {
        get
        {
            var ink = MeasureInkBounds();
            var doc = MapLocalToDoc(ink ?? LocalBounds);
            return HasDeform ? Deform!.MapBounds(doc) : doc;
        }
    }

    /// <summary>逐行量測著墨範圍（未旋轉、已含 ScaleX 的本地座標）；沒有墨（空白）回傳 null。</summary>
    private SKRect? MeasureInkBounds()
    {
        if (Text.Length == 0) return null;
        using var shaper = CreateShaper();

        var lines = Text.Split('\n');
        var widths = new float[lines.Length];
        var maxWidth = 0f;
        for (var i = 0; i < lines.Length; i++)
        {
            widths[i] = shaper.MeasureLine(lines[i]);
            maxWidth = Math.Max(maxWidth, widths[i]);
        }

        var metrics = shaper.PrimaryFont.Metrics;
        var baseline = -metrics.Ascent;
        var underlineY = metrics.UnderlinePosition ?? FontSize * 0.1f;
        var strikeY = metrics.StrikeoutPosition ?? -FontSize * 0.3f;
        var lineThickness = Math.Max(1f,
            metrics.UnderlineThickness ?? metrics.StrikeoutThickness ?? FontSize / 18f);

        SKRect? acc = null;
        void Add(SKRect r)
        {
            acc = acc is { } a
                ? new SKRect(Math.Min(a.Left, r.Left), Math.Min(a.Top, r.Top),
                    Math.Max(a.Right, r.Right), Math.Max(a.Bottom, r.Bottom))
                : r;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var x = Alignment switch
            {
                TextAlign.Center => (maxWidth - widths[i]) / 2,
                TextAlign.Right => maxWidth - widths[i],
                _ => 0f,
            };
            var ink = shaper.MeasureLineInk(lines[i]);
            if (ink is { } r)
            {
                r.Offset(x, baseline);
                Add(r);
            }
            if (widths[i] > 0)
            {
                if (Underline) Add(SKRect.Create(x, baseline + underlineY, widths[i], lineThickness));
                if (Strikethrough) Add(SKRect.Create(x, baseline + strikeY, widths[i], lineThickness));
            }
            baseline += LineHeight;
        }

        if (acc is not { } result) return null;
        // 繪製時整體套 canvas.Scale(ScaleX, 1)，量測結果的 x 也要跟著放
        return new SKRect(result.Left * ScaleX, result.Top, result.Right * ScaleX, result.Bottom);
    }

    /// <summary>本地矩形 → doc 座標（含 Position 平移與旋轉後的軸對齊外接矩形）。</summary>
    private SKRect MapLocalToDoc(SKRect local)
    {
        if (Math.Abs(Rotation) < 0.01f)
        {
            return SKRect.Create(
                Position.X + local.Left, Position.Y + local.Top, local.Width, local.Height);
        }
        var m = SKMatrix.CreateRotationDegrees(Rotation);
        Span<SKPoint> corners =
        [
            m.MapPoint(local.Left, local.Top), m.MapPoint(local.Right, local.Top),
            m.MapPoint(local.Right, local.Bottom), m.MapPoint(local.Left, local.Bottom),
        ];
        float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, b = float.MinValue;
        foreach (var c in corners)
        {
            l = Math.Min(l, c.X); t = Math.Min(t, c.Y);
            r = Math.Max(r, c.X); b = Math.Max(b, c.Y);
        }
        return new SKRect(Position.X + l, Position.Y + t, Position.X + r, Position.Y + b);
    }

    /// <summary>
    /// 命中測試用排版框（LocalBounds，不含效果外擴）—— 陰影/光暈不該吃掉點擊；
    /// 旋轉時把點反轉回未旋轉空間再測。
    /// </summary>
    public override bool HitTest(SKPoint p)
    {
        if (HasDeform)
        {
            // 透視：把點反轉回變形前；彎曲沒有閉式反函數，退回用變形後的框
            if (Deform!.Warp != null) return FrameBounds.Contains(p.X, p.Y);
            if (!Deform.Projective.TryInvert(out var inv)) return false;
            p = inv.MapPoint(p);
        }
        var local = Math.Abs(Rotation) < 0.01f
            ? new SKPoint(p.X - Position.X, p.Y - Position.Y)
            : SKMatrix.CreateRotationDegrees(-Rotation).MapPoint(p.X - Position.X, p.Y - Position.Y);
        return LocalBounds.Contains(local.X, local.Y);
    }

    public override void Render(SKCanvas canvas)
    {
        if (string.IsNullOrEmpty(Text)) return;

        if (HasDeform)
        {
            var deform = Deform!;
            if (deform.Warp is { } warp)
            {
                // 彎曲：先把（透視後的）文字算到離線影像，再沿貝茲曲面貼上去
                var pre = deform.Projective.MapRect(UndeformedPaddedBounds);
                var src = SKRectI.Ceiling(pre);
                src.Inflate(1, 1);
                if (src.Width <= 0 || src.Height <= 0 || src.Width > 16384 || src.Height > 16384) return;
                using var surface = SKSurface.Create(new SKImageInfo(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
                if (surface == null) return;
                var c = surface.Canvas;
                c.Clear(SKColors.Transparent);
                c.Translate(-src.Left, -src.Top);
                var proj = deform.Projective;
                c.Concat(ref proj);
                RenderCore(c);
                c.Flush();
                using var image = surface.Snapshot();
                warp.Draw(canvas, image, src, SKMatrix.Identity, SKFilterQuality.High,
                    cover: new SKRect(src.Left, src.Top, src.Right, src.Bottom));
                return;
            }

            // 只有透視：直接把矩陣疊進 canvas（Skia 的文字在透視下改走路徑繪製）
            var m = deform.Projective;
            canvas.Save();
            canvas.Concat(ref m);
            RenderCore(canvas);
            canvas.Restore();
            return;
        }

        RenderCore(canvas);
    }

    private void RenderCore(SKCanvas canvas)
    {
        using var shaper = CreateShaper();

        var lines = Text.Split('\n');
        var widths = new float[lines.Length];
        var maxWidth = 0f;
        for (var i = 0; i < lines.Length; i++)
        {
            widths[i] = shaper.MeasureLine(lines[i]);
            maxWidth = Math.Max(maxWidth, widths[i]);
        }

        var metrics = shaper.PrimaryFont.Metrics;
        var firstBaseline = -metrics.Ascent; // Ascent 為負
        var underlineY = metrics.UnderlinePosition ?? FontSize * 0.1f;
        var strikeY = metrics.StrikeoutPosition ?? -FontSize * 0.3f;
        var lineThickness = Math.Max(1f,
            metrics.UnderlineThickness ?? metrics.StrikeoutThickness ?? FontSize / 18f);

        // 同一份排版要重畫好幾次（陰影／外框／字身），只換 paint
        void DrawPass(SKPaint paint)
        {
            var baseline = firstBaseline;
            for (var i = 0; i < lines.Length; i++)
            {
                var x = Alignment switch
                {
                    TextAlign.Center => (maxWidth - widths[i]) / 2,
                    TextAlign.Right => maxWidth - widths[i],
                    _ => 0f,
                };
                shaper.DrawLine(canvas, lines[i], x, baseline, paint);
                if (widths[i] > 0)
                {
                    if (Underline)
                        canvas.DrawRect(x, baseline + underlineY, widths[i], lineThickness, paint);
                    if (Strikethrough)
                        canvas.DrawRect(x, baseline + strikeY, widths[i], lineThickness, paint);
                }
                baseline += LineHeight;
            }
        }

        canvas.Save();
        canvas.Translate(Position.X, Position.Y);
        if (Math.Abs(Rotation) > 0.01f) canvas.RotateDegrees(Rotation);
        if (Math.Abs(ScaleX - 1f) > 0.001f) canvas.Scale(ScaleX, 1f);

        // 漸層的座標空間就是這裡（已套旋轉/ScaleX）—— 漸層因此跟著文字一起轉、一起拉
        var block = SKRect.Create(0, 0, Math.Max(1f, maxWidth), Math.Max(1f, lines.Length * LineHeight));

        // 光暈 → 陰影 → 外框 → 字身。外框以「兩倍寬描邊」畫在字身之下，內側那一半被字身蓋掉，
        // 效果等同 PS 的「位置：外部」（真正的外側描邊要取字型輪廓做布林運算，代價高很多）。
        // 多層外框：每層包在前一層外面 —— 由外而內、以「累積寬度 × 2」依序描邊，
        // 內層蓋住外層的內側，看起來就是一圈圈往外疊。
        var strokeLayers = Stroke?.Layers().Where(s => s.Width > 0.01f).ToList() ?? [];
        var strokeWidth = 0f;
        foreach (var s in strokeLayers) strokeWidth += s.Width;

        // 陰影／光暈都是「把整個可見輪廓（字身＋外框）描粗再模糊」，只差位移與參數
        void DrawSilhouette(SKColor color, float blurRadius, float grow, float dx, float dy)
        {
            using var blur = blurRadius > 0.01f
                ? SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blurRadius / 2f)
                : null;
            using var paint = new SKPaint
            {
                Color = color,
                IsAntialias = true,
                MaskFilter = blur,
                StrokeJoin = SKStrokeJoin.Round,
                StrokeCap = SKStrokeCap.Round,
            };
            canvas.Save();
            if (dx != 0 || dy != 0) canvas.Translate(dx, dy);
            var outline = strokeWidth + Math.Max(0f, grow);
            if (outline > 0.01f)
            {
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = outline * 2;
                DrawPass(paint);
                paint.Style = SKPaintStyle.Fill;
            }
            DrawPass(paint);
            canvas.Restore();
        }

        if (Glow is { } glow && (glow.Size > 0.01f || glow.Spread > 0.01f))
            DrawSilhouette(glow.Color, glow.Size, glow.Spread, 0, 0);

        if (Shadow is { } shadow)
        {
            var rad = shadow.Angle * MathF.PI / 180f;
            DrawSilhouette(shadow.Color, shadow.Blur, shadow.Spread,
                MathF.Cos(rad) * shadow.Distance, MathF.Sin(rad) * shadow.Distance);
        }

        var cumulative = strokeWidth;
        for (var i = strokeLayers.Count - 1; i >= 0; i--)
        {
            var layer = strokeLayers[i];
            using var strokeShader = layer.Gradient?.CreateShader(block);
            using var strokePaint = new SKPaint
            {
                Color = layer.Color,
                Shader = strokeShader,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = cumulative * 2,
                StrokeJoin = SKStrokeJoin.Round,
                StrokeCap = SKStrokeCap.Round,
            };
            DrawPass(strokePaint);
            cumulative -= layer.Width;
        }

        using var fillShader = Gradient?.CreateShader(block);
        using var fillPaint = new SKPaint { Color = Color, Shader = fillShader, IsAntialias = true };
        DrawPass(fillPaint);
        canvas.Restore();
    }

    public override VectorElement Translated(float dx, float dy) =>
        this with
        {
            Position = new SKPoint(Position.X + dx, Position.Y + dy),
            Deform = Deform?.Translated(dx, dy),
        };

    /// <summary>
    /// 位置走完整矩陣；外形以參數表達 —— 垂直縮放進字級、水平比例差進 ScaleX、旋轉累加。
    /// （已旋轉的文字遇到非等比縮放時只能近似：縮放先於旋轉套用。）
    /// 已有透視／彎曲的文字：排版參數不動，矩陣疊在變形的輸出端（精確，且改字仍可）。
    /// </summary>
    public override VectorElement TransformedBy(SKMatrix matrix, float sx, float sy, float rotationDeg) =>
        HasDeform ? Deformed(matrix) :
        this with
        {
            Position = matrix.MapPoint(Position),
            BaseFontSize = Math.Abs(sy - 1f) > 0.0001f ? BaseFontSize ?? FontSize : BaseFontSize,
            FontSize = Math.Max(1f, FontSize * sy),
            ScaleX = sy > 0.0001f ? ScaleX * (sx / sy) : ScaleX,
            LetterSpacing = LetterSpacing * Math.Abs(sy), // 字距是排版的一部分，跟著字級縮放
            Rotation = NormalizeDegrees(Rotation + rotationDeg),
        };

    /// <summary>有沒有被轉過、拉歪或縮放過（角度≠0、ScaleX≠1、或字級離開原始值）。</summary>
    public bool IsTransformed =>
        HasDeform ||
        Math.Abs(Rotation) > 0.01f || Math.Abs(ScaleX - 1f) > 0.001f ||
        (BaseFontSize is { } b && Math.Abs(b - FontSize) > 0.01f);

    /// <summary>
    /// 轉回 0°、比例回到 1（字級不動），以使用者看到的框（<see cref="FrameBounds"/>）中心為軸 ——
    /// 文字留在原地轉正／縮回原比例，而不是繞著左上角甩出去。沒被動過就回傳自己。
    /// </summary>
    public TextElement WithTransformReset()
    {
        if (!IsTransformed) return this;
        var before = FrameBounds;
        var baseSize = BaseFontSize ?? FontSize;
        var ratio = FontSize > 0.01f ? baseSize / FontSize : 1f;
        var straight = this with
        {
            Deform = null, // 透視／彎曲也一起拿掉（回到最原始）
            Rotation = 0f,
            ScaleX = 1f,
            FontSize = Math.Max(1f, baseSize),
            LetterSpacing = LetterSpacing * ratio, // 字距跟著字級縮放，退回時一起退
            BaseFontSize = null,
        };
        var after = straight.FrameBounds;
        return straight with
        {
            Position = new SKPoint(
                Position.X + (before.MidX - after.MidX),
                Position.Y + (before.MidY - after.MidY)),
        };
    }

    private static float NormalizeDegrees(float deg)
    {
        deg %= 360f;
        if (deg > 180f) deg -= 360f;
        if (deg < -180f) deg += 360f;
        return deg;
    }

    /// <summary>B 鈕疊在字重之上：已是粗字重（如 Black 900）就維持原字重。</summary>
    private SKFontStyle CreateFontStyle() => new(
        Bold ? Math.Max((int)SKFontStyleWeight.Bold, FontWeight) : FontWeight,
        (int)SKFontStyleWidth.Normal,
        Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);

    private TextShaper CreateShaper() =>
        new(FontFamily, CreateFontStyle(), FontSize, Bold, Italic, LetterSpacing);

    /// <summary>
    /// 逐字字面後備的排版器：Skia 的 DrawText 不做字型後備，選到不含中文（或任何缺字）
    /// 的字型時會畫出 .notdef 豆腐框 —— 這裡把每行拆成「同字面的連續段」，缺字的段用
    /// <see cref="SKFontManager.MatchCharacter(string, SKFontStyle, string[], int)"/> 找系統後備字型。
    /// 量測與繪製共用同一套分段，寬度/對齊/底線因此一致。
    /// 家族缺粗體/斜體字面時（CJK 字型常見）以合成效果（Embolden/SkewX）後備。
    /// </summary>
    private sealed class TextShaper : IDisposable
    {
        private readonly SKTypeface _primary;
        private readonly SKFontStyle _style;
        private readonly float _size;
        private readonly bool _bold;
        private readonly bool _italic;
        private readonly float _letterSpacing;
        private readonly Dictionary<int, SKTypeface?> _fallbackByCodepoint = new();
        private readonly List<SKTypeface> _owned = new();
        private SKFont? _primaryFont;

        public TextShaper(string family, SKFontStyle style, float size, bool bold, bool italic,
            float letterSpacing = 0f)
        {
            _style = style;
            _size = size;
            _bold = bold;
            _italic = italic;
            _letterSpacing = letterSpacing;
            _primary = SKTypeface.FromFamilyName(family, style) ?? SKTypeface.Default;
            _owned.Add(_primary);
        }

        /// <summary>主字面的 font（基線/底線位置一律以它為準，後備段落畫在同一條基線上）。</summary>
        public SKFont PrimaryFont => _primaryFont ??= CreateFont(_primary);

        public float MeasureLine(string line)
        {
            var width = 0f;
            foreach (var (typeface, segment) in Runs(line))
                width += Measure(typeface, segment);
            // 字距是「每個字之後」多留的距離（含最後一個，與 CSS letter-spacing 同義），
            // 量測與繪製必須用同一條規則，對齊與底線長度才對得上
            if (Math.Abs(_letterSpacing) > 0.0001f && line.Length > 0)
                width += _letterSpacing * CountRunes(line);
            return width;
        }

        public void DrawLine(SKCanvas canvas, string line, float x, float baseline, SKPaint paint)
        {
            var spaced = Math.Abs(_letterSpacing) > 0.0001f;
            foreach (var (typeface, segment) in Runs(line))
            {
                using var font = CreateFont(typeface);
                if (!spaced)
                {
                    canvas.DrawText(segment, x, baseline, font, paint);
                    x += Measure(typeface, segment);
                    continue;
                }
                // 有字距就得逐字擺（放棄字間 kerning —— 手動調字距本來就是要蓋掉它）
                foreach (var rune in segment.EnumerateRunes())
                {
                    var glyph = rune.ToString();
                    canvas.DrawText(glyph, x, baseline, font, paint);
                    x += Measure(typeface, glyph) + _letterSpacing;
                }
            }
        }

        /// <summary>
        /// 一行的實際著墨範圍（相對「筆起點在 x=0、baseline 在 y=0」）；
        /// 分段規則與繪製完全相同，含字距的逐字位移。空白行回傳 null。
        /// </summary>
        public SKRect? MeasureLineInk(string line)
        {
            var spaced = Math.Abs(_letterSpacing) > 0.0001f;
            var x = 0f;
            SKRect? acc = null;
            void Add(SKRect r)
            {
                acc = acc is { } a
                    ? new SKRect(Math.Min(a.Left, r.Left), Math.Min(a.Top, r.Top),
                        Math.Max(a.Right, r.Right), Math.Max(a.Bottom, r.Bottom))
                    : r;
            }

            foreach (var (typeface, segment) in Runs(line))
            {
                using var measure = CreateMeasurePaint(typeface);
                if (!spaced)
                {
                    var ink = SKRect.Empty;
                    var advance = measure.MeasureText(segment, ref ink);
                    if (ink.Width > 0 && ink.Height > 0)
                    {
                        ink.Offset(x, 0);
                        Add(ink);
                    }
                    x += advance;
                    continue;
                }
                foreach (var rune in segment.EnumerateRunes())
                {
                    var glyph = rune.ToString();
                    var ink = SKRect.Empty;
                    var advance = measure.MeasureText(glyph, ref ink);
                    if (ink.Width > 0 && ink.Height > 0)
                    {
                        ink.Offset(x, 0);
                        Add(ink);
                    }
                    x += advance + _letterSpacing;
                }
            }
            return acc;
        }

        private static int CountRunes(string line)
        {
            var n = 0;
            foreach (var _ in line.EnumerateRunes()) n++;
            return n;
        }

        private IEnumerable<(SKTypeface Typeface, string Segment)> Runs(string line)
        {
            if (line.Length == 0) yield break;
            var segment = new System.Text.StringBuilder();
            SKTypeface? current = null;
            foreach (var rune in line.EnumerateRunes())
            {
                var typeface = TypefaceFor(rune.Value);
                if (current != null && !ReferenceEquals(typeface, current))
                {
                    yield return (current, segment.ToString());
                    segment.Clear();
                }
                current = typeface;
                segment.Append(rune.ToString());
            }
            if (current != null && segment.Length > 0)
                yield return (current, segment.ToString());
        }

        private SKTypeface TypefaceFor(int codepoint)
        {
            if (_primary.ContainsGlyph(codepoint)) return _primary;
            if (_fallbackByCodepoint.TryGetValue(codepoint, out var cached)) return cached ?? _primary;

            var match = SKFontManager.Default.MatchCharacter(_primary.FamilyName, _style, null, codepoint);
            if (match != null) _owned.Add(match);
            _fallbackByCodepoint[codepoint] = match;
            return match ?? _primary; // 連系統後備都沒有 → 用主字面畫 .notdef
        }

        private SKFont CreateFont(SKTypeface typeface)
        {
            var font = new SKFont(typeface, _size);
            if (_bold && typeface.FontWeight < 600) font.Embolden = true;
            if (_italic && !typeface.IsItalic) font.SkewX = -0.25f;
            return font;
        }

        private float Measure(SKTypeface typeface, string segment)
        {
            using var measure = CreateMeasurePaint(typeface);
            return measure.MeasureText(segment);
        }

        /// <summary>與繪製同設定的量測 paint（2.88 的 SKFont.MeasureText 不吃 string）。</summary>
        private SKPaint CreateMeasurePaint(SKTypeface typeface) => new()
        {
            Typeface = typeface,
            TextSize = _size,
            FakeBoldText = _bold && typeface.FontWeight < 600,
            TextSkewX = _italic && !typeface.IsItalic ? -0.25f : 0f,
        };

        public void Dispose()
        {
            _primaryFont?.Dispose();
            foreach (var typeface in _owned) typeface.Dispose();
            _style.Dispose();
        }
    }
}

public enum ShapeKind
{
    Rectangle,
    Ellipse,
    Line,
}

public sealed record ShapeElement : VectorElement
{
    public ShapeKind Kind { get; init; } = ShapeKind.Rectangle;

    /// <summary>形狀外框；Line 以 (Left,Top)→(Right,Bottom) 為端點（可為「負尺寸」表達方向）。</summary>
    public SKRect Rect { get; init; }

    public SKColor? FillColor { get; init; }
    public SKColor StrokeColor { get; init; } = SKColors.Black;
    public float StrokeWidth { get; init; } = 4f;

    public override SKRectI Bounds
    {
        get
        {
            var r = SKRect.Create(
                Math.Min(Rect.Left, Rect.Right), Math.Min(Rect.Top, Rect.Bottom),
                Math.Abs(Rect.Width), Math.Abs(Rect.Height));
            var pad = StrokeWidth / 2 + 2;
            r.Inflate(pad, pad);
            return SKRectI.Ceiling(r);
        }
    }

    public override void Render(SKCanvas canvas)
    {
        var r = SKRect.Create(
            Math.Min(Rect.Left, Rect.Right), Math.Min(Rect.Top, Rect.Bottom),
            Math.Abs(Rect.Width), Math.Abs(Rect.Height));

        if (Kind == ShapeKind.Line)
        {
            using var stroke = new SKPaint
            {
                Color = StrokeColor,
                StrokeWidth = StrokeWidth,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeCap = SKStrokeCap.Round,
            };
            canvas.DrawLine(Rect.Left, Rect.Top, Rect.Right, Rect.Bottom, stroke);
            return;
        }

        if (FillColor is { } fill)
        {
            using var fillPaint = new SKPaint { Color = fill, IsAntialias = true, Style = SKPaintStyle.Fill };
            if (Kind == ShapeKind.Rectangle) canvas.DrawRect(r, fillPaint);
            else canvas.DrawOval(r, fillPaint);
        }

        if (StrokeWidth > 0)
        {
            using var strokePaint = new SKPaint
            {
                Color = StrokeColor,
                StrokeWidth = StrokeWidth,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
            };
            if (Kind == ShapeKind.Rectangle) canvas.DrawRect(r, strokePaint);
            else canvas.DrawOval(r, strokePaint);
        }
    }

    public override VectorElement Translated(float dx, float dy) =>
        this with { Rect = new SKRect(Rect.Left + dx, Rect.Top + dy, Rect.Right + dx, Rect.Bottom + dy) };

    /// <summary>形狀不支援旋轉參數：取矩陣映射後的外接矩形（近似）。</summary>
    public override VectorElement TransformedBy(SKMatrix matrix, float sx, float sy, float rotationDeg) =>
        this with
        {
            Rect = matrix.MapRect(Rect),
            StrokeWidth = Math.Max(0f, StrokeWidth * (Math.Abs(sx) + Math.Abs(sy)) / 2),
        };
}
