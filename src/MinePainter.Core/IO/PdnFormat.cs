using System.IO.Compression;
using MinePainter.Core.Documents;
using MinePainter.Core.Layers;
using SkiaSharp;

namespace MinePainter.Core.IO;

/// <summary>
/// paint.net 專案檔（.pdn）匯入 —— 唯讀。
///
/// 檔案結構：<c>"PDN3"</c> + 3 位元組小端的 XML 標頭長度 + XML 標頭（縮圖等中繼資料）
/// + 2 位元組指示子 + BinaryFormatter 序列化的 PaintDotNet.Document 物件圖
/// + 各圖層的像素（「延後資料」，接在物件圖後面，依序列化順序排列）。
///
/// 指示子 <c>00 01</c> = 其後未壓縮（正好也是 NRBF 標頭的頭兩個位元組，容易看混）；
/// <c>1F 8B</c> = 其後整段（物件圖 + 像素）是一條 gzip 串流，舊版 paint.net 會這樣寫。
///
/// 像素本身是 BGRA <b>直通 alpha</b>（非預乘），每層一段 chunk 化的資料：
/// 1 位元組格式版本（0 = 每塊 gzip、1 = 原始）+ 4 位元組區塊大小，接著每塊
/// 4 位元組區塊編號 + 4 位元組資料長度 + 資料，全部大端序、區塊順序不保證。
/// </summary>
public static class PdnFormat
{
    private const int MaxDimension = 65535;
    private const long MaxSurfaceBytes = int.MaxValue;
    private const int MaxChunkBytes = 1 << 28;

    /// <summary>快速判斷副檔名以外的真實格式（拖放與「支援的檔案」篩選器用）。</summary>
    public static bool IsPdnFile(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[4];
            return file.Read(magic) == 4 && magic.SequenceEqual("PDN3"u8);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static Document Load(string path) => Load(path, out _);

    /// <summary><paramref name="warnings"/> 收集「讀得進來但語意有損」的地方（例如 Skia 沒有的混合模式）。</summary>
    public static Document Load(string path, out IReadOnlyList<string> warnings)
    {
        using var file = File.OpenRead(path);
        return Load(file, out warnings);
    }

    public static Document Load(Stream stream, out IReadOnlyList<string> warnings)
    {
        if (!stream.CanSeek)
            throw new ArgumentException("讀取 .pdn 需要可搜尋的資料流。", nameof(stream));

        var notes = new List<string>();
        warnings = notes;

        using var body = OpenBody(stream);
        var payload = NrbfReader.Read(body);
        var document = ReadDocument(payload, body, notes);
        return document;
    }

    // ---- 容器 ----

    /// <summary>跳過 PDN3 標頭，回傳「物件圖 + 延後像素」那一段（必要時解壓）。</summary>
    private static Stream OpenBody(Stream stream)
    {
        var magic = ReadExactly(stream, 4);
        if (!magic.AsSpan().SequenceEqual("PDN3"u8))
            throw new InvalidDataException("不是 paint.net 專案檔（缺少 PDN3 標記）。");

        var sizeBytes = ReadExactly(stream, 3);
        var headerSize = sizeBytes[0] | (sizeBytes[1] << 8) | (sizeBytes[2] << 16);
        stream.Seek(headerSize, SeekOrigin.Current);    // XML 標頭只有縮圖等中繼資料，物件圖裡都有

        var indicator = ReadExactly(stream, 2);
        if (indicator[0] == 0x1F && indicator[1] == 0x8B)
        {
            stream.Seek(-2, SeekOrigin.Current);
            return new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
        }

        if (indicator[0] != 0x00 || indicator[1] != 0x01)
            throw new InvalidDataException(
                $"無法辨識的 .pdn 內容指示子（{indicator[0]:X2} {indicator[1]:X2}）。");

        return new NonClosingStream(stream);
    }

    // ---- 物件圖 ----

    private static Document ReadDocument(NrbfPayload payload, Stream body, List<string> notes)
    {
        var root = payload.Root;
        if (!root.TypeName.EndsWith("Document", StringComparison.Ordinal))
            throw new InvalidDataException($"預期 PaintDotNet.Document，實際是 {root.TypeName}。");

        var width = root.Int32("width") ?? throw new InvalidDataException(".pdn 缺少文件寬度。");
        var height = root.Int32("height") ?? throw new InvalidDataException(".pdn 缺少文件高度。");
        if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension)
            throw new InvalidDataException($".pdn 文件尺寸不合理：{width}×{height}。");

        var layers = ReadLayerList(root);

        // 延後像素接在物件圖後面，順序 = MemoryBlock 被序列化的順序，所以要一口氣依序讀完，
        // 就算某一段我們用不到也一樣（少讀一段後面全部會錯位）。
        var pixels = ReadDeferredBlocks(payload, body);

        var document = new Document(width, height);
        try
        {
            lock (document.SyncRoot)
            {
                foreach (var source in layers)
                    document.Root.Add(BuildLayer(source, width, height, pixels, notes));

                // paint.net 的 layers[0] 是最底層，和我們的 Children 同向。
                document.ActiveLayer = document.Root.Children.LastOrDefault();
            }
        }
        catch
        {
            document.Dispose();
            throw;
        }
        return document;
    }

    private static List<NrbfObject> ReadLayerList(NrbfObject root)
    {
        var list = root.Member("layers") as NrbfObject
            ?? throw new InvalidDataException(".pdn 缺少圖層清單。");

        // LayerList 繼承 ArrayList：_items 是容量陣列，尾端補 null，真正的數量在 _size。
        var items = list.Member("ArrayList+_items") as object?[]
            ?? throw new InvalidDataException(".pdn 圖層清單格式不符。");
        var count = list.Int32("ArrayList+_size") ?? items.Length;
        count = Math.Clamp(count, 0, items.Length);

        var layers = new List<NrbfObject>(count);
        for (var i = 0; i < count; i++)
        {
            if (items[i] is NrbfObject layer) layers.Add(layer);
            else throw new InvalidDataException($".pdn 第 {i + 1} 個圖層是空的或格式不符。");
        }
        if (layers.Count == 0) throw new InvalidDataException(".pdn 沒有任何圖層。");
        return layers;
    }

    private static RasterLayer BuildLayer(
        NrbfObject source, int docWidth, int docHeight,
        IReadOnlyDictionary<NrbfObject, byte[]> pixels, List<string> notes)
    {
        // Layer 與 BitmapLayer 都有 properties 欄位，名字會被改寫成 Layer+properties；
        // 用型別找比用名字穩。
        var layerProps = source.MemberOfType(t =>
            t.EndsWith("LayerProperties", StringComparison.Ordinal) &&
            !t.EndsWith("BitmapLayerProperties", StringComparison.Ordinal));
        var bitmapProps = source.MemberOfType(t =>
            t.EndsWith("BitmapLayerProperties", StringComparison.Ordinal));

        var name = layerProps?.String("name");
        var layer = new RasterLayer
        {
            Name = string.IsNullOrEmpty(name) ? "圖層" : name,
            IsVisible = layerProps?.Bool("visible") ?? true,
            Opacity = (layerProps?.Byte("opacity") ?? 255) / 255f,
            BlendMode = ReadBlendMode(bitmapProps, layerProps, name, notes),
        };

        try
        {
            var surface = source.Member("surface") as NrbfObject
                ?? source.MemberOfType(t => t.EndsWith("Surface", StringComparison.Ordinal));
            if (surface != null)
                CopySurface(surface, layer, docWidth, docHeight, pixels, notes);
            else
                notes.Add($"圖層「{layer.Name}」沒有像素資料，已略過。");
        }
        catch
        {
            layer.Dispose();
            throw;
        }
        return layer;
    }

    private static void CopySurface(
        NrbfObject surface, RasterLayer layer, int docWidth, int docHeight,
        IReadOnlyDictionary<NrbfObject, byte[]> pixels, List<string> notes)
    {
        var width = surface.Int32("width") ?? 0;
        var height = surface.Int32("height") ?? 0;
        var stride = surface.Int32("stride") ?? width * 4;
        var block = surface.Member("scan0") as NrbfObject
            ?? surface.MemberOfType(t => t.EndsWith("MemoryBlock", StringComparison.Ordinal));

        if (width <= 0 || height <= 0 || block == null) return;
        if (stride < width * 4)
            throw new InvalidDataException($".pdn 圖層 stride 不合理（{stride} < {width * 4}）。");

        if (!pixels.TryGetValue(block, out var data))
        {
            notes.Add($"圖層「{layer.Name}」的像素資料無法讀取，已留空。");
            return;
        }
        if ((long)height * stride > data.Length)
            throw new InvalidDataException($".pdn 圖層「{layer.Name}」的像素資料長度不足。");

        if (width != docWidth || height != docHeight)
            notes.Add($"圖層「{layer.Name}」尺寸 {width}×{height} 與畫布 {docWidth}×{docHeight} 不同，已對齊左上角。");

        if (IsFullyTransparent(data, width, height, stride)) return;  // 空圖層不必配置 tile
        CopyUnpremultiplied(layer, data, width, height, stride);
    }

    /// <summary>paint.net 存的是 BGRA 直通 alpha，我們的 tile 是預乘，交給 Skia 轉。</summary>
    private static unsafe void CopyUnpremultiplied(
        RasterLayer layer, byte[] data, int width, int height, int stride)
    {
        var sourceInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var premultiplied = new SKBitmap(sourceInfo.WithAlphaType(SKAlphaType.Premul));
        using var destination = premultiplied.PeekPixels();

        fixed (byte* scan0 = data)
        {
            using var source = new SKPixmap(sourceInfo, (IntPtr)scan0, stride);
            if (!source.ReadPixels(destination))
                throw new InvalidDataException(".pdn 像素轉換失敗（直通 alpha → 預乘）。");
        }

        layer.Surface.CopyFrom(destination, SKPointI.Empty);
    }

    private static bool IsFullyTransparent(byte[] data, int width, int height, int stride)
    {
        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            for (var x = 3; x < width * 4; x += 4)
                if (data[row + x] != 0) return false;
        }
        return true;
    }

    // ---- 混合模式 ----

    /// <summary>PaintDotNet.LayerBlendMode 的列舉順序（4.x 起 LayerProperties 也會存一份）。</summary>
    private static readonly string[] LayerBlendModeNames =
    [
        "Normal", "Multiply", "Additive", "ColorBurn", "ColorDodge", "Reflect", "Glow",
        "Overlay", "Difference", "Negation", "Lighten", "Darken", "Screen", "Xor",
    ];

    private static BlendMode ReadBlendMode(
        NrbfObject? bitmapProps, NrbfObject? layerProps, string? layerName, List<string> notes)
    {
        // 首選 blendOp 的型別名（新舊版都有）；只有 4.x 才有的 blendMode 列舉當後備。
        var name = (bitmapProps?.Member("blendOp") as NrbfObject)?.TypeName is { } typeName
            ? typeName[(typeName.LastIndexOf('+') + 1)..].Replace("BlendOp", "")
            : LayerBlendModeName(layerProps);

        if (name is null or "") return BlendMode.Normal;

        // Reflect/Glow/Negation/Xor 是 paint.net 自有的算式，Skia 沒有對應。
        if (Enum.TryParse<BlendMode>(name, ignoreCase: false, out var mode)) return mode;

        notes.Add($"圖層「{layerName ?? "?"}」的混合模式「{name}」沒有對應，已改為一般。");
        return BlendMode.Normal;
    }

    private static string? LayerBlendModeName(NrbfObject? layerProps)
    {
        if ((layerProps?.Member("blendMode") as NrbfObject)?.Int32("value__") is not { } value) return null;
        return value >= 0 && value < LayerBlendModeNames.Length ? LayerBlendModeNames[value] : null;
    }

    // ---- 延後像素 ----

    private static Dictionary<NrbfObject, byte[]> ReadDeferredBlocks(NrbfPayload payload, Stream body)
    {
        var blocks = new Dictionary<NrbfObject, byte[]>();
        foreach (var block in payload.ObjectsInStreamOrder)
        {
            if (!block.TypeName.EndsWith("MemoryBlock", StringComparison.Ordinal)) continue;

            var length = block.Int64("length64") ?? block.Int64("length") ?? 0;
            if (length <= 0 || length > MaxSurfaceBytes)
                throw new InvalidDataException($".pdn 像素區塊長度不合理（{length}）。");

            if (block.Bool("hasParent") == true)
                throw new InvalidDataException(".pdn 使用了共用像素區塊，目前不支援。");

            if (block.Bool("deferred") == true)
                blocks[block] = ReadDeferredBlock(body, length);
            else if (block.Member("pointerData") is byte[] inline)
                blocks[block] = inline;
        }
        return blocks;
    }

    private static byte[] ReadDeferredBlock(Stream body, long length)
    {
        var formatVersion = body.ReadByte();
        if (formatVersion is not (0 or 1))
            throw new InvalidDataException($".pdn 像素區塊格式版本無法辨識（{formatVersion}）。");

        var chunkSize = ReadUInt32BigEndian(body);
        if (chunkSize == 0) throw new InvalidDataException(".pdn 像素區塊大小為 0。");

        var data = new byte[length];
        var chunkCount = (int)((length + chunkSize - 1) / chunkSize);
        var seen = new bool[chunkCount];

        for (var i = 0; i < chunkCount; i++)
        {
            var chunkNumber = ReadUInt32BigEndian(body);
            var dataSize = ReadUInt32BigEndian(body);
            if (chunkNumber >= (uint)chunkCount)
                throw new InvalidDataException($".pdn 像素區塊編號越界（{chunkNumber} / {chunkCount}）。");
            if (seen[chunkNumber])
                throw new InvalidDataException($".pdn 像素區塊 {chunkNumber} 重複。");
            seen[chunkNumber] = true;

            if (dataSize > MaxChunkBytes)
                throw new InvalidDataException($".pdn 像素區塊資料長度不合理（{dataSize}）。");

            var offset = (long)chunkNumber * chunkSize;
            var expected = (int)Math.Min(chunkSize, length - offset);
            var raw = ReadExactly(body, (int)dataSize);

            if (formatVersion == 0) Decompress(raw, data, (int)offset, expected);
            else if (raw.Length < expected)
                throw new InvalidDataException(".pdn 像素區塊資料長度不足。");
            else Array.Copy(raw, 0, data, offset, expected);
        }
        return data;
    }

    private static void Decompress(byte[] compressed, byte[] destination, int offset, int expected)
    {
        using var gzip = new GZipStream(new MemoryStream(compressed, writable: false), CompressionMode.Decompress);
        var written = 0;
        while (written < expected)
        {
            var n = gzip.Read(destination, offset + written, expected - written);
            if (n <= 0) throw new InvalidDataException(".pdn 像素區塊解壓後長度不足。");
            written += n;
        }
    }

    private static uint ReadUInt32BigEndian(Stream stream)
    {
        var bytes = ReadExactly(stream, 4);
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static byte[] ReadExactly(Stream stream, int count)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = stream.Read(buffer, read, count - read);
            if (n <= 0) throw new EndOfStreamException(".pdn 檔案提前結束，可能已損毀。");
            read += n;
        }
        return buffer;
    }

    /// <summary>讓 NRBF 讀取器與延後像素共用同一條 stream，但 using 掉它時不關掉底層檔案。</summary>
    private sealed class NonClosingStream : Stream
    {
        private readonly Stream _inner;

        public NonClosingStream(Stream inner) => _inner = inner;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override int ReadByte() => _inner.ReadByte();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
