using MinePainter.Core.Adjustments;
using MinePainter.Core.Effects;
using MinePainter.Core.Vectors;
using SkiaSharp;

namespace MinePainter.Core.IO;

/// <summary>
/// 我們的效果堆疊（與文字自己的外框／陰影／光暈／漸層）→ Photoshop 圖層樣式（<c>lfx2</c>）。
/// <see cref="PsdLayerStyle"/> 的反向：鍵與單位照它讀的寫（長度 #Pxl 是文件像素、展開／填塞 Ckmt 是 #Prc、
/// 角度 #Ang 是數學慣例、光源角度指「光從哪來」）。
///
/// 對不上的效果（模糊、扭曲、帶選取遮罩的、外框柔邊…）列在 <see cref="Unsupported"/>，
/// 呼叫端看到非空就把整層烙成像素 —— 少一條效果的「可編輯」比整層走樣更糟。
/// 堆疊裡的調整（<see cref="AdjustmentEffect"/>）PS 的樣式沒有這一項，改寫成剪裁到這層的調整圖層。
///
/// 一個樣式只能有一個外光暈／內光暈／斜角；陰影、內陰影、筆畫、純色、漸層可以多個（*Multi 清單，CC 2015 起）。
/// 外框疊外框時 PS 每一條都從內容邊緣量，所以第二條的大小要累加前面外側外框的寬度。
/// </summary>
internal sealed class PsdStyleWriter
{
    public List<string> Unsupported { get; } = [];
    public List<(IAdjustment Adjustment, bool Enabled)> ClippedAdjustments { get; } = [];

    private readonly List<PsdDescriptorBuilder> _shadows = [];
    private readonly List<PsdDescriptorBuilder> _innerShadows = [];
    private readonly List<PsdDescriptorBuilder> _strokes = [];
    private readonly List<PsdDescriptorBuilder> _fills = [];
    private readonly List<PsdDescriptorBuilder> _gradients = [];
    private PsdDescriptorBuilder? _outerGlow;
    private PsdDescriptorBuilder? _innerGlow;
    private PsdDescriptorBuilder? _bevel;
    private float _outerStrokeTotal;

    public bool IsEmpty => _shadows.Count == 0 && _innerShadows.Count == 0 && _strokes.Count == 0 && _fills.Count == 0
        && _gradients.Count == 0 && _outerGlow == null && _innerGlow == null && _bevel == null;

    public static PsdStyleWriter Build(IReadOnlyList<LayerEffect> effects, TextElement? text)
    {
        var writer = new PsdStyleWriter();
        if (text != null) writer.AddTextAppearance(text);
        foreach (var fx in effects) writer.Add(fx);
        return writer;
    }

    /// <summary>lfx2 區塊；沒有任何樣式時回 null。</summary>
    public byte[]? ToLfx2()
    {
        if (IsEmpty) return null;
        var root = new PsdDescriptorBuilder("null")
            .Add("Scl ", PsdDesc.Prc(100))
            .Add("masterFXSwitch", true);
        AddSingleOrMulti(root, "DrSh", "dropShadowMulti", _shadows);
        if (_outerGlow != null) root.Add("OrGl", _outerGlow);
        if (_innerGlow != null) root.Add("IrGl", _innerGlow);
        AddSingleOrMulti(root, "IrSh", "innerShadowMulti", _innerShadows);
        if (_bevel != null) root.Add("ebbl", _bevel);
        AddSingleOrMulti(root, "SoFi", "solidFillMulti", _fills);
        AddSingleOrMulti(root, "GrFl", "gradientFillMulti", _gradients);
        AddSingleOrMulti(root, "FrFX", "frameFXMulti", _strokes);

        var w = new PsdByteWriter();
        w.U32(0);    // 版本
        w.U32(16);   // 描述子版本
        root.WriteTo(w);
        return w.ToArray();
    }

    private static void AddSingleOrMulti(PsdDescriptorBuilder root, string single, string multi, List<PsdDescriptorBuilder> items)
    {
        if (items.Count == 1) root.Add(single, items[0]);
        else if (items.Count > 1) root.Add(multi, items.Cast<object>().ToList());
    }

    // ---- 文字自己的外觀 ----

    private void AddTextAppearance(TextElement t)
    {
        if (t.Gradient is { } g)
            _gradients.Add(GradientFill(GradientStops.Two(g.Start, g.End), g.Angle, g.Radial, 100, alignToLayer: true, enabled: true));
        if (t.Stroke is { } stroke)
        {
            foreach (var layer in stroke.Layers())
            {
                if (layer.Width <= 0) continue;
                _outerStrokeTotal += layer.Width;
                GradientStops? stops = layer.Gradient is { } sg ? GradientStops.Two(sg.Start, sg.End) : null;
                var angle = layer.Gradient?.Angle ?? 90f;
                _strokes.Add(Stroke(_outerStrokeTotal, layer.Color, "OutF", stops, angle, enabled: true));
            }
        }
        if (t.Glow is { } glow)
            _outerGlow = Glow("OrGl", glow.Color.WithAlpha(255), Percent(glow.Color.Alpha), glow.Size, glow.Spread, enabled: true);
        if (t.Shadow is { } s)
            _shadows.Add(Shadow(s.Color.WithAlpha(255), Percent(s.Color.Alpha), s.Angle, s.Distance, s.Blur, s.Spread, enabled: true));
    }

    // ---- 效果堆疊 ----

    private void Add(LayerEffect fx)
    {
        if (fx.Mask != null)
        {
            Unsupported.Add($"{fx.Name}（帶選取遮罩）");
            return;
        }
        var on = fx.Enabled;
        switch (fx.Effect)
        {
            case ObjectFillEffect f:
                _fills.Add(SolidFill(f.Color, f.Opacity, on));
                break;
            case ObjectGradientEffect g:
                _gradients.Add(GradientFill(g.Stops, g.Angle, g.Radial, 100, alignToLayer: g.RelativeToObject, enabled: on));
                break;
            case ObjectOutlineEffect o when o.Softness == 0 && o.Smooth == 0:
            {
                var size = (float)o.Width;
                var position = o.Position switch { 1 => "CtrF", 2 => "InsF", _ => "OutF" };
                if (o.Position == 0)
                {
                    _outerStrokeTotal += o.Width;
                    size = _outerStrokeTotal;
                }
                _strokes.Add(Stroke(size, o.Color, position, o.Gradient ? o.GradientStops : null, o.GradientAngle, on));
                break;
            }
            case ObjectShadowEffect s when s.Thickness == 0:
            {
                var distance = (float)Math.Sqrt(s.OffsetX * s.OffsetX + s.OffsetY * s.OffsetY);
                var angle = (float)(Math.Atan2(s.OffsetY, s.OffsetX) * 180 / Math.PI);
                _shadows.Add(Shadow(s.Color.WithAlpha(255), s.Opacity, angle, distance, s.Blur, 0, on));
                break;
            }
            case ObjectGlowEffect g when _outerGlow == null:
                _outerGlow = Glow("OrGl", g.Color.WithAlpha(255), g.Opacity, g.Size, g.Spread, on);
                break;
            case InnerGlowEffect g when _innerGlow == null && !g.Directional && !g.GlowCanvasEdge:
                _innerGlow = Glow("IrGl", g.Color.WithAlpha(255), g.Opacity, g.Size, g.Spread, on)
                    .Add("glwS", PsdDesc.Enum("IGSr", "SrcE"));
                break;
            case InnerShadowEffect s:
                _innerShadows.Add(InnerShadow(s, on));
                break;
            case BevelEmbossEffect b when _bevel == null:
                _bevel = Bevel(b, on);
                break;
            case AdjustmentEffect a when PsdAdjustmentWriter.CanWrite(a.Adjustment):
                ClippedAdjustments.Add((a.Adjustment, on));
                break;
            default:
                Unsupported.Add(fx.Name);
                break;
        }
    }

    // ---- 各種樣式的描述子 ----

    private static PsdDescriptorBuilder Common(string classId, bool enabled, string blend, int opacity) =>
        new PsdDescriptorBuilder(classId)
            .Add("enab", enabled)
            .Add("present", true)
            .Add("showInDialog", true)
            .Add("Md  ", PsdDesc.Blend(blend))
            .Add("Opct", PsdDesc.Prc(Math.Clamp(opacity, 0, 100)));

    /// <summary>線性輪廓（PS 每個效果都帶一條；沒有它舊版 PS 會用預設，寫上去最保險）。</summary>
    private static PsdDescriptorBuilder LinearContour() => new PsdDescriptorBuilder("ShpC")
        .Add("Nm  ", "Linear")
        .Add("Crv ", new List<object>
        {
            new PsdDescriptorBuilder("CrPt").Add("Hrzn", 0.0).Add("Vrtc", 0.0),
            new PsdDescriptorBuilder("CrPt").Add("Hrzn", 255.0).Add("Vrtc", 255.0),
        });

    /// <summary>陰影：<paramref name="shadowAngle"/> 是陰影落下的方向（螢幕順時針、0 = 右）；PS 存光源角度（對面）。</summary>
    private static PsdDescriptorBuilder Shadow(SKColor color, int opacity, float shadowAngle, float distance, float blur, float spread, bool enabled)
    {
        var light = Normalize(180 - shadowAngle);
        return Common("DrSh", enabled, "Mltp", opacity)
            .Add("Clr ", PsdDesc.Rgb(color))
            .Add("uglg", false)
            .Add("lagl", PsdDesc.Ang(light))
            .Add("Dstn", PsdDesc.Px(distance))
            .Add("Ckmt", PsdDesc.Prc(SpreadPercent(spread, blur)))
            .Add("blur", PsdDesc.Px(blur))
            .Add("Nose", PsdDesc.Prc(0))
            .Add("AntA", false)
            .Add("TrnS", LinearContour())
            .Add("layerConceals", true);
    }

    private static PsdDescriptorBuilder InnerShadow(InnerShadowEffect s, bool enabled) =>
        Common("IrSh", enabled, "Mltp", s.Opacity)
            .Add("Clr ", PsdDesc.Rgb(s.Color))
            .Add("uglg", false)
            .Add("lagl", PsdDesc.Ang(Normalize(s.Angle)))
            .Add("Dstn", PsdDesc.Px(s.Distance))
            .Add("Ckmt", PsdDesc.Prc(Math.Clamp(s.Choke, 0, 100)))
            .Add("blur", PsdDesc.Px(s.Size))
            .Add("Nose", PsdDesc.Prc(0))
            .Add("AntA", false)
            .Add("TrnS", LinearContour());

    private static PsdDescriptorBuilder Glow(string classId, SKColor color, int opacity, float size, float spread, bool enabled) =>
        Common(classId, enabled, "Scrn", opacity)
            .Add("Clr ", PsdDesc.Rgb(color))
            .Add("GlwT", PsdDesc.Enum("BETE", "SfBL"))
            .Add("Ckmt", PsdDesc.Prc(SpreadPercent(spread, size)))
            .Add("blur", PsdDesc.Px(size))
            .Add("Nose", PsdDesc.Prc(0))
            .Add("ShdN", PsdDesc.Prc(0))
            .Add("AntA", false)
            .Add("TrnS", LinearContour())
            .Add("Inpr", PsdDesc.Prc(50));

    private static PsdDescriptorBuilder Stroke(float size, SKColor color, string position, GradientStops? gradient, float angle, bool enabled)
    {
        var fx = Common("FrFX", enabled, "Nrml", Percent(color.Alpha))
            .Add("Styl", PsdDesc.Enum("FStl", position))
            .Add("PntT", PsdDesc.Enum("FrFl", gradient != null ? "GrFl" : "SClr"))
            .Add("Sz  ", PsdDesc.Px(size));
        if (gradient != null)
        {
            fx.Add("Grad", Gradient(gradient))
                .Add("Angl", PsdDesc.Ang(ToPsAngle(angle)))
                .Add("Type", PsdDesc.Enum("GrdT", "Lnr"))
                .Add("Rvrs", false)
                .Add("Algn", true)
                .Add("Scl ", PsdDesc.Prc(100))
                .Add("Ofst", Offset());
        }
        else
        {
            fx.Add("Clr ", PsdDesc.Rgb(color.WithAlpha(255)));
        }
        return fx;
    }

    private static PsdDescriptorBuilder SolidFill(SKColor color, int opacity, bool enabled) =>
        Common("SoFi", enabled, "Nrml", opacity).Add("Clr ", PsdDesc.Rgb(color.WithAlpha(255)));

    private static PsdDescriptorBuilder GradientFill(GradientStops stops, float angle, bool radial, int opacity, bool alignToLayer, bool enabled) =>
        Common("GrFl", enabled, "Nrml", opacity)
            .Add("Grad", Gradient(stops))
            .Add("Angl", PsdDesc.Ang(ToPsAngle(angle)))
            .Add("Type", PsdDesc.Enum("GrdT", radial ? "Rdl" : "Lnr"))
            .Add("Rvrs", false)
            .Add("Algn", alignToLayer)
            .Add("Scl ", PsdDesc.Prc(100))
            .Add("Ofst", Offset());

    private static PsdDescriptorBuilder Bevel(BevelEmbossEffect b, bool enabled)
    {
        var size = Math.Clamp(b.Size, 1, 50);
        return new PsdDescriptorBuilder("ebbl")
            .Add("enab", enabled)
            .Add("present", true)
            .Add("showInDialog", true)
            .Add("hglM", PsdDesc.Blend("Scrn"))
            .Add("hglC", PsdDesc.Rgb(b.HighlightColor))
            .Add("hglO", PsdDesc.Prc(Math.Clamp(b.HighlightOpacity, 0, 100)))
            .Add("sdwM", PsdDesc.Blend("Mltp"))
            .Add("sdwC", PsdDesc.Rgb(b.ShadowColor))
            .Add("sdwO", PsdDesc.Prc(Math.Clamp(b.ShadowOpacity, 0, 100)))
            .Add("bvlT", PsdDesc.Enum("bvlT", "SfBL"))
            .Add("bvlS", PsdDesc.Enum("BESl", b.Style switch { 1 => "OtrB", 2 => "Embs", 3 => "PlEb", _ => "InrB" }))
            .Add("uglg", false)
            .Add("lagl", PsdDesc.Ang(Normalize(b.Angle)))
            .Add("Lald", PsdDesc.Ang(Math.Clamp(b.Altitude, 0, 90)))
            .Add("srgR", PsdDesc.Prc(Math.Clamp(b.Depth, 1, 1000)))
            .Add("blur", PsdDesc.Px(size))
            .Add("Sz  ", PsdDesc.Px(size))    // 讀取端（PsdLayerStyle）看的是這個鍵；Photoshop 看 blur
            .Add("bvlD", PsdDesc.Enum("BESs", b.Up ? "In" : "Out"))
            .Add("Sftn", PsdDesc.Px(Math.Clamp(b.Soften, 0, 16)))
            .Add("useShape", false)
            .Add("useTexture", false)
            .Add("antialiasGloss", false)
            .Add("TrnS", LinearContour());
    }

    /// <summary>Photoshop 漸層：色節點位置 0..4096，透明度節點另存（兩端全不透明）。</summary>
    private static PsdDescriptorBuilder Gradient(GradientStops stops)
    {
        var colors = new List<object>();
        foreach (var stop in stops.Stops)
        {
            colors.Add(new PsdDescriptorBuilder("Clrt")
                .Add("Clr ", PsdDesc.Rgb(stop.Color.WithAlpha(255)))
                .Add("Type", PsdDesc.Enum("Clry", "UsrS"))
                .Add("Lctn", (int)Math.Round(Math.Clamp(stop.Position, 0, 1) * 4096))
                .Add("Mdpn", 50));
        }
        var transparency = new List<object>
        {
            new PsdDescriptorBuilder("TrnS").Add("Opct", PsdDesc.Prc(100)).Add("Lctn", 0).Add("Mdpn", 50),
            new PsdDescriptorBuilder("TrnS").Add("Opct", PsdDesc.Prc(100)).Add("Lctn", 4096).Add("Mdpn", 50),
        };
        return new PsdDescriptorBuilder("Grdn")
            .Add("Nm  ", "Custom")
            .Add("GrdF", PsdDesc.Enum("GrdF", "CstS"))
            .Add("Intr", 4096.0)
            .Add("Clrs", colors)
            .Add("Trns", transparency);
    }

    private static PsdDescriptorBuilder Offset() => new PsdDescriptorBuilder("Pnt ")
        .Add("Hrzn", PsdDesc.Prc(0))
        .Add("Vrtc", PsdDesc.Prc(0));

    // ---- 單位換算 ----

    private static int Percent(byte alpha) => (int)Math.Round(alpha / 2.55);

    /// <summary>展開（px）→ 大小的百分比；沒有模糊時展開就是全部。</summary>
    private static double SpreadPercent(float spread, float size)
    {
        if (spread <= 0) return 0;
        if (size <= 0) return 100;
        return Math.Clamp(spread / size * 100, 0, 100);
    }

    /// <summary>我們的漸層角度（順時針、90 = 由上往下）→ PS（逆時針、90 = 由下往上）。</summary>
    private static double ToPsAngle(float ours) => Normalize(360 - ours);

    /// <summary>PS 的角度寫在 −180..180。</summary>
    private static double Normalize(double degrees)
    {
        var d = degrees % 360;
        if (d > 180) d -= 360;
        if (d <= -180) d += 360;
        return d;
    }
}
