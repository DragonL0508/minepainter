using MinePainter.Core.Effects;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 「角度跟著物件轉」要涵蓋每一個指得出方向的效果（使用者 2026-09-05 明示：
/// 陰影的方向也要跟著物件，對標傾斜與漸層）。
///
/// 這裡守三件事：
/// 　1. 每個有角度參數的效果都要提供這個開關（新加的效果忘了就會被抓到）
/// 　2. 開關打開時真的有把物件角度算進去；關掉時物件轉幾度都一樣（舊行為）
/// 　3. 方向的正負：陰影往右的位移，物件順時針轉 90° 之後要跑到正下方
/// </summary>
public class AngleFollowsObjectTests
{
    private const int Size = 160;
    private const string FollowKey = "relative";

    /// <summary>
    /// 角度不是「方向」的效果，跟著物件轉沒有意義，所以不強制要有開關：
    /// 放射狀模糊的「角度」是繞著中心掃過的**總角度**（對稱的，轉了也一樣）。
    /// </summary>
    private static readonly HashSet<string> NoDirection = ["radialBlur"];

    /// <summary>中央一塊不對稱的圖形（左上角多一塊），轉了角度的效果結果才會不一樣。</summary>
    private static uint[] Blob()
    {
        var pixels = new uint[Size * Size];
        for (var y = 64; y < 96; y++)
        for (var x = 64; x < 96; x++)
        {
            pixels[y * Size + x] = 0xFF3080C0;
        }
        for (var y = 56; y < 64; y++)
        for (var x = 64; x < 80; x++)
        {
            pixels[y * Size + x] = 0xFF3080C0;
        }
        return pixels;
    }

    private static uint[] Render(IEffect effect, float contentRotation, uint[]? source = null)
    {
        var rect = new SKRectI(0, 0, Size, Size);
        var ctx = new EffectContext(rect, rect, source ?? Blob(), new SKSizeI(Size, Size))
        {
            ContentRotation = contentRotation,
        };
        effect.Render(ctx);
        return ctx.Dst;
    }

    /// <summary>從參數表把「跟著物件轉」設成指定值（同時證明這個效果真的有這個開關）。</summary>
    private static IEffect WithFollow(IEffect effect, bool follow)
    {
        var param = effect.Parameters.OfType<BoolParam>().FirstOrDefault(p => p.Key == FollowKey);
        Assert.True(param != null, $"「{effect.Name}」沒有「跟著物件轉」的開關");
        return (IEffect)param!.With(effect, follow);
    }

    /// <summary>指得出方向的效果 + 一個會用到方向的參數組合。</summary>
    public static TheoryData<string, IEffect> Directional() => new()
    {
        { "動態模糊", new MotionBlurEffect() },
        { "碎片", new FragmentEffect { Distance = 12 } },
        { "拼貼反射", new TileReflectionEffect() },
        { "傾斜", new SkewEffect() },
        { "外框（漸層）", new ObjectOutlineEffect { Gradient = true, Width = 6 } },
        { "陰影", new ObjectShadowEffect { OffsetX = 20, OffsetY = 0, Blur = 0 } },
        { "漸層", new ObjectGradientEffect() },
        { "內光暈", new InnerGlowEffect { Directional = true } },
        { "茱莉亞碎形", new JuliaFractalEffect() },
        { "曼德博碎形", new MandelbrotFractalEffect() },
        { "邊緣偵測", new EdgeDetectEffect() },
        { "浮雕", new EmbossEffect() },
        { "浮雕效果", new ReliefEffect() },
    };

    [Theory]
    [MemberData(nameof(Directional))]
    public void 開著時物件的角度會算進去(string name, IEffect effect)
    {
        var follows = WithFollow(effect, true);
        Assert.NotEqual(Render(follows, 0f), Render(follows, 40f));
        _ = name;
    }

    [Theory]
    [MemberData(nameof(Directional))]
    public void 關掉時物件轉幾度都一樣(string name, IEffect effect)
    {
        var fixedToCanvas = WithFollow(effect, false);
        Assert.Equal(Render(fixedToCanvas, 0f), Render(fixedToCanvas, 40f));
        _ = name;
    }

    /// <summary>新加的效果只要有角度參數就必須提供開關（放射狀模糊那種「總角度」除外）。</summary>
    [Fact]
    public void 每個有角度參數的效果都要有跟著物件轉的開關()
    {
        // 內光暈的角度只在「指定角度」模式下出現，參數表要用那個模式的
        IEnumerable<(string Id, IEffect Effect)> all =
        [
            .. EffectRegistry.All.Select(e => (e.Id, e.Create())),
            ("innerGlow", new InnerGlowEffect { Directional = true }),
        ];

        foreach (var (id, effect) in all)
        {
            if (NoDirection.Contains(id)) continue;
            if (!effect.Parameters.OfType<AngleParam>().Any()) continue;
            Assert.True(
                effect.Parameters.OfType<BoolParam>().Any(p => p.Key == FollowKey),
                $"「{effect.Name}」({id}) 有角度參數卻沒有「角度跟著物件轉」");
        }
    }

    /// <summary>
    /// 正負號：位移 (40, 0)（往右）的陰影，物件順時針轉 90° 之後應該落在正下方。
    /// 螢幕座標 y 朝下，順時針 90° 就是「右 → 下」。位移拉到 40 是為了讓陰影完全露在
    /// 物件外面 —— 半掩的話量到的是「露出來那半塊」的重心，不是陰影自己的位置。
    /// </summary>
    [Fact]
    public void 陰影的位移方向跟著物件轉()
    {
        var shadow = new ObjectShadowEffect
        {
            OffsetX = 40,
            OffsetY = 0,
            Blur = 0,
            Opacity = 100,
            Color = SKColors.Red,
            RelativeToObject = true,
        };

        var (baseX, baseY) = ShadowCentre(Render(shadow, 0f));
        var (turnedX, turnedY) = ShadowCentre(Render(shadow, 90f));

        // 沒轉：陰影在物件右邊；轉了 90°：跑到正下方（同樣的距離）
        Assert.InRange(turnedX - baseX, -41, -39);
        Assert.InRange(turnedY - baseY, 39, 41);
    }

    /// <summary>純紅（陰影露出來的部分）像素的重心。</summary>
    private static (double X, double Y) ShadowCentre(uint[] pixels)
    {
        double sx = 0, sy = 0;
        var n = 0;
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var p = pixels[y * Size + x];
            var a = (p >> 24) & 0xFF;
            var r = (p >> 16) & 0xFF;
            var g = (p >> 8) & 0xFF;
            var b = p & 0xFF;
            if (a < 250 || r < 200 || g > 40 || b > 40) continue;
            sx += x; sy += y; n++;
        }
        Assert.True(n > 0, "畫面上找不到陰影");
        return (sx / n, sy / n);
    }
}
