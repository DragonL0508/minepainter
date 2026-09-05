using System.Globalization;
using System.Text;

namespace MinePainter.Core.Adjustments;

/// <summary>
/// 3D 色彩查找表（.cube 那種）：把每個 RGB 對到另一個 RGB，格點之間三線性內插。
/// 不可變；Data 的排列照 .cube 規格 —— 紅最快變、藍最慢（index = ((b·N + g)·N + r)·3）。
/// Skia 的色彩濾鏡表達不了 3D 查表（SkiaSharp 2.88 的 runtime shader 在 CPU raster 會直接崩），
/// 所以這個表只走逐像素路徑（<see cref="IAdjustment.ApplyPixels"/>）。
/// </summary>
public sealed class Lut3D
{
    public const int MinSize = 2;
    public const int MaxSize = 65;

    public int Size { get; }
    public string Name { get; }

    /// <summary>N³×3 個 0..1 的輸出值。</summary>
    public float[] Data { get; }

    public Lut3D(int size, float[] data, string name)
    {
        if (size < MinSize || size > MaxSize) throw new ArgumentOutOfRangeException(nameof(size), $"LUT 邊長 {size} 不在 {MinSize}..{MaxSize}");
        if (data.Length != size * size * size * 3) throw new ArgumentException($"LUT 資料長度 {data.Length} 與邊長 {size} 不符", nameof(data));
        Size = size;
        Data = data;
        Name = name;
    }

    /// <summary>單位表（輸入＝輸出），套了等於沒套。</summary>
    public static Lut3D Identity(int size = 2) => FromFunction(size, (r, g, b) => (r, g, b), "無");

    /// <summary>把一個 RGB→RGB 的函數取樣成表（內建預設集用）。</summary>
    public static Lut3D FromFunction(int size, Func<float, float, float, (float R, float G, float B)> f, string name)
    {
        var data = new float[size * size * size * 3];
        var i = 0;
        for (var b = 0; b < size; b++)
        for (var g = 0; g < size; g++)
        for (var r = 0; r < size; r++)
        {
            var (or, og, ob) = f(r / (size - 1f), g / (size - 1f), b / (size - 1f));
            data[i++] = Math.Clamp(or, 0f, 1f);
            data[i++] = Math.Clamp(og, 0f, 1f);
            data[i++] = Math.Clamp(ob, 0f, 1f);
        }
        return new Lut3D(size, data, name);
    }

    /// <summary>三線性內插查表（輸入輸出都是 0..255）。</summary>
    public void Lookup(int r, int g, int b, out int outR, out int outG, out int outB)
    {
        var n = Size;
        var scale = (n - 1) / 255f;
        var fr = r * scale;
        var fg = g * scale;
        var fb = b * scale;
        var r0 = Math.Min((int)fr, n - 2);
        var g0 = Math.Min((int)fg, n - 2);
        var b0 = Math.Min((int)fb, n - 2);
        var tr = fr - r0;
        var tg = fg - g0;
        var tb = fb - b0;

        var d = Data;
        var strideG = n * 3;
        var strideB = n * n * 3;
        var i000 = (b0 * n + g0) * n * 3 + r0 * 3;
        var i100 = i000 + 3;
        var i010 = i000 + strideG;
        var i110 = i010 + 3;
        var i001 = i000 + strideB;
        var i101 = i001 + 3;
        var i011 = i001 + strideG;
        var i111 = i011 + 3;

        outR = (int)(Tri(d[i000], d[i100], d[i010], d[i110], d[i001], d[i101], d[i011], d[i111], tr, tg, tb) * 255f + 0.5f);
        outG = (int)(Tri(d[i000 + 1], d[i100 + 1], d[i010 + 1], d[i110 + 1], d[i001 + 1], d[i101 + 1], d[i011 + 1], d[i111 + 1], tr, tg, tb) * 255f + 0.5f);
        outB = (int)(Tri(d[i000 + 2], d[i100 + 2], d[i010 + 2], d[i110 + 2], d[i001 + 2], d[i101 + 2], d[i011 + 2], d[i111 + 2], tr, tg, tb) * 255f + 0.5f);
    }

    private static float Tri(float c000, float c100, float c010, float c110, float c001, float c101, float c011, float c111,
        float tr, float tg, float tb)
    {
        var c00 = c000 + (c100 - c000) * tr;
        var c10 = c010 + (c110 - c010) * tr;
        var c01 = c001 + (c101 - c001) * tr;
        var c11 = c011 + (c111 - c011) * tr;
        var c0 = c00 + (c10 - c00) * tg;
        var c1 = c01 + (c11 - c01) * tg;
        return c0 + (c1 - c0) * tb;
    }

    // ---- .cube ----

    /// <summary>
    /// 讀 Adobe／Resolve 的 .cube 文字格式（LUT_3D_SIZE；LUT_1D_SIZE 也吃，會展成三維）。
    /// DOMAIN_MIN／MAX 不是 0..1 時把輸入對應過去。格式不對丟 InvalidDataException。
    /// </summary>
    public static Lut3D ParseCube(string text, string fallbackName)
    {
        var name = fallbackName;
        var size3 = 0;
        var size1 = 0;
        var domainMin = new[] { 0f, 0f, 0f };
        var domainMax = new[] { 1f, 1f, 1f };
        var values = new List<float>();
        var inv = CultureInfo.InvariantCulture;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line.StartsWith("TITLE", StringComparison.OrdinalIgnoreCase))
            {
                var t = line[5..].Trim().Trim('"');
                if (t.Length > 0) name = t;
                continue;
            }
            if (line.StartsWith("LUT_3D_SIZE", StringComparison.OrdinalIgnoreCase)) { size3 = int.Parse(line[11..].Trim(), inv); continue; }
            if (line.StartsWith("LUT_1D_SIZE", StringComparison.OrdinalIgnoreCase)) { size1 = int.Parse(line[11..].Trim(), inv); continue; }
            if (line.StartsWith("DOMAIN_MIN", StringComparison.OrdinalIgnoreCase)) { domainMin = ParseTriple(line[10..], inv); continue; }
            if (line.StartsWith("DOMAIN_MAX", StringComparison.OrdinalIgnoreCase)) { domainMax = ParseTriple(line[10..], inv); continue; }
            if (line.StartsWith("LUT_3D_INPUT_RANGE", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("LUT_1D_INPUT_RANGE", StringComparison.OrdinalIgnoreCase))
            {
                var range = ParseTriple(line[18..], inv, allowTwo: true);
                domainMin = [range[0], range[0], range[0]];
                domainMax = [range[1], range[1], range[1]];
                continue;
            }
            if (!char.IsDigit(line[0]) && line[0] != '-' && line[0] != '.') continue; // 不認識的關鍵字跳過
            var parts = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) throw new InvalidDataException($"這一行不是三個數字：{line}");
            values.Add(float.Parse(parts[0], inv));
            values.Add(float.Parse(parts[1], inv));
            values.Add(float.Parse(parts[2], inv));
        }

        if (size3 > 0)
        {
            if (size3 < MinSize || size3 > MaxSize) throw new InvalidDataException($"LUT_3D_SIZE {size3} 不支援（{MinSize}..{MaxSize}）");
            if (values.Count != size3 * size3 * size3 * 3) throw new InvalidDataException($"資料筆數 {values.Count / 3} 與 LUT_3D_SIZE {size3}³ 不符");
            var lut = new Lut3D(size3, values.ToArray(), name);
            return IsUnitDomain(domainMin, domainMax) ? lut : lut.RemapDomain(domainMin, domainMax);
        }
        if (size1 > 0)
        {
            if (values.Count != size1 * 3) throw new InvalidDataException($"資料筆數 {values.Count / 3} 與 LUT_1D_SIZE {size1} 不符");
            // 一維表：三通道各自查，展成三維（邊長取 33 夠平滑）
            var table = values.ToArray();
            var lut = FromFunction(33, (r, g, b) =>
                (Sample1D(table, size1, 0, Norm(r, domainMin[0], domainMax[0])),
                 Sample1D(table, size1, 1, Norm(g, domainMin[1], domainMax[1])),
                 Sample1D(table, size1, 2, Norm(b, domainMin[2], domainMax[2]))), name);
            return lut;
        }
        throw new InvalidDataException("找不到 LUT_3D_SIZE（不是 .cube 檔？）");
    }

    private static float[] ParseTriple(string s, CultureInfo inv, bool allowTwo = false)
    {
        var parts = s.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < (allowTwo ? 2 : 3)) throw new InvalidDataException($"數字不夠：{s}");
        return parts.Take(3).Select(p => float.Parse(p, inv)).ToArray();
    }

    private static bool IsUnitDomain(float[] min, float[] max) =>
        min.All(v => Math.Abs(v) < 1e-5f) && max.All(v => Math.Abs(v - 1f) < 1e-5f);

    private static float Norm(float v, float min, float max) => max > min ? (v - min) / (max - min) : v;

    private static float Sample1D(float[] table, int size, int channel, float t)
    {
        var f = Math.Clamp(t, 0f, 1f) * (size - 1);
        var i0 = Math.Min((int)f, size - 2);
        var frac = f - i0;
        var a = table[i0 * 3 + channel];
        var b = table[(i0 + 1) * 3 + channel];
        return a + (b - a) * frac;
    }

    /// <summary>輸入域不是 0..1 的表：重新取樣成 0..1 輸入的同尺寸表。</summary>
    private Lut3D RemapDomain(float[] min, float[] max)
    {
        var src = this;
        return FromFunction(Size, (r, g, b) =>
        {
            src.LookupF(Norm(r, min[0], max[0]), Norm(g, min[1], max[1]), Norm(b, min[2], max[2]), out var or, out var og, out var ob);
            return (or, og, ob);
        }, Name);
    }

    /// <summary>浮點版查表（0..1）。</summary>
    public void LookupF(float r, float g, float b, out float outR, out float outG, out float outB)
    {
        var n = Size;
        var fr = Math.Clamp(r, 0f, 1f) * (n - 1);
        var fg = Math.Clamp(g, 0f, 1f) * (n - 1);
        var fb = Math.Clamp(b, 0f, 1f) * (n - 1);
        var r0 = Math.Min((int)fr, n - 2);
        var g0 = Math.Min((int)fg, n - 2);
        var b0 = Math.Min((int)fb, n - 2);
        var tr = fr - r0;
        var tg = fg - g0;
        var tb = fb - b0;
        var d = Data;
        var strideG = n * 3;
        var strideB = n * n * 3;
        var i000 = (b0 * n + g0) * n * 3 + r0 * 3;
        outR = Tri(d[i000], d[i000 + 3], d[i000 + strideG], d[i000 + strideG + 3], d[i000 + strideB], d[i000 + strideB + 3], d[i000 + strideB + strideG], d[i000 + strideB + strideG + 3], tr, tg, tb);
        outG = Tri(d[i000 + 1], d[i000 + 4], d[i000 + strideG + 1], d[i000 + strideG + 4], d[i000 + strideB + 1], d[i000 + strideB + 4], d[i000 + strideB + strideG + 1], d[i000 + strideB + strideG + 4], tr, tg, tb);
        outB = Tri(d[i000 + 2], d[i000 + 5], d[i000 + strideG + 2], d[i000 + strideG + 5], d[i000 + strideB + 2], d[i000 + strideB + 5], d[i000 + strideB + strideG + 2], d[i000 + strideB + strideG + 5], tr, tg, tb);
    }

    // ---- 存檔（.mpp 與效果堆疊的 data 欄位）----

    /// <summary>
    /// 序列化成一行文字：<c>1|名字|邊長|base64(每值 16 位元)</c>。
    /// 33³ 的表約 280 KB，放在 manifest JSON 裡可以接受；名字裡的 '|' 會被換掉。
    /// </summary>
    public string Serialize()
    {
        var bytes = new byte[Data.Length * 2];
        for (var i = 0; i < Data.Length; i++)
        {
            var v = (ushort)Math.Round(Math.Clamp(Data[i], 0f, 1f) * 65535f);
            bytes[i * 2] = (byte)v;
            bytes[i * 2 + 1] = (byte)(v >> 8);
        }
        var sb = new StringBuilder();
        sb.Append("1|").Append(Name.Replace('|', '/')).Append('|').Append(Size.ToString(CultureInfo.InvariantCulture)).Append('|');
        sb.Append(Convert.ToBase64String(bytes));
        return sb.ToString();
    }

    public static Lut3D Deserialize(string text)
    {
        var parts = text.Split('|', 4);
        if (parts.Length != 4 || parts[0] != "1") throw new InvalidDataException("LUT 資料格式不認識");
        var size = int.Parse(parts[2], CultureInfo.InvariantCulture);
        var bytes = Convert.FromBase64String(parts[3]);
        var data = new float[bytes.Length / 2];
        for (var i = 0; i < data.Length; i++)
            data[i] = (bytes[i * 2] | (bytes[i * 2 + 1] << 8)) / 65535f;
        return new Lut3D(size, data, parts[1]);
    }
}
