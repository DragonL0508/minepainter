using MinePainter.Core.Effects;
using Xunit;
using static MinePainter.Core.Effects.EffectMath;

namespace MinePainter.Core.Tests;

public class BackgroundRemovalAndFeatherTests
{
    private static uint[] Canvas(int w, int h, Func<int, int, uint> f)
    {
        var a = new uint[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            a[y * w + x] = f(x, y);
        return a;
    }

    [Fact]
    public void Feather_FadesEdgeInward_KeepsCore()
    {
        const int w = 64, h = 64;
        // 中央 40×40 的不透明紅色方塊
        var src = Canvas(w, h, (x, y) => x is >= 12 and < 52 && y is >= 12 and < 52 ? Premul(0, 0, 255, 255) : 0);
        var fx = new ObjectFeatherEffect { Radius = 8, Strength = 100 };
        var ctx = EffectContext.FromPixels(src, w, h, fx.SourceMargin);
        fx.Render(ctx);

        Assert.Equal(255, A(ctx.Dst[32 * w + 32]));         // 核心不變
        Assert.InRange(A(ctx.Dst[32 * w + 12]), 0, 40);     // 最外圈幾乎透明
        Assert.InRange(A(ctx.Dst[32 * w + 16]), 60, 200);   // 中段半透明
        Assert.Equal(0u, ctx.Dst[32 * w + 5]);               // 原本透明的仍透明
        // 單調：越往內越不透明
        Assert.True(A(ctx.Dst[32 * w + 14]) < A(ctx.Dst[32 * w + 17]));
    }

    [Fact]
    public void Feather_Strength_LimitsHowTransparentEdgeGets()
    {
        const int w = 32, h = 32;
        var src = Canvas(w, h, (x, y) => x >= 8 && x < 24 && y >= 8 && y < 24 ? Premul(0, 0, 255, 255) : 0);
        var fx = new ObjectFeatherEffect { Radius = 6, Strength = 50 };
        var ctx = EffectContext.FromPixels(src, w, h, fx.SourceMargin);
        fx.Render(ctx);
        Assert.InRange(A(ctx.Dst[16 * w + 8]), 120, 140); // 最外圈保留約 50%
    }

    [Fact]
    public void Feather_CanvasEdgeOption()
    {
        const int w = 32, h = 32;
        var src = Canvas(w, h, (_, _) => Premul(0, 0, 255, 255)); // 整層填滿
        var keep = new ObjectFeatherEffect { Radius = 6, FeatherCanvasEdge = false };
        var ctx = EffectContext.FromPixels(src, w, h, keep.SourceMargin);
        keep.Render(ctx);
        Assert.Equal(255, A(ctx.Dst[0])); // 畫布邊不算物件邊 → 不羽化

        var fade = new ObjectFeatherEffect { Radius = 6, FeatherCanvasEdge = true };
        ctx = EffectContext.FromPixels(src, w, h, fade.SourceMargin);
        fade.Render(ctx);
        Assert.InRange(A(ctx.Dst[0]), 0, 40);
    }

    [Fact]
    public void BackgroundRemoval_WithoutModels_PassesThrough()
    {
        var src = Canvas(8, 8, (_, _) => Premul(1, 2, 3, 255));
        var fx = new BackgroundRemovalEffect([]);
        Assert.Null(fx.SelectedModel);
        var ctx = EffectContext.FromPixels(src, 8, 8);
        fx.Render(ctx);
        Assert.Equal(src, ctx.Dst);
    }

    /// <summary>
    /// 真的跑一次模型：只在環境變數 MINEPAINTER_TEST_MODELS 指到含 u2netp.onnx 的資料夾時執行。
    /// 圖：灰底中央一個高對比的深色圓（顯著物件），期望圓內 alpha 高、角落 alpha 低。
    /// </summary>
    [Fact]
    public void BackgroundRemoval_RealModel_SeparatesSalientObject()
    {
        var dir = Environment.GetEnvironmentVariable("MINEPAINTER_TEST_MODELS");
        if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "u2netp.onnx"))) return;

        // 不動靜態的 ModelDirectories：其他測試（如「所有效果預設渲染」）平行跑時不該看到模型
        var model = new OnnxModelInfo("u2netp", Path.Combine(dir, "u2netp.onnx"));
        var fx = new BackgroundRemovalEffect([model]);

        const int w = 256, h = 256;
        var src = Canvas(w, h, (x, y) =>
        {
            var dx = x - 128; var dy = y - 128;
            return dx * dx + dy * dy < 60 * 60 ? Premul(30, 20, 200, 255) : Premul(200, 200, 200, 255);
        });
        var ctx = EffectContext.FromPixels(src, w, h);
        fx.Render(ctx);

        Assert.True(A(ctx.Dst[128 * w + 128]) > 180, $"center alpha {A(ctx.Dst[128 * w + 128])}");
        Assert.True(A(ctx.Dst[4 * w + 4]) < 80, $"corner alpha {A(ctx.Dst[4 * w + 4])}");
    }
}
