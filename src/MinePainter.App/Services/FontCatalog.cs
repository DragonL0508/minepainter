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

    /// <summary>已安裝的字型家族（去重、依語系排序）。</summary>
    public static string[] Families => _families ??= SKFontManager.Default.FontFamilies
        .Distinct()
        .OrderBy(f => f, StringComparer.CurrentCulture)
        .ToArray();

    /// <summary>
    /// 家族可用的直立字重（斜體交給 I 鈕），依字重排序；列不出來時退回單一 Regular 400。
    /// </summary>
    public static FontStyleOption[] StylesFor(string family)
    {
        var options = new List<FontStyleOption>();
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
        try
        {
            var family = new FontFamily(name);
            _ = new Typeface(family).GlyphTypeface;
            return family;
        }
        catch
        {
            return FontFamily.Default;
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
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        ToolTip.SetTip(tb, name); // 被截掉時懸停看全名
        return tb;
    });
}
