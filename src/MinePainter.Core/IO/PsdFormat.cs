using System.Text;
using MinePainter.Core.Documents;
using MinePainter.Core.Effects;
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
public static partial class PsdFormat
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
        var (globalAngle, dpi, icc) = ReadImageResources(reader);
        using var colorSpace = ColorSpaceFromIcc(icc);
        header = header with { GlobalAngle = globalAngle, Dpi = dpi, ColorSpace = colorSpace };

        var records = ReadLayerSection(reader, header, notes);

        var document = new Document(header.Width, header.Height) { Dpi = header.Dpi };
        try
        {
            lock (document.SyncRoot)
            {
                if (records.Count > 0)
                    LayerBuilder.BuildLayerTree(document, records, header, palette, notes);
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

        /// <summary>內嵌 ICC（影像資源 1039）解析出的色彩空間；null＝沒有、就是 sRGB、或 Skia 不認得（CMYK／灰階）。</summary>
        public SKColorSpace? ColorSpace { get; init; }

        /// <summary>解析度（影像資源 1005；Photoshop 預設 72）。</summary>
        public float Dpi { get; init; } = 72f;

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
    /// 只要整體光源角度（1037）、解析度（1005：hRes 是 16.16 定點數，單位 1 = 每英寸、2 = 每公分）
    /// 與 ICC 設定檔（1039：Adobe RGB／ProPhoto 的檔案要轉成 sRGB，不然整張偏淡），其餘（縮圖）匯入用不到。
    /// </summary>
    private static (int GlobalAngle, float Dpi, byte[]? Icc) ReadImageResources(Reader reader)
    {
        var length = reader.UInt32();
        var end = reader.Position + length;
        var globalAngle = 120;
        var dpi = 72f;
        byte[]? icc = null;
        while (reader.Position + 12 <= end)
        {
            if (!reader.Bytes(4).AsSpan().SequenceEqual("8BIM"u8)) break;
            var id = reader.UInt16();
            var nameLength = reader.Byte();
            reader.Skip(nameLength + (nameLength + 1) % 2);
            var size = reader.UInt32();
            var dataStart = reader.Position;
            if (id == 1037 && size >= 4) globalAngle = reader.Int32();
            if (id == 1039 && size > 0) icc = reader.Bytes((int)size);
            if (id == 1005 && size >= 6)
            {
                var fixedRes = reader.UInt32() / 65536f;
                var unit = reader.UInt16();
                if (fixedRes > 0) dpi = unit == 2 ? fixedRes * PhysicalUnits.CentimetersPerInch : fixedRes;
            }
            reader.Position = dataStart + size + size % 2;
        }
        reader.Position = end;
        return (globalAngle, dpi, icc);
    }

    /// <summary>
    /// ICC → Skia 色彩空間；sRGB 或解析不了（CMYK、灰階、壞檔）都回 null＝不轉。
    /// 先自己解析 profile 再建色彩空間：<c>SKColorSpace.CreateIcc(byte[])</c> 解析失敗時
    /// 是丟 ArgumentNullException 而不是回 null，直接呼叫會讓一份壞掉的設定檔炸掉整個匯入。
    /// </summary>
    private static SKColorSpace? ColorSpaceFromIcc(byte[]? icc)
    {
        if (icc == null || icc.Length == 0) return null;
        var profile = SKColorSpaceIccProfile.Create(icc);
        if (profile == null) return null;
        using (profile)
        {
            var space = SKColorSpace.CreateIcc(profile);
            if (space == null) return null;
            if (space.IsSrgb)
            {
                space.Dispose();
                return null;
            }
            return space;
        }
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
        public int RestrictedChannels;
        public bool Clipped;
        public bool Hidden;
        public string Name = "";
        public bool HasMask;
        public SKRectI MaskRect;
        public byte MaskDefault = 255;
        public byte MaskFlags;
        public float MaskDensity = 1;
        public float MaskFeather;
        public int SectionType;     // lsct：0 一般、1／2 群組本體、3 群組底部界線
        public bool IsAdjustmentOrFill;
        public readonly Dictionary<string, byte[]> ParameterBlocks = [];   // 調整／填色圖層的參數區塊（key → 原始位元組）
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
                channel.Samples = ChannelDecoder.ReadChannelData(reader, channel.Length, rect.Width, rect.Height, header, record.Name);
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
            if ((record.MaskFlags & 16) != 0 && reader.Position < maskStart + maskLength)
            {
                var parameters = reader.Byte();
                if ((parameters & 1) != 0) record.MaskDensity = reader.Byte() / 255f;
                if ((parameters & 2) != 0) record.MaskFeather = (float)BitConverter.Int64BitsToDouble(reader.Int64());
                if ((parameters & 4) != 0) reader.Byte();
                if ((parameters & 8) != 0) reader.Int64();
                if (reader.Position > maskStart + maskLength) throw new InvalidDataException("Truncated mask parameters.");
            }
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
                if (length >= 12 && ReadBlockSignature(reader))
                    record.BlendKey = Encoding.ASCII.GetString(reader.Bytes(4));
                break;
            case "iOpa":
                if (length >= 1) record.FillOpacity = reader.Byte();
                break;
            case "brst":
                for (var i = 0L; i + 4 <= length; i += 4)
                {
                    var channel = reader.Int32();
                    if (channel is >= 0 and <= 2) record.RestrictedChannels |= 1 << channel;
                }
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
                if (AdjustmentAndFillKeys.Contains(key) || PsdAdjustmentLayer.Keys.Contains(key))
                {
                    record.IsAdjustmentOrFill = true;
                    record.ParameterBlocks[key] = reader.Bytes(length);
                }
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
                        if (!ChannelDecoder.UnpackBits(packed, planes[c].AsSpan(y * rowBytes, rowBytes)))
                            throw new InvalidDataException(".psd 合成影像的 RLE 資料長度不足。");
                    }
                }
                break;
            case 2:
            case 3:
                var all = new byte[planeBytes * header.Channels];
                ChannelDecoder.Inflate(reader.Bytes(checked((int)(reader.Remaining))), all, "合成影像");
                for (var c = 0; c < planes.Length; c++)
                {
                    Array.Copy(all, c * planeBytes, planes[c], 0, planeBytes);
                    if (compression == 3) ChannelDecoder.UndoPrediction(planes[c], rowBytes, height, bytesPerSample);
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
                Samples = bytesPerSample == 1 ? planes[c] : Downconvert16(planes[c], width),
            });
        }

        var layer = new RasterLayer { Name = "背景" };
        try
        {
            var bgra = LayerBuilder.ComposeBgra(record, header, palette);
            if (!LayerBuilder.IsFullyTransparent(bgra)) LayerBuilder.CopyUnpremultiplied(layer, bgra, record.Rect, header.ColorSpace);
            return layer;
        }
        catch
        {
            layer.Dispose();
            throw;
        }
    }

    /// <summary>將 PSD 16 位元通道以有序抖色轉成 8 位元樣本。</summary>
    internal static byte[] Downconvert16(byte[] raw, int width) => ChannelDecoder.Downconvert16(raw, width);
}
