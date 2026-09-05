using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// .psd 匯入。測試資料由 <see cref="PsdWriter"/> 照 Adobe 規格現組：每一種容器變化
/// （RLE、zip 預測、16 位元的 Lr16、PSB 的 8 位元組長度、群組界線、遮色片）都得真的寫出一份才測得到。
/// </summary>
public class PsdFormatTests
{
    private static SKColor GetLayerPixel(RasterLayer layer, int x, int y)
    {
        var idx = TileIndex.FromPixel(x, y);
        var tile = layer.Surface.GetTileForRead(idx);
        if (tile == null) return SKColors.Empty;
        var rect = idx.ToPixelRect();
        var offset = ((y - rect.Top) * Tile.Size + (x - rect.Left)) * 4;
        var s = tile.PixelSpan;
        return new SKColor(s[offset + 2], s[offset + 1], s[offset + 0], s[offset + 3]);
    }

    private static byte[] Plane(int width, int height, Func<int, int, byte> value)
    {
        var data = new byte[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                data[y * width + x] = value(x, y);
        return data;
    }

    private static byte[] Solid(int width, int height, byte value) => Plane(width, height, (_, _) => value);

    [Fact]
    public void Load_ReadsLayerPropertiesPositionsAndPixels()
    {
        // 底層：整張紅，RLE；上層：4×3 半透明藍，放在 (2,1)，隱藏、20% 不透明、色彩增值、Unicode 名稱
        var file = PsdWriter.Build(8, 6,
        [
            new PsdWriter.Layer("bg", new SKRectI(0, 0, 8, 6))
            {
                UnicodeName = "背景",
                Compression = 1,
                Channels = { [0] = Solid(8, 6, 255), [1] = Solid(8, 6, 0), [2] = Solid(8, 6, 0), [-1] = Solid(8, 6, 255) },
            },
            new PsdWriter.Layer("top", new SKRectI(2, 1, 6, 4))
            {
                UnicodeName = "上層",
                Hidden = true,
                Opacity = 51,
                BlendKey = "mul ",
                Channels = { [0] = Solid(4, 3, 0), [1] = Solid(4, 3, 0), [2] = Solid(4, 3, 255), [-1] = Solid(4, 3, 128) },
            },
        ]);

        using var stream = new MemoryStream(file);
        using var doc = PsdFormat.Load(stream, out var warnings);

        Assert.Empty(warnings);
        Assert.Equal(8, doc.Width);
        Assert.Equal(6, doc.Height);
        Assert.Equal(2, doc.Root.Children.Count);

        var background = Assert.IsType<RasterLayer>(doc.Root.Children[0]);
        Assert.Equal("背景", background.Name);
        Assert.True(background.IsVisible);
        Assert.Equal(1f, background.Opacity, 3);
        Assert.Equal(BlendMode.Normal, background.BlendMode);
        Assert.Equal(new SKColor(255, 0, 0, 255), GetLayerPixel(background, 7, 5));

        var top = Assert.IsType<RasterLayer>(doc.Root.Children[1]);
        Assert.Equal("上層", top.Name);
        Assert.False(top.IsVisible);
        Assert.Equal(51f / 255f, top.Opacity, 3);
        Assert.Equal(BlendMode.Multiply, top.BlendMode);

        // 圖層範圍不是從 (0,0) 開始：像素要落在文件座標 (2,1)–(6,4)
        Assert.Equal(SKColors.Empty, GetLayerPixel(top, 1, 1));
        var converted = GetLayerPixel(top, 2, 1);
        Assert.Equal(128, converted.Alpha);
        Assert.InRange(converted.Blue, 127, 129);   // 直通 alpha 轉預乘
        Assert.Equal(0, converted.Red);
        Assert.NotEqual(SKColors.Empty, GetLayerPixel(top, 5, 3));
        Assert.Equal(SKColors.Empty, GetLayerPixel(top, 6, 3));

        Assert.Same(top, doc.ActiveLayer);
    }

    [Fact]
    public void Load_BuildsGroupsFromSectionDividers()
    {
        // 檔案順序（由下而上）：界線(3) → 子層 → 群組本體(1)，再一層在群組外面
        var file = PsdWriter.Build(4, 4,
        [
            new PsdWriter.Layer("</Layer group>", SKRectI.Empty) { SectionType = 3 },
            new PsdWriter.Layer("child", new SKRectI(0, 0, 4, 4))
            {
                Channels = { [0] = Solid(4, 4, 10), [1] = Solid(4, 4, 20), [2] = Solid(4, 4, 30), [-1] = Solid(4, 4, 255) },
            },
            new PsdWriter.Layer("folder", SKRectI.Empty) { SectionType = 1, Opacity = 128, UnicodeName = "我的群組", Hidden = true },
            new PsdWriter.Layer("outside", new SKRectI(0, 0, 4, 4))
            {
                Channels = { [0] = Solid(4, 4, 1), [1] = Solid(4, 4, 2), [2] = Solid(4, 4, 3), [-1] = Solid(4, 4, 255) },
            },
        ]);

        using var stream = new MemoryStream(file);
        using var doc = PsdFormat.Load(stream, out var warnings);

        Assert.Empty(warnings);
        Assert.Equal(2, doc.Root.Children.Count);

        var group = Assert.IsType<GroupLayer>(doc.Root.Children[0]);
        Assert.Equal("我的群組", group.Name);
        Assert.False(group.IsVisible);
        Assert.Equal(128f / 255f, group.Opacity, 3);
        var child = Assert.IsType<RasterLayer>(Assert.Single(group.Children));
        Assert.Equal("child", child.Name);
        Assert.Equal(new SKColor(10, 20, 30, 255), GetLayerPixel(child, 0, 0));

        Assert.Equal("outside", Assert.IsType<RasterLayer>(doc.Root.Children[1]).Name);
    }

    [Fact]
    public void Load_BakesUserMaskIntoAlpha()
    {
        // 遮色片只蓋右半（範圍外用預設 0 = 全遮），左半被遮成透明、右半照遮色片的 255 保留
        var file = PsdWriter.Build(4, 2,
        [
            new PsdWriter.Layer("masked", new SKRectI(0, 0, 4, 2))
            {
                Channels =
                {
                    [0] = Solid(4, 2, 200), [1] = Solid(4, 2, 0), [2] = Solid(4, 2, 0), [-1] = Solid(4, 2, 255),
                    [-2] = Solid(2, 2, 255),
                },
                MaskRect = new SKRectI(2, 0, 4, 2),
                MaskDefault = 0,
            },
        ]);

        using var stream = new MemoryStream(file);
        using var doc = PsdFormat.Load(stream, out _);

        var layer = Assert.IsType<RasterLayer>(Assert.Single(doc.Root.Children));
        Assert.Equal(SKColors.Empty, GetLayerPixel(layer, 0, 0));
        Assert.Equal(SKColors.Empty, GetLayerPixel(layer, 1, 1));
        Assert.Equal(new SKColor(200, 0, 0, 255), GetLayerPixel(layer, 2, 0));
        Assert.Equal(new SKColor(200, 0, 0, 255), GetLayerPixel(layer, 3, 1));
    }

    [Fact]
    public void Load_ClipsToLayerBelowAndToGroupAlpha()
    {
        // 底層只有左半有像素；剪裁上去的整張紅只能留在左半。
        // 群組（半透明的子層）收尾後，剪裁到群組的整張綠：alpha = 綠 × 群組 alpha（128 × 50% 不透明度）
        static Dictionary<int, byte[]> Rgb(byte r, byte g, byte b, byte[] alpha) =>
            new() { [0] = Solid(4, 2, r), [1] = Solid(4, 2, g), [2] = Solid(4, 2, b), [-1] = alpha };

        var file = PsdWriter.Build(4, 2,
        [
            new PsdWriter.Layer("base", new SKRectI(0, 0, 4, 2)) { Channels = Rgb(0, 0, 255, Plane(4, 2, (x, _) => x < 2 ? (byte)255 : (byte)0)) },
            new PsdWriter.Layer("clipped", new SKRectI(0, 0, 4, 2)) { Clipped = true, Channels = Rgb(255, 0, 0, Solid(4, 2, 255)) },
            new PsdWriter.Layer("</Layer group>", SKRectI.Empty) { SectionType = 3 },
            new PsdWriter.Layer("inner", new SKRectI(0, 0, 4, 2)) { Opacity = 128, Channels = Rgb(1, 2, 3, Solid(4, 2, 128)) },
            new PsdWriter.Layer("group", SKRectI.Empty) { SectionType = 1 },
            new PsdWriter.Layer("onGroup", new SKRectI(0, 0, 4, 2)) { Clipped = true, Channels = Rgb(0, 255, 0, Solid(4, 2, 255)) },
        ]);

        using var doc = PsdFormat.Load(new MemoryStream(file), out var warnings);

        Assert.Empty(warnings);
        Assert.Equal(4, doc.Root.Children.Count);
        var clipped = Assert.IsType<RasterLayer>(doc.Root.Children[1]);
        Assert.Equal(new SKColor(255, 0, 0, 255), GetLayerPixel(clipped, 1, 0));
        Assert.Equal(SKColors.Empty, GetLayerPixel(clipped, 2, 0));

        var onGroup = Assert.IsType<RasterLayer>(doc.Root.Children[3]);
        var alpha = GetLayerPixel(onGroup, 0, 0).Alpha;
        Assert.InRange(alpha, 62, 66);   // 128/255 × 128/255 ≈ 0.25
    }

    [Fact]
    public void Load_HidesLayersClippedToHiddenBase()
    {
        var file = PsdWriter.Build(2, 2,
        [
            new PsdWriter.Layer("base", new SKRectI(0, 0, 2, 2))
            {
                Hidden = true,
                Channels = { [0] = Solid(2, 2, 0), [1] = Solid(2, 2, 0), [2] = Solid(2, 2, 0), [-1] = Solid(2, 2, 255) },
            },
            new PsdWriter.Layer("clipped", new SKRectI(0, 0, 2, 2))
            {
                Clipped = true,
                Channels = { [0] = Solid(2, 2, 9), [1] = Solid(2, 2, 9), [2] = Solid(2, 2, 9), [-1] = Solid(2, 2, 255) },
            },
        ]);

        using var doc = PsdFormat.Load(new MemoryStream(file), out _);
        var clipped = Assert.IsType<RasterLayer>(doc.Root.Children[1]);
        Assert.False(clipped.IsVisible);
        Assert.Equal(new SKColor(9, 9, 9, 255), GetLayerPixel(clipped, 0, 0));   // 像素還在，只是跟著底層藏起來
    }

    [Fact]
    public void Load_Reads16BitLayersFromLr16WithZipPrediction()
    {
        // 16 位元：正規圖層清單長度 0，圖層在 Lr16 區塊；樣本 0x8000 應轉成 128
        var wide = new byte[4 * 4 * 2];
        for (var i = 0; i < 16; i++) BinaryPrimitives.WriteUInt16BigEndian(wide.AsSpan(i * 2), (ushort)(0x8000 + i * 16));
        var opaque = new byte[4 * 4 * 2];
        for (var i = 0; i < 16; i++) BinaryPrimitives.WriteUInt16BigEndian(opaque.AsSpan(i * 2), 0xFFFF);
        var zero = new byte[4 * 4 * 2];

        var file = PsdWriter.Build(4, 4,
        [
            new PsdWriter.Layer("deep", new SKRectI(0, 0, 4, 4))
            {
                Compression = 3,
                Channels = { [0] = wide, [1] = zero, [2] = zero, [-1] = opaque },
            },
        ], depth: 16);

        using var stream = new MemoryStream(file);
        using var doc = PsdFormat.Load(stream, out var warnings);

        Assert.Empty(warnings);
        var layer = Assert.IsType<RasterLayer>(Assert.Single(doc.Root.Children));
        Assert.Equal("deep", layer.Name);
        var first = GetLayerPixel(layer, 0, 0);
        Assert.Equal(255, first.Alpha);
        Assert.Equal(128, first.Red);
        Assert.Equal(0, first.Green);
        // 差分預測要正確累加，第 15 個樣本 = 0x8000 + 240 → 仍是 128～129
        Assert.InRange(GetLayerPixel(layer, 3, 3).Red, 128, 129);
    }

    [Fact]
    public void Load_FlattenedFileFallsBackToMergedImage()
    {
        // 沒有圖層區：讀合成影像（RGB + alpha，RLE）
        var file = PsdWriter.Build(3, 2, [],
            merged:
            [
                Plane(3, 2, (x, _) => (byte)(x * 100)),
                Solid(3, 2, 7),
                Solid(3, 2, 9),
                Plane(3, 2, (_, y) => y == 0 ? (byte)255 : (byte)0),
            ],
            mergedCompression: 1, channels: 4);

        using var stream = new MemoryStream(file);
        using var doc = PsdFormat.Load(stream, out var warnings);

        Assert.Empty(warnings);
        var layer = Assert.IsType<RasterLayer>(Assert.Single(doc.Root.Children));
        Assert.Equal("背景", layer.Name);
        Assert.Equal(new SKColor(200, 7, 9, 255), GetLayerPixel(layer, 2, 0));
        Assert.Equal(SKColors.Empty, GetLayerPixel(layer, 2, 1));
    }

    [Fact]
    public void Load_ConvertsCmykAndGrayscaleAndIndexed()
    {
        // CMYK 反相存放：C=0（滿墨）、其餘 255、K=255 → 純青色 (0,255,255)
        var cmyk = PsdWriter.Build(1, 1,
        [
            new PsdWriter.Layer("ink", new SKRectI(0, 0, 1, 1))
            {
                Channels = { [0] = [0], [1] = [255], [2] = [255], [3] = [255] },
            },
        ], mode: 4, channels: 4);
        using (var doc = PsdFormat.Load(new MemoryStream(cmyk), out _))
            Assert.Equal(new SKColor(0, 255, 255, 255), GetLayerPixel((RasterLayer)doc.Root.Children[0], 0, 0));

        var gray = PsdWriter.Build(1, 1,
        [
            new PsdWriter.Layer("g", new SKRectI(0, 0, 1, 1)) { Channels = { [0] = [77], [-1] = [255] } },
        ], mode: 1, channels: 1);
        using (var doc = PsdFormat.Load(new MemoryStream(gray), out _))
            Assert.Equal(new SKColor(77, 77, 77, 255), GetLayerPixel((RasterLayer)doc.Root.Children[0], 0, 0));

        var palette = new byte[768];
        palette[5] = 11; palette[256 + 5] = 22; palette[512 + 5] = 33;
        var indexed = PsdWriter.Build(1, 1,
        [
            new PsdWriter.Layer("i", new SKRectI(0, 0, 1, 1)) { Channels = { [0] = [5] } },
        ], mode: 2, channels: 1, palette: palette);
        using (var doc = PsdFormat.Load(new MemoryStream(indexed), out _))
            Assert.Equal(new SKColor(11, 22, 33, 255), GetLayerPixel((RasterLayer)doc.Root.Children[0], 0, 0));
    }

    [Fact]
    public void Load_ReadsPsbLengths()
    {
        var file = PsdWriter.Build(2, 2,
        [
            new PsdWriter.Layer("big", new SKRectI(0, 0, 2, 2))
            {
                Compression = 1,
                Channels = { [0] = Solid(2, 2, 5), [1] = Solid(2, 2, 6), [2] = Solid(2, 2, 7), [-1] = Solid(2, 2, 255) },
            },
        ], psb: true);

        using var doc = PsdFormat.Load(new MemoryStream(file), out var warnings);
        Assert.Empty(warnings);
        Assert.Equal(new SKColor(5, 6, 7, 255), GetLayerPixel((RasterLayer)doc.Root.Children[0], 1, 1));
    }

    [Fact]
    public void Load_ReportsSkippedAdjustmentTextAndUnknownBlend()
    {
        var file = PsdWriter.Build(2, 2,
        [
            new PsdWriter.Layer("levels", SKRectI.Empty) { ExtraKeys = { "levl" } },
            new PsdWriter.Layer("title", new SKRectI(0, 0, 2, 2))
            {
                ExtraKeys = { "TySh" },
                BlendKey = "vLit",
                Channels = { [0] = Solid(2, 2, 1), [1] = Solid(2, 2, 1), [2] = Solid(2, 2, 1), [-1] = Solid(2, 2, 255) },
            },
        ]);

        using var doc = PsdFormat.Load(new MemoryStream(file), out var warnings);

        var layer = Assert.IsType<RasterLayer>(Assert.Single(doc.Root.Children));
        Assert.Equal("title", layer.Name);
        Assert.Equal(BlendMode.HardLight, layer.BlendMode);
        Assert.Contains(warnings, w => w.Contains("levels") && w.Contains("略過"));
        Assert.Contains(warnings, w => w.Contains("文字圖層"));
        Assert.Contains(warnings, w => w.Contains("強烈光源"));
    }

    [Fact]
    public void Load_RejectsUnsupportedDepthAndGarbage()
    {
        var hdr = PsdWriter.Build(1, 1, [], merged: [[0], [0], [0]], depth: 32);
        var ex = Assert.Throws<InvalidDataException>(() => PsdFormat.Load(new MemoryStream(hdr), out _));
        Assert.Contains("32 位元", ex.Message);

        Assert.Throws<InvalidDataException>(() => PsdFormat.Load(new MemoryStream("PDN3garbage"u8.ToArray()), out _));
    }

    [Fact]
    public void IsPsdFile_ChecksContentNotExtension()
    {
        var dir = Path.Combine(Path.GetTempPath(), "minepainter-psd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var real = Path.Combine(dir, "photo.dat");
            File.WriteAllBytes(real, PsdWriter.Build(1, 1, [], merged: [[0], [0], [0]]));
            var fake = Path.Combine(dir, "fake.psd");
            File.WriteAllBytes(fake, "PNG"u8.ToArray());

            Assert.True(PsdFormat.IsPsdFile(real));
            Assert.False(PsdFormat.IsPsdFile(fake));
            Assert.False(PsdFormat.IsPsdFile(Path.Combine(dir, "missing.psd")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- 依規格現寫的 PSD ----

    internal static class PsdWriter
    {
        public sealed class Layer(string pascalName, SKRectI rect)
        {
            public string PascalName { get; } = pascalName;
            public SKRectI Rect { get; } = rect;
            public string? UnicodeName { get; init; }
            public bool Hidden { get; init; }
            public bool Clipped { get; init; }
            public byte Opacity { get; init; } = 255;
            public string BlendKey { get; init; } = "norm";
            public int SectionType { get; init; }
            /// <summary>0 原始、1 RLE、2 zip、3 zip＋預測。</summary>
            public int Compression { get; init; }
            public Dictionary<int, byte[]> Channels { get; init; } = new();
            public SKRectI? MaskRect { get; init; }
            public byte MaskDefault { get; init; } = 255;
            public byte MaskFlags { get; init; }
            /// <summary>只放 key、內容為空的附加資訊區塊（TySh、levl…），讀取端只看 key。</summary>
            public List<string> ExtraKeys { get; } = new();
            /// <summary>帶內容的附加資訊區塊（lfx2、TySh…）。</summary>
            public Dictionary<string, byte[]> Blocks { get; } = new();
        }

        public static byte[] Build(
            int width, int height, IReadOnlyList<Layer> layers,
            byte[][]? merged = null, int mergedCompression = 0,
            int depth = 8, int mode = 3, int channels = 3, byte[]? palette = null, bool psb = false, int? globalAngle = null)
        {
            var file = new MemoryStream();
            file.Write("8BPS"u8);
            U16(file, (ushort)(psb ? 2 : 1));
            file.Write(new byte[6]);
            U16(file, (ushort)channels);
            U32(file, (uint)height);
            U32(file, (uint)width);
            U16(file, (ushort)depth);
            U16(file, (ushort)mode);

            U32(file, (uint)(palette?.Length ?? 0));
            if (palette != null) file.Write(palette);

            WriteImageResources(file, globalAngle);

            if (layers.Count == 0)
            {
                Len(file, 0, psb);
            }
            else
            {
                var info = new MemoryStream();
                WriteLayerInfo(info, layers, depth, psb);
                var section = new MemoryStream();
                if (depth == 8)
                {
                    Len(section, info.Length, psb);
                    info.WriteTo(section);
                }
                else
                {
                    // 16 位元：正規清單為空，內容藏在 Lr16 區塊
                    Len(section, 0, psb);
                    U32(section, 0);    // 全域遮罩
                    section.Write("8BIM"u8);
                    section.Write(Encoding.ASCII.GetBytes("Lr16"));
                    Len(section, info.Length, psb);
                    info.WriteTo(section);
                }
                Len(file, section.Length, psb);
                section.WriteTo(file);
            }

            // 合成影像（有圖層時隨便塞一張全黑的即可）
            merged ??= Enumerable.Range(0, channels).Select(_ => new byte[width * height * depth / 8]).ToArray();
            WriteMerged(file, merged, width, height, depth, mergedCompression, psb);
            return file.ToArray();
        }

        /// <summary>影像資源區：只在要測整體光源時放一筆 1037（8BIM + ID + 空名稱 + 長度 + int32）。</summary>
        private static void WriteImageResources(MemoryStream file, int? globalAngle)
        {
            if (globalAngle == null)
            {
                U32(file, 0);
                return;
            }
            var res = new MemoryStream();
            res.Write("8BIM"u8);
            U16(res, 1037);
            res.Write(new byte[2]);     // 空的 Pascal 名稱（長度 0 + 補位）
            U32(res, 4);
            I32(res, globalAngle.Value);
            U32(file, (uint)res.Length);
            res.WriteTo(file);
        }

        private static void WriteLayerInfo(MemoryStream info, IReadOnlyList<Layer> layers, int depth, bool psb)
        {
            I16(info, (short)layers.Count);
            var channelData = new List<byte[]>();

            foreach (var layer in layers)
            {
                I32(info, layer.Rect.Top);
                I32(info, layer.Rect.Left);
                I32(info, layer.Rect.Bottom);
                I32(info, layer.Rect.Right);

                var encoded = new List<(int Id, byte[] Data)>();
                foreach (var (id, samples) in layer.Channels)
                {
                    var rect = id == -2 ? layer.MaskRect!.Value : layer.Rect;
                    encoded.Add((id, EncodeChannel(samples, rect.Width, rect.Height, depth, layer.Compression, psb)));
                }

                U16(info, (ushort)encoded.Count);
                foreach (var (id, data) in encoded)
                {
                    I16(info, (short)id);
                    Len(info, data.Length, psb);
                    channelData.Add(data);
                }

                info.Write("8BIM"u8);
                info.Write(Encoding.ASCII.GetBytes(layer.BlendKey));
                info.WriteByte(layer.Opacity);
                info.WriteByte((byte)(layer.Clipped ? 1 : 0));
                info.WriteByte((byte)(layer.Hidden ? 0x02 : 0x00));
                info.WriteByte(0);

                var extra = new MemoryStream();
                if (layer.MaskRect is { } mask)
                {
                    U32(extra, 20);
                    I32(extra, mask.Top);
                    I32(extra, mask.Left);
                    I32(extra, mask.Bottom);
                    I32(extra, mask.Right);
                    extra.WriteByte(layer.MaskDefault);
                    extra.WriteByte(layer.MaskFlags);
                    extra.Write(new byte[2]);
                }
                else
                {
                    U32(extra, 0);
                }
                U32(extra, 0);   // 混合範圍

                var name = Encoding.Latin1.GetBytes(layer.PascalName);
                extra.WriteByte((byte)name.Length);
                extra.Write(name);
                extra.Write(new byte[(4 - (name.Length + 1) % 4) % 4]);

                if (layer.UnicodeName != null)
                {
                    var chars = Encoding.BigEndianUnicode.GetBytes(layer.UnicodeName);
                    var block = new MemoryStream();
                    U32(block, (uint)layer.UnicodeName.Length);
                    block.Write(chars);
                    WriteBlock(extra, "luni", block.ToArray());
                }
                if (layer.SectionType != 0)
                {
                    var block = new MemoryStream();
                    U32(block, (uint)layer.SectionType);
                    WriteBlock(extra, "lsct", block.ToArray());
                }
                foreach (var key in layer.ExtraKeys) WriteBlock(extra, key, []);
                foreach (var (key, payload) in layer.Blocks) WriteBlock(extra, key, payload);

                U32(info, (uint)extra.Length);
                extra.WriteTo(info);
            }

            foreach (var data in channelData) info.Write(data);
        }

        /// <summary>Photoshop 把區塊長度補到偶數；讀取端要能跳過補位。</summary>
        private static void WriteBlock(MemoryStream extra, string key, byte[] payload)
        {
            extra.Write("8BIM"u8);
            extra.Write(Encoding.ASCII.GetBytes(key));
            U32(extra, (uint)payload.Length);
            extra.Write(payload);
            if (payload.Length % 2 != 0) extra.WriteByte(0);
        }

        private static byte[] EncodeChannel(byte[] samples, int width, int height, int depth, int compression, bool psb)
        {
            var stream = new MemoryStream();
            U16(stream, (ushort)compression);
            var rowBytes = width * depth / 8;
            switch (compression)
            {
                case 0:
                    stream.Write(samples);
                    break;
                case 1:
                    var rows = new List<byte[]>();
                    for (var y = 0; y < height; y++) rows.Add(PackBits(samples.AsSpan(y * rowBytes, rowBytes)));
                    foreach (var row in rows)
                        if (psb) U32(stream, (uint)row.Length);
                        else U16(stream, (ushort)row.Length);
                    foreach (var row in rows) stream.Write(row);
                    break;
                case 2:
                case 3:
                    var payload = (byte[])samples.Clone();
                    if (compression == 3) Predict(payload, rowBytes, height, depth / 8);
                    using (var zlib = new ZLibStream(stream, CompressionLevel.Optimal, leaveOpen: true))
                        zlib.Write(payload);
                    break;
            }
            return stream.ToArray();
        }

        private static void Predict(byte[] data, int rowBytes, int height, int bytesPerSample)
        {
            for (var y = 0; y < height; y++)
            {
                var row = data.AsSpan(y * rowBytes, rowBytes);
                if (bytesPerSample == 1)
                {
                    for (var x = row.Length - 1; x >= 1; x--) row[x] -= row[x - 1];
                }
                else
                {
                    for (var x = row.Length - 2; x >= 2; x -= 2)
                    {
                        var current = BinaryPrimitives.ReadUInt16BigEndian(row[x..]);
                        var previous = BinaryPrimitives.ReadUInt16BigEndian(row[(x - 2)..]);
                        BinaryPrimitives.WriteUInt16BigEndian(row[x..], (ushort)(current - previous));
                    }
                }
            }
        }

        /// <summary>最簡單的 PackBits：連續相同用重複段，否則逐段字面。</summary>
        private static byte[] PackBits(ReadOnlySpan<byte> row)
        {
            var output = new MemoryStream();
            var i = 0;
            while (i < row.Length)
            {
                var run = 1;
                while (i + run < row.Length && run < 128 && row[i + run] == row[i]) run++;
                if (run >= 2)
                {
                    output.WriteByte((byte)(sbyte)(1 - run));
                    output.WriteByte(row[i]);
                    i += run;
                    continue;
                }
                var start = i;
                while (i < row.Length && i - start < 128 && (i + 1 >= row.Length || row[i + 1] != row[i])) i++;
                output.WriteByte((byte)(i - start - 1));
                output.Write(row[start..i]);
            }
            return output.ToArray();
        }

        private static void WriteMerged(MemoryStream file, byte[][] planes, int width, int height, int depth, int compression, bool psb)
        {
            U16(file, (ushort)compression);
            var rowBytes = width * depth / 8;
            if (compression == 0)
            {
                foreach (var plane in planes) file.Write(plane);
                return;
            }
            var rows = planes.SelectMany(p => Enumerable.Range(0, height).Select(y => PackBits(p.AsSpan(y * rowBytes, rowBytes)))).ToList();
            foreach (var row in rows)
                if (psb) U32(file, (uint)row.Length);
                else U16(file, (ushort)row.Length);
            foreach (var row in rows) file.Write(row);
        }

        private static void Len(Stream s, long value, bool psb)
        {
            if (psb) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, value); s.Write(b); }
            else U32(s, (uint)value);
        }

        private static void U16(Stream s, ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); s.Write(b); }
        private static void I16(Stream s, short v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteInt16BigEndian(b, v); s.Write(b); }
        private static void U32(Stream s, uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); s.Write(b); }
        private static void I32(Stream s, int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); s.Write(b); }
    }
}
