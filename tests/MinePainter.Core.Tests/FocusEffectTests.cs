using MinePainter.Core.Effects;
using Xunit;
using static MinePainter.Core.Effects.EffectMath;

namespace MinePainter.Core.Tests;

/// <summary>聚焦效果：焦點內原樣、往外漸漸套上模式對應的變化。</summary>
public class FocusEffectTests
{
    private const int W = 96, H = 96;

    /// <summary>2px 黑白棋盤：模糊會抹成灰，對比／飽和度看得出數值變化。</summary>
    private static uint[] Checker()
    {
        var src = new uint[W * H];
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
            src[y * W + x] = ((x / 2 + y / 2) % 2 == 0) ? Pack(0, 0, 0, 255) : Pack(255, 255, 255, 255);
        return src;
    }

    private static uint[] SolidColor(int b, int g, int r)
    {
        var src = new uint[W * H];
        Array.Fill(src, Pack(b, g, r, 255));
        return src;
    }

    private static uint[] Render(FocusEffect fx, uint[] src)
    {
        var margin = fx.SourceMargin == EffectContext.WholeLayer ? 0 : fx.SourceMargin;
        var ctx = EffectContext.FromPixels(src, W, H, margin);
        fx.Render(ctx);
        return ctx.Dst;
    }

    private static uint Center(uint[] px) => px[(H / 2) * W + W / 2];
    private static uint Corner(uint[] px) => px[3 * W + 3];

    [Fact]
    public void 景深模式_中心清楚_角落模糊()
    {
        var src = Checker();
        var dst = Render(new FocusEffect { Mode = FocusEffect.ModeDepth, Radius = 20, Feather = 30, BlurRadius = 8 }, src);

        Assert.Equal(src[(H / 2) * W + W / 2], Center(dst)); // 焦點內一個像素都不能動
        var c = B(Corner(dst));
        Assert.InRange(c, 40, 215); // 角落的棋盤被抹成灰＝有模糊到
    }

    [Fact]
    public void 飽和度模式_中心原色_角落去色()
    {
        var src = SolidColor(30, 60, 220);
        var dst = Render(new FocusEffect { Mode = FocusEffect.ModeSaturation, Radius = 20, Feather = 30, Saturation = -100 }, src);

        Assert.Equal(src[0], Center(dst));
        var p = Corner(dst);
        Assert.InRange(Math.Abs(R(p) - B(p)), 0, 3); // 角落三通道幾乎相等＝灰階
        Assert.Equal(255, A(p));
    }

    [Fact]
    public void 對比模式_角落對比拉高()
    {
        var src = new uint[W * H];
        for (var i = 0; i < src.Length; i++) src[i] = (i % 2 == 0) ? Pack(100, 100, 100, 255) : Pack(156, 156, 156, 255);
        var dst = Render(new FocusEffect { Mode = FocusEffect.ModeContrast, Radius = 20, Feather = 30, Contrast = 100 }, src);

        Assert.Equal(100, B(Center(dst)));
        var lo = B(dst[3 * W + 2]);
        var hi = B(dst[3 * W + 3]);
        Assert.True(hi - lo > 56 * 2, $"角落兩相鄰像素差 {hi - lo}，應明顯大於原本的 56，代表對比沒往外拉高");
    }

    [Fact]
    public void 亮度模式_角落變暗_中心不動()
    {
        var src = SolidColor(200, 200, 200);
        var dst = Render(new FocusEffect { Mode = FocusEffect.ModeBrightness, Radius = 20, Feather = 30, Brightness = -100 }, src);
        Assert.Equal(200, B(Center(dst)));
        Assert.InRange(B(Corner(dst)), 0, 40);
    }

    [Fact]
    public void 反轉_焦點內套效果_角落原樣()
    {
        var src = SolidColor(200, 200, 200);
        var dst = Render(new FocusEffect { Mode = FocusEffect.ModeBrightness, Radius = 20, Feather = 30, Brightness = -100, Invert = true }, src);
        Assert.Equal(200, B(Corner(dst)));
        Assert.InRange(B(Center(dst)), 0, 40);
    }

    [Fact]
    public void 中心可移動()
    {
        var src = SolidColor(200, 200, 200);
        var dst = Render(new FocusEffect { Mode = FocusEffect.ModeBrightness, Radius = 15, Feather = 20, Brightness = -100, CenterX = -0.9f, CenterY = -0.9f }, src);
        Assert.Equal(200, B(Corner(dst))); // 焦點搬到左上角，左上角就變成清楚的
        Assert.InRange(B(Center(dst)), 0, 40);
    }

    [Fact]
    public void 存檔往返_只顯示目前模式的參數但都存得回來()
    {
        var fx = new FocusEffect { Mode = FocusEffect.ModeSaturation, Saturation = -40, Radius = 33, Feather = 12, Curve = 2.5f, Elliptical = true, CenterX = 0.3f };
        var typeId = EffectSerializer.TypeIdOf(fx);
        var saved = EffectSerializer.Save(fx);
        Assert.Equal("focus", typeId);
        Assert.DoesNotContain("blurRadius", saved.Keys); // 非目前模式的滑桿不顯示、也不存

        var loaded = Assert.IsType<FocusEffect>(EffectSerializer.Load(typeId, saved));
        Assert.Equal(fx.Mode, loaded.Mode);
        Assert.Equal(fx.Saturation, loaded.Saturation);
        Assert.Equal(fx.Radius, loaded.Radius);
        Assert.Equal(fx.Feather, loaded.Feather);
        Assert.Equal(fx.Curve, loaded.Curve, 2);
        Assert.True(loaded.Elliptical);
        Assert.Equal(fx.CenterX, loaded.CenterX, 3);
    }
}
