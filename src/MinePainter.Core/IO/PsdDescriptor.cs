using System.Buffers.Binary;
using System.Text;
using SkiaSharp;

namespace MinePainter.Core.IO;

/// <summary>位元組陣列上的大端序讀取器，給 Photoshop 的 Action Descriptor 與文字圖層資料用。</summary>
internal sealed class PsdByteReader
{
    private readonly byte[] _data;

    public PsdByteReader(byte[] data, int position = 0)
    {
        _data = data;
        Position = position;
    }

    public int Position { get; set; }
    public int Remaining => _data.Length - Position;

    public byte Byte() => _data[Take(1)];
    public ushort UInt16() => BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(Take(2)));
    public int Int32() => BinaryPrimitives.ReadInt32BigEndian(_data.AsSpan(Take(4)));
    public uint UInt32() => BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(Take(4)));
    public long Int64() => BinaryPrimitives.ReadInt64BigEndian(_data.AsSpan(Take(8)));
    public double Double() => BinaryPrimitives.ReadDoubleBigEndian(_data.AsSpan(Take(8)));

    public byte[] Bytes(int count) => _data.AsSpan(Take(count), count).ToArray();
    public string Ascii(int count) => Encoding.ASCII.GetString(_data, Take(count), count);

    /// <summary>4 位元組字元數 + UTF-16BE（含結尾的 NUL）。</summary>
    public string UnicodeString()
    {
        var chars = checked((int)UInt32());
        var bytes = checked(chars * 2);
        var s = Encoding.BigEndianUnicode.GetString(_data, Take(bytes), bytes);
        return s.TrimEnd('\0');
    }

    /// <summary>描述子的鍵／類別 ID：長度 0 代表固定 4 字元，否則是指定長度的 ASCII。</summary>
    public string Key()
    {
        var length = checked((int)UInt32());
        return Ascii(length == 0 ? 4 : length);
    }

    private int Take(int count)
    {
        if (count < 0 || Position + count > _data.Length)
            throw new InvalidDataException(".psd 附加資料提前結束，可能已損毀。");
        var start = Position;
        Position += count;
        return start;
    }
}

/// <summary>描述子裡的列舉值：型別 ID 與值（例如 BlnM／multiply）。</summary>
internal readonly record struct PsdEnum(string Type, string Value);

/// <summary>描述子裡帶單位的數值（#Pxl、#Prc、#Ang、#Pnt…）。</summary>
internal readonly record struct PsdUnit(string Unit, double Value);

/// <summary>
/// Photoshop 的 Action Descriptor（圖層樣式 lfx2、文字 TySh、彎曲參數都用它）。
/// 鍵值樹：值是 double／PsdUnit／int／long／bool／string／PsdEnum／List／PsdDescriptor／byte[]。
/// 只讀不寫；不認得的參考型別會照規格跳過，避免整份放棄。
/// </summary>
internal sealed class PsdDescriptor
{
    private readonly Dictionary<string, object?> _items = new(StringComparer.Ordinal);

    public string ClassId { get; private init; } = "";
    public IReadOnlyDictionary<string, object?> Items => _items;

    public static PsdDescriptor Read(PsdByteReader reader)
    {
        reader.UnicodeString();     // 類別名稱（顯示用）
        var descriptor = new PsdDescriptor { ClassId = reader.Key() };
        var count = checked((int)reader.UInt32());
        for (var i = 0; i < count; i++)
        {
            var key = reader.Key();
            descriptor._items[key] = ReadItem(reader);
        }
        return descriptor;
    }

    public object? this[string key] => _items.GetValueOrDefault(key);

    public PsdDescriptor? Child(string key) => this[key] as PsdDescriptor;
    public List<object?>? List(string key) => this[key] as List<object?>;
    public bool? Bool(string key) => this[key] as bool?;
    public string? Text(string key) => this[key] as string;
    public string? Enum(string key) => (this[key] as PsdEnum?)?.Value;
    public byte[]? Raw(string key) => this[key] as byte[];

    /// <summary>數值不管是 doub、UntF、long 都拿成 double。</summary>
    public double? Number(string key) => this[key] switch
    {
        double d => d,
        PsdUnit u => u.Value,
        int i => i,
        long l => l,
        _ => null,
    };

    /// <summary>顏色子描述子：RGB（0..255 的 double）或灰階；其他色彩空間回 null。</summary>
    public SKColor? Color(string key)
    {
        var c = Child(key);
        if (c == null) return null;
        if (c.Number("Rd  ") is { } r && c.Number("Grn ") is { } g && c.Number("Bl  ") is { } b)
            return new SKColor(Clamp(r), Clamp(g), Clamp(b));
        if (c.Number("Gry ") is { } gray)
        {
            var v = Clamp(255 - gray * 2.55);   // 灰階存的是 0..100 的墨量
            return new SKColor(v, v, v);
        }
        return null;
    }

    private static byte Clamp(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);

    private static object? ReadItem(PsdByteReader reader)
    {
        var type = reader.Ascii(4);
        switch (type)
        {
            case "Objc":
            case "GlbO":
                return Read(reader);
            case "VlLs":
                var count = checked((int)reader.UInt32());
                var list = new List<object?>(count);
                for (var i = 0; i < count; i++) list.Add(ReadItem(reader));
                return list;
            case "doub":
                return reader.Double();
            case "UntF":
                return new PsdUnit(reader.Ascii(4), reader.Double());
            case "UnFl":
                var unit = reader.Ascii(4);
                var n = checked((int)reader.UInt32());
                var values = new List<object?>(n);
                for (var i = 0; i < n; i++) values.Add(new PsdUnit(unit, reader.Double()));
                return values;
            case "TEXT":
                return reader.UnicodeString();
            case "enum":
                return new PsdEnum(reader.Key(), reader.Key());
            case "long":
                return reader.Int32();
            case "comp":
                return reader.Int64();
            case "bool":
                return reader.Byte() != 0;
            case "type":
            case "GlbC":
                reader.UnicodeString();
                return reader.Key();
            case "alis":
            case "tdta":
                return reader.Bytes(checked((int)reader.UInt32()));
            case "obj ":
                SkipReference(reader);
                return null;
            case "ObAr":
                reader.UInt32();
                reader.UnicodeString();
                reader.Key();
                var items = checked((int)reader.UInt32());
                var array = new List<object?>(items);
                for (var i = 0; i < items; i++)
                {
                    reader.Key();
                    array.Add(ReadItem(reader));
                }
                return array;
            case "Pth ":
                return reader.Bytes(checked((int)reader.UInt32()));
            default:
                throw new InvalidDataException($".psd 描述子含無法辨識的型別「{type}」。");
        }
    }

    private static void SkipReference(PsdByteReader reader)
    {
        var count = checked((int)reader.UInt32());
        for (var i = 0; i < count; i++)
        {
            var form = reader.Ascii(4);
            reader.UnicodeString();
            reader.Key();
            switch (form)
            {
                case "prop": reader.Key(); break;
                case "Clss": break;
                case "Enmr": reader.Key(); reader.Key(); break;
                case "rele": reader.Int32(); break;
                case "Idnt": reader.UInt32(); break;
                case "indx": reader.UInt32(); break;
                case "name": reader.UnicodeString(); break;
                default: throw new InvalidDataException($".psd 描述子含無法辨識的參考「{form}」。");
            }
        }
    }
}
