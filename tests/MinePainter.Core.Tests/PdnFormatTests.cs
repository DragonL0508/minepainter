using System.IO.Compression;
using System.Text;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// .pdn 匯入。測試資料由 <see cref="PdnWriter"/> 現組 —— paint.net 的檔案是
/// BinaryFormatter 物件圖 + 自訂的延後像素段，只有真的照格式寫一份出來才測得到讀取器。
/// </summary>
public class PdnFormatTests
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

    /// <summary>BGRA 直通 alpha 的來源緩衝（paint.net 的 Surface 排列）。</summary>
    private static byte[] Bgra(int width, int height, Func<int, int, SKColor> pixel)
    {
        var data = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var c = pixel(x, y);
                var i = (y * width + x) * 4;
                data[i + 0] = c.Blue;
                data[i + 1] = c.Green;
                data[i + 2] = c.Red;
                data[i + 3] = c.Alpha;
            }
        }
        return data;
    }

    [Fact]
    public void Load_ReadsLayerTreePropertiesAndPixels()
    {
        var red = new SKColor(255, 0, 0, 255);
        var halfBlue = new SKColor(0, 0, 255, 128);

        var file = PdnWriter.Build(8, 6,
        [
            new PdnWriter.Layer("背景", true, 255, "NormalBlendOp", Bgra(8, 6, (_, _) => red)),
            new PdnWriter.Layer("上層", false, 51, "MultiplyBlendOp",
                Bgra(8, 6, (x, _) => x < 4 ? halfBlue : SKColors.Empty)),
        ]);

        using var stream = new MemoryStream(file);
        using var doc = PdnFormat.Load(stream, out var warnings);

        Assert.Empty(warnings);
        Assert.Equal(8, doc.Width);
        Assert.Equal(6, doc.Height);
        Assert.Equal(2, doc.Root.Children.Count);

        // paint.net 的 layers[0] 是最底層，和我們的 Children 同向
        var background = Assert.IsType<RasterLayer>(doc.Root.Children[0]);
        Assert.Equal("背景", background.Name);
        Assert.True(background.IsVisible);
        Assert.Equal(1f, background.Opacity, 3);
        Assert.Equal(BlendMode.Normal, background.BlendMode);
        Assert.Equal(red, GetLayerPixel(background, 3, 4));

        var top = Assert.IsType<RasterLayer>(doc.Root.Children[1]);
        Assert.Equal("上層", top.Name);
        Assert.False(top.IsVisible);
        Assert.Equal(51f / 255f, top.Opacity, 3);
        Assert.Equal(BlendMode.Multiply, top.BlendMode);

        // 直通 alpha 轉預乘：藍 255 × alpha 128/255 ≈ 128
        var converted = GetLayerPixel(top, 1, 1);
        Assert.Equal(128, converted.Alpha);
        Assert.InRange(converted.Blue, 127, 129);
        Assert.Equal(0, converted.Red);
        Assert.Equal(SKColors.Empty, GetLayerPixel(top, 6, 1));

        // 作用中圖層 = 最上層
        Assert.Same(top, doc.ActiveLayer);
    }

    [Fact]
    public void Load_HandlesGzippedAndOutOfOrderChunks()
    {
        var pixels = Bgra(40, 40, (x, y) => new SKColor((byte)x, (byte)y, 0, 255));
        var file = PdnWriter.Build(40, 40,
            [new PdnWriter.Layer("chunky", true, 255, "NormalBlendOp", pixels)],
            gzipChunks: true, chunkSize: 512, reverseChunkOrder: true);

        using var stream = new MemoryStream(file);
        using var doc = PdnFormat.Load(stream, out _);

        var layer = Assert.IsType<RasterLayer>(doc.Root.Children[0]);
        Assert.Equal(new SKColor(7, 33, 0, 255), GetLayerPixel(layer, 7, 33));
        Assert.Equal(new SKColor(39, 39, 0, 255), GetLayerPixel(layer, 39, 39));
    }

    [Fact]
    public void Load_HandlesGzippedBody()
    {
        var file = PdnWriter.Build(4, 4,
            [new PdnWriter.Layer("舊版", true, 255, "NormalBlendOp",
                Bgra(4, 4, (_, _) => new SKColor(9, 8, 7, 255)))],
            gzipBody: true);

        using var stream = new MemoryStream(file);
        using var doc = PdnFormat.Load(stream, out _);

        var layer = Assert.IsType<RasterLayer>(doc.Root.Children[0]);
        Assert.Equal(new SKColor(9, 8, 7, 255), GetLayerPixel(layer, 2, 2));
    }

    [Fact]
    public void Load_UnsupportedBlendMode_FallsBackToNormalWithWarning()
    {
        var file = PdnWriter.Build(2, 2,
            [new PdnWriter.Layer("發光", true, 255, "GlowBlendOp",
                Bgra(2, 2, (_, _) => SKColors.White))]);

        using var stream = new MemoryStream(file);
        using var doc = PdnFormat.Load(stream, out var warnings);

        Assert.Equal(BlendMode.Normal, doc.Root.Children[0].BlendMode);
        Assert.Contains(warnings, w => w.Contains("Glow"));
    }

    [Fact]
    public void Load_FullyTransparentLayer_AllocatesNoTiles()
    {
        var file = PdnWriter.Build(300, 300,
            [new PdnWriter.Layer("空", true, 255, "NormalBlendOp",
                Bgra(300, 300, (_, _) => SKColors.Empty))]);

        using var stream = new MemoryStream(file);
        using var doc = PdnFormat.Load(stream, out _);

        var layer = Assert.IsType<RasterLayer>(doc.Root.Children[0]);
        Assert.Equal(0, layer.Surface.TileCount);
    }

    [Fact]
    public void Load_RejectsFilesWithoutMagic()
    {
        using var stream = new MemoryStream("not a paint.net project"u8.ToArray());
        var ex = Assert.Throws<InvalidDataException>(() => PdnFormat.Load(stream, out _));
        Assert.Contains("PDN3", ex.Message);
    }

    [Fact]
    public void IsPdnFile_ChecksContentNotExtension()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pdn_test_{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(path, PdnWriter.Build(2, 2,
                [new PdnWriter.Layer("a", true, 255, "NormalBlendOp", Bgra(2, 2, (_, _) => SKColors.White))]));
            Assert.True(PdnFormat.IsPdnFile(path));

            File.WriteAllBytes(path, "PNG?"u8.ToArray());
            Assert.False(PdnFormat.IsPdnFile(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ---- 測試用的 .pdn 產生器 ----

    /// <summary>
    /// 寫出一份真正的 PDN3 檔：MS-NRBF 物件圖（paint.net 4.x 的欄位配置）+ 延後像素段。
    /// 只涵蓋讀取器會走到的記錄型別，包含第二層之後改用 ClassWithId 重用中繼資料。
    /// </summary>
    private static class PdnWriter
    {
        public sealed record Layer(
            string Name, bool Visible, byte Opacity, string BlendOp, byte[] Bgra);

        private const int LibraryId = 2;
        private const byte TypePrimitive = 0, TypeString = 1, TypeClass = 4, TypeObjectArray = 5;
        private const byte PrimBoolean = 1, PrimByte = 2, PrimInt32 = 8, PrimInt64 = 9;

        private readonly record struct Member(string Name, byte BinaryType, object? Info);

        private static Member Prim(string name, byte primitive) => new(name, TypePrimitive, primitive);
        private static Member Str(string name) => new(name, TypeString, null);
        private static Member Cls(string name, string typeName) => new(name, TypeClass, typeName);
        private static Member ObjArray(string name) => new(name, TypeObjectArray, null);

        public static byte[] Build(
            int width, int height, IReadOnlyList<Layer> layers,
            bool gzipChunks = false, bool gzipBody = false,
            int chunkSize = 256 * 1024, bool reverseChunkOrder = false)
        {
            var body = new MemoryStream();
            WriteObjectGraph(body, width, height, layers);
            foreach (var layer in layers)
                WriteDeferredBlock(body, layer.Bgra, gzipChunks, chunkSize, reverseChunkOrder);

            var header = Encoding.UTF8.GetBytes(
                $"<pdnImage width=\"{width}\" height=\"{height}\" layers=\"{layers.Count}\" />");

            var file = new MemoryStream();
            file.Write("PDN3"u8);
            file.WriteByte((byte)(header.Length & 0xFF));
            file.WriteByte((byte)((header.Length >> 8) & 0xFF));
            file.WriteByte((byte)((header.Length >> 16) & 0xFF));
            file.Write(header);

            if (gzipBody)
            {
                using (var gzip = new GZipStream(file, CompressionLevel.Fastest, leaveOpen: true))
                {
                    body.Position = 0;
                    body.CopyTo(gzip);
                }
            }
            else
            {
                file.WriteByte(0x00);
                file.WriteByte(0x01);
                file.Write(body.ToArray());
            }
            return file.ToArray();
        }

        private static void WriteObjectGraph(Stream stream, int width, int height, IReadOnlyList<Layer> layers)
        {
            using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            var nextId = 100;
            var layerMetadataId = 0;

            // SerializedStreamHeader + BinaryLibrary
            w.Write((byte)0); w.Write(1); w.Write(-1); w.Write(1); w.Write(0);
            w.Write((byte)12); w.Write(LibraryId);
            w.Write("PaintDotNet.Data, Version=4.9.0.0, Culture=neutral, PublicKeyToken=null");

            WriteClassHeader(w, 1, "PaintDotNet.Document",
                [Cls("layers", "PaintDotNet.LayerList"), Prim("width", PrimInt32), Prim("height", PrimInt32)]);

            // Document.layers
            WriteClassHeader(w, nextId++, "PaintDotNet.LayerList",
                [ObjArray("ArrayList+_items"), Prim("ArrayList+_size", PrimInt32)]);

            // ArrayList 的容量陣列比實際數量長，尾端補 null —— 真的檔案就長這樣
            var capacity = layers.Count + 3;
            w.Write((byte)16); w.Write(nextId++); w.Write(capacity);
            foreach (var layer in layers)
            {
                if (layerMetadataId == 0)
                {
                    layerMetadataId = nextId;
                    WriteClassHeader(w, nextId++, "PaintDotNet.BitmapLayer",
                    [
                        Cls("properties", "PaintDotNet.BitmapLayer+BitmapLayerProperties"),
                        Cls("surface", "PaintDotNet.Surface"),
                        Prim("Layer+width", PrimInt32),
                        Prim("Layer+height", PrimInt32),
                        Cls("Layer+properties", "PaintDotNet.Layer+LayerProperties"),
                    ]);
                }
                else
                {
                    w.Write((byte)1); w.Write(nextId++); w.Write(layerMetadataId);   // ClassWithId
                }

                WriteClassHeader(w, nextId++, "PaintDotNet.BitmapLayer+BitmapLayerProperties",
                    [Cls("blendOp", "PaintDotNet.UserBlendOp")]);
                WriteClassHeader(w, nextId++, "PaintDotNet.UserBlendOps+" + layer.BlendOp, []);

                WriteClassHeader(w, nextId++, "PaintDotNet.Surface",
                [
                    Prim("width", PrimInt32), Prim("height", PrimInt32), Prim("stride", PrimInt32),
                    Cls("scan0", "PaintDotNet.MemoryBlock"),
                ]);
                w.Write(width); w.Write(height); w.Write(width * 4);

                WriteClassHeader(w, nextId++, "PaintDotNet.MemoryBlock",
                    [Prim("length64", PrimInt64), Prim("hasParent", PrimBoolean), Prim("deferred", PrimBoolean)]);
                w.Write((long)layer.Bgra.Length); w.Write(false); w.Write(true);

                w.Write(width); w.Write(height);

                WriteClassHeader(w, nextId++, "PaintDotNet.Layer+LayerProperties",
                [
                    Str("name"), Prim("visible", PrimBoolean),
                    Prim("isBackground", PrimBoolean), Prim("opacity", PrimByte),
                ]);
                w.Write((byte)6); w.Write(nextId++); w.Write(layer.Name);
                w.Write(layer.Visible); w.Write(false); w.Write(layer.Opacity);
            }
            w.Write((byte)13); w.Write((byte)(capacity - layers.Count));   // ObjectNullMultiple256

            w.Write(layers.Count);      // ArrayList+_size
            w.Write(width);             // Document.width
            w.Write(height);            // Document.height
            w.Write((byte)11);          // MessageEnd
        }

        private static void WriteClassHeader(BinaryWriter w, int objectId, string typeName, Member[] members)
        {
            w.Write((byte)5);           // ClassWithMembersAndTypes
            w.Write(objectId);
            w.Write(typeName);
            w.Write(members.Length);
            foreach (var m in members) w.Write(m.Name);
            foreach (var m in members) w.Write(m.BinaryType);
            foreach (var m in members)
            {
                switch (m.BinaryType)
                {
                    case TypePrimitive: w.Write((byte)m.Info!); break;
                    case TypeClass: w.Write((string)m.Info!); w.Write(LibraryId); break;
                }
            }
            w.Write(LibraryId);
        }

        private static void WriteDeferredBlock(
            Stream stream, byte[] data, bool gzip, int chunkSize, bool reverseOrder)
        {
            stream.WriteByte((byte)(gzip ? 0 : 1));
            WriteUInt32BigEndian(stream, (uint)chunkSize);

            var count = (data.Length + chunkSize - 1) / chunkSize;
            var order = Enumerable.Range(0, count);
            foreach (var i in reverseOrder ? order.Reverse() : order)
            {
                var offset = i * chunkSize;
                var length = Math.Min(chunkSize, data.Length - offset);
                byte[] payload;
                if (gzip)
                {
                    var compressed = new MemoryStream();
                    using (var gz = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
                        gz.Write(data, offset, length);
                    payload = compressed.ToArray();
                }
                else
                {
                    payload = data.AsSpan(offset, length).ToArray();
                }

                WriteUInt32BigEndian(stream, (uint)i);
                WriteUInt32BigEndian(stream, (uint)payload.Length);
                stream.Write(payload);
            }
        }

        private static void WriteUInt32BigEndian(Stream stream, uint value)
        {
            stream.WriteByte((byte)(value >> 24));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
        }
    }
}
