using MinePainter.Core.Effects;
using SkiaSharp;
using static MinePainter.Core.Effects.EffectMath;

namespace MinePainter.Core.Adjustments;

/// <summary>
/// LUT 調色：內建預設集或載入 .cube 檔，一鍵套整套色調。
/// Preset ≥ 0 = 內建（存檔只記索引）；-1 = 自訂表（存檔帶完整資料）。
/// 走逐像素路徑（<see cref="RequiresPixelPath"/>）：Skia 色彩濾鏡做不了 3D 查表。
/// </summary>
public sealed record LutAdjustment : IAdjustment
{
    public const int CustomPreset = -1;

    public int Preset { get; init; } = 0;
    public Lut3D Lut { get; init; } = LutPresets.All[0].Lut;
    public int Amount { get; init; } = 100; // 0..100

    public string DisplayName => "LUT 調色";
    public string TypeId => "lut";

    /// <summary>目前套的是哪一張表的名字（自訂＝檔名）。</summary>
    public string LutName => Lut.Name;

    private static readonly string[] PresetOptions = [.. LutPresets.Names, "自訂（.cube 檔）"];

    private static readonly ParamDef[] Params =
    [
        new ChoiceParam("preset", "預設集", PresetOptions,
            a => ((LutAdjustment)a).Preset == CustomPreset ? PresetOptions.Length - 1 : ((LutAdjustment)a).Preset,
            (a, v) => v >= LutPresets.All.Length
                ? (LutAdjustment)a // 選到「自訂」：表不換，等使用者用下面的按鈕載檔
                : ((LutAdjustment)a) with { Preset = v, Lut = LutPresets.All[v].Lut }),
        new FileParam("file", "載入 .cube 檔", ["*.cube"],
            a => ((LutAdjustment)a).Preset == CustomPreset ? ((LutAdjustment)a).LutName : "",
            (a, path) => ((LutAdjustment)a).WithCubeFile(path)),
        new SliderParam("amount", "強度", 0, 100, a => ((LutAdjustment)a).Amount,
            (a, v) => ((LutAdjustment)a) with { Amount = (int)v }, "%"),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    /// <summary>讀 .cube 檔換成自訂表（格式錯誤丟 InvalidDataException，由 UI 轉成 toast）。</summary>
    public LutAdjustment WithCubeFile(string path)
    {
        var lut = Lut3D.ParseCube(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path));
        return this with { Preset = CustomPreset, Lut = lut };
    }

    public Dictionary<string, float> SaveParams() => new() { ["preset"] = Preset, ["amount"] = Amount };

    public string? SaveData() => Preset == CustomPreset ? Lut.Serialize() : null;

    public static LutAdjustment Load(IReadOnlyDictionary<string, float> p, string? data)
    {
        var preset = (int)p.GetValueOrDefault("preset", 0);
        var amount = (int)p.GetValueOrDefault("amount", 100);
        if (preset >= 0 && preset < LutPresets.All.Length)
            return new LutAdjustment { Preset = preset, Lut = LutPresets.All[preset].Lut, Amount = amount };
        // 自訂表；資料不見了（舊版存的？）就退成單位表，至少檔案打得開
        var lut = data != null ? Lut3D.Deserialize(data) : Lut3D.Identity();
        return new LutAdjustment { Preset = CustomPreset, Lut = lut, Amount = amount };
    }

    public bool RequiresPixelPath => true;

    /// <summary>
    /// 只有沒接像素路徑的呼叫端才會拿到這個：用表的灰階對角線做成逐通道查表，
    /// 對比／亮度會像、分離色調不會像。所有真正的路徑（合成器、破壞性套用、匯出）都走 <see cref="ApplyPixels"/>。
    /// </summary>
    public SKColorFilter CreateColorFilter()
    {
        var tr = new byte[256];
        var tg = new byte[256];
        var tb = new byte[256];
        var k = Math.Clamp(Amount, 0, 100) / 100f;
        for (var i = 0; i < 256; i++)
        {
            Lut.Lookup(i, i, i, out var r, out var g, out var b);
            tr[i] = (byte)Clamp255(i + (r - i) * k);
            tg[i] = (byte)Clamp255(i + (g - i) * k);
            tb[i] = (byte)Clamp255(i + (b - i) * k);
        }
        return SKColorFilter.CreateTable(null, tr, tg, tb);
    }

    public void ApplyPixels(uint[] pixels, int count)
    {
        var k = Math.Clamp(Amount, 0, 100);
        if (k == 0) return;
        var lut = Lut;
        const int chunk = 8192;
        Parallel.For(0, (count + chunk - 1) / chunk, c =>
        {
            var end = Math.Min(count, (c + 1) * chunk);
            for (var i = c * chunk; i < end; i++)
            {
                var p = pixels[i];
                if (A(p) == 0) continue;
                Unpremul(p, out var b, out var g, out var r, out var a);
                lut.Lookup(r, g, b, out var nr, out var ng, out var nb);
                if (k < 100)
                {
                    nr = r + (nr - r) * k / 100;
                    ng = g + (ng - g) * k / 100;
                    nb = b + (nb - b) * k / 100;
                }
                pixels[i] = Premul(Clamp255(nb), Clamp255(ng), Clamp255(nr), a);
            }
        });
    }
}
