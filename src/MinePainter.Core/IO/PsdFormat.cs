using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using MinePainter.Core.Documents;
using MinePainter.Core.Layers;
using MinePainter.Core.Vectors;
using SkiaSharp;

namespace MinePainter.Core.IO;

/// <summary>
/// Adobe Photoshop 文件（.psd／.psb）匯入 —— 唯讀。全部自己解析，Core 不多帶任何套件。
///
/// 檔案由五段組成，全部大端序：
/// 標頭（<c>"8BPS"</c>、版本 1＝PSD／2＝PSB、通道數、尺寸、位元深度、色彩模式）
/// → 色彩模式資料（只有索引色的調色盤有內容）→ 影像資源（縮圖、ICC 等，用不到）
/// → 圖層與遮罩資訊 → 最後是平面化的合成影像。
///
/// 圖層記錄由下而上排列，和我們的 <see cref="GroupLayer.Children"/> 同向。群組不是巢狀結構，
/// 而是用「區段分隔」記錄夾出來：先出現一筆 <c>lsct</c> 類型 3 的界線（群組底部），
/// 接著是子圖層，最後一筆類型 1／2 的記錄才是群組本身（名稱、不透明度都在這一筆）。
/// 16 位元檔案的圖層清單不在正規位置，而是藏在 <c>Lr16</c> 附加資訊區塊裡。
///
/// 每個通道各存一份（planar），壓縮方式有原始、PackBits RLE、zlib、zlib＋逐列差分預測。
/// 通道編號 0／1／2 是 R／G／B（灰階與索引色只有 0），−1 是透明度，−2 是使用者遮色片。
///
/// 剪裁遮色片（clipping）烙成像素：被剪裁圖層的 alpha 乘上底下那層的 alpha。剪裁到群組時，
/// 底是群組合成後的透明度，所以每個群組邊讀邊累加一張畫布大小的 alpha（混合模式不影響 alpha，只看不透明度）。
///
/// 圖層樣式（<c>lfx2</c>）對成我們的效果（見 <see cref="PsdLayerStyle"/>）；文字圖層（<c>TySh</c>）
/// 解成可編輯文字（見 <see cref="PsdTextLayer"/>），解不出來才退回 Photoshop 存好的點陣快照。
///
/// 刻意不做的：調整圖層與填色圖層（沒有像素，略過並提示）、智慧型物件的可編輯性（只拿它的點陣結果）、
/// 32 位元／通道的 HDR 檔。
/// </summary>
public static class PsdFormat
{
    /// <summary>PSB 的尺寸上限（PSD 本身只到 30000）。</summary>
    private const int MaxDimension = 300_000;
    private const long MaxPixelBytes = int.MaxValue;
    private const int MaxChannels = 56;

    /// <summary>快速判斷副檔名以外的真實格式（拖放與「支援的檔案」篩選器用）。</summary>
    public static bool IsPsdFile(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[4];
            return file.Read(magic) == 4 && magic.SequenceEqual("8BPS"u8);
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

    /// <summary><paramref name="warnings"/> 收集「讀得進來但語意有損」的地方（略過的調整圖層、沒對應的混合模式…）。</summary>
    public static Document Load(string path, out IReadOnlyList<string> warnings)
    {
        using var file = File.OpenRead(path);
        return Load(file, out warnings);
    }

    public static Document Load(Stream stream, out IReadOnlyList<string> warnings)
    {
        if (!stream.CanSeek)
            throw new ArgumentException("讀取 .psd 需要可搜尋的資料流。", nameof(stream));

        var notes = new List<string>();
        warnings = notes;

        var reader = new Reader(stream);
        var header = ReadHeader(reader);
        var palette = ReadColorModeData(reader, header);
        header = header with { GlobalAngle = ReadImageResources(reader) };

        var records = ReadLayerSection(reader, header, notes);

        var document = new Document(header.Width, header.Height);
        try
        {
            lock (document.SyncRoot)
            {
                if (records.Count > 0)
                    BuildLayerTree(document, records, header, palette, notes);
                else
                    document.Root.Add(ReadMergedImage(reader, header, palette));

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

    // ---- 標頭 ----

    private enum ColorMode { Bitmap = 0, Grayscale = 1, Indexed = 2, Rgb = 3, Cmyk = 4, Multichannel = 7, Duotone = 8, Lab = 9 }

    private readonly record struct Header(bool IsPsb, int Channels, int Width, int Height, int Depth, ColorMode Mode)
    {
        /// <summary>圖層樣式「使用整體光源」的角度（影像資源 1037；Photoshop 預設 120）。</summary>
        public int GlobalAngle { get; init; } = 120;

        /// <summary>這個色彩模式本身佔幾個通道；合成影像多出來的第一個就是透明度。</summary>
        public int ColorChannels => Mode switch
        {
            ColorMode.Rgb => 3,
            ColorMode.Cmyk => 4,
            _ => 1,
        };
    }

    private static Header ReadHeader(Reader reader)
    {
        if (!reader.Bytes(4).AsSpan().SequenceEqual("8BPS"u8))
            throw new InvalidDataException("不是 Photoshop 文件（缺少 8BPS 標記）。");

        var version = reader.UInt16();
        if (version is not (1 or 2))
            throw new InvalidDataException($"無法辨識的 Photoshop 文件版本（{version}）。");
        reader.Skip(6);

        var channels = reader.UInt16();
        var height = checked((int)Math.Min(reader.UInt32(), int.MaxValue));
        var width = checked((int)Math.Min(reader.UInt32(), int.MaxValue));
        var depth = reader.UInt16();
        var mode = (ColorMode)reader.UInt16();

        if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension)
            throw new InvalidDataException($".psd 文件尺寸不合理：{width}×{height}。");
        if ((long)width * height * 4 > MaxPixelBytes)
            throw new InvalidDataException($".psd 文件太大（{width}×{height}），無法載入。");
        if (channels < 1 || channels > MaxChannels)
            throw new InvalidDataException($".psd 通道數不合理（{channels}）。");
        if (depth is not (8 or 16))
            throw new InvalidDataException(depth == 32
                ? "32 位元／通道的 Photoshop 文件目前不支援，請先在 Photoshop 轉成 8 或 16 位元。"
                : $"不支援的位元深度（{depth}）。");
        if (mode is not (ColorMode.Grayscale or ColorMode.Indexed or ColorMode.Rgb or ColorMode.Cmyk or ColorMode.Duotone))
            throw new InvalidDataException($"不支援的色彩模式（{mode}），請先在 Photoshop 轉成 RGB。");

        return new Header(version == 2, channels, width, height, depth, mode);
    }

    /// <summary>索引色的調色盤：R×256、G×256、B×256 三段連著放。其他模式這段是空的。</summary>
    private static byte[]? ReadColorModeData(Reader reader, Header header)
    {
        var length = reader.UInt32();
        if (header.Mode != ColorMode.Indexed)
        {
            reader.Skip(length);
            return null;
        }
        if (length < 768)
            throw new InvalidDataException(".psd 索引色文件缺少調色盤。");
        var palette = reader.Bytes(768);
        reader.Skip(length - 768);
        return palette;
    }

    /// <summary>
    /// 影像資源區：一串 8BIM + ID + Pascal 名稱（補到偶數）+ 長度 + 資料（補到偶數）。
    /// 只要整體光源角度（1037），其餘（縮圖、ICC、解析度）匯入用不到。
    /// </summary>
    private static int ReadImageResources(Reader reader)
    {
        var length = reader.UInt32();
        var end = reader.Position + length;
        var globalAngle = 120;
        while (reader.Position + 12 <= end)
        {
            if (!reader.Bytes(4).AsSpan().SequenceEqual("8BIM"u8)) break;
            var id = reader.UInt16();
            var nameLength = reader.Byte();
            reader.Skip(nameLength + (nameLength + 1) % 2);
            var size = reader.UInt32();
            var dataStart = reader.Position;
            if (id == 1037 && size >= 4) globalAngle = reader.Int32();
            reader.Position = dataStart + size + size % 2;
        }
        reader.Position = end;
        return globalAngle;
    }

    // ---- 圖層區 ----

    private sealed class ChannelRecord
    {
        public int Id;
        public long Length;
        public byte[]? Samples;   // 已轉成 8 位元的樣本
    }

    private sealed class LayerRecord
    {
        public SKRectI Rect;
        public readonly List<ChannelRecord> Channels = [];
        public string BlendKey = "norm";
        public byte Opacity = 255;
        public byte FillOpacity = 255;
        public bool Clipped;
        public bool Hidden;
        public string Name = "";
        public bool HasMask;
        public SKRectI MaskRect;
        public byte MaskDefault = 255;
        public byte MaskFlags;
        public int SectionType;     // lsct：0 一般、1／2 群組本體、3 群組底部界線
        public bool IsAdjustmentOrFill;
        public byte[]? StyleData;       // lfx2 原始位元組
        public byte[]? TextData;        // TySh 原始位元組
        public bool HasLegacyStyle;     // 只有舊版 lrFX、沒有 lfx2
    }

    /// <summary>附加資訊區塊中，PSB 用 8 位元組長度的那幾個 key（其餘仍是 4 位元組）。</summary>
    private static readonly HashSet<string> PsbLongLengthKeys =
    [
        "LMsk", "Lr16", "Lr32", "Layr", "Mt16", "Mt32", "Mtrn", "Alph", "FMsk", "lnk2", "FEid", "FXid", "PxSD", "cinf",
    ];

    private static List<LayerRecord> ReadLayerSection(Reader reader, Header header, List<string> notes)
    {
        var sectionLength = reader.Length(header.IsPsb);
        if (sectionLength == 0) return [];
        var sectionEnd = reader.Position + sectionLength;

        var records = new List<LayerRecord>();
        var infoLength = reader.Length(header.IsPsb);
        var infoStart = reader.Position;
        if (infoLength > 0)
        {
            records = ReadLayerInfo(reader, header, notes);
            reader.Position = infoStart + infoLength;
        }

        // 16 位元檔的正規圖層清單是空的，真正的清單在全域遮罩之後的 Lr16 區塊裡。
        if (records.Count == 0 && reader.Position + 4 <= sectionEnd)
        {
            var globalMask = reader.UInt32();
            reader.Skip(globalMask);

            while (reader.Position + 12 <= sectionEnd)
            {
                if (!ReadBlockSignature(reader)) break;
                var key = Encoding.ASCII.GetString(reader.Bytes(4));
                var length = header.IsPsb && PsbLongLengthKeys.Contains(key) ? reader.Int64() : reader.UInt32();
                var start = reader.Position;
                if (key is "Lr16" or "Lr32")
                    records = ReadLayerInfo(reader, header, notes);
                reader.Position = start + length;
                AlignToSignature(reader, sectionEnd);
            }
        }

        reader.Position = sectionEnd;
        return records;
    }

    private static List<LayerRecord> ReadLayerInfo(Reader reader, Header header, List<string> notes)
    {
        // 負數代表合成影像的第一個多出來的通道是透明度；圖層本身的數量取絕對值。
        var count = Math.Abs((int)reader.Int16());
        var records = new List<LayerRecord>(count);
        for (var i = 0; i < count; i++)
            records.Add(ReadLayerRecord(reader, header));

        foreach (var record in records)
        {
            foreach (var channel in record.Channels)
            {
                var start = reader.Position;
                var rect = channel.Id switch
                {
                    -2 or -3 => record.MaskRect,
                    _ => record.Rect,
                };
                channel.Samples = ReadChannelData(reader, channel.Length, rect.Width, rect.Height, header, record.Name);
                reader.Position = start + channel.Length;
            }
        }

        return records;
    }

    private static LayerRecord ReadLayerRecord(Reader reader, Header header)
    {
        var record = new LayerRecord();
        var top = reader.Int32();
        var left = reader.Int32();
        var bottom = reader.Int32();
        var right = reader.Int32();
        record.Rect = new SKRectI(left, top, right, bottom);
        if (record.Rect.Width < 0 || record.Rect.Height < 0 || record.Rect.Width > MaxDimension || record.Rect.Height > MaxDimension)
            throw new InvalidDataException($".psd 圖層範圍不合理（{left},{top} – {right},{bottom}）。");

        var channelCount = reader.UInt16();
        if (channelCount > MaxChannels)
            throw new InvalidDataException($".psd 圖層通道數不合理（{channelCount}）。");
        for (var i = 0; i < channelCount; i++)
        {
            var id = reader.Int16();
            var length = reader.Length(header.IsPsb);
            if (length < 2 || length > MaxPixelBytes)
                throw new InvalidDataException($".psd 通道資料長度不合理（{length}）。");
            record.Channels.Add(new ChannelRecord { Id = id, Length = length });
        }

        if (!reader.Bytes(4).AsSpan().SequenceEqual("8BIM"u8))
            throw new InvalidDataException(".psd 圖層記錄缺少 8BIM 混合模式標記。");
        record.BlendKey = Encoding.ASCII.GetString(reader.Bytes(4));
        record.Opacity = reader.Byte();
        record.Clipped = reader.Byte() != 0;
        var flags = reader.Byte();
        record.Hidden = (flags & 0x02) != 0;
        reader.Skip(1);

        var extraLength = reader.UInt32();
        var extraEnd = reader.Position + extraLength;

        // 遮色片：長度 0 沒有；20 只有一份；36 以上多帶「真實」使用者遮色片的參數
        var maskLength = reader.UInt32();
        if (maskLength >= 20)
        {
            var maskStart = reader.Position;
            var mTop = reader.Int32();
            var mLeft = reader.Int32();
            var mBottom = reader.Int32();
            var mRight = reader.Int32();
            record.MaskRect = new SKRectI(mLeft, mTop, mRight, mBottom);
            record.MaskDefault = reader.Byte();
            record.MaskFlags = reader.Byte();
            record.HasMask = true;
            reader.Position = maskStart + maskLength;
        }
        else
        {
            reader.Skip(maskLength);
        }

        reader.Skip(reader.UInt32());   // 混合範圍（blending ranges），對匯入沒有意義

        // Pascal 字串名稱，含長度位元組補到 4 的倍數。這裡是系統字碼頁，中文名稱幾乎一定是亂碼，
        // 所以只當後備；正確的 Unicode 名稱在 luni 區塊。
        var nameLength = reader.Byte();
        var nameBytes = reader.Bytes(nameLength);
        reader.Skip((4 - (nameLength + 1) % 4) % 4);
        record.Name = Encoding.Latin1.GetString(nameBytes);

        while (reader.Position + 12 <= extraEnd)
        {
            if (!ReadBlockSignature(reader)) break;
            var key = Encoding.ASCII.GetString(reader.Bytes(4));
            var length = header.IsPsb && PsbLongLengthKeys.Contains(key) ? reader.Int64() : reader.UInt32();
            var start = reader.Position;
            if (start + length > extraEnd)
                throw new InvalidDataException($".psd 圖層附加資訊「{key}」超出範圍。");
            ReadAdditionalInfo(reader, key, length, record);
            reader.Position = start + length;
            AlignToSignature(reader, extraEnd);
        }

        reader.Position = extraEnd;
        return record;
    }

    /// <summary>沒有像素、只靠參數呈現的圖層種類（調整圖層與填色圖層），匯入時只能略過。</summary>
    private static readonly HashSet<string> AdjustmentAndFillKeys =
    [
        "SoCo", "GdFl", "PtFl",                                          // 純色／漸層／圖樣填色
        "levl", "curv", "brit", "blnc", "hue ", "hue2", "selc", "mixr",  // 色階／曲線／亮度／色彩平衡／色相／選取顏色／混合器
        "grdm", "phfl", "expA", "vibA", "thrs", "nvrt", "post", "blwh",  // 漸層對應／相片濾鏡／曝光／自然飽和／臨界值／負片／色調分離／黑白
        "CgEd", "clrL",
    ];

    private static void ReadAdditionalInfo(Reader reader, string key, long length, LayerRecord record)
    {
        switch (key)
        {
            case "luni":
                var chars = reader.UInt32();
                if (chars > 0 && chars * 2 <= length - 4)
                    record.Name = Encoding.BigEndianUnicode.GetString(reader.Bytes((int)(chars * 2))).TrimEnd('\0');
                break;
            case "lsct":
                if (length >= 4) record.SectionType = (int)reader.UInt32();
                // 群組自己的混合模式（有 12 位元組以上時）通常是 pass 直通，圖層記錄本身那個 key 才是可用的
                break;
            case "iOpa":
                if (length >= 1) record.FillOpacity = reader.Byte();
                break;
            case "TySh":
                record.TextData = reader.Bytes(length);
                break;
            case "lfx2":
                record.StyleData = reader.Bytes(length);
                break;
            case "lrFX":
                record.HasLegacyStyle = true;
                break;
            default:
                if (AdjustmentAndFillKeys.Contains(key)) record.IsAdjustmentOrFill = true;
                break;
        }
    }

    private static bool ReadBlockSignature(Reader reader)
    {
        var signature = reader.Bytes(4);
        return signature.AsSpan().SequenceEqual("8BIM"u8) || signature.AsSpan().SequenceEqual("8B64"u8);
    }

    /// <summary>
    /// 附加資訊區塊的長度到底補不補齊（2 或 4 位元組），不同版本的 Photoshop 與第三方軟體寫法不一。
    /// 與其押一種，不如直接看下一個位置是不是簽章，不是就往後最多找 3 個位元組。
    /// </summary>
    private static void AlignToSignature(Reader reader, long limit)
    {
        var position = reader.Position;
        for (var pad = 0; pad <= 3; pad++)
        {
            if (position + pad + 4 > limit) return;
            reader.Position = position + pad;
            var isSignature = ReadBlockSignature(reader);
            reader.Position = position + pad;
            if (isSignature) return;
        }
        reader.Position = position;
    }

    // ---- 通道解碼 ----

    /// <summary>讀一個通道並轉成 8 位元樣本（列主序，緊密排列）。範圍為空時回傳 null。</summary>
    private static byte[]? ReadChannelData(Reader reader, long length, int width, int height, Header header, string layerName)
    {
        if (width <= 0 || height <= 0) return null;
        var bytesPerSample = header.Depth / 8;
        var rawLength = (long)width * height * bytesPerSample;
        if (rawLength > MaxPixelBytes)
            throw new InvalidDataException($".psd 圖層「{layerName}」太大，無法載入。");

        var compression = reader.UInt16();
        var raw = new byte[rawLength];
        var rowBytes = width * bytesPerSample;

        switch (compression)
        {
            case 0:
                reader.Fill(raw);
                break;
            case 1:
                DecodeRle(reader, raw, rowBytes, height, header.IsPsb, layerName);
                break;
            case 2:
            case 3:
                Inflate(reader.Bytes(checked((int)(length - 2))), raw, layerName);
                if (compression == 3) UndoPrediction(raw, rowBytes, height, bytesPerSample);
                break;
            default:
                throw new InvalidDataException($".psd 圖層「{layerName}」使用了無法辨識的壓縮方式（{compression}）。");
        }

        return bytesPerSample == 1 ? raw : Downconvert16(raw);
    }

    /// <summary>PackBits：先是每一列的壓縮後長度（PSD 2 位元組、PSB 4 位元組），接著才是資料。</summary>
    private static void DecodeRle(Reader reader, byte[] raw, int rowBytes, int height, bool isPsb, string layerName)
    {
        var rowLengths = new int[height];
        for (var y = 0; y < height; y++)
            rowLengths[y] = isPsb ? checked((int)reader.UInt32()) : reader.UInt16();

        for (var y = 0; y < height; y++)
        {
            var packed = reader.Bytes(rowLengths[y]);
            if (!UnpackBits(packed, raw.AsSpan(y * rowBytes, rowBytes)))
                throw new InvalidDataException($".psd 圖層「{layerName}」的 RLE 資料長度不足。");
        }
    }

    private static bool UnpackBits(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var read = 0;
        var written = 0;
        while (written < destination.Length && read < source.Length)
        {
            var header = (sbyte)source[read++];
            if (header >= 0)
            {
                var count = Math.Min(header + 1, destination.Length - written);
                if (read + count > source.Length) return false;
                source.Slice(read, count).CopyTo(destination.Slice(written, count));
                read += count;
                written += count;
            }
            else if (header != -128)
            {
                if (read >= source.Length) return false;
                var count = Math.Min(1 - header, destination.Length - written);
                destination.Slice(written, count).Fill(source[read++]);
                written += count;
            }
        }
        return written == destination.Length;
    }

    private static void Inflate(byte[] compressed, byte[] destination, string layerName)
    {
        using var zlib = new ZLibStream(new MemoryStream(compressed, writable: false), CompressionMode.Decompress);
        var written = 0;
        while (written < destination.Length)
        {
            var n = zlib.Read(destination, written, destination.Length - written);
            if (n <= 0) throw new InvalidDataException($".psd 圖層「{layerName}」的 zip 資料解壓後長度不足。");
            written += n;
        }
    }

    /// <summary>「zip 加預測」存的是每個樣本與左鄰的差，逐列累加還原。</summary>
    private static void UndoPrediction(byte[] raw, int rowBytes, int height, int bytesPerSample)
    {
        for (var y = 0; y < height; y++)
        {
            var row = raw.AsSpan(y * rowBytes, rowBytes);
            if (bytesPerSample == 1)
            {
                for (var x = 1; x < row.Length; x++) row[x] += row[x - 1];
            }
            else
            {
                ushort previous = BinaryPrimitives.ReadUInt16BigEndian(row);
                for (var x = 2; x < row.Length; x += 2)
                {
                    previous = (ushort)(previous + BinaryPrimitives.ReadUInt16BigEndian(row[x..]));
                    BinaryPrimitives.WriteUInt16BigEndian(row[x..], previous);
                }
            }
        }
    }

    private static byte[] Downconvert16(byte[] raw)
    {
        var result = new byte[raw.Length / 2];
        for (var i = 0; i < result.Length; i++)
        {
            var value = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(i * 2));
            result[i] = (byte)((value * 255 + 32767) / 65535);
        }
        return result;
    }

    // ---- 組圖層樹 ----

    private static void BuildLayerTree(
        Document document, List<LayerRecord> records, Header header, byte[]? palette, List<string> notes)
    {
        var stack = new Stack<GroupLayer>();
        var opened = new List<GroupLayer>();
        GroupLayer Current() => stack.Count > 0 ? stack.Peek() : document.Root;

        // 剪裁的底：最近一個沒被剪裁的點陣圖層（含它的遮色片），或剛收尾的群組（它累加出來的 alpha）。
        // 進入新群組時清掉 —— 群組裡第一層不可能剪裁到群組外面。
        ClipBase? clipBase = null;
        var planes = new Stack<AlphaPlane>();
        planes.Push(new AlphaPlane(document.Width, document.Height));

        try
        {
            foreach (var record in records)
            {
                switch (record.SectionType)
                {
                    case 3:
                        var group = new GroupLayer();
                        stack.Push(group);
                        opened.Add(group);
                        planes.Push(new AlphaPlane(document.Width, document.Height));
                        clipBase = null;
                        break;

                    case 1:
                    case 2:
                        if (stack.Count == 0)
                        {
                            notes.Add($"群組「{record.Name}」缺少開頭界線，已略過。");
                            break;
                        }
                        var finished = stack.Pop();
                        opened.Remove(finished);
                        ApplyProperties(finished, record, notes, isGroup: true);
                        Current().Add(finished);

                        var groupPlane = planes.Pop();
                        if (finished.IsVisible) planes.Peek().Accumulate(groupPlane, finished.Opacity);
                        clipBase = new ClipBase(null, SKRectI.Empty, groupPlane, finished.IsVisible);
                        break;

                    default:
                        var layer = BuildRasterLayer(record, header, palette, notes, record.Clipped ? clipBase : null, out var bgra);
                        if (layer == null) break;
                        Current().Add(layer);
                        if (record.Clipped && clipBase is { Visible: false }) layer.IsVisible = false;   // 底層藏著，剪裁上去的也看不到
                        if (layer.IsVisible && bgra != null) planes.Peek().Accumulate(bgra, record.Rect, layer.Opacity);
                        if (!record.Clipped)
                            clipBase = new ClipBase(bgra, bgra == null ? SKRectI.Empty : record.Rect, null, layer.IsVisible);
                        break;
                }
            }

            // 檔案在群組還沒收尾就結束了：把開著的群組原樣掛上去，內容不丟
            while (stack.Count > 0)
            {
                var group = stack.Pop();
                opened.Remove(group);
                group.Name = "群組";
                Current().Add(group);
                notes.Add("有群組缺少結尾記錄，已自動補上。");
            }
        }
        catch
        {
            foreach (var group in opened) group.Dispose();
            throw;
        }
    }

    private static void ApplyProperties(LayerNode node, LayerRecord record, List<string> notes, bool isGroup)
    {
        node.Name = string.IsNullOrEmpty(record.Name) ? (isGroup ? "群組" : "圖層") : record.Name;
        node.IsVisible = !record.Hidden;
        node.Opacity = record.Opacity / 255f * (record.FillOpacity / 255f);
        node.BlendMode = MapBlendMode(record.BlendKey, node.Name, isGroup, notes);
    }

    /// <summary>
    /// 剪裁的底層：點陣圖層給直通 alpha 的 BGRA 與它在文件上的範圍（空範圍＝沒有像素，剪裁後全透明）；
    /// 群組給累加好的畫布 alpha。<paramref name="Visible"/> 是底層本身有沒有顯示。
    /// </summary>
    private sealed record ClipBase(byte[]? Bgra, SKRectI Rect, AlphaPlane? Plane, bool Visible);

    /// <summary>畫布大小的透明度累加器：一層層 src-over 疊上去，就是群組合成後的 alpha。畫布外的不記（剪裁到畫布外看不到）。</summary>
    private sealed class AlphaPlane
    {
        private readonly byte[] _alpha;
        private readonly int _width;
        private readonly int _height;

        public AlphaPlane(int width, int height)
        {
            _width = width;
            _height = height;
            _alpha = new byte[width * height];
        }

        public byte At(int x, int y) =>
            x < 0 || y < 0 || x >= _width || y >= _height ? (byte)0 : _alpha[y * _width + x];

        public void Accumulate(byte[] bgra, SKRectI rect, float opacity)
        {
            var scale = (int)Math.Round(opacity * 255);
            var left = Math.Max(rect.Left, 0);
            var top = Math.Max(rect.Top, 0);
            var right = Math.Min(rect.Right, _width);
            var bottom = Math.Min(rect.Bottom, _height);
            for (var y = top; y < bottom; y++)
            {
                for (var x = left; x < right; x++)
                {
                    var a = bgra[((y - rect.Top) * rect.Width + (x - rect.Left)) * 4 + 3] * scale / 255;
                    Over(y * _width + x, a);
                }
            }
        }

        public void Accumulate(AlphaPlane other, float opacity)
        {
            var scale = (int)Math.Round(opacity * 255);
            for (var i = 0; i < _alpha.Length; i++)
                Over(i, other._alpha[i] * scale / 255);
        }

        private void Over(int index, int a)
        {
            if (a == 0) return;
            _alpha[index] = (byte)(a + _alpha[index] * (255 - a) / 255);
        }
    }

    /// <summary>
    /// 組出一個點陣圖層。<paramref name="clipBase"/> 非 null 時把它的 alpha 乘進來（Photoshop 的剪裁遮色片）；
    /// <paramref name="bgra"/> 回傳這層算好的直通 alpha 像素，供下一層當剪裁的底。
    /// </summary>
    private static RasterLayer? BuildRasterLayer(
        LayerRecord record, Header header, byte[]? palette, List<string> notes, ClipBase? clipBase, out byte[]? bgra)
    {
        bgra = null;
        var layer = new RasterLayer();
        ApplyProperties(layer, record, notes, isGroup: false);
        try
        {
            if (record.Rect.Width <= 0 || record.Rect.Height <= 0)
            {
                if (record.IsAdjustmentOrFill)
                {
                    notes.Add($"「{layer.Name}」是調整或填色圖層，沒有像素可匯入，已略過。");
                    layer.Dispose();
                    return null;
                }
                return layer;   // 真的空白圖層：留一層空的，名字與順序不變
            }

            bgra = ComposeBgra(record, header, palette);
            if (record.HasMask) ApplyMask(bgra, record);
            if (record.Clipped)
            {
                if (clipBase != null) ApplyClip(bgra, record.Rect, clipBase);
                else notes.Add($"「{layer.Name}」設了剪裁但底下沒有圖層，已當成一般圖層。");
            }

            var style = ParseStyle(record, header, layer.Name, notes);
            if (record.TextData != null && BuildText(record, style, layer.Name, notes) is { } text)
            {
                // 文字圖層不變式：有物件就沒有像素。點陣快照只留給剪裁／群組 alpha 當底用
                layer.AddElement(text);
                return layer;
            }

            if (style is { IsEmpty: false }) layer.SetEffects(style.ToLayerEffects());
            if (style is { Unsupported.Count: > 0 })
                notes.Add($"「{layer.Name}」的圖層樣式裡，{string.Join("、", style.Unsupported.Distinct())}沒有對應，已略過。");

            if (!IsFullyTransparent(bgra)) CopyUnpremultiplied(layer, bgra, record.Rect);
            return layer;
        }
        catch
        {
            layer.Dispose();
            throw;
        }
    }

    /// <summary>把各色彩模式的 planar 通道組成 BGRA 直通 alpha。</summary>
    private static byte[] ComposeBgra(LayerRecord record, Header header, byte[]? palette)
    {
        var width = record.Rect.Width;
        var height = record.Rect.Height;
        var count = width * height;
        var bgra = new byte[count * 4];

        byte[]? Channel(int id) => record.Channels.FirstOrDefault(c => c.Id == id)?.Samples;
        var alpha = Channel(-1);

        switch (header.Mode)
        {
            case ColorMode.Rgb:
                var r = Channel(0);
                var g = Channel(1);
                var b = Channel(2);
                for (var i = 0; i < count; i++)
                {
                    bgra[i * 4 + 0] = b?[i] ?? 0;
                    bgra[i * 4 + 1] = g?[i] ?? 0;
                    bgra[i * 4 + 2] = r?[i] ?? 0;
                    bgra[i * 4 + 3] = alpha?[i] ?? 255;
                }
                break;

            case ColorMode.Cmyk:
                // Photoshop 存的 CMYK 是反相的（255 = 沒有油墨），所以直接相乘就是 RGB
                var c = Channel(0);
                var m = Channel(1);
                var y = Channel(2);
                var k = Channel(3);
                for (var i = 0; i < count; i++)
                {
                    var ink = k?[i] ?? 255;
                    bgra[i * 4 + 0] = (byte)((y?[i] ?? 255) * ink / 255);
                    bgra[i * 4 + 1] = (byte)((m?[i] ?? 255) * ink / 255);
                    bgra[i * 4 + 2] = (byte)((c?[i] ?? 255) * ink / 255);
                    bgra[i * 4 + 3] = alpha?[i] ?? 255;
                }
                break;

            case ColorMode.Indexed:
                var index = Channel(0);
                for (var i = 0; i < count; i++)
                {
                    var p = index?[i] ?? 0;
                    bgra[i * 4 + 0] = palette![512 + p];
                    bgra[i * 4 + 1] = palette[256 + p];
                    bgra[i * 4 + 2] = palette[p];
                    bgra[i * 4 + 3] = alpha?[i] ?? 255;
                }
                break;

            default:    // 灰階、雙色調
                var gray = Channel(0);
                for (var i = 0; i < count; i++)
                {
                    var v = gray?[i] ?? 0;
                    bgra[i * 4 + 0] = v;
                    bgra[i * 4 + 1] = v;
                    bgra[i * 4 + 2] = v;
                    bgra[i * 4 + 3] = alpha?[i] ?? 255;
                }
                break;
        }
        return bgra;
    }

    /// <summary>
    /// 使用者遮色片烙進 alpha。我們沒有「圖層遮色片」這個物件，烙進去畫面一樣，
    /// 只是使用者之後不能再單獨編輯遮色片。旗標：bit 1 停用、bit 2 反相、bit 0 位置相對於圖層。
    /// </summary>
    private static void ApplyMask(byte[] bgra, LayerRecord record)
    {
        if ((record.MaskFlags & 0x02) != 0) return;
        var mask = record.Channels.FirstOrDefault(c => c.Id == -2)?.Samples;
        var invert = (record.MaskFlags & 0x04) != 0;
        var maskRect = record.MaskRect;
        if ((record.MaskFlags & 0x01) != 0)
            maskRect.Offset(record.Rect.Left, record.Rect.Top);

        var width = record.Rect.Width;
        var height = record.Rect.Height;
        for (var y = 0; y < height; y++)
        {
            var docY = record.Rect.Top + y;
            for (var x = 0; x < width; x++)
            {
                var docX = record.Rect.Left + x;
                int coverage = record.MaskDefault;
                if (mask != null && maskRect.Contains(docX, docY))
                    coverage = mask[(docY - maskRect.Top) * maskRect.Width + (docX - maskRect.Left)];
                if (invert) coverage = 255 - coverage;

                var i = (y * width + x) * 4 + 3;
                bgra[i] = (byte)((bgra[i] * coverage + 127) / 255);
            }
        }
    }

    private static PsdLayerStyle? ParseStyle(LayerRecord record, Header header, string name, List<string> notes)
    {
        if (record.StyleData == null)
        {
            if (record.HasLegacyStyle) notes.Add($"「{name}」用的是舊版圖層樣式，沒有匯入。");
            return null;
        }
        try
        {
            return PsdLayerStyle.Parse(record.StyleData, header.GlobalAngle);
        }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
        {
            notes.Add($"「{name}」的圖層樣式無法解析，已略過。");
            return null;
        }
    }

    /// <summary>解出可編輯文字並套上樣式；解不出來提示原因並回 null（呼叫端退回點陣）。</summary>
    private static TextElement? BuildText(LayerRecord record, PsdLayerStyle? style, string name, List<string> notes)
    {
        TextElement? text;
        string? failure;
        try
        {
            text = PsdTextLayer.TryBuild(record.TextData!, notes, out failure);
        }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException or FormatException)
        {
            text = null;
            failure = "排版資料無法解析";
        }

        if (text == null)
        {
            notes.Add($"文字圖層「{name}」已轉成像素（{failure}）。");
            return null;
        }

        if (style != null)
        {
            text = style.ApplyTo(text);
            if (style.Unsupported.Count > 0)
                notes.Add($"文字「{name}」的圖層樣式裡，{string.Join("、", style.Unsupported.Distinct())}沒有對應，已略過。");
        }
        return text;
    }

    /// <summary>剪裁遮色片：只留底層有像素的地方。底層範圍外一律透明。</summary>
    private static void ApplyClip(byte[] bgra, SKRectI rect, ClipBase clipBase)
    {
        var width = rect.Width;
        for (var y = 0; y < rect.Height; y++)
        {
            var docY = rect.Top + y;
            for (var x = 0; x < width; x++)
            {
                var docX = rect.Left + x;
                var i = (y * width + x) * 4 + 3;
                int baseAlpha;
                if (clipBase.Plane != null)
                    baseAlpha = clipBase.Plane.At(docX, docY);
                else if (clipBase.Bgra != null && clipBase.Rect.Contains(docX, docY))
                    baseAlpha = clipBase.Bgra[((docY - clipBase.Rect.Top) * clipBase.Rect.Width + (docX - clipBase.Rect.Left)) * 4 + 3];
                else
                    baseAlpha = 0;
                bgra[i] = (byte)((bgra[i] * baseAlpha + 127) / 255);
            }
        }
    }

    /// <summary>Photoshop 的直通 alpha 交給 Skia 轉成我們 tile 用的預乘，寫到圖層範圍的左上角。</summary>
    private static unsafe void CopyUnpremultiplied(RasterLayer layer, byte[] bgra, SKRectI rect)
    {
        var sourceInfo = new SKImageInfo(rect.Width, rect.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var premultiplied = new SKBitmap(sourceInfo.WithAlphaType(SKAlphaType.Premul));
        using var destination = premultiplied.PeekPixels();

        fixed (byte* scan0 = bgra)
        {
            using var source = new SKPixmap(sourceInfo, (IntPtr)scan0, rect.Width * 4);
            if (!source.ReadPixels(destination))
                throw new InvalidDataException(".psd 像素轉換失敗（直通 alpha → 預乘）。");
        }

        layer.Surface.CopyFrom(destination, new SKPointI(rect.Left, rect.Top));
    }

    private static bool IsFullyTransparent(byte[] bgra)
    {
        for (var i = 3; i < bgra.Length; i += 4)
            if (bgra[i] != 0) return false;
        return true;
    }

    // ---- 混合模式 ----

    private static BlendMode MapBlendMode(string key, string name, bool isGroup, List<string> notes)
    {
        switch (key)
        {
            case "norm": return BlendMode.Normal;
            case "pass": return BlendMode.Normal;   // 群組直通：我們的群組一律先合成再疊，多數情況看起來一樣
            case "mul ": return BlendMode.Multiply;
            case "scrn": return BlendMode.Screen;
            case "over": return BlendMode.Overlay;
            case "dark": return BlendMode.Darken;
            case "lite": return BlendMode.Lighten;
            case "div ": return BlendMode.ColorDodge;
            case "idiv": return BlendMode.ColorBurn;
            case "hLit": return BlendMode.HardLight;
            case "sLit": return BlendMode.SoftLight;
            case "diff": return BlendMode.Difference;
            case "smud": return BlendMode.Exclusion;
            case "hue ": return BlendMode.Hue;
            case "sat ": return BlendMode.Saturation;
            case "colr": return BlendMode.Color;
            case "lum ": return BlendMode.Luminosity;
            case "lddg": return BlendMode.Additive;
        }

        // Skia 沒有的算式，挑最接近的頂著並提示
        var (fallback, label) = key switch
        {
            "diss" => (BlendMode.Normal, "溶解"),
            "lbrn" => (BlendMode.Multiply, "線性加深"),
            "dkCl" => (BlendMode.Darken, "顏色變暗"),
            "lgCl" => (BlendMode.Lighten, "顏色變亮"),
            "vLit" => (BlendMode.HardLight, "強烈光源"),
            "lLit" => (BlendMode.HardLight, "線性光源"),
            "pLit" => (BlendMode.HardLight, "小光源"),
            "hMix" => (BlendMode.HardLight, "實色疊印混合"),
            "fsub" => (BlendMode.Difference, "減去"),
            "fdiv" => (BlendMode.ColorDodge, "分割"),
            _ => (BlendMode.Normal, key.Trim()),
        };
        notes.Add($"{(isGroup ? "群組" : "圖層")}「{name}」的混合模式「{label}」沒有對應，已改為{Describe(fallback)}。");
        return fallback;
    }

    private static string Describe(BlendMode mode) => mode switch
    {
        BlendMode.Normal => "一般",
        BlendMode.Multiply => "色彩增值",
        BlendMode.Darken => "變暗",
        BlendMode.Lighten => "變亮",
        BlendMode.HardLight => "實光",
        BlendMode.Difference => "差異化",
        BlendMode.ColorDodge => "加亮顏色",
        _ => mode.ToString(),
    };

    // ---- 合成影像（沒有圖層時的後備） ----

    /// <summary>
    /// 平面化存檔的 PSD 只有合成影像：所有通道一段接一段（RLE 時所有列長度先集中放在前面）。
    /// 色彩模式本身的通道之後多出來的第一個當透明度。
    /// </summary>
    private static RasterLayer ReadMergedImage(Reader reader, Header header, byte[]? palette)
    {
        var width = header.Width;
        var height = header.Height;
        var bytesPerSample = header.Depth / 8;
        var rowBytes = width * bytesPerSample;
        var planeBytes = (long)rowBytes * height;
        if (planeBytes * header.Channels > MaxPixelBytes)
            throw new InvalidDataException(".psd 合成影像太大，無法載入。");

        var compression = reader.UInt16();
        var planes = new byte[header.Channels][];
        for (var c = 0; c < planes.Length; c++) planes[c] = new byte[planeBytes];

        switch (compression)
        {
            case 0:
                foreach (var plane in planes) reader.Fill(plane);
                break;
            case 1:
                var rowLengths = new int[header.Channels * height];
                for (var i = 0; i < rowLengths.Length; i++)
                    rowLengths[i] = header.IsPsb ? checked((int)reader.UInt32()) : reader.UInt16();
                for (var c = 0; c < planes.Length; c++)
                {
                    for (var y = 0; y < height; y++)
                    {
                        var packed = reader.Bytes(rowLengths[c * height + y]);
                        if (!UnpackBits(packed, planes[c].AsSpan(y * rowBytes, rowBytes)))
                            throw new InvalidDataException(".psd 合成影像的 RLE 資料長度不足。");
                    }
                }
                break;
            case 2:
            case 3:
                var all = new byte[planeBytes * header.Channels];
                Inflate(reader.Bytes(checked((int)(reader.Remaining))), all, "合成影像");
                for (var c = 0; c < planes.Length; c++)
                {
                    Array.Copy(all, c * planeBytes, planes[c], 0, planeBytes);
                    if (compression == 3) UndoPrediction(planes[c], rowBytes, height, bytesPerSample);
                }
                break;
            default:
                throw new InvalidDataException($".psd 合成影像使用了無法辨識的壓縮方式（{compression}）。");
        }

        var record = new LayerRecord { Rect = new SKRectI(0, 0, width, height), Name = "背景" };
        for (var c = 0; c < planes.Length; c++)
        {
            var id = c < header.ColorChannels ? c : (c == header.ColorChannels ? -1 : int.MinValue);
            if (id == int.MinValue) continue;   // 特別色與額外 alpha 通道用不到
            record.Channels.Add(new ChannelRecord
            {
                Id = id,
                Samples = bytesPerSample == 1 ? planes[c] : Downconvert16(planes[c]),
            });
        }

        var layer = new RasterLayer { Name = "背景" };
        try
        {
            var bgra = ComposeBgra(record, header, palette);
            if (!IsFullyTransparent(bgra)) CopyUnpremultiplied(layer, bgra, record.Rect);
            return layer;
        }
        catch
        {
            layer.Dispose();
            throw;
        }
    }

    // ---- 大端序讀取 ----

    /// <summary>可搜尋資料流上的大端序讀取器；讀不到就丟提前結束，不會安靜回傳 0。</summary>
    private sealed class Reader
    {
        private readonly Stream _stream;
        private readonly byte[] _scratch = new byte[8];

        public Reader(Stream stream) => _stream = stream;

        public long Position
        {
            get => _stream.Position;
            set
            {
                if (value < 0 || value > _stream.Length)
                    throw new InvalidDataException(".psd 檔案結構指向檔案之外，可能已損毀。");
                _stream.Position = value;
            }
        }

        public long Remaining => _stream.Length - _stream.Position;

        public byte Byte()
        {
            var b = _stream.ReadByte();
            if (b < 0) throw new EndOfStreamException(".psd 檔案提前結束，可能已損毀。");
            return (byte)b;
        }

        public ushort UInt16() => BinaryPrimitives.ReadUInt16BigEndian(Read(2));
        public short Int16() => BinaryPrimitives.ReadInt16BigEndian(Read(2));
        public uint UInt32() => BinaryPrimitives.ReadUInt32BigEndian(Read(4));
        public int Int32() => BinaryPrimitives.ReadInt32BigEndian(Read(4));
        public long Int64() => BinaryPrimitives.ReadInt64BigEndian(Read(8));

        /// <summary>PSD 用 4 位元組、PSB 用 8 位元組的「長度」欄位。</summary>
        public long Length(bool isPsb)
        {
            var length = isPsb ? Int64() : UInt32();
            if (length < 0 || length > Remaining)
                throw new InvalidDataException($".psd 區段長度（{length}）超出檔案大小。");
            return length;
        }

        public byte[] Bytes(long count)
        {
            if (count < 0 || count > Remaining)
                throw new InvalidDataException($".psd 要讀的資料（{count} 位元組）超出檔案大小。");
            var buffer = new byte[count];
            Fill(buffer);
            return buffer;
        }

        public void Fill(byte[] buffer)
        {
            try
            {
                _stream.ReadExactly(buffer);
            }
            catch (EndOfStreamException)
            {
                throw new EndOfStreamException(".psd 檔案提前結束，可能已損毀。");
            }
        }

        public void Skip(long count)
        {
            if (count < 0 || count > Remaining)
                throw new InvalidDataException($".psd 要略過的區段（{count} 位元組）超出檔案大小。");
            _stream.Seek(count, SeekOrigin.Current);
        }

        private ReadOnlySpan<byte> Read(int count)
        {
            var span = _scratch.AsSpan(0, count);
            try
            {
                _stream.ReadExactly(span);
            }
            catch (EndOfStreamException)
            {
                throw new EndOfStreamException(".psd 檔案提前結束，可能已損毀。");
            }
            return span;
        }
    }
}
