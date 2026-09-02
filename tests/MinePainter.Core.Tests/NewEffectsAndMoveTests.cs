using MinePainter.Core.Effects;
using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Selections;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;
using static MinePainter.Core.Effects.EffectMath;

namespace MinePainter.Core.Tests;

public class NewEffectsAndMoveTests
{
    // ---- 顏色透明化 ----

    [Fact]
    public void ColorToAlpha_ClearsMatchingColorAndKeepsOthers()
    {
        var src = new uint[4];
        src[0] = Premul(0, 0, 255, 255);        // 純紅（= 要清掉的顏色）
        src[1] = Premul(0, 10, 245, 255);       // 接近紅（在容許度內）
        src[2] = Premul(255, 0, 0, 255);        // 藍（保留）
        src[3] = 0;                             // 本來就透明
        var ctx = EffectContext.FromPixels(src, 4, 1);

        new ColorToAlphaEffect { Color = SKColors.Red, Tolerance = 20, Softness = 0 }.Render(ctx);

        Assert.Equal(0, A(ctx.Dst[0]));
        Assert.Equal(0, A(ctx.Dst[1]));
        Assert.Equal(255, A(ctx.Dst[2]));
        Assert.Equal(0, A(ctx.Dst[3]));
    }

    [Fact]
    public void ColorToAlpha_SoftnessGivesPartialAlpha()
    {
        var src = new uint[] { Premul(0, 0, 205, 255) }; // 與紅差 50
        var ctx = EffectContext.FromPixels(src, 1, 1);
        new ColorToAlphaEffect { Color = SKColors.Red, Tolerance = 0, Softness = 100 }.Render(ctx);
        Assert.InRange(A(ctx.Dst[0]), 100, 155); // 50/100 ≈ 半透明
    }

    [Fact]
    public void ColorToAlpha_InvertKeepsOnlyThatColor()
    {
        var src = new uint[] { Premul(0, 0, 255, 255), Premul(255, 0, 0, 255) };
        var ctx = EffectContext.FromPixels(src, 2, 1);
        new ColorToAlphaEffect { Color = SKColors.Red, Tolerance = 20, Softness = 0, Invert = true }.Render(ctx);
        Assert.Equal(255, A(ctx.Dst[0]));
        Assert.Equal(0, A(ctx.Dst[1]));
    }

    // ---- 傾斜 ----

    [Fact]
    public void Skew_ZeroAngles_IsIdentity()
    {
        var src = new uint[16 * 16];
        for (var i = 0; i < src.Length; i++) src[i] = Premul(i % 255, 30, 60, 255);
        var ctx = EffectContext.FromPixels(src, 16, 16);
        new SkewEffect { Horizontal = 0, Vertical = 0 }.Render(ctx);
        Assert.Equal(src, ctx.Dst);
    }

    [Fact]
    public void Skew_HorizontalMovesTopRowRight()
    {
        // 中央一根垂直線；正的水平傾斜＝上面往右倒（下面往左）
        const int n = 32;
        var src = new uint[n * n];
        for (var y = 0; y < n; y++) src[y * n + 16] = Premul(255, 255, 255, 255);
        var ctx = EffectContext.FromPixels(src, n, n);
        new SkewEffect { Horizontal = 45, Vertical = 0, Pivot = 0 }.Render(ctx);

        Assert.True(BrightestX(ctx.Dst, n, 4) > 16, "上方應該往右移");
        Assert.True(BrightestX(ctx.Dst, n, n - 5) < 16, "下方應該往左移");
    }

    private static int BrightestX(uint[] pixels, int width, int row)
    {
        var best = 0;
        var bestA = -1;
        for (var x = 0; x < width; x++)
        {
            var a = A(pixels[row * width + x]);
            if (a > bestA) (bestA, best) = (a, x);
        }
        return best;
    }

    // ---- 放射狀模糊：角度愈大愈糊，而且不會爆炸 ----

    [Fact]
    public void RadialBlur_ZeroAngleIsIdentity()
    {
        var src = new uint[24 * 24];
        for (var i = 0; i < src.Length; i++) src[i] = Premul(i % 200, 90, 10, 255);
        var ctx = EffectContext.FromPixels(src, 24, 24);
        new RadialBlurEffect { Angle = 0 }.Render(ctx);
        Assert.Equal(src, ctx.Dst);
    }

    [Fact]
    public void RadialBlur_MoreAngleBlursMore()
    {
        const int n = 64;
        uint[] Make()
        {
            var a = new uint[n * n];
            for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
                a[y * n + x] = x < n / 2 ? Premul(0, 0, 0, 255) : Premul(255, 255, 255, 255);
            return a;
        }

        double Spread(float angle)
        {
            var ctx = EffectContext.FromPixels(Make(), n, n);
            new RadialBlurEffect { Angle = angle }.Render(ctx);
            // 邊界那一列有多少「既不是全黑也不是全白」的像素＝糊掉的程度
            var count = 0;
            for (var x = 0; x < n; x++)
            {
                var v = B(ctx.Dst[4 * n + x]);
                if (v is > 10 and < 245) count++;
            }
            return count;
        }

        Assert.True(Spread(60) > Spread(10), "角度愈大應該愈糊");
        Assert.True(Spread(360) >= Spread(60), "拉到底仍然是連續變化，不該反而變回原樣");
    }

    [Fact]
    public void RadialBlur_KeepsOpaqueImageOpaque()
    {
        const int n = 48;
        var src = new uint[n * n];
        for (var y = 0; y < n; y++)
        for (var x = 0; x < n; x++)
            src[y * n + x] = Premul(x * 5 % 256, y * 5 % 256, 128, 255);
        var ctx = EffectContext.FromPixels(src, n, n);
        new RadialBlurEffect { Angle = 45 }.Render(ctx);
        Assert.All(ctx.Dst, p => Assert.Equal(255, A(p)));
    }

    // ---- 全選：文字圖層不給選 ----

    [Fact]
    public void SelectAll_DoesNothingOnTextLayer()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(128, 128, SKColors.White));
        var text = VectorCommands.CreateTextLayerSilently(session.Document);
        lock (session.Document.SyncRoot)
        {
            text.AddElement(new TextElement { Text = "abc", Position = new SKPoint(10, 10), FontSize = 24 });
            session.Document.ActiveLayer = text;
        }

        EditCommands.SelectAll(session);
        Assert.Null(session.Selection);

        // 一般圖層照樣可以全選
        lock (session.Document.SyncRoot) session.Document.ActiveLayer = session.Document.Root.Children[0];
        EditCommands.SelectAll(session);
        Assert.NotNull(session.Selection);
    }

    // ---- 移動工具：整層平移時選取範圍跟著走 ----

    [Fact]
    public void MoveLayer_MovesSelectionWithIt()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(512, 512, SKColors.White));
        var layer = (RasterLayer)session.Document.ActiveLayer!;
        layer.Surface.Fill(new SKRectI(100, 100, 200, 200), new SKColor(255, 0, 0));

        // 文字圖層才會走「整層平移」那條（一般圖層有選取時是提起選取內容）
        var text = VectorCommands.CreateTextLayerSilently(session.Document);
        lock (session.Document.SyncRoot)
        {
            text.AddElement(new TextElement { Text = "abc", Position = new SKPoint(120, 120), FontSize = 24 });
            session.Document.ActiveLayer = text;
        }

        using var path = new SKPath();
        path.AddRect(SKRect.Create(100, 100, 100, 100));
        session.Selection = SelectionMask.FromPath(path, session.Document.Bounds);

        // 從選取範圍外拖曳
        session.Move.OnPointerDown(new ToolPointerEvent(new SKPoint(400, 400), 1f), session);
        session.Move.OnPointerMove(new ToolPointerEvent(new SKPoint(450, 430), 1f), session);
        session.Move.OnPointerUp(new ToolPointerEvent(new SKPoint(450, 430), 1f), session);

        Assert.Equal(new SKPointI(50, 30), text.Offset);
        Assert.Equal(150, session.Selection!.Bounds.Left);
        Assert.Equal(130, session.Selection!.Bounds.Top);

        // 一步 undo：圖層與選取範圍一起回去
        session.Undo();
        Assert.Equal(SKPointI.Empty, text.Offset);
        Assert.Equal(100, session.Selection!.Bounds.Left);
        Assert.Equal(100, session.Selection!.Bounds.Top);
    }
}
