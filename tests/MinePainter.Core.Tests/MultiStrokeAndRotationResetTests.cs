using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>多層外框（PS 式疊多個「筆畫」）＋畫布上的「角度重置」。</summary>
public class MultiStrokeAndRotationResetTests : IDisposable
{
    private readonly string _tempPath = Path.Combine(Path.GetTempPath(), $"mpp_ms_{Guid.NewGuid():N}.mpp");

    public void Dispose()
    {
        if (File.Exists(_tempPath)) File.Delete(_tempPath);
    }

    private static readonly TextElement Base = new()
    {
        Text = "AB",
        FontFamily = "Arial",
        FontSize = 40,
        Color = SKColors.Red,
        Position = new SKPoint(80, 80),
    };

    private static SKBitmap Render(TextElement element, int width = 400, int height = 240)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        element.Render(canvas);
        canvas.Flush();
        return bitmap;
    }

    private static int CountOf(SKBitmap bmp, SKColor color)
    {
        var count = 0;
        for (var y = 0; y < bmp.Height; y++)
        for (var x = 0; x < bmp.Width; x++)
        {
            var p = bmp.GetPixel(x, y);
            if (p.Alpha > 200 && p.Red == color.Red && p.Green == color.Green && p.Blue == color.Blue)
                count++;
        }
        return count;
    }

    [Fact]
    public void StrokeChain_LayersAndFromLayers_RoundTrip()
    {
        var layers = new List<TextStroke>
        {
            new() { Color = SKColors.White, Width = 2 },
            new() { Color = SKColors.Black, Width = 3 },
            new() { Color = SKColors.Blue, Width = 4 },
        };
        var chain = TextStroke.FromLayers(layers)!;

        Assert.Equal(layers, chain.Layers().Select(s => s with { Outer = null }).ToList());
        Assert.Equal(9f, chain.TotalWidth);
        Assert.Null(TextStroke.FromLayers([]));

        // record 的值相等要涵蓋整條鏈 —— undo 落地各處靠 Equals 判斷「有沒有變」
        Assert.Equal(chain, TextStroke.FromLayers(layers));
        Assert.NotEqual(chain, TextStroke.FromLayers(layers.Take(2).ToList()));
    }

    [Fact]
    public void MultiStroke_OuterLayerVisibleOutsideInner_AndBoundsGrow()
    {
        var inner = new SKColor(0, 200, 0);
        var outer = new SKColor(0, 0, 250);
        var single = Base with { Stroke = new TextStroke { Color = inner, Width = 4 } };
        var doubled = Base with
        {
            Stroke = new TextStroke
            {
                Color = inner, Width = 4,
                Outer = new TextStroke { Color = outer, Width = 5 },
            },
        };

        using var one = Render(single);
        using var two = Render(doubled);

        Assert.True(CountOf(two, outer) > 0, "第二層外框要看得到");
        // 內層不能被外層蓋掉（外層畫在內層之下）
        Assert.True(CountOf(two, inner) > CountOf(one, inner) * 0.8, "內層外框應仍可見");
        Assert.True(CountOf(two, SKColors.Red) > 0, "字身仍在最上層");

        var b1 = single.Bounds;
        var b2 = doubled.Bounds;
        Assert.True(b2.Width > b1.Width && b2.Height > b1.Height, "失效區要含所有層的寬度");
    }

    [Fact]
    public void MultiStroke_RoundTripsThroughMpp()
    {
        using var doc = ImageCodec.CreateBlankDocument(200, 120, SKColors.White);
        var layer = (RasterLayer)doc.Root.Children[0];
        var stroke = new TextStroke
        {
            Color = new SKColor(1, 2, 3), Width = 2,
            Outer = new TextStroke
            {
                Color = new SKColor(4, 5, 6), Width = 3.5f,
                Gradient = new TextGradient { Start = SKColors.Red, End = SKColors.Blue, Angle = 30 },
                Outer = new TextStroke { Color = new SKColor(7, 8, 9), Width = 1 },
            },
        };
        layer.AddElement(Base with { Stroke = stroke });

        MppFormat.Save(doc, _tempPath);
        using var loaded = MppFormat.Load(_tempPath);

        // 舊檔遷移：文字拆成自己一層，多層外框變成三筆「物件外框」效果（由內而外），元素本身不再帶外框
        var textLayer = Assert.IsType<RasterLayer>(loaded.Root.Children[1]);
        var text = Assert.IsType<TextElement>(Assert.Single(textLayer.Elements));
        Assert.Null(text.Stroke);
        var outlines = textLayer.Effects.Select(e => Assert.IsType<Effects.ObjectOutlineEffect>(e.Effect)).ToList();
        Assert.Equal(3, outlines.Count);
        Assert.Equal(new SKColor(1, 2, 3), outlines[0].Color);
        Assert.Equal(2, outlines[0].Width);
        Assert.Equal(new SKColor(4, 5, 6), outlines[1].Color);
        Assert.Equal(4, outlines[1].Width);
        Assert.Equal(new SKColor(7, 8, 9), outlines[2].Color);
    }

    [Fact]
    public void ResetTransform_Text_KeepsFrameCenter_SingleUndo()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(400, 400, SKColors.White));
        var doc = session.Document;
        var layer = (RasterLayer)doc.Root.Children[0];
        var element = (TextElement)(Base with { Position = new SKPoint(150, 150), ScaleX = 1.6f })
            .TransformedBy(SKMatrix.CreateRotationDegrees(37, 150, 150), 1, 1, 37);
        lock (doc.SyncRoot) layer.AddElement(element);
        session.SelectedElement = (layer.Id, element.Id);

        Assert.Equal(37f, session.FrameRotation!.Value, 2);
        Assert.True(session.CanResetTransform);
        var before = element.FrameBounds;

        Assert.True(session.ResetTransform());

        var after = (TextElement)layer.FindElement(element.Id)!;
        Assert.Equal(0f, after.Rotation);
        Assert.Equal(1f, after.ScaleX);
        Assert.Equal(element.FontSize, after.FontSize); // 比例重設不動字級
        Assert.Equal(before.MidX, after.FrameBounds.MidX, 1.5);
        Assert.Equal(before.MidY, after.FrameBounds.MidY, 1.5);
        Assert.Equal("重設角度與比例", session.History.UndoLabel);
        Assert.False(session.CanResetTransform);

        session.Undo();
        Assert.Equal(element, layer.FindElement(element.Id));

        // 已是 0° 時沒有可重設的東西
        session.SelectedElement = null;
        Assert.Null(session.FrameRotation);
        Assert.False(session.CanResetTransform);
        Assert.False(session.ResetTransform());
    }

    [Fact]
    public void ResetTransform_TransformSession_RestoresAngleAndSize_KeepsSessionOpen()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(512, 512, SKColors.White));
        var doc = session.Document;
        var layer = (RasterLayer)doc.Root.Children[0];
        lock (doc.SyncRoot)
        {
            layer.Surface.Fill(new SKRectI(100, 100, 200, 200), new SKColor(255, 0, 0));
        }
        session.ActiveTool = session.Move;
        var t = session.BeginTransform()!;
        var source = t.SourceRect;
        t.RotationDeg = 30;
        t.TargetRect = SKRect.Create(source.Left, source.Top, source.Width * 2, source.Height * 1.5f);
        t.Apply(preview: false);
        session.RefreshSelectionHandles();
        Assert.Equal(30f, session.FrameRotation!.Value, 2);
        Assert.Equal(30f, session.SelectionHandlesRotation, 2);
        Assert.True(session.CanResetTransform);

        Assert.True(session.ResetTransform());
        Assert.NotNull(session.Transform);
        Assert.Equal(0f, session.SelectionHandlesRotation);
        Assert.Equal(source.Width, t.TargetRect.Width, 0.5);
        Assert.Equal(source.Height, t.TargetRect.Height, 0.5);
        Assert.False(session.CanResetTransform);
        Assert.False(session.ResetTransform());

        // 回到原尺寸但位置不同 → 仍有變形可落地（純平移）；把它搬回原位再提交＝無損還原
        t.TargetRect = source;
        t.Apply(preview: false);

        session.CommitTransform(); // 回到 identity → 無損還原、不記步驟
        Assert.False(session.History.CanUndo);
    }
}
