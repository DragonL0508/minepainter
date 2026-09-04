using MinePainter.App.Rendering;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.App.Tests;

/// <summary>
/// 手勢中「轉起來的文字」走的是快照那條路（<see cref="RotatedTextCache"/>）：
/// 快一個數量級，但畫出來的位置必須跟精算的那份完全對得上 —— 差一點點使用者看到的
/// 就是「放開的瞬間字跳一下」。這裡守的就是那個等價性。
/// </summary>
public class RotatedTextCacheTests
{
    private const int Size = 640;

    // 同一個物件（同一個 Id）被手勢逐幀 with 出新樣子 —— 與 TransformSession 的做法一致
    private static readonly TextElement Base = new()
    {
        Text = "MinePainter 特效",
        FontSize = 64f,
        Position = new SKPoint(120, 240),
        Color = SKColors.Black,
        Glow = new TextGlow { Size = 20, Spread = 4 },
        Stroke = new TextStroke { Width = 5 },
    };

    private static TextElement Sample(float rotation) => Base with { Rotation = rotation };

    private static SKBitmap Render(Action<SKCanvas> draw)
    {
        using var surface = SKSurface.Create(
            new SKImageInfo(Size, Size, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        draw(surface.Canvas);
        surface.Canvas.Flush();
        using var image = surface.Snapshot();
        return SKBitmap.FromImage(image);
    }

    /// <summary>兩張圖的平均通道差（0＝完全一樣，255＝完全相反）。</summary>
    private static double MeanDiff(SKBitmap a, SKBitmap b)
    {
        double sum = 0;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var pa = a.GetPixel(x, y);
                var pb = b.GetPixel(x, y);
                sum += Math.Abs(pa.Red - pb.Red) + Math.Abs(pa.Green - pb.Green) +
                       Math.Abs(pa.Blue - pb.Blue) + Math.Abs(pa.Alpha - pb.Alpha);
            }
        }
        return sum / (Size * (double)Size * 4);
    }

    /// <summary>內容的「重心」：位置有沒有跑掉，看這個最直接（重取樣不會動到它）。</summary>
    private static (double X, double Y, double Mass) Centroid(SKBitmap bmp)
    {
        double sx = 0, sy = 0, m = 0;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                double a = bmp.GetPixel(x, y).Alpha;
                sx += x * a; sy += y * a; m += a;
            }
        }
        return m > 0 ? (sx / m, sy / m, m) : (0, 0, 0);
    }

    [Fact]
    public void 快照畫出來的位置要跟精算的一致()
    {
        var text = Sample(37f);
        using var direct = Render(c => text.Render(c));

        using var cache = new RotatedTextCache();
        using var cached = Render(c =>
        {
            Assert.False(cache.TryDraw(c, text)); // 第一幀只登記，還不點陣化
            Assert.True(cache.TryDraw(c, text));  // 第二幀才走快照
        });

        var a = Centroid(direct);
        var b = Centroid(cached);
        Assert.True(a.Mass > 0, "精算那份沒畫出東西，測試本身壞了");
        Assert.InRange(b.X - a.X, -0.6, 0.6);
        Assert.InRange(b.Y - a.Y, -0.6, 0.6);
        Assert.InRange(b.Mass / a.Mass, 0.97, 1.03);
        Assert.True(MeanDiff(direct, cached) < 3.0, $"重取樣的誤差過大：{MeanDiff(direct, cached):F2}");
    }

    [Fact]
    public void 換了角度照樣沿用同一張快照()
    {
        using var cache = new RotatedTextCache();
        using var _ = Render(c =>
        {
            Assert.False(cache.TryDraw(c, Sample(10f)));
            Assert.True(cache.TryDraw(c, Sample(10f)));
            // 只有角度與位置變＝同一份快照（旋轉手勢每幀就是這樣）
            Assert.True(cache.TryDraw(c, Sample(48f)));
        });
    }

    [Fact]
    public void 字級變了不沿用快照()
    {
        using var cache = new RotatedTextCache();
        using var _ = Render(c =>
        {
            Assert.False(cache.TryDraw(c, Sample(10f)));
            Assert.True(cache.TryDraw(c, Sample(10f)));
            // 縮放手勢：每幀的字級都不一樣，快照沒有意義，要退回精算
            Assert.False(cache.TryDraw(c, Sample(10f) with { FontSize = 70f }));
        });
    }

    [Fact]
    public void 沒轉的文字不走快照()
    {
        using var cache = new RotatedTextCache();
        using var _ = Render(c => Assert.False(cache.TryDraw(c, Sample(0f))));
    }
}
