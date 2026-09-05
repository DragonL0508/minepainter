using MinePainter.Core.Effects;
using MinePainter.Core.Vectors;
using SkiaSharp;

namespace MinePainter.Core.IO;

/// <summary>
/// Photoshop 圖層樣式（<c>lfx2</c>）→ 我們的效果。點陣圖層掛成非破壞性效果堆疊
/// （<see cref="ObjectShadowEffect"/>、<see cref="ObjectGlowEffect"/>、<see cref="ObjectOutlineEffect"/>…），
/// 文字圖層則寫進 <see cref="TextElement"/> 自己的外框／陰影／光暈／漸層。
///
/// 數值單位：描述子裡的長度就是文件像素（拿 350 ppi 的真檔對過 Photoshop 自己的合成影像：
/// 乘上根目錄的 <c>Scl</c> 會大 4.86 倍，所以 <c>Scl</c> 不能用）；「展開／填塞」（Ckmt）是模糊大小的百分比。
/// 角度是數學慣例（逆時針、0 = 右），光源角度指「光從哪來」，陰影落在對面；
/// 勾了「使用整體光源」的用影像資源 1037 的整體角度。
///
/// 對不上的（內陰影、斜角浮雕、緞面、圖樣覆蓋、內側筆畫、效果本身的混合模式）列在
/// <see cref="Unsupported"/>，由呼叫端提示。
/// </summary>
internal sealed class PsdLayerStyle
{
    public sealed record Stroke(float Size, SKColor Color, string Position, GradientStops? Gradient, float GradientAngle);
    public sealed record Shadow(SKColor Color, float Angle, float Distance, float Blur, float Spread);
    public sealed record Glow(SKColor Color, float Size, float Spread);
    public sealed record Overlay(SKColor Color, int Opacity);
    public sealed record GradientOverlay(GradientStops Stops, float Angle, bool Radial, int Opacity);

    public List<Stroke> Strokes { get; } = [];
    public Shadow? DropShadow { get; private set; }
    public Glow? OuterGlow { get; private set; }
    public Glow? InnerGlow { get; private set; }
    public Overlay? ColorOverlay { get; private set; }
    public GradientOverlay? Gradient { get; private set; }
    public List<string> Unsupported { get; } = [];

    public bool IsEmpty => Strokes.Count == 0 && DropShadow == null && OuterGlow == null && InnerGlow == null
        && ColorOverlay == null && Gradient == null;

    /// <summary>解析 lfx2 區塊；整組被關掉（masterFXSwitch）或格式不對回 null。</summary>
    public static PsdLayerStyle? Parse(byte[] block, int globalAngle)
    {
        var reader = new PsdByteReader(block);
        reader.UInt32();    // 版本 0
        reader.UInt32();    // 描述子版本 16
        var root = PsdDescriptor.Read(reader);
        if (root.Bool("masterFXSwitch") == false) return null;

        const float scale = 1f;
        var style = new PsdLayerStyle();

        foreach (var fx in Instances(root, "DrSh", "dropShadowMulti"))
            style.DropShadow ??= ReadShadow(fx, scale, globalAngle);
        foreach (var fx in Instances(root, "OrGl", null))
            style.OuterGlow ??= ReadGlow(fx, scale);
        foreach (var fx in Instances(root, "IrGl", null))
            style.InnerGlow ??= ReadGlow(fx, scale);
        foreach (var fx in Instances(root, "FrFX", "frameFXMulti"))
            if (ReadStroke(fx, scale, style.Unsupported) is { } stroke) style.Strokes.Add(stroke);
        foreach (var fx in Instances(root, "SoFi", "solidFillMulti"))
            style.ColorOverlay ??= ReadOverlay(fx);
        foreach (var fx in Instances(root, "GrFl", "gradientFillMulti"))
            style.Gradient ??= ReadGradient(fx);

        if (Instances(root, "IrSh", "innerShadowMulti").Any()) style.Unsupported.Add("內陰影");
        if (Instances(root, "ebbl", null).Any()) style.Unsupported.Add("斜角和浮雕");
        if (Instances(root, "ChFX", null).Any()) style.Unsupported.Add("緞面");
        if (Instances(root, "patternFill", null).Any()) style.Unsupported.Add("圖樣覆蓋");

        return style;
    }

    /// <summary>單一（舊鍵）與多重（*Multi 清單）兩種寫法都收，只留有開啟的。</summary>
    private static IEnumerable<PsdDescriptor> Instances(PsdDescriptor root, string single, string? multi)
    {
        if (root.Child(single) is { } one && IsOn(one)) yield return one;
        if (multi != null && root.List(multi) is { } list)
            foreach (var item in list)
                if (item is PsdDescriptor fx && IsOn(fx)) yield return fx;
    }

    private static bool IsOn(PsdDescriptor fx) => fx.Bool("enab") != false && fx.Bool("present") != false;

    private static SKColor WithOpacity(SKColor color, PsdDescriptor fx)
    {
        var percent = fx.Number("Opct") ?? 100;
        return color.WithAlpha((byte)Math.Clamp(Math.Round(percent * 2.55), 0, 255));
    }

    private static Shadow ReadShadow(PsdDescriptor fx, float scale, int globalAngle)
    {
        var color = WithOpacity(fx.Color("Clr ") ?? SKColors.Black, fx);
        var lightAngle = fx.Bool("uglg") == true ? globalAngle : fx.Number("lagl") ?? 120;
        // 光源角度（數學逆時針）→ 陰影方向（螢幕順時針、0 = 右）：陰影在光源對面
        var shadowAngle = (float)(((180 - lightAngle) % 360 + 360) % 360);
        var blur = (float)(fx.Number("blur") ?? 0) * scale;
        return new Shadow(color, shadowAngle, (float)(fx.Number("Dstn") ?? 0) * scale, blur, SpreadOf(fx, blur));
    }

    private static Glow ReadGlow(PsdDescriptor fx, float scale)
    {
        var size = (float)(fx.Number("blur") ?? 0) * scale;
        return new Glow(WithOpacity(fx.Color("Clr ") ?? SKColors.White, fx), size, SpreadOf(fx, size));
    }

    /// <summary>展開（Ckmt）是大小的百分比：先實心外擴這麼多，剩下的才模糊。</summary>
    private static float SpreadOf(PsdDescriptor fx, float size) =>
        size * (float)Math.Clamp((fx.Number("Ckmt") ?? 0) / 100.0, 0, 1);

    private static Stroke? ReadStroke(PsdDescriptor fx, float scale, List<string> unsupported)
    {
        var size = (float)(fx.Number("Sz  ") ?? 0) * scale;
        if (size <= 0) return null;
        var position = fx.Enum("Styl") ?? "OutF";
        if (position == "InsF") unsupported.Add("內側筆畫");

        var paint = fx.Enum("PntT") ?? "SClr";
        if (paint == "Ptrn") unsupported.Add("圖樣筆畫");
        GradientStops? gradient = null;
        var angle = 90f;
        if (paint == "GrFl" && fx.Child("Grad") is { } grad)
        {
            gradient = ReadStops(grad, fx.Bool("Rvrs") == true);
            angle = ToOurAngle(fx.Number("Angl") ?? 90);
        }

        var color = WithOpacity(fx.Color("Clr ") ?? SKColors.Black, fx);
        return new Stroke(size, color, position, gradient, angle);
    }

    private static Overlay ReadOverlay(PsdDescriptor fx) =>
        new(fx.Color("Clr ") ?? SKColors.Black, (int)Math.Clamp(Math.Round(fx.Number("Opct") ?? 100), 0, 100));

    private static GradientOverlay? ReadGradient(PsdDescriptor fx)
    {
        if (fx.Child("Grad") is not { } grad) return null;
        var stops = ReadStops(grad, fx.Bool("Rvrs") == true);
        if (stops == null) return null;
        return new GradientOverlay(stops, ToOurAngle(fx.Number("Angl") ?? 90),
            fx.Enum("Type") == "Rdl", (int)Math.Clamp(Math.Round(fx.Number("Opct") ?? 100), 0, 100));
    }

    /// <summary>Photoshop 漸層：色節點位置 0..4096；透明度節點另存（Trns），這裡只取顏色。</summary>
    private static GradientStops? ReadStops(PsdDescriptor grad, bool reverse)
    {
        var colors = grad.List("Clrs");
        if (colors == null) return null;
        var stops = new List<GradientStop>();
        foreach (var item in colors)
        {
            if (item is not PsdDescriptor stop || stop.Color("Clr ") is not { } color) continue;
            var t = (float)Math.Clamp((stop.Number("Lctn") ?? 0) / 4096.0, 0, 1);
            stops.Add(new GradientStop(reverse ? 1 - t : t, color));
        }
        if (stops.Count < 2) return null;
        stops.Sort((a, b) => a.Position.CompareTo(b.Position));
        return new GradientStops(stops);
    }

    /// <summary>PS 漸層角度（逆時針、90 = 由下往上）→ 我們的（順時針、90 = 由上往下）。</summary>
    private static float ToOurAngle(double psAngle) => (float)(((360 - psAngle) % 360 + 360) % 360);

    // ---- 套到點陣圖層 ----

    /// <summary>
    /// 效果堆疊的順序有講究：覆蓋類不會長大、先做；外框描在內容外；光暈與陰影最後
    /// —— 它們算的是「內容 + 外框」的形狀，才會像 PS 那樣包在筆畫外面。
    /// </summary>
    public IReadOnlyList<LayerEffect> ToLayerEffects()
    {
        var effects = new List<LayerEffect>();
        if (ColorOverlay is { } fill)
            effects.Add(LayerEffect.Create(new ObjectFillEffect { Color = fill.Color, Opacity = fill.Opacity }, color: fill.Color));
        if (Gradient is { } gradient)
            effects.Add(LayerEffect.Create(new ObjectGradientEffect
            {
                Stops = gradient.Stops, Angle = gradient.Angle, Radial = gradient.Radial, RelativeToObject = false,
            }));
        if (InnerGlow is { } inner)
            effects.Add(LayerEffect.Create(new InnerGlowEffect
            {
                Color = inner.Color.WithAlpha(255), Opacity = Percent(inner.Color.Alpha),
                Size = Math.Clamp((int)Math.Round(inner.Size), 1, 100), Spread = Math.Clamp((int)Math.Round(inner.Spread), 0, 30),
            }, color: inner.Color));
        foreach (var stroke in Strokes.OrderBy(s => s.Size))
        {
            var width = stroke.Position == "CtrF" ? stroke.Size / 2 : stroke.Size;
            effects.Add(LayerEffect.Create(new ObjectOutlineEffect
            {
                Width = Math.Clamp((int)Math.Round(width), 1, 100), Color = stroke.Color,
                Gradient = stroke.Gradient != null, GradientAngle = stroke.GradientAngle, RelativeToObject = false,
                GradientStops = stroke.Gradient ?? GradientStops.Two(stroke.Color, SKColors.White),
            }, color: stroke.Color));
        }
        if (OuterGlow is { } glow)
            effects.Add(LayerEffect.Create(new ObjectGlowEffect
            {
                Color = glow.Color.WithAlpha(255), Opacity = Percent(glow.Color.Alpha),
                Size = Math.Clamp((int)Math.Round(glow.Size), 1, 100), Spread = Math.Clamp((int)Math.Round(glow.Spread), 0, 30),
            }, color: glow.Color));
        if (DropShadow is { } shadow)
        {
            var rad = shadow.Angle * Math.PI / 180;
            effects.Add(LayerEffect.Create(new ObjectShadowEffect
            {
                OffsetX = Math.Clamp((int)Math.Round(Math.Cos(rad) * shadow.Distance), -100, 100),
                OffsetY = Math.Clamp((int)Math.Round(Math.Sin(rad) * shadow.Distance), -100, 100),
                Blur = Math.Clamp((int)Math.Round(shadow.Blur), 0, 50),
                Opacity = Percent(shadow.Color.Alpha), Color = shadow.Color.WithAlpha(255), RelativeToObject = false,
            }, color: shadow.Color));
        }
        return effects;
    }

    private static int Percent(byte alpha) => (int)Math.Round(alpha / 2.55);

    // ---- 套到文字 ----

    /// <summary>文字用自己的外框／陰影／光暈／漸層；內光暈文字沒有，記到 <see cref="Unsupported"/>。</summary>
    public TextElement ApplyTo(TextElement text)
    {
        if (InnerGlow != null) Unsupported.Add("內光暈");

        if (Strokes.Count > 0)
        {
            // PS 多重筆畫：清單前面的畫在上面；同為外側時，粗的被細的蓋住只露出差值 → 由內而外的寬度取差
            var layers = new List<TextStroke>();
            var previous = 0f;
            foreach (var stroke in Strokes.OrderBy(s => s.Size))
            {
                var visible = (stroke.Position == "CtrF" ? stroke.Size / 2 : stroke.Size) - previous;
                if (visible <= 0.5f) continue;
                previous += visible;
                layers.Add(new TextStroke
                {
                    Color = stroke.Color, Width = visible,
                    Gradient = stroke.Gradient == null ? null : new TextGradient
                    {
                        Start = stroke.Gradient.First, End = stroke.Gradient.Last, Angle = stroke.GradientAngle,
                    },
                });
            }
            text = text with { Stroke = TextStroke.FromLayers(layers) };
        }

        if (DropShadow is { } shadow)
            text = text with
            {
                Shadow = new TextShadow
                {
                    Color = shadow.Color, Angle = shadow.Angle, Distance = shadow.Distance, Blur = shadow.Blur, Spread = shadow.Spread,
                },
            };

        if (OuterGlow is { } glow)
            text = text with { Glow = new TextGlow { Color = glow.Color, Size = Math.Max(1, glow.Size), Spread = glow.Spread } };

        if (Gradient is { } gradient)
            text = text with
            {
                Gradient = new TextGradient
                {
                    Start = gradient.Stops.First, End = gradient.Stops.Last, Angle = gradient.Angle, Radial = gradient.Radial,
                },
            };
        else if (ColorOverlay is { } overlay)
            text = text with { Color = Blend(text.Color, overlay.Color, overlay.Opacity) };

        return text;
    }

    private static SKColor Blend(SKColor under, SKColor over, int percent)
    {
        var t = percent / 100f;
        byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * t);
        return new SKColor(Mix(under.Red, over.Red), Mix(under.Green, over.Green), Mix(under.Blue, over.Blue), under.Alpha);
    }
}
