using MinePainter.Core.Effects;
using SkiaSharp;

namespace MinePainter.Core.IO;

/// <summary>
/// Photoshop 圖層樣式（<c>lfx2</c>）→ 我們的非破壞性效果堆疊
/// （<see cref="ObjectShadowEffect"/>、<see cref="ObjectGlowEffect"/>、<see cref="ObjectOutlineEffect"/>、
/// <see cref="InnerShadowEffect"/>、<see cref="BevelEmbossEffect"/>…）。文字圖層也一樣掛在圖層上 ——
/// 統一在圖層屬性的效果面板編輯（之前文字寫進 TextElement 自己的外框／陰影，使用者找不到地方改）。
///
/// 數值單位：描述子裡的長度就是文件像素（拿 350 ppi 的真檔對過 Photoshop 自己的合成影像：
/// 乘上根目錄的 <c>Scl</c> 會大 4.86 倍，所以 <c>Scl</c> 不能用）；「展開／填塞」（Ckmt）是模糊大小的百分比。
/// 角度是數學慣例（逆時針、0 = 右），光源角度指「光從哪來」，陰影落在對面；
/// 勾了「使用整體光源」的用影像資源 1037 的整體角度。
///
/// 對不上的（緞面、圖樣覆蓋、效果本身的混合模式、輪廓曲線）列在 <see cref="Unsupported"/>，由呼叫端提示。
/// </summary>
internal sealed class PsdLayerStyle
{
    public sealed record Stroke(float Size, SKColor Color, string Position, GradientStops? Gradient, float GradientAngle);
    public sealed record Shadow(SKColor Color, float Angle, float Distance, float Blur, float Spread);
    /// <summary>內陰影：角度是 PS 原始的光源角度（數學慣例），InnerShadowEffect 直接吃。</summary>
    public sealed record InnerShadowStyle(SKColor Color, float LightAngle, float Distance, float Size, int ChokePercent);
    public sealed record Glow(SKColor Color, float Size, float Spread);
    public sealed record Bevel(int Style, bool Up, float Size, int Depth, float Soften, float LightAngle, float Altitude,
        SKColor Highlight, int HighlightOpacity, SKColor ShadowColor, int ShadowOpacity);
    public sealed record Overlay(SKColor Color, int Opacity);
    public sealed record GradientOverlay(GradientStops Stops, float Angle, bool Radial, int Opacity);

    public List<Stroke> Strokes { get; } = [];
    public Shadow? DropShadow { get; private set; }
    public InnerShadowStyle? InnerShadow { get; private set; }
    public Bevel? BevelEmboss { get; private set; }
    public Glow? OuterGlow { get; private set; }
    public Glow? InnerGlow { get; private set; }
    public Overlay? ColorOverlay { get; private set; }
    public GradientOverlay? Gradient { get; private set; }
    public List<string> Unsupported { get; } = [];

    public bool IsEmpty => Strokes.Count == 0 && DropShadow == null && OuterGlow == null && InnerGlow == null
        && ColorOverlay == null && Gradient == null && InnerShadow == null && BevelEmboss == null;

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

        foreach (var fx in Instances(root, "IrSh", "innerShadowMulti"))
            style.InnerShadow ??= ReadInnerShadow(fx, scale, globalAngle);
        foreach (var fx in Instances(root, "ebbl", null))
            style.BevelEmboss ??= ReadBevel(fx, scale, globalAngle);
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

    private static InnerShadowStyle ReadInnerShadow(PsdDescriptor fx, float scale, int globalAngle)
    {
        var color = WithOpacity(fx.Color("Clr ") ?? SKColors.Black, fx);
        var lightAngle = (float)(fx.Bool("uglg") == true ? globalAngle : fx.Number("lagl") ?? 120);
        return new InnerShadowStyle(color, lightAngle,
            (float)(fx.Number("Dstn") ?? 0) * scale,
            (float)(fx.Number("blur") ?? 0) * scale,
            (int)Math.Clamp(Math.Round(fx.Number("Ckmt") ?? 0), 0, 100));
    }

    /// <summary>
    /// 斜角和浮雕：bvlS 樣式（InrB 內斜角／OtrB 外斜角／Embs 浮雕／PlEb 枕狀浮雕／strokeEmboss 筆畫浮雕→浮雕）、
    /// bvlD 方向（In 上／Out 下）、Sz 大小、srgR 深度 %、Sftn 柔化、lagl／Lald 光源方位與高度、
    /// hglC／hglO 亮部色與不透明度、sdwC／sdwO 陰影色與不透明度。
    /// </summary>
    private static Bevel ReadBevel(PsdDescriptor fx, float scale, int globalAngle)
    {
        var style = fx.Enum("bvlS") switch
        {
            "OtrB" => 1,
            "Embs" or "strokeEmboss" => 2,
            "PlEb" => 3,
            _ => 0,
        };
        var up = fx.Enum("bvlD") != "Out";
        var lightAngle = (float)(fx.Bool("uglg") == true ? globalAngle : fx.Number("lagl") ?? 120);
        return new Bevel(style, up,
            (float)(fx.Number("Sz  ") ?? 5) * scale,
            (int)Math.Clamp(Math.Round(fx.Number("srgR") ?? 100), 1, 1000),
            (float)(fx.Number("Sftn") ?? 0) * scale,
            lightAngle,
            (float)(fx.Number("Lald") ?? 30),
            fx.Color("hglC") ?? SKColors.White, (int)Math.Clamp(Math.Round(fx.Number("hglO") ?? 75), 0, 100),
            fx.Color("sdwC") ?? SKColors.Black, (int)Math.Clamp(Math.Round(fx.Number("sdwO") ?? 75), 0, 100));
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
    /// 效果堆疊的順序有講究（PS 由下往上：陰影、外光暈、內容、覆蓋、內光暈、內陰影、斜角、筆畫）：
    /// 覆蓋類不會長大、先做；內光暈／內陰影／斜角只畫在內容裡；外框描在內容外；
    /// 光暈與陰影最後 —— 它們算的是「內容 + 外框」的形狀，才會像 PS 那樣包在筆畫外面。
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
        if (InnerShadow is { } innerShadow)
            effects.Add(LayerEffect.Create(new InnerShadowEffect
            {
                Color = innerShadow.Color.WithAlpha(255), Opacity = Percent(innerShadow.Color.Alpha),
                Angle = innerShadow.LightAngle, Distance = Math.Clamp((int)Math.Round(innerShadow.Distance), 0, 50),
                Size = Math.Clamp((int)Math.Round(innerShadow.Size), 0, 50), Choke = innerShadow.ChokePercent, RelativeToObject = false,
            }, color: innerShadow.Color));
        if (BevelEmboss is { } bevel)
            effects.Add(LayerEffect.Create(new BevelEmbossEffect
            {
                Style = bevel.Style, Up = bevel.Up, Size = Math.Clamp((int)Math.Round(bevel.Size), 1, 50), Depth = bevel.Depth,
                Soften = Math.Clamp((int)Math.Round(bevel.Soften), 0, 16), Angle = bevel.LightAngle, Altitude = bevel.Altitude,
                HighlightColor = bevel.Highlight, HighlightOpacity = bevel.HighlightOpacity,
                ShadowColor = bevel.ShadowColor, ShadowOpacity = bevel.ShadowOpacity, RelativeToObject = false,
            }));
        foreach (var stroke in Strokes.OrderBy(s => s.Size))
        {
            effects.Add(LayerEffect.Create(new ObjectOutlineEffect
            {
                Width = Math.Clamp((int)Math.Round(stroke.Size), 1, 100), Color = stroke.Color,
                Position = stroke.Position switch { "InsF" => 2, "CtrF" => 1, _ => 0 },
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
}
