using System.Buffers.Binary;
using MinePainter.Core.Adjustments;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>.psd 的調整圖層與填色圖層匯入，以及為此新增的四個調整（臨界值、色彩平衡、相片濾鏡、通道混合器）。</summary>
public class PsdAdjustmentLayerTests
{
    private static byte[] Be16(params int[] values)
    {
        var bytes = new byte[values.Length * 2];
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(i * 2), (short)values[i]);
        return bytes;
    }

    private static PsdFormatTests.PsdWriter.Layer Adjustment(string name, string key, byte[] payload)
    {
        var layer = new PsdFormatTests.PsdWriter.Layer(name, SKRectI.Empty);
        layer.Blocks[key] = payload;
        return layer;
    }

    private static PsdFormatTests.PsdWriter.Layer Background(int size) => new("bg", new SKRectI(0, 0, size, size))
    {
        Channels =
        {
            [0] = Enumerable.Repeat((byte)100, size * size).ToArray(), [1] = Enumerable.Repeat((byte)100, size * size).ToArray(),
            [2] = Enumerable.Repeat((byte)100, size * size).ToArray(), [-1] = Enumerable.Repeat((byte)255, size * size).ToArray(),
        },
    };

    [Fact]
    public void Load_BuildsAdjustmentLayersFromLegacyBinaryBlocks()
    {
        var levels = Be16(2, 10, 240, 5, 250, 120).Concat(new byte[28 * 10]).ToArray();
        // 曲線：只有合成通道，兩點 (0,0)、(255,200)（存的順序是輸出、輸入）
        var curves = Be16(1).Concat(new byte[] { 0, 0, 0, 1 }).Concat(Be16(2, 0, 0, 200, 255)).ToArray();
        var hue = Be16(2, 0, 0, 0, 0, 30, 25, -10);   // 版本、上色(0)+補位、上色三值、主調整 30° / +25 / −10
        var balance = Be16(20, 0, -30, 0, 15, 0, 0, 0, 40).Concat(new byte[] { 1 }).ToArray();
        var exposure = new byte[14];
        BinaryPrimitives.WriteInt16BigEndian(exposure, 1);
        BinaryPrimitives.WriteSingleBigEndian(exposure.AsSpan(2), 1.5f);
        BinaryPrimitives.WriteSingleBigEndian(exposure.AsSpan(6), -0.1f);
        BinaryPrimitives.WriteSingleBigEndian(exposure.AsSpan(10), 0.8f);
        var mixer = Be16(1, 0, 0, 100, 0, 0, 100, 0, 0, 0, 0, 0, 100, 10);   // 紅←綠、綠←紅、藍←藍+10%

        var file = PsdFormatTests.PsdWriter.Build(8, 8,
        [
            Background(8),
            Adjustment("levels", "levl", levels),
            Adjustment("curves", "curv", curves),
            Adjustment("hue", "hue2", hue),
            Adjustment("balance", "blnc", balance),
            Adjustment("exposure", "expA", exposure),
            Adjustment("threshold", "thrs", Be16(100)),
            Adjustment("posterize", "post", Be16(6)),
            Adjustment("invert", "nvrt", []),
            Adjustment("mixer", "mixr", mixer),
            Adjustment("brightness", "brit", Be16(40, -20, 0, 0)),
        ]);

        using var doc = PsdFormat.Load(new MemoryStream(file), out var warnings);
        var children = doc.Root.Children;
        Assert.Equal(11, children.Count);
        Assert.IsType<RasterLayer>(children[0]);

        var lv = (LevelsAdjustment)Assert.IsType<AdjustmentLayer>(children[1]).Adjustment;
        Assert.Equal((10, 240, 5, 250), (lv.InputLow, lv.InputHigh, lv.OutputLow, lv.OutputHigh));
        Assert.Equal(1.2f, lv.Gamma, 2);
        Assert.Equal("levels", children[1].Name);

        var cv = (CurvesAdjustment)((AdjustmentLayer)children[2]).Adjustment;
        Assert.Equal(CurvesAdjustment.ModeLuminosity, cv.Mode);
        Assert.Equal(200f / 255f, cv.Curves[0][1].Y, 3);

        var hs = (HueSaturationAdjustment)((AdjustmentLayer)children[3]).Adjustment;
        Assert.Equal((30f, 0.25f, -0.1f), (hs.Hue, hs.Saturation, hs.Lightness));

        var cb = (ColorBalanceAdjustment)((AdjustmentLayer)children[4]).Adjustment;
        Assert.Equal((20, -30, 15, 40), (cb.ShadowsRed, cb.ShadowsBlue, cb.MidtonesGreen, cb.HighlightsBlue));
        Assert.True(cb.PreserveLuminosity);

        var ex = (ExposureAdjustment)((AdjustmentLayer)children[5]).Adjustment;
        Assert.Equal((1.5f, -0.1f, 0.8f), (ex.Exposure, ex.Offset, ex.Gamma));

        Assert.Equal(100, ((ThresholdAdjustment)((AdjustmentLayer)children[6]).Adjustment).Level);
        Assert.Equal(6, ((PosterizeAdjustment)((AdjustmentLayer)children[7]).Adjustment).Red);
        Assert.IsType<InvertAdjustment>(((AdjustmentLayer)children[8]).Adjustment);

        var mx = (ChannelMixerAdjustment)((AdjustmentLayer)children[9]).Adjustment;
        Assert.False(mx.Monochrome);
        Assert.Equal(100, mx.Rows[1]);   // 輸出紅 ← 綠
        Assert.Equal(10, mx.Rows[11]);   // 藍的常數

        var bc = (BrightnessContrastAdjustment)((AdjustmentLayer)children[10]).Adjustment;
        Assert.Equal((0.4f, -0.2f), (bc.Brightness, bc.Contrast));

        Assert.DoesNotContain(warnings, w => w.Contains("略過"));
    }

    [Fact]
    public void Load_UnsupportedAdjustmentIsSkippedWithNote_AndMaskWarns()
    {
        var gradientMap = new PsdFormatTests.PsdWriter.Layer("gm", SKRectI.Empty);
        gradientMap.Blocks["grdm"] = [0, 1, 0, 0];
        var masked = Adjustment("thr", "thrs", Be16(128));
        var maskedLayer = new PsdFormatTests.PsdWriter.Layer("thr", SKRectI.Empty)
        {
            MaskRect = new SKRectI(0, 0, 4, 4), MaskDefault = 0,
            Channels = { [-2] = Enumerable.Repeat((byte)255, 16).ToArray() },
        };
        maskedLayer.Blocks["thrs"] = Be16(128);
        _ = masked;

        var file = PsdFormatTests.PsdWriter.Build(8, 8, [Background(8), gradientMap, maskedLayer]);
        using var doc = PsdFormat.Load(new MemoryStream(file), out var warnings);

        Assert.Equal(2, doc.Root.Children.Count);   // 漸層對應略過
        Assert.IsType<AdjustmentLayer>(doc.Root.Children[1]);
        Assert.Contains(warnings, w => w.Contains("漸層對應") && w.Contains("略過"));
        Assert.Contains(warnings, w => w.Contains("遮色片") && w.Contains("thr"));
    }

    [Fact]
    public void Load_SolidAndGradientFillLayersBecomeCanvasPixels()
    {
        var soco = PsdStyleAndTextTests.Desc.Descriptor16(("Clr ", PsdStyleAndTextTests.Desc.Rgb(0, 128, 255)));
        var solid = new PsdFormatTests.PsdWriter.Layer("solid", SKRectI.Empty)
        {
            MaskRect = new SKRectI(0, 0, 4, 8), MaskDefault = 0,
            Channels = { [-2] = Enumerable.Repeat((byte)255, 32).ToArray() },   // 左半邊
        };
        solid.Blocks["SoCo"] = soco;

        var gdfl = PsdStyleAndTextTests.Desc.Descriptor16(
            ("Angl", PsdStyleAndTextTests.Desc.Ang(0)),
            ("Type", PsdStyleAndTextTests.Desc.Enum("GrdT", "Lnr")),
            ("Grad", new PsdStyleAndTextTests.Desc.Obj("Grdn",
            [
                ("Clrs", new List<object>
                {
                    new PsdStyleAndTextTests.Desc.Obj("Clrt", [("Clr ", PsdStyleAndTextTests.Desc.Rgb(255, 0, 0)), ("Lctn", 0)]),
                    new PsdStyleAndTextTests.Desc.Obj("Clrt", [("Clr ", PsdStyleAndTextTests.Desc.Rgb(0, 0, 255)), ("Lctn", 4096)]),
                }),
            ])));
        var gradient = new PsdFormatTests.PsdWriter.Layer("gradient", SKRectI.Empty);
        gradient.Blocks["GdFl"] = gdfl;

        var file = PsdFormatTests.PsdWriter.Build(8, 8, [solid, gradient]);
        using var doc = PsdFormat.Load(new MemoryStream(file), out var warnings);

        var solidLayer = Assert.IsType<RasterLayer>(doc.Root.Children[0]);
        var left = BackgroundRemovalCommandReadPixel(solidLayer, 1, 4);
        var right = BackgroundRemovalCommandReadPixel(solidLayer, 6, 4);
        Assert.Equal(new SKColor(0, 128, 255, 255), left);
        Assert.Equal(SKColors.Empty, right);   // 遮色片外透明

        var gradientLayer = Assert.IsType<RasterLayer>(doc.Root.Children[1]);
        var leftEdge = BackgroundRemovalCommandReadPixel(gradientLayer, 0, 4);
        var rightEdge = BackgroundRemovalCommandReadPixel(gradientLayer, 7, 4);
        Assert.True(leftEdge.Red > 200 && rightEdge.Blue > 200, $"角度 0 的漸層應由左紅到右藍：{leftEdge} → {rightEdge}");
        Assert.Empty(warnings);
    }

    private static SKColor BackgroundRemovalCommandReadPixel(RasterLayer layer, int x, int y)
    {
        var p = MinePainter.Core.History.BackgroundRemovalCommand.ReadRegion(layer.Surface, new SKRectI(x, y, x + 1, y + 1))[0];
        return new SKColor((byte)((p >> 16) & 0xFF), (byte)((p >> 8) & 0xFF), (byte)(p & 0xFF), (byte)(p >> 24));
    }

    // ---- 新調整本身 ----

    private static SKColor Apply(IAdjustment adjustment, SKColor input)
    {
        using var filter = adjustment.CreateColorFilter();
        using var bmp = new SKBitmap(new SKImageInfo(1, 1, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bmp);
        using var paint = new SKPaint { Color = input, ColorFilter = filter, BlendMode = SKBlendMode.Src };
        canvas.DrawRect(0, 0, 1, 1, paint);
        return bmp.GetPixel(0, 0);
    }

    [Fact]
    public void NewAdjustments_BehaveAsTheirNamesSay()
    {
        Assert.Equal(SKColors.White, Apply(new ThresholdAdjustment(100), new SKColor(120, 120, 120)));
        Assert.Equal(SKColors.Black, Apply(new ThresholdAdjustment(100), new SKColor(90, 90, 90)));

        var warmShadows = Apply(new ColorBalanceAdjustment { ShadowsRed = 60, PreserveLuminosity = false }, new SKColor(30, 30, 30));
        Assert.True(warmShadows.Red > 30 + 20 && warmShadows.Green == 30, $"陰影偏紅只該動紅：{warmShadows}");
        var brightUnaffected = Apply(new ColorBalanceAdjustment { ShadowsRed = 60, PreserveLuminosity = false }, new SKColor(250, 250, 250));
        Assert.InRange(brightUnaffected.Red, 250, 252);

        var filtered = Apply(new PhotoFilterAdjustment { Color = new SKColor(255, 128, 0), Density = 100, PreserveLuminosity = false }, SKColors.White);
        Assert.Equal(new SKColor(255, 128, 0), filtered);

        var swapped = Apply(new ChannelMixerAdjustment { Rows = [0, 100, 0, 0, 100, 0, 0, 0, 0, 0, 100, 0] }, new SKColor(200, 50, 10));
        Assert.Equal(new SKColor(50, 200, 10), swapped);
        var mono = Apply(new ChannelMixerAdjustment { Monochrome = true, Rows = [100, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0] }, new SKColor(200, 50, 10));
        Assert.Equal(new SKColor(200, 200, 200), mono);

        foreach (var id in new[] { "threshold", "colorBalance", "photoFilter", "channelMixer" })
        {
            var entry = AdjustmentRegistry.Find(id);
            Assert.NotNull(entry);
            var created = entry.CreateDefault();
            Assert.Equal(created.SaveParams(), entry.Load(created.SaveParams()).SaveParams());
        }
    }
}
