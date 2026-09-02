namespace MinePainter.Core.Effects;

/// <summary>
/// 效果（paint.net 的「效果」選單；也可作為圖層的非破壞性效果堆疊條目）。
/// 不可變 record：參數改動 = 新實例。
/// Render 在背景執行緒被呼叫，讀 <see cref="EffectContext.Src"/>、寫 <see cref="EffectContext.Dst"/>。
/// </summary>
public interface IEffect : IParameterized
{
    string Name { get; }

    /// <summary>選單分類（藝術／模糊／扭曲／雜訊／物件／相片／演算／風格化）。</summary>
    string Category { get; }

    /// <summary>
    /// 目標範圍外還需要多少來源像素（卷積半徑之類）；<see cref="EffectContext.WholeLayer"/>
    /// 表示要整張圖層（扭曲類會取樣任何位置）。
    /// </summary>
    int SourceMargin { get; }

    /// <summary>
    /// 結果是否只取決於局部鄰域（與目標範圍的原點／大小無關）。
    /// true 的效果可以只重算髒區；以「範圍中心」或「格子對齊」為準的效果（暈影、像素化、碎形…）必須整層重算。
    /// </summary>
    bool IsPositionIndependent => true;

    void Render(EffectContext ctx);
}

/// <summary>效果目錄：效果選單由此長出；Id 為存檔用的穩定識別。</summary>
public static class EffectRegistry
{
    public sealed record Entry(string Id, string Category, string Name, Func<IEffect> Create);

    public static readonly string[] Categories = ["藝術", "模糊", "扭曲", "雜訊", "物件", "相片", "演算", "風格化"];

    public static readonly Entry[] All =
    [
        new("inkSketch", "藝術", "墨水素描", () => new InkSketchEffect()),
        new("oilPainting", "藝術", "油畫", () => new OilPaintingEffect()),
        new("pencilSketch", "藝術", "鉛筆素描", () => new PencilSketchEffect()),

        new("bokeh", "模糊", "散景", () => new BokehEffect()),
        new("fragment", "模糊", "碎片", () => new FragmentEffect()),
        new("gaussianBlur", "模糊", "高斯模糊", () => new GaussianBlurEffect()),
        new("motionBlur", "模糊", "動態模糊", () => new MotionBlurEffect()),
        new("radialBlur", "模糊", "放射狀模糊", () => new RadialBlurEffect()),
        new("surfaceBlur", "模糊", "表面模糊", () => new SurfaceBlurEffect()),
        new("unfocus", "模糊", "失焦", () => new UnfocusEffect()),
        new("zoomBlur", "模糊", "縮放模糊", () => new ZoomBlurEffect()),

        new("bulge", "扭曲", "凸起", () => new BulgeEffect()),
        new("crystalize", "扭曲", "結晶化", () => new CrystalizeEffect()),
        new("dents", "扭曲", "凹痕", () => new DentsEffect()),
        new("frostedGlass", "扭曲", "霧面玻璃", () => new FrostedGlassEffect()),
        new("pixelate", "扭曲", "像素化", () => new PixelateEffect()),
        new("polarInversion", "扭曲", "極座標反轉", () => new PolarInversionEffect()),
        new("tileReflection", "扭曲", "拼貼反射", () => new TileReflectionEffect()),
        new("twist", "扭曲", "扭轉", () => new TwistEffect()),

        new("addNoise", "雜訊", "加入雜訊", () => new AddNoiseEffect()),
        new("median", "雜訊", "中位數", () => new MedianEffect()),
        new("reduceNoise", "雜訊", "降低雜訊", () => new ReduceNoiseEffect()),

        new("objectOutline", "物件", "物件外框", () => new ObjectOutlineEffect()),
        new("objectShadow", "物件", "物件陰影", () => new ObjectShadowEffect()),
        new("objectGlow", "物件", "物件光暈", () => new ObjectGlowEffect()),
        new("objectGradient", "物件", "物件漸層", () => new ObjectGradientEffect()),

        new("glow", "相片", "光暈", () => new GlowEffect()),
        new("redEye", "相片", "紅眼移除", () => new RedEyeRemovalEffect()),
        new("sharpen", "相片", "銳利化", () => new SharpenEffect()),
        new("softenPortrait", "相片", "柔化人像", () => new SoftenPortraitEffect()),
        new("vignette", "相片", "暈影", () => new VignetteEffect()),

        new("clouds", "演算", "雲朵", () => new CloudsEffect()),
        new("julia", "演算", "茱莉亞碎形", () => new JuliaFractalEffect()),
        new("mandelbrot", "演算", "曼德博碎形", () => new MandelbrotFractalEffect()),

        new("edgeDetect", "風格化", "邊緣偵測", () => new EdgeDetectEffect()),
        new("emboss", "風格化", "浮雕", () => new EmbossEffect()),
        new("outline", "風格化", "外框", () => new OutlineEffect()),
        new("relief", "風格化", "浮雕效果", () => new ReliefEffect()),
    ];

    public static IEnumerable<Entry> InCategory(string category) => All.Where(e => e.Category == category);
}

/// <summary>效果對話框的即時預覽目標：破壞性（EffectSession）與圖層效果堆疊（LayerEffectPreview）共用。</summary>
public interface IEffectPreviewTarget
{
    /// <summary>套用目前參數讓畫布顯示結果（可在背景執行緒呼叫；取消時丟 OperationCanceledException）。</summary>
    void Preview(IEffect effect, CancellationToken ct);

    /// <summary>來源的 RGB 直方圖（色階／曲線 UI）。</summary>
    long[] Histogram();

    /// <summary>來源縮圖（選點器底圖）。</summary>
    SkiaSharp.SKBitmap RenderThumbnail(int maxSize);
}
