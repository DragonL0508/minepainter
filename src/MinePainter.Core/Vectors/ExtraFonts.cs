using SkiaSharp;

namespace MinePainter.Core.Vectors;

/// <summary>
/// 程式跑著的時候才裝進系統的字型。Skia 的字型管理器（<see cref="SKFontManager.Default"/>）在程序啟動時
/// 就把系統字型集合抓死了，之後裝的字型它一律看不到，只能重開程式（使用者 2026-09-07 要求不用重開）。
/// 這裡由 App 監看字型資料夾，新出現的字型檔直接從檔案載入登記在這，排版時（<see cref="BundledFont.Resolve"/>）
/// 先問這裡再問系統。重開程式後系統就認得它們了，這裡自然是空的。
/// </summary>
public static class ExtraFonts
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, List<SKTypeface>> ByFamily = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>有新字型登記進來（背景執行緒上觸發）。</summary>
    public static event Action? Changed;

    /// <summary>已登記的家族名。</summary>
    public static IReadOnlyList<string> Families
    {
        get { lock (Gate) return ByFamily.Keys.ToArray(); }
    }

    public static bool Has(string family)
    {
        lock (Gate) return ByFamily.ContainsKey(family ?? "");
    }

    /// <summary>這支字面是這裡登記的（全程共用，呼叫端不可 Dispose）。</summary>
    public static bool Owns(SKTypeface typeface)
    {
        lock (Gate) return ByFamily.Values.Any(list => list.Contains(typeface));
    }

    /// <summary>
    /// 登記一個字型檔（.ttf／.otf／.ttc，集合裡每一支都收）。同一個檔案只登記一次；
    /// 讀不出來（檔案還在複製中、壞掉）回 false，呼叫端可以稍後再試。
    /// </summary>
    public static bool Register(string path)
    {
        lock (Gate)
        {
            if (Files.Contains(path)) return false;
        }
        var loaded = new List<SKTypeface>();
        for (var index = 0; index < 64; index++)
        {
            SKTypeface? typeface;
            try
            {
                typeface = SKTypeface.FromFile(path, index);
            }
            catch (Exception)
            {
                typeface = null;
            }
            if (typeface == null) break;
            // 非集合檔第 1 支以後 Skia 可能回同一支：家族＋樣式都一樣就不重複收
            if (loaded.Any(t => t.FamilyName == typeface.FamilyName && t.FontStyle.Weight == typeface.FontStyle.Weight
                    && t.FontStyle.Slant == typeface.FontStyle.Slant))
            {
                typeface.Dispose();
                break;
            }
            loaded.Add(typeface);
        }
        if (loaded.Count == 0) return false;

        lock (Gate)
        {
            Files.Add(path);
            foreach (var typeface in loaded)
            {
                if (!ByFamily.TryGetValue(typeface.FamilyName, out var list)) ByFamily[typeface.FamilyName] = list = [];
                list.Add(typeface);
            }
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>家族＋字重／斜體最接近的字面（同斜體優先、字重差最小）；沒登記這個家族回 null。全程共用，呼叫端不可 Dispose。</summary>
    public static SKTypeface? Resolve(string family, SKFontStyle style)
    {
        lock (Gate)
        {
            if (!ByFamily.TryGetValue(family ?? "", out var list) || list.Count == 0) return null;
            SKTypeface? best = null;
            var bestScore = int.MaxValue;
            foreach (var typeface in list)
            {
                var score = Math.Abs(typeface.FontStyle.Weight - style.Weight)
                    + (typeface.FontStyle.Slant == style.Slant ? 0 : 1000);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = typeface;
                }
            }
            return best;
        }
    }

    /// <summary>家族可用的直立字重（去重、排序）；沒登記回空陣列。</summary>
    public static IReadOnlyList<(string Name, int Weight)> Styles(string family)
    {
        lock (Gate)
        {
            if (!ByFamily.TryGetValue(family ?? "", out var list)) return [];
            return list.Where(t => t.FontStyle.Slant == SKFontStyleSlant.Upright)
                .Select(t => t.FontStyle.Weight)
                .Distinct()
                .OrderBy(w => w)
                .Select(w => (WeightName(w), w))
                .ToArray();
        }
    }

    private static string WeightName(int weight) => weight switch
    {
        <= 150 => "Thin",
        <= 250 => "ExtraLight",
        <= 350 => "Light",
        <= 450 => "Regular",
        <= 550 => "Medium",
        <= 650 => "SemiBold",
        <= 750 => "Bold",
        <= 850 => "ExtraBold",
        _ => "Black",
    };

    /// <summary>測試用：清空登記。</summary>
    internal static void Clear()
    {
        lock (Gate)
        {
            foreach (var list in ByFamily.Values)
                foreach (var typeface in list) typeface.Dispose();
            ByFamily.Clear();
            Files.Clear();
        }
    }
}
