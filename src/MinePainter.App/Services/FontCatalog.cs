using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using SkiaSharp;

namespace MinePainter.App.Services;

/// <summary>字重／變種選項（Noto Sans TC 的 Light/Black 這類命名字重）。</summary>
public sealed record FontStyleOption(string Name, int Weight);

/// <summary>
/// 本機字型清單與字重列舉的共用處（工具列與進階文字設定視窗都要一份同樣的下拉）。
/// 家族清單啟動後只讀一次（SKFontManager 列舉不便宜）。
/// </summary>
public static class FontCatalog
{
    private static string[]? _families;

    /// <summary>字型清單變了（程式跑著時裝了新字型；UI 執行緒上觸發）。</summary>
    public static event Action? Changed;

    /// <summary>已安裝的字型家族＋程式跑著時新裝的（<see cref="Core.Vectors.ExtraFonts"/>）＋內嵌的保底字型（去重、依語系排序）。</summary>
    public static string[] Families => _families ??= SKFontManager.Default.FontFamilies
        .Concat(Core.Vectors.ExtraFonts.Families)
        .Append(EmbeddedFonts.FamilyName) // 系統沒安裝也選得到（尤其英文版 Windows 沒中文字型時）
        .Distinct()
        .OrderBy(f => f, StringComparer.CurrentCulture)
        .ToArray();

    /// <summary>新字型裝好了：清單重列、新家族的快取清掉，通知下拉重填。UI 執行緒。</summary>
    public static void Invalidate()
    {
        _families = null;
        foreach (var family in Core.Vectors.ExtraFonts.Families)
        {
            StyleCache.TryRemove(family, out _);
            FamilyCache.Remove(family);
        }
        Changed?.Invoke();
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, FontStyleOption[]> StyleCache = new();
    private static readonly Dictionary<string, FontFamily> FamilyCache = new();
    private static bool _warmed;

    /// <summary>
    /// 家族可用的直立字重（斜體交給 I 鈕），依字重排序；列不出來時退回單一 Regular 400。
    /// 結果快取：DirectWrite 列舉一個家族最慢要 40ms，切字型時每次重列會直接掉幀。
    /// </summary>
    public static FontStyleOption[] StylesFor(string family) => StyleCache.GetOrAdd(family ?? "", EnumerateStyles);

    private static FontStyleOption[] EnumerateStyles(string family)
    {
        var options = new List<FontStyleOption>();
        // 程式跑著時新裝的字型：系統的字型管理器看不到它，字重從我們自己載入的字面列
        foreach (var (name, weight) in Core.Vectors.ExtraFonts.Styles(family))
            options.Add(new FontStyleOption(name, weight));
        if (options.Count > 0) return options.ToArray();
        try
        {
            using var set = SKFontManager.Default.GetFontStyles(family);
            var seen = new HashSet<int>();
            for (var i = 0; i < set.Count; i++)
            {
                var style = set[i];
                if (style.Slant != SKFontStyleSlant.Upright) continue;
                if (!seen.Add(style.Weight)) continue;
                var name = set.GetStyleName(i);
                options.Add(new FontStyleOption(
                    string.IsNullOrWhiteSpace(name) ? $"{style.Weight}" : name, style.Weight));
            }
        }
        catch
        {
            // 字型檔壞掉／列舉失敗：當作只有 Regular
        }
        if (options.Count == 0) options.Add(new FontStyleOption("Regular", 400));
        options.Sort((a, b) => a.Weight.CompareTo(b.Weight));
        return options.ToArray();
    }

    /// <summary>
    /// 啟動後預熱：字重列舉丟到背景執行緒（純 Skia，與 UI 無關），GlyphTypeface 探測在 UI 執行緒
    /// 閒置時分批做（Avalonia 的字型物件要在 UI 執行緒建）。之後切字型就只剩查表。
    /// </summary>
    public static void WarmUp()
    {
        if (_warmed) return;
        _warmed = true;
        var families = Families;
        Task.Run(() =>
        {
            foreach (var f in families) StylesFor(f);
        });

        var index = 0;
        void Step()
        {
            var end = Math.Min(index + 8, families.Length);
            for (; index < end; index++) SafeFontFamily(families[index]);
            if (index < families.Length)
                Avalonia.Threading.Dispatcher.UIThread.Post(Step, Avalonia.Threading.DispatcherPriority.Background);
        }
        Avalonia.Threading.Dispatcher.UIThread.Post(Step, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>最接近指定字重的索引（清單不可為空）。</summary>
    public static int ClosestIndex(FontStyleOption[] options, int weight)
    {
        var best = 0;
        for (var i = 1; i < options.Length; i++)
        {
            if (Math.Abs(options[i].Weight - weight) < Math.Abs(options[best].Weight - weight))
                best = i;
        }
        return best;
    }

    /// <summary>
    /// 某些字型 Avalonia 建不出 GlyphTypeface（變數字型集合、名稱含 # 等），直接指定
    /// FontFamily 會在排版時 crash —— 先探測，失敗就退回預設字面（Skia 渲染端自己會 fallback）。
    /// </summary>
    public static FontFamily SafeFontFamily(string name)
    {
        if (string.IsNullOrEmpty(name)) return FontFamily.Default;
        if (FamilyCache.TryGetValue(name, out var cached)) return cached;
        var resolved = ProbeFontFamily(name);
        FamilyCache[name] = resolved;
        return resolved;
    }

    private static FontFamily ProbeFontFamily(string name)
    {
        try
        {
            // 內嵌字型不在系統裡，只能以 avares 位址取用 —— 但系統裝了同名家族時要用系統那份，
            // 內嵌的只有 Regular 一個字重，接走家族名就選不到 Bold／Black
            var family = string.Equals(name, EmbeddedFonts.FamilyName, StringComparison.OrdinalIgnoreCase) &&
                         !SystemHasFamily(name)
                ? new FontFamily(EmbeddedFonts.FamilyUri)
                : new FontFamily(name);
            _ = new Typeface(family).GlyphTypeface;
            return family;
        }
        catch
        {
            return FontFamily.Default;
        }
    }

    /// <summary>系統實際安裝了這個家族嗎（Skia 列得出字型樣式就是有）。</summary>
    private static bool SystemHasFamily(string name)
    {
        try
        {
            using var set = SKFontManager.Default.GetFontStyles(name);
            return set.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 字型下拉的項目樣板：以各字型自己的字面顯示（paint.net 式預覽）。
    /// 固定寬度＋截字：各字型字面寬度不同，寬度浮動會讓下拉選單一直變寬變窄。
    /// </summary>
    public static IDataTemplate FamilyItemTemplate(double width) => new FuncDataTemplate<string>((name, _) =>
    {
        var tb = new TextBlock
        {
            Text = name,
            FontFamily = SafeFontFamily(name),
            FontSize = 13,
            Width = width,
            // 定寬比可用空間寬時，Stretch 會把它置中 → 兩端都被裁、名稱開頭消失；靠左只裁尾端
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        ToolTip.SetTip(tb, name); // 被截掉時懸停看全名
        return tb;
    });

    /// <summary>
    /// 下拉「收起時」選取框的樣板：不定寬、吃滿可用空間、尾端截字 ——
    /// 清單項目的定寬版放進比它窄的選取框會被置中裁切，字型名稱的開頭就看不到了。
    /// </summary>
    public static IDataTemplate SelectionBoxTemplate() => new FuncDataTemplate<string>((name, _) =>
    {
        var tb = new TextBlock
        {
            Text = name,
            FontFamily = SafeFontFamily(name),
            FontSize = 13,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        ToolTip.SetTip(tb, name);
        return tb;
    });
}
