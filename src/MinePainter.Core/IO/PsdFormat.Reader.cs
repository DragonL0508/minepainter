using System.Buffers.Binary;

namespace MinePainter.Core.IO;

public static partial class PsdFormat
{
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
