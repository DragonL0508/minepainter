using SkiaSharp;

namespace MinePainter.Core.Vectors;
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

    /// <summary>
    /// 效果外擴在 x 方向的倍率：繪製時整體套 canvas.Scale(ScaleX, 1)，外框／陰影／光暈的寬度
    /// 在水平方向也跟著被拉寬（拉寬兩倍的字，外框左右就是兩倍厚）——外擴量不乘上去，
    /// 左右就少算、被 tile 直線切掉。縮窄時不縮（保守）。
    /// </summary>
    private float EffectPadScaleX => Math.Max(1f, Math.Abs(ScaleX));

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
            if (BoundsCache.TryGetValue(this, out var cached)) return cached.Value;
            var result = ComputeBounds();
            BoundsCache.AddOrUpdate(this, new StrongBox<SKRectI>(result));
            return result;
        }
    }

    private SKRectI ComputeBounds()
    {
        var doc = MapLocalToDoc(PaddedLocalBounds);
        if (!HasDeform) return SKRectI.Ceiling(doc);
        // 彎曲網格在框外是貝茲外插（三次成長），把含效果外擴的大框整個送進去會爆成離譜的範圍；
        // 只映射排版框（含著墨），效果外擴在變形後再往外加（效果本來就是在算繪結果上長出去的）
        var deformed = Deform!.MapBounds(MapLocalToDoc(CoreLocalBounds));
        var pad = EffectPad + 2;
        deformed.Inflate(pad * EffectPadScaleX, pad);
        return SKRectI.Ceiling(deformed);
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

    /// <summary>
    /// 排版框 ∪ 實際著墨框：CJK 展示字型（07TetsubinGothic 之類）的字面常超出 em box，
    /// 只用行高算的框會少算，效果快取的餘裕就在那裡被吃掉 —— 外框拉大時被直線切掉就是這個。
    /// </summary>
    private SKRect CoreLocalBounds
    {
        get
        {
            var local = LocalBounds;
            if (MeasureInkBounds() is { } ink)
            {
                ink.Inflate(2, 2);
                local = new SKRect(Math.Min(local.Left, ink.Left), Math.Min(local.Top, ink.Top),
                    Math.Max(local.Right, ink.Right), Math.Max(local.Bottom, ink.Bottom));
            }
            return local;
        }
    }

    private SKRect PaddedLocalBounds
    {
        get
        {
            var local = CoreLocalBounds;
            var pad = EffectPad;
            if (pad > 0) local.Inflate(pad * EffectPadScaleX, pad);
            return local;
        }
    }

    // Bounds 每格 tile 都會被問（合成器、效果快取、命中）；量測著墨要跑排版，同一個（immutable）實例只算一次
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextElement, StrongBox<SKRectI>> BoundsCache = new();

    private sealed class StrongBox<T>(T value) { public T Value = value; }

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
            // 合成器是逐 tile 呼叫 Render 的：透視／彎曲的文字每格都重算一次離線影像＋網格貼圖，
            // 拉大時一步就是幾十次完整算繪 —— 同一個（immutable）實例只算一次，之後每格只是貼圖。
            var cache = DeformCache.GetValue(this, _ => new DeformRenderCache());
            lock (cache)
            {
                if (!cache.Tried)
                {
                    cache.Tried = true;
                    var b = Bounds;
                    if (b.Width > 0 && b.Height > 0 && b.Width <= 8192 && b.Height <= 8192)
                    {
                        using var surface = SKSurface.Create(new SKImageInfo(b.Width, b.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
                        if (surface != null)
                        {
                            var c = surface.Canvas;
                            c.Clear(SKColors.Transparent);
                            c.Translate(-b.Left, -b.Top);
                            RenderDeformedDirect(c);
                            c.Flush();
                            cache.Image = surface.Snapshot();
                            cache.Origin = new SKPoint(b.Left, b.Top);
                        }
                    }
                }
                if (cache.Image != null)
                {
                    canvas.DrawImage(cache.Image, cache.Origin.X, cache.Origin.Y);
                    return;
                }
            }
            RenderDeformedDirect(canvas);
            return;
        }

        RenderCore(canvas);
    }

    private sealed class DeformRenderCache
    {
        public bool Tried;
        public SKImage? Image;
        public SKPoint Origin;
        ~DeformRenderCache() => Image?.Dispose();
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextElement, DeformRenderCache> DeformCache = new();

    /// <summary>不經快取，直接把透視／彎曲後的文字畫到 canvas（doc 座標）。</summary>
    private void RenderDeformedDirect(SKCanvas canvas)
    {
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
        }
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

        // 同一份排版要重畫好幾次（光暈／陰影／外框／字身，最多八趟），只換 paint。
        // 分段、挑字面、量寬、建 glyph 全都只做一次，之後每趟只是把同一批 blob 換個 paint 再畫一遍
        // —— 旋轉中的帶效果文字每幀都在重跑這幾趟，逐趟重新排版就是那裡掉的幀。
        var blobs = new List<SKTextBlob>();
        var rules = new List<SKRect>(); // 底線／刪除線
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
                shaper.BuildLine(lines[i], x, baseline, blobs);
                if (widths[i] > 0)
                {
                    if (Underline)
                        rules.Add(SKRect.Create(x, baseline + underlineY, widths[i], lineThickness));
                    if (Strikethrough)
                        rules.Add(SKRect.Create(x, baseline + strikeY, widths[i], lineThickness));
                }
                baseline += LineHeight;
            }
        }

        void DrawPass(SKPaint paint)
        {
            foreach (var blob in blobs) canvas.DrawText(blob, 0, 0, paint);
            foreach (var rule in rules) canvas.DrawRect(rule, paint);
        }

        try
        {
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
                    // 描粗與實心一起畫（StrokeAndFill）：兩者是同一個顏色，分兩趟只是把同一塊
                    // 模糊了兩次再疊起來 —— 重疊處因此比真正的「輪廓聯集模糊一次」更濃，
                    // 而且整整多跑一趟最貴的模糊。合成一趟同時更快也更接近 PS。
                    paint.Style = SKPaintStyle.StrokeAndFill;
                    paint.StrokeWidth = outline * 2;
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
        finally
        {
            foreach (var blob in blobs) blob.Dispose();
        }
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

    /// <summary>一行文字的排版寬度（未套 ScaleX；含字距，與繪製同一套量法）。</summary>
    public float MeasureLineWidth(string line)
    {
        using var shaper = CreateShaper();
        return shaper.MeasureLine(line);
    }

    /// <summary>
    /// 排版座標（第一行左上為原點、未套 ScaleX）→ 文件座標：繪製時是 Translate(Position) → Rotate → Scale(ScaleX, 1)，
    /// 這裡照同一順序算回去。不含 <see cref="Deform"/>。
    /// </summary>
    public SKPoint LayoutToDoc(float x, float y)
    {
        var sx = x * ScaleX;
        if (Math.Abs(Rotation) < 0.01f) return new SKPoint(Position.X + sx, Position.Y + y);
        var rad = Rotation * MathF.PI / 180f;
        var cos = MathF.Cos(rad);
        var sin = MathF.Sin(rad);
        return new SKPoint(Position.X + sx * cos - y * sin, Position.Y + sx * sin + y * cos);
    }

    /// <summary>
    /// 把文字拆成「選取範圍之前／之中／之後」的獨立物件，每一段都擺在原本字面所在的位置
    /// （像素位置一模一樣）。跨行的段落再依行切開 —— 每一段都是單行、靠左對齊，
    /// 位置＝原本那一行的起點加上前面文字的寬度，所以換字型、換顏色之後其他字不會動。
    /// 回傳依原文順序排列；<paramref name="selectedIndex"/> 是選取範圍那些段在清單裡的索引。
    /// 有非仿射變形（<see cref="Deform"/>）的文字拆不了（每段各自的變形對不上），回傳空清單。
    /// </summary>
    public IReadOnlyList<TextElement> SplitPieces(int start, int length, out List<int> selectedIndex)
    {
        selectedIndex = [];
        var pieces = new List<TextElement>();
        if (HasDeform || Text.Length == 0) return pieces;
        start = Math.Clamp(start, 0, Text.Length);
        var end = Math.Clamp(start + Math.Max(0, length), start, Text.Length);

        using var shaper = CreateShaper();
        var lines = Text.Split('\n');
        var widths = new float[lines.Length];
        var maxWidth = 0f;
        for (var i = 0; i < lines.Length; i++)
        {
            widths[i] = shaper.MeasureLine(lines[i]);
            maxWidth = Math.Max(maxWidth, widths[i]);
        }

        var offset = 0;   // 這一行第一個字在 Text 裡的索引
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var lineX = Alignment switch
            {
                TextAlign.Center => (maxWidth - widths[i]) / 2,
                TextAlign.Right => maxWidth - widths[i],
                _ => 0f,
            };
            // 這一行被選取範圍切成最多三段：[0, s) [s, e) [e, len)
            var s = Math.Clamp(start - offset, 0, line.Length);
            var e = Math.Clamp(end - offset, 0, line.Length);
            foreach (var (from, to, selected) in new[] { (0, s, false), (s, e, true), (e, line.Length, false) })
            {
                if (to <= from) continue;
                var prefix = shaper.MeasureLine(line[..from]);
                var position = LayoutToDoc(lineX + prefix, i * LineHeight);
                pieces.Add(this with
                {
                    Id = Guid.NewGuid(),
                    Text = line[from..to],
                    Alignment = TextAlign.Left,
                    Position = position,
                    Deform = null,
                });
                if (selected) selectedIndex.Add(pieces.Count - 1);
            }
            offset += line.Length + 1;   // 加上換行字元
        }
        return pieces;
    }
}
