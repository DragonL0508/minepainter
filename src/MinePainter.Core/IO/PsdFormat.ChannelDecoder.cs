using System.Buffers.Binary;
using System.IO.Compression;

namespace MinePainter.Core.IO;

public static partial class PsdFormat
{
    /// <summary>通道壓縮解碼與位元深度轉換；不建立文件或圖層。</summary>
    private static class ChannelDecoder
    {
        // ---- 通道解碼 ----

        /// <summary>讀一個通道並轉成 8 位元樣本（列主序，緊密排列）。範圍為空時回傳 null。</summary>
        public static byte[]? ReadChannelData(Reader reader, long length, int width, int height, Header header, string layerName)
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

            return bytesPerSample == 1 ? raw : Downconvert16(raw, width);
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

        public static bool UnpackBits(ReadOnlySpan<byte> source, Span<byte> destination)
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

        public static void Inflate(byte[] compressed, byte[] destination, string layerName)
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
        public static void UndoPrediction(byte[] raw, int rowBytes, int height, int bytesPerSample)
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

        /// <summary>8×8 Bayer 矩陣（0..63）：16→8 位元的有序抖色用。</summary>
        private static readonly byte[] Bayer8 =
        [
            0, 32, 8, 40, 2, 34, 10, 42,
            48, 16, 56, 24, 50, 18, 58, 26,
            12, 44, 4, 36, 14, 46, 6, 38,
            60, 28, 52, 20, 62, 30, 54, 22,
            3, 35, 11, 43, 1, 33, 9, 41,
            51, 19, 59, 27, 49, 17, 57, 25,
            15, 47, 7, 39, 13, 45, 5, 37,
            63, 31, 55, 23, 61, 29, 53, 21,
        ];

        /// <summary>
        /// 16 位元樣本降成 8 位元：不是四捨五入而是有序抖色（Photoshop 轉 8 位元預設也開抖色）——
        /// 平滑漸層直接量化會出一條條色帶，抖色把餘數攤成 ±1 的細紋，平坦區看不出來、色帶消失。
        /// <paramref name="width"/> 是平面的列寬，抖色矩陣要按 x/y 取。
        /// </summary>
        public static byte[] Downconvert16(byte[] raw, int width)
        {
            var result = new byte[raw.Length / 2];
            if (width <= 0) width = result.Length;
            for (var i = 0; i < result.Length; i++)
            {
                var value = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(i * 2));
                var x = i % width;
                var y = i / width;
                // value/257 的整數部分是底，餘數（0..256）與門檻（0..255，平均 128）比：超過就進位
                var scaled = value * 255;          // 0..16711425
                var floor = scaled / 65535;        // 0..255
                var remainder = scaled - floor * 65535; // 0..65534
                var threshold = (Bayer8[(y & 7) * 8 + (x & 7)] * 2 + 1) * 65535 / 128; // 1/128..127/128，平均 1/2
                result[i] = (byte)Math.Min(255, floor + (remainder >= threshold ? 1 : 0));
            }
            return result;
        }
    }
}
