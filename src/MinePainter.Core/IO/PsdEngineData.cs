using System.Globalization;
using System.Text;

namespace MinePainter.Core.IO;

/// <summary>
/// 文字圖層的 EngineData：PostScript 風格的巢狀字典，<c>&lt;&lt; /Key value &gt;&gt;</c>、
/// <c>[ ... ]</c> 清單、數字、true／false、<c>(字串)</c>。字串是 UTF-16BE 帶 FE FF 前導，
/// 裡面的 <c>(</c>、<c>)</c>、<c>\</c> 用反斜線跳脫 —— 注意 UTF-16 的高位元組本來就可能是這些值，
/// 所以要先照跳脫規則收完整段位元組再解碼，不能先當文字處理。
///
/// 解出來的值：<see cref="Dictionary{TKey,TValue}"/>（string → object?）、<see cref="List{T}"/>、double、bool、string。
/// </summary>
internal static class PsdEngineData
{
    public static Dictionary<string, object?> Parse(byte[] data)
    {
        var position = 0;
        var value = ReadValue(data, ref position);
        return value as Dictionary<string, object?> ?? throw new InvalidDataException("EngineData 最外層不是字典。");
    }

    /// <summary>沿路徑往下找（字典用鍵、清單用索引），任何一段不存在就回 null。</summary>
    public static object? Path(object? root, params object[] path)
    {
        var current = root;
        foreach (var step in path)
        {
            current = step switch
            {
                string key when current is Dictionary<string, object?> dict => dict.GetValueOrDefault(key),
                int index when current is List<object?> list && index >= 0 && index < list.Count => list[index],
                _ => null,
            };
            if (current == null) return null;
        }
        return current;
    }

    public static double? Number(object? root, params object[] path) => Path(root, path) as double?;
    public static bool? Bool(object? root, params object[] path) => Path(root, path) as bool?;
    public static string? Text(object? root, params object[] path) => Path(root, path) as string;
    public static Dictionary<string, object?>? Dict(object? root, params object[] path) => Path(root, path) as Dictionary<string, object?>;
    public static List<object?>? List(object? root, params object[] path) => Path(root, path) as List<object?>;

    private static object? ReadValue(byte[] data, ref int p)
    {
        SkipWhitespace(data, ref p);
        if (p >= data.Length) throw new InvalidDataException("EngineData 提前結束。");

        var c = data[p];
        if (c == '<' && Peek(data, p + 1) == '<')
        {
            p += 2;
            return ReadDictionary(data, ref p);
        }
        if (c == '[')
        {
            p++;
            var list = new List<object?>();
            while (true)
            {
                SkipWhitespace(data, ref p);
                if (p >= data.Length) throw new InvalidDataException("EngineData 清單沒有結尾。");
                if (data[p] == ']')
                {
                    p++;
                    return list;
                }
                list.Add(ReadValue(data, ref p));
            }
        }
        if (c == '(')
        {
            p++;
            return ReadString(data, ref p);
        }
        if (c == '/')
        {
            // 值位置出現的名稱（罕見）：當字串
            p++;
            return ReadToken(data, ref p);
        }

        var token = ReadToken(data, ref p);
        if (token == "true") return true;
        if (token == "false") return false;
        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return number;
        throw new InvalidDataException($"EngineData 含無法辨識的值「{token}」。");
    }

    private static Dictionary<string, object?> ReadDictionary(byte[] data, ref int p)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        while (true)
        {
            SkipWhitespace(data, ref p);
            if (p >= data.Length) throw new InvalidDataException("EngineData 字典沒有結尾。");
            if (data[p] == '>' && Peek(data, p + 1) == '>')
            {
                p += 2;
                return dict;
            }
            if (data[p] != '/')
                throw new InvalidDataException("EngineData 字典的鍵不是 /名稱。");
            p++;
            var key = ReadToken(data, ref p);
            dict[key] = ReadValue(data, ref p);
        }
    }

    private static string ReadString(byte[] data, ref int p)
    {
        var bytes = new List<byte>();
        while (true)
        {
            if (p >= data.Length) throw new InvalidDataException("EngineData 字串沒有結尾。");
            var c = data[p++];
            if (c == '\\')
            {
                if (p >= data.Length) throw new InvalidDataException("EngineData 字串跳脫不完整。");
                bytes.Add(data[p++]);
                continue;
            }
            if (c == ')') break;
            bytes.Add(c);
        }

        var raw = bytes.ToArray();
        if (raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(raw, 2, raw.Length - 2);
        return Encoding.Latin1.GetString(raw);
    }

    private static string ReadToken(byte[] data, ref int p)
    {
        var start = p;
        while (p < data.Length && !IsDelimiter(data[p])) p++;
        if (p == start) throw new InvalidDataException("EngineData 出現空的記號。");
        return Encoding.ASCII.GetString(data, start, p - start);
    }

    private static bool IsDelimiter(byte c) =>
        c is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f' or (byte)'\0'
            or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'(' or (byte)')' or (byte)'/';

    private static void SkipWhitespace(byte[] data, ref int p)
    {
        while (p < data.Length && data[p] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f' or (byte)'\0') p++;
    }

    private static int Peek(byte[] data, int index) => index < data.Length ? data[index] : -1;
}
