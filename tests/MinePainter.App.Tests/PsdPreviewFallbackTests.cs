using MinePainter.App.Rendering;
using MinePainter.Core.Adjustments;
using MinePainter.Core.Compositing;
using MinePainter.Core.Documents;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.App.Tests;

public class PsdPreviewFallbackTests
{
    [Fact]
    public void PsdCompositionShaderCompiles()
    {
        var source = (string)typeof(GpuLayerRenderer).GetField("PsdCompositeShader",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetRawConstantValue()!;
        using var effect = SKRuntimeEffect.Create(source, out var error);
        Assert.True(effect != null, error);
    }

    [Fact]
    public void CustomBlendShaderCompiles()
    {
        var source = (string)typeof(GpuLayerRenderer).GetField("PsdBlendShader",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetRawConstantValue()!;
        using var effect = SKRuntimeEffect.Create(source, out var error);
        Assert.True(effect != null, error);
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (var feature in new[] { "pass", "isolated", "group-mask", "pass-mask", "channels",
                     "translucent-channels", "group-channels", "raster-mask", "adjustment-mask", "pass-adjustment", "feather", "disabled-mask",
                     "custom", "custom-mask", "custom-group", "custom-pass-child" })
        foreach (var transformed in new[] { false, true })
            yield return [feature, transformed];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void NativeGpuPreviewMatchesCpuComposite(string feature, bool transformed)
    {
        using var doc = new Document(8, 8);
        doc.Root.Add(Solid(doc, new SKColor(90, 110, 130, feature == "translucent-channels" ? (byte)170 : (byte)255)));
        var mask = new LayerMask(new SKRectI(1, 2, 5, 4), [0, 64, 128, 255, 255, 128, 64, 0], 192);
        var foreground = Solid(doc, new SKColor(170, 130, 100));
        switch (feature)
        {
            case "pass":
            case "isolated":
            case "group-mask":
            case "pass-mask":
            case "group-channels":
            case "pass-adjustment":
            {
                var group = new GroupLayer
                {
                    IsPassThrough = feature is "pass" or "pass-mask" or "pass-adjustment",
                    Opacity = feature is "group-mask" or "pass-mask" or "pass-adjustment" ? .5f : 1,
                    Mask = feature is "group-mask" or "pass-mask" ? mask : null,
                    RestrictedChannels = feature == "group-channels" ? 5 : 0,
                };
                foreground.BlendMode = BlendMode.Multiply;
                group.Add(foreground);
                if (feature == "pass-adjustment") group.Add(new AdjustmentLayer(new InvertAdjustment()));
                doc.Root.Add(group);
                break;
            }
            case "channels":
            case "translucent-channels":
                foreground.BlendMode = BlendMode.Additive;
                foreground.RestrictedChannels = 6;
                doc.Root.Add(foreground);
                break;
            case "adjustment-mask":
                doc.Root.Add(new AdjustmentLayer(new InvertAdjustment()) { Mask = mask, Opacity = .7f });
                break;
            case "custom":
            case "custom-mask":
                // Skia 沒有的混合模式：GPU 路徑用 runtime shader、軟體退路用 CustomBlend 逐像素；都要跟合成器一樣
                foreground.BlendMode = BlendMode.DarkerColor;
                foreground.Opacity = .85f;
                if (feature == "custom-mask") foreground.Mask = mask;
                doc.Root.Add(foreground);
                var linear = Solid(doc, new SKColor(60, 200, 90, 128));
                linear.BlendMode = BlendMode.LinearLight;
                doc.Root.Add(linear);
                break;
            case "custom-group":
            {
                var group = new GroupLayer { BlendMode = BlendMode.VividLight, Opacity = .6f, Mask = mask };
                group.Add(foreground);
                doc.Root.Add(group);
                break;
            }
            case "custom-pass-child":
            {
                var group = new GroupLayer { IsPassThrough = true };
                foreground.BlendMode = BlendMode.Subtract;
                group.Add(foreground);
                doc.Root.Add(group);
                break;
            }
            default:
                foreground.Mask = feature switch
                {
                    "feather" => mask with { Feather = 1, Density = .7f, Inverted = true },
                    "disabled-mask" => mask with { Enabled = false },
                    _ => mask,
                };
                foreground.Opacity = .7f;
                doc.Root.Add(foreground);
                break;
        }
        using var session = new EditorSession(doc);
        session.Compositor.StopRendering();
        using var composite = Compositor.RenderComposite(doc);
        using var expected = SKSurface.Create(new SKImageInfo(24, 24, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var actual = SKSurface.Create(new SKImageInfo(24, 24, SKColorType.Bgra8888, SKAlphaType.Premul));
        Configure(expected.Canvas, transformed);
        Configure(actual.Canvas, transformed);
        expected.Canvas.DrawImage(composite, 0, 0);
        using var renderer = new GpuLayerRenderer();
        lock (doc.SyncRoot) Assert.True(renderer.TryDraw(actual.Canvas, session, doc.Bounds, transformed ? 2 : 1));
        using var expectedImage = expected.Snapshot();
        using var actualImage = actual.Snapshot();
        using var a = SKBitmap.FromImage(expectedImage);
        using var b = SKBitmap.FromImage(actualImage);
        var before = a.Bytes;
        var after = b.Bytes;
        for (var i = 0; i < before.Length; i++)
            Assert.True(Math.Abs(before[i] - after[i]) <= 2,
                $"{feature}, transformed={transformed}, byte {i}: CPU {before[i]}, GPU {after[i]}");
    }

    private static RasterLayer Solid(Document doc, SKColor color)
    {
        var layer = new RasterLayer();
        layer.Surface.Fill(doc.Bounds, color);
        return layer;
    }

    private static void Configure(SKCanvas canvas, bool transformed)
    {
        canvas.Clear(SKColors.Transparent);
        if (transformed) { canvas.Translate(3, 5); canvas.Scale(2); }
        canvas.ClipRect(new SKRect(0, 0, 7, 8));
    }
}
