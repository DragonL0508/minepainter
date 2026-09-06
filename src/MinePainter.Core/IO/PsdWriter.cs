using System.Buffers.Binary;
using System.Text;
using SkiaSharp;

namespace MinePainter.Core.IO;

/// <summary>大端序寫入器（記憶體），給 .psd 的各段與描述子用。與 <see cref="PsdByteReader"/> 成對。</summary>
internal sealed class PsdByteWriter
{
    private readonly MemoryStream _s = new();

    public long Length => _s.Length;
    public long Position { get => _s.Position; set => _s.Position = value; }

    public void U8(int v) => _s.WriteByte((byte)v);
    public void U16(int v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); _s.Write(b); }
    public void I16(int v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteInt16BigEndian(b, (short)v); _s.Write(b); }
    public void U32(long v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, (uint)v); _s.Write(b); }
    public void I32(int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); _s.Write(b); }
    public void I64(long v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, v); _s.Write(b); }
    public void F32(float v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteSingleBigEndian(b, v); _s.Write(b); }
    public void F64(double v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteDoubleBigEndian(b, v); _s.Write(b); }
    public void Bytes(ReadOnlySpan<byte> bytes) => _s.Write(bytes);
    public void Ascii(string s) => _s.Write(Encoding.ASCII.GetBytes(s));
    public void Zero(int count) { for (var i = 0; i < count; i++) _s.WriteByte(0); }

    /// <summary>PSD 用 4 位元組、PSB 用 8 位元組的「長度」欄位。</summary>
    public void LengthField(long value, bool psb)
    {
        if (psb) I64(value);
        else U32(value);
    }

    /// <summary>4 位元組字元數 + UTF-16BE（含結尾的 NUL）。</summary>
    public void UnicodeString(string value)
    {
        U32(value.Length + 1);
        _s.Write(Encoding.BigEndianUnicode.GetBytes(value + "\0"));
    }

    /// <summary>描述子的鍵／類別 ID：4 字元寫 0 + 字元，否則寫長度 + 字元。</summary>
    public void Key(string key)
    {
        if (key.Length == 4)
        {
            U32(0);
            Ascii(key);
        }
        else
        {
            U32(key.Length);
            Ascii(key);
        }
    }

    public byte[] ToArray() => _s.ToArray();
    public void WriteTo(Stream target) => _s.WriteTo(target);
    public void Append(PsdByteWriter other) => other._s.WriteTo(_s);
}

/// <summary>
/// 可寫出的 Action Descriptor（<see cref="PsdDescriptor"/> 的寫入端）。
/// 值：double／<see cref="PsdUnit"/>／int／bool／string／<see cref="PsdEnum"/>／<see cref="List{T}"/>（object）／
/// <see cref="PsdDescriptorBuilder"/>／byte[]（tdta）。鍵的順序照加入的順序寫。
/// </summary>
internal sealed class PsdDescriptorBuilder(string classId)
{
    public string ClassId { get; } = classId;
    public List<(string Key, object Value)> Items { get; } = [];

    public PsdDescriptorBuilder Add(string key, object value)
    {
        Items.Add((key, value));
        return this;
    }

    public PsdDescriptorBuilder Add(string key, PsdDescriptorBuilder child) => Add(key, (object)child);

    /// <summary>「版本 16 + 描述子」的整段位元組（SoCo、CgEd、vibA 這類參數區塊）。</summary>
    public byte[] ToBlockWithVersion()
    {
        var w = new PsdByteWriter();
        w.U32(16);
        WriteTo(w);
        return w.ToArray();
    }

    public void WriteTo(PsdByteWriter w)
    {
        w.UnicodeString("");
        w.Key(ClassId);
        w.U32(Items.Count);
        foreach (var (key, value) in Items)
        {
            w.Key(key);
            WriteItem(w, value);
        }
    }

    private static void WriteItem(PsdByteWriter w, object value)
    {
        switch (value)
        {
            case PsdDescriptorBuilder o:
                w.Ascii("Objc");
                o.WriteTo(w);
                break;
            case List<object> list:
                w.Ascii("VlLs");
                w.U32(list.Count);
                foreach (var item in list) WriteItem(w, item);
                break;
            case double d: w.Ascii("doub"); w.F64(d); break;
            case float f: w.Ascii("doub"); w.F64(f); break;
            case PsdUnit u: w.Ascii("UntF"); w.Ascii(u.Unit); w.F64(u.Value); break;
            case string s: w.Ascii("TEXT"); w.UnicodeString(s); break;
            case PsdEnum e: w.Ascii("enum"); w.Key(e.Type); w.Key(e.Value); break;
            case int i: w.Ascii("long"); w.I32(i); break;
            case bool b: w.Ascii("bool"); w.U8(b ? 1 : 0); break;
            case byte[] raw: w.Ascii("tdta"); w.U32(raw.Length); w.Bytes(raw); break;
            default: throw new ArgumentException($"描述子不能寫這種值：{value.GetType().Name}");
        }
    }
}

/// <summary>描述子常用值的簡寫。</summary>
internal static class PsdDesc
{
    public static PsdUnit Px(double v) => new("#Pxl", v);
    public static PsdUnit Prc(double v) => new("#Prc", v);
    public static PsdUnit Ang(double v) => new("#Ang", v);
    public static PsdUnit Pnt(double v) => new("#Pnt", v);
    public static PsdEnum Enum(string type, string value) => new(type, value);
    public static PsdEnum Blend(string key) => new("BlnM", key);

    /// <summary>RGB 顏色子描述子（0..255 的 double；alpha 另外走 Opct）。</summary>
    public static PsdDescriptorBuilder Rgb(SKColor c) => new PsdDescriptorBuilder("RGBC")
        .Add("Rd  ", (double)c.Red)
        .Add("Grn ", (double)c.Green)
        .Add("Bl  ", (double)c.Blue);
}
