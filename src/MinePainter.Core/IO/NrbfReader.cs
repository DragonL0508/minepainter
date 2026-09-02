using System.Globalization;
using System.Text;

namespace MinePainter.Core.IO;

/// <summary>
/// MS-NRBF（.NET BinaryFormatter 的線上格式）最小讀取器，只為了讀 paint.net 的 .pdn。
///
/// 兩個刻意的性質：
/// • 讀到 MessageEnd 就停，而且一個位元組都不預讀 —— .pdn 把圖層像素接在序列化資料「後面」，
///   呼叫端必須能從同一個 stream 繼續往下讀（BinaryReader 對這些型別都是精確長度讀取）。
/// • 只還原成 NrbfObject／陣列／基本型別，不做反射、不建構任何檔案裡指名的型別，
///   所以讀到惡意 .pdn 也不會變成 BinaryFormatter 那種 RCE 面。
/// </summary>
internal sealed class NrbfReader
{
    private readonly BinaryReader _reader;
    private readonly Dictionary<int, object?> _objects = new();
    private readonly Dictionary<int, ClassMetadata> _metadata = new();
    private readonly List<NrbfObject> _order = new();
    private readonly List<object?[]> _objectArrays = new();
    private int _rootId;

    private NrbfReader(Stream stream) =>
        _reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

    /// <summary>讀完一個序列化訊息；回傳後 stream 正好停在 MessageEnd 之後。</summary>
    public static NrbfPayload Read(Stream stream)
    {
        var reader = new NrbfReader(stream);
        reader.ReadMessage();
        return reader.Build();
    }

    // ---- 訊息 ----

    private void ReadMessage()
    {
        if ((RecordType)_reader.ReadByte() != RecordType.SerializedStreamHeader)
            throw new InvalidDataException("NRBF：缺少序列化標頭。");

        _rootId = _reader.ReadInt32();
        _reader.ReadInt32();                    // headerId
        var major = _reader.ReadInt32();
        _reader.ReadInt32();                    // minorVersion
        if (major != 1)
            throw new InvalidDataException($"NRBF：不支援的版本 {major}。");

        while (true)
        {
            var type = (RecordType)_reader.ReadByte();
            if (type == RecordType.MessageEnd) break;
            ReadRecord(type);
        }
    }

    private NrbfPayload Build()
    {
        // 參照是後綁定的（物件圖有環，例如 LayerList.parent 指回 Document），
        // 所以先整包讀完，再把 NrbfRef 換成真正的物件。
        foreach (var obj in _order)
        {
            foreach (var name in obj.Members.Keys.ToList())
                obj.Members[name] = Resolve(obj.Members[name]);
        }
        foreach (var array in _objectArrays)
        {
            for (var i = 0; i < array.Length; i++)
                array[i] = Resolve(array[i]);
        }

        if (Resolve(new NrbfRef(_rootId)) is not NrbfObject root)
            throw new InvalidDataException("NRBF：根物件不是類別實例。");

        return new NrbfPayload(root, _order);
    }

    private object? Resolve(object? value)
    {
        if (value is not NrbfRef reference) return value;
        if (!_objects.TryGetValue(reference.Id, out var target))
            throw new InvalidDataException($"NRBF：找不到參照的物件 #{reference.Id}。");
        return target;
    }

    // ---- 記錄 ----

    private object? ReadRecord(RecordType type)
    {
        switch (type)
        {
            case RecordType.ClassWithId:
            {
                var id = _reader.ReadInt32();
                var metadataId = _reader.ReadInt32();
                if (!_metadata.TryGetValue(metadataId, out var meta))
                    throw new InvalidDataException($"NRBF：找不到類別中繼資料 #{metadataId}。");
                return ReadClassMembers(id, meta);
            }

            case RecordType.SystemClassWithMembersAndTypes:
            {
                var meta = ReadClassMetadata(hasLibraryId: false);
                return ReadClassMembers(meta.ObjectId, meta);
            }

            case RecordType.ClassWithMembersAndTypes:
            {
                var meta = ReadClassMetadata(hasLibraryId: true);
                return ReadClassMembers(meta.ObjectId, meta);
            }

            case RecordType.BinaryObjectString:
            {
                var id = _reader.ReadInt32();
                var text = _reader.ReadString();
                _objects[id] = text;
                return text;
            }

            case RecordType.BinaryArray:
                return ReadBinaryArray();

            case RecordType.ArraySinglePrimitive:
            {
                var id = _reader.ReadInt32();
                var length = ReadArrayLength();
                var primitive = (PrimitiveType)_reader.ReadByte();
                var array = ReadPrimitiveArray(length, primitive);
                _objects[id] = array;
                return array;
            }

            case RecordType.ArraySingleObject:
            {
                var id = _reader.ReadInt32();
                var length = ReadArrayLength();
                return StoreObjectArray(id, ReadElements(length, BinaryType.Object, null));
            }

            case RecordType.ArraySingleString:
            {
                var id = _reader.ReadInt32();
                var length = ReadArrayLength();
                return StoreObjectArray(id, ReadElements(length, BinaryType.String, null));
            }

            case RecordType.MemberPrimitiveTyped:
                return ReadPrimitive((PrimitiveType)_reader.ReadByte());

            case RecordType.MemberReference:
                return new NrbfRef(_reader.ReadInt32());

            case RecordType.ObjectNull:
                return null;

            case RecordType.ObjectNullMultiple256:
                return new NullRun(_reader.ReadByte());

            case RecordType.ObjectNullMultiple:
                return new NullRun(_reader.ReadInt32());

            case RecordType.BinaryLibrary:
                _reader.ReadInt32();
                _reader.ReadString();
                return NoValue;

            case RecordType.SystemClassWithMembers:
            case RecordType.ClassWithMembers:
                // 無型別資訊的記錄只有 FormatterTypeStyle.TypesWhenNeeded 才會出現，
                // 沒有 MemberTypeInfo 就無從得知成員該怎麼讀。paint.net 不會寫這種。
                throw new InvalidDataException($"NRBF：不支援缺少型別資訊的記錄（{type}）。");

            default:
                throw new InvalidDataException($"NRBF：未知的記錄類型 {(byte)type}。");
        }
    }

    /// <summary>成員／陣列元素位置上的記錄；BinaryLibrary 只是宣告，跳過再往下讀。</summary>
    private object? ReadNestedRecord()
    {
        while (true)
        {
            var value = ReadRecord((RecordType)_reader.ReadByte());
            if (!ReferenceEquals(value, NoValue)) return value;
        }
    }

    private ClassMetadata ReadClassMetadata(bool hasLibraryId)
    {
        var objectId = _reader.ReadInt32();
        var typeName = _reader.ReadString();
        var memberCount = _reader.ReadInt32();
        if (memberCount < 0 || memberCount > MaxMembers)
            throw new InvalidDataException($"NRBF：成員數量不合理（{memberCount}）。");

        var names = new string[memberCount];
        for (var i = 0; i < memberCount; i++) names[i] = _reader.ReadString();

        var types = new BinaryType[memberCount];
        for (var i = 0; i < memberCount; i++) types[i] = (BinaryType)_reader.ReadByte();

        // AdditionalInfo 是「先全部型別、再全部附加資訊」，不是逐一交錯。
        var info = new object?[memberCount];
        for (var i = 0; i < memberCount; i++) info[i] = ReadAdditionalInfo(types[i]);

        if (hasLibraryId) _reader.ReadInt32();

        var meta = new ClassMetadata(objectId, typeName, names, types, info);
        _metadata[objectId] = meta;
        return meta;
    }

    private object? ReadAdditionalInfo(BinaryType type) => type switch
    {
        BinaryType.Primitive or BinaryType.PrimitiveArray => (PrimitiveType)_reader.ReadByte(),
        BinaryType.SystemClass => _reader.ReadString(),
        BinaryType.Class => ReadClassTypeInfo(),
        _ => null,
    };

    private object ReadClassTypeInfo()
    {
        var name = _reader.ReadString();
        _reader.ReadInt32();    // libraryId
        return name;
    }

    private NrbfObject ReadClassMembers(int objectId, ClassMetadata meta)
    {
        var obj = new NrbfObject(meta.TypeName);
        // 先登記再讀成員，否則自我參照（LayerList.parent → Document）會找不到目標。
        _objects[objectId] = obj;
        _order.Add(obj);

        for (var i = 0; i < meta.MemberNames.Length; i++)
            obj.Members[meta.MemberNames[i]] = ReadMemberValue(meta.MemberTypes[i], meta.AdditionalInfo[i]);

        return obj;
    }

    private object? ReadMemberValue(BinaryType type, object? info) => type == BinaryType.Primitive
        ? ReadPrimitive((PrimitiveType)info!)
        : ReadNestedRecord();

    // ---- 陣列 ----

    private object ReadBinaryArray()
    {
        var id = _reader.ReadInt32();
        var arrayType = (BinaryArrayType)_reader.ReadByte();
        var rank = _reader.ReadInt32();
        if (rank < 1 || rank > 32)
            throw new InvalidDataException($"NRBF：陣列維度不合理（{rank}）。");

        long total = 1;
        for (var i = 0; i < rank; i++)
        {
            total *= ReadArrayLength();
            if (total > MaxArrayLength)
                throw new InvalidDataException("NRBF：陣列元素數量不合理。");
        }

        if (arrayType is BinaryArrayType.SingleOffset or BinaryArrayType.JaggedOffset
            or BinaryArrayType.RectangularOffset)
        {
            for (var i = 0; i < rank; i++) _reader.ReadInt32();  // lowerBounds
        }

        var elementType = (BinaryType)_reader.ReadByte();
        var info = ReadAdditionalInfo(elementType);

        if (elementType == BinaryType.Primitive)
        {
            var array = ReadPrimitiveArray((int)total, (PrimitiveType)info!);
            _objects[id] = array;
            return array;
        }

        return StoreObjectArray(id, ReadElements((int)total, elementType, info));
    }

    private object?[] ReadElements(int length, BinaryType type, object? info)
    {
        var items = new object?[length];
        var i = 0;
        while (i < length)
        {
            var value = ReadMemberValue(type, info);
            if (value is NullRun run)
            {
                if (run.Count <= 0 || i + run.Count > length)
                    throw new InvalidDataException("NRBF：null 連續段超出陣列長度。");
                i += run.Count;     // items 已經是 null
                continue;
            }
            items[i++] = value;
        }
        return items;
    }

    private object?[] StoreObjectArray(int id, object?[] items)
    {
        _objects[id] = items;
        _objectArrays.Add(items);
        return items;
    }

    private object ReadPrimitiveArray(int length, PrimitiveType type)
    {
        if (type == PrimitiveType.Byte) return ReadExactly(length);

        var items = new object?[length];
        for (var i = 0; i < length; i++) items[i] = ReadPrimitive(type);
        return items;
    }

    private int ReadArrayLength()
    {
        var length = _reader.ReadInt32();
        if (length < 0 || length > MaxArrayLength)
            throw new InvalidDataException($"NRBF：陣列長度不合理（{length}）。");
        return length;
    }

    private byte[] ReadExactly(int count)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = _reader.Read(buffer, read, count - read);
            if (n <= 0) throw new EndOfStreamException("NRBF：資料提前結束。");
            read += n;
        }
        return buffer;
    }

    private object ReadPrimitive(PrimitiveType type) => type switch
    {
        PrimitiveType.Boolean => _reader.ReadBoolean(),
        PrimitiveType.Byte => _reader.ReadByte(),
        PrimitiveType.Char => _reader.ReadChar(),
        PrimitiveType.Decimal => decimal.Parse(_reader.ReadString(), CultureInfo.InvariantCulture),
        PrimitiveType.Double => _reader.ReadDouble(),
        PrimitiveType.Int16 => _reader.ReadInt16(),
        PrimitiveType.Int32 => _reader.ReadInt32(),
        PrimitiveType.Int64 => _reader.ReadInt64(),
        PrimitiveType.SByte => _reader.ReadSByte(),
        PrimitiveType.Single => _reader.ReadSingle(),
        PrimitiveType.TimeSpan => new TimeSpan(_reader.ReadInt64()),
        PrimitiveType.DateTime => ReadDateTime(),
        PrimitiveType.UInt16 => _reader.ReadUInt16(),
        PrimitiveType.UInt32 => _reader.ReadUInt32(),
        PrimitiveType.UInt64 => _reader.ReadUInt64(),
        PrimitiveType.String => _reader.ReadString(),
        _ => throw new InvalidDataException($"NRBF：不支援的基本型別 {(byte)type}。"),
    };

    private object ReadDateTime()
    {
        var raw = _reader.ReadInt64();
        try { return DateTime.FromBinary(raw); }
        catch (ArgumentException) { return default(DateTime); }   // 值壞掉不該讓整份檔案讀不了
    }

    // ---- 內部型別 ----

    private const int MaxMembers = 4096;
    private const int MaxArrayLength = 1 << 26;

    /// <summary>「這個記錄不產生值」（BinaryLibrary）；不用 null，null 是合法的成員值。</summary>
    private static readonly object NoValue = new();

    private readonly record struct NrbfRef(int Id);

    private sealed record NullRun(int Count);

    private sealed record ClassMetadata(
        int ObjectId,
        string TypeName,
        string[] MemberNames,
        BinaryType[] MemberTypes,
        object?[] AdditionalInfo);

    private enum RecordType : byte
    {
        SerializedStreamHeader = 0,
        ClassWithId = 1,
        SystemClassWithMembers = 2,
        ClassWithMembers = 3,
        SystemClassWithMembersAndTypes = 4,
        ClassWithMembersAndTypes = 5,
        BinaryObjectString = 6,
        BinaryArray = 7,
        MemberPrimitiveTyped = 8,
        MemberReference = 9,
        ObjectNull = 10,
        MessageEnd = 11,
        BinaryLibrary = 12,
        ObjectNullMultiple256 = 13,
        ObjectNullMultiple = 14,
        ArraySinglePrimitive = 15,
        ArraySingleObject = 16,
        ArraySingleString = 17,
    }

    private enum BinaryType : byte
    {
        Primitive = 0,
        String = 1,
        Object = 2,
        SystemClass = 3,
        Class = 4,
        ObjectArray = 5,
        StringArray = 6,
        PrimitiveArray = 7,
    }

    private enum BinaryArrayType : byte
    {
        Single = 0,
        Jagged = 1,
        Rectangular = 2,
        SingleOffset = 3,
        JaggedOffset = 4,
        RectangularOffset = 5,
    }

    private enum PrimitiveType : byte
    {
        Boolean = 1,
        Byte = 2,
        Char = 3,
        Decimal = 5,
        Double = 6,
        Int16 = 7,
        Int32 = 8,
        Int64 = 9,
        SByte = 10,
        Single = 11,
        TimeSpan = 12,
        DateTime = 13,
        UInt16 = 14,
        UInt32 = 15,
        UInt64 = 16,
        Null = 17,
        String = 18,
    }
}

/// <summary>NRBF 裡的一個類別實例。成員值可能是基本型別、string、NrbfObject、object?[]、byte[] 或 null。</summary>
internal sealed class NrbfObject
{
    public NrbfObject(string typeName) => TypeName = typeName;

    /// <summary>組件限定名之前的型別名，例如 <c>PaintDotNet.BitmapLayer</c>。</summary>
    public string TypeName { get; }

    public Dictionary<string, object?> Members { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 取成員值。BinaryFormatter 遇到基底類別同名欄位會改寫成 <c>宣告型別+欄位名</c>
    /// （例如 BitmapLayer 也有 properties，Layer 的就變成 <c>Layer+properties</c>），
    /// 所以完全比對不到時再退回後綴比對。
    /// </summary>
    public object? Member(string name)
    {
        if (Members.TryGetValue(name, out var value)) return value;
        var suffix = "+" + name;
        foreach (var pair in Members)
            if (pair.Key.EndsWith(suffix, StringComparison.Ordinal)) return pair.Value;
        return null;
    }

    /// <summary>依成員的型別名找子物件（同名成員撞在一起時比名字可靠）。</summary>
    public NrbfObject? MemberOfType(Func<string, bool> matches)
    {
        foreach (var pair in Members)
            if (pair.Value is NrbfObject child && matches(child.TypeName)) return child;
        return null;
    }

    public int? Int32(string name) => Member(name) as int?;

    public long? Int64(string name) => Member(name) switch
    {
        long value => value,
        int value => value,
        _ => null,
    };

    public bool? Bool(string name) => Member(name) as bool?;

    public byte? Byte(string name) => Member(name) as byte?;

    public string? String(string name) => Member(name) as string;

    public override string ToString() => TypeName;
}

/// <summary>一次 NRBF 解析的結果。</summary>
internal sealed class NrbfPayload
{
    public NrbfPayload(NrbfObject root, IReadOnlyList<NrbfObject> objectsInStreamOrder)
    {
        Root = root;
        ObjectsInStreamOrder = objectsInStreamOrder;
    }

    public NrbfObject Root { get; }

    /// <summary>所有類別實例，依它們在資料流中出現的順序 —— 也就是序列化的順序。</summary>
    public IReadOnlyList<NrbfObject> ObjectsInStreamOrder { get; }
}
