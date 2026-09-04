using MinePainter.Core.Documents;
using MinePainter.Core.Layers;
using SkiaSharp;

namespace MinePainter.Core.Vectors;

/// <summary>文件裡用到、但這台機器上沒有的字型。</summary>
/// <param name="Family">字型家族名（存檔時記的那個名字）。</param>
/// <param name="TextCount">用到它的文字物件數。</param>
/// <param name="Sample">其中一段文字（讓使用者看得出是哪裡在用）。</param>
public sealed record MissingFont(string Family, int TextCount, string Sample);

/// <summary>
/// 字型有沒有裝。開專案檔時要先問這件事：檔案只記家族名，
/// 換一台機器沒裝那支字型，Skia 會安靜地換一支畫出來 —— 排版跑掉了卻沒有任何提示。
/// </summary>
public static class FontAvailability
{
    /// <summary>這台機器認得這個家族嗎（系統裝了、或它就是內建的保底字型）。</summary>
    public static bool IsAvailable(string family)
    {
        if (string.IsNullOrWhiteSpace(family)) return true; // 空的＝用預設字型，不算缺
        if (BundledFont.ForFamily(family) != null) return true;
        try
        {
            using var set = SKFontManager.Default.GetFontStyles(family);
            return set.Count > 0;
        }
        catch
        {
            return true; // 查不出來就別誤報
        }
    }

    /// <summary>
    /// 文件裡的文字物件用到、但這台機器沒有的字型（依用到的次數多寡排序）。
    /// 呼叫端須持有 Document.SyncRoot（或在文件還沒公開給別的執行緒之前呼叫，例如剛載入完）。
    /// </summary>
    public static IReadOnlyList<MissingFont> MissingIn(Document doc)
    {
        var used = new Dictionary<string, (int Count, string Sample)>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in doc.Descendants())
        {
            if (node is not RasterLayer layer) continue;
            foreach (var element in layer.Elements)
            {
                if (element is not TextElement text) continue;
                var family = text.FontFamily;
                if (string.IsNullOrWhiteSpace(family)) continue;
                used.TryGetValue(family, out var entry);
                var sample = entry.Sample;
                if (string.IsNullOrEmpty(sample)) sample = FirstLine(text.Text);
                used[family] = (entry.Count + 1, sample);
            }
        }

        var missing = new List<MissingFont>();
        foreach (var (family, entry) in used)
        {
            if (IsAvailable(family)) continue;
            missing.Add(new MissingFont(family, entry.Count, entry.Sample));
        }
        missing.Sort((a, b) => b.TextCount != a.TextCount
            ? b.TextCount.CompareTo(a.TextCount)
            : string.Compare(a.Family, b.Family, StringComparison.CurrentCulture));
        return missing;
    }

    /// <summary>取第一行、去掉頭尾空白，太長就截斷（對話框裡只是給個線索）。</summary>
    private static string FirstLine(string text)
    {
        var line = text.Split('\n')[0].Trim();
        return line.Length <= 24 ? line : line[..24] + "…";
    }
}
