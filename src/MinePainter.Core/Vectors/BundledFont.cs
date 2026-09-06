using SkiaSharp;

namespace MinePainter.Core.Vectors;

/// <summary>
/// 隨程式一起帶的保底字型。英文版 Windows 可能整台沒有中日韓字型（Microsoft JhengHei
/// 這類在 Windows 10/11 屬 Features on Demand，沒裝中文語言支援就不存在），
/// <see cref="SKFontManager.MatchCharacter(string, SKFontStyle, string[], int)"/> 因此找不到
/// 任何後備字面，中文會整片畫成 .notdef 豆腐框。排版的最後一關改用這支。
///
/// App 啟動時（<c>App.Initialize</c>）把內嵌的 Noto Sans TC 交進來；沒註冊時全部行為不變，
/// Core 的測試與非 App 的使用端不受影響。
/// </summary>
public static class BundledFont
{
    private static SKTypeface? _typeface;

    /// <summary>保底字面（尚未註冊時為 null）。全程共用一份，呼叫端不可 Dispose。</summary>
    public static SKTypeface? Typeface => _typeface;

    /// <summary>保底字型的家族名（未註冊時為空字串）。</summary>
    public static string FamilyName => _typeface?.FamilyName ?? "";

    /// <summary>註冊字型檔內容（只吃第一次；讀壞了就當作沒有保底字型）。</summary>
    public static void Register(Stream stream)
    {
        if (_typeface != null) return;
        try
        {
            using var data = SKData.Create(stream);
            _typeface = SKTypeface.FromData(data);
        }
        catch
        {
            // 資源缺少或字型檔壞掉：維持沒有保底字型的行為
        }
    }

    /// <summary>要求的家族就是保底字型時給它（系統沒安裝這支，只能從內嵌的拿）。</summary>
    public static SKTypeface? ForFamily(string family) =>
        _typeface != null && string.Equals(family, _typeface.FamilyName, StringComparison.OrdinalIgnoreCase)
            ? _typeface
            : null;

    /// <summary>
    /// 解析某個家族＋字重的字面：**系統裝了就用系統的**（才有 Bold／Black 等各種字重），
    /// 系統沒有這支才退回內嵌的那份。
    ///
    /// 不能反過來先問內嵌字型 —— 內嵌的只有 Regular 一個字重，家族名一撞就把整個家族接走，
    /// 選 Bold／Black 也還是畫 Regular（使用者 2026-09-04 回報「只有 Noto Sans TC 選不了字重」）。
    /// 也不能只靠 <see cref="SKTypeface.FromFamilyName(string, SKFontStyle)"/>：家族不存在時
    /// Skia 不回 null，而是悄悄給一支預設字面，那樣內嵌的保底字型永遠輪不到。
    /// </summary>
    public static SKTypeface? Resolve(string family, SKFontStyle style)
    {
        // 程式跑著時才裝的字型：系統的字型管理器看不到，只有我們自己從檔案載入的那份（ExtraFonts）認得
        if (ExtraFonts.Resolve(family, style) is { } extra) return extra;
        var system = SKTypeface.FromFamilyName(family, style);
        if (system != null &&
            string.Equals(system.FamilyName, family, StringComparison.OrdinalIgnoreCase))
        {
            return system;
        }
        system?.Dispose();
        return ForFamily(family);
    }

    /// <summary>保底字面含這個碼位就給它，否則 null。</summary>
    public static SKTypeface? Match(int codepoint) =>
        _typeface != null && _typeface.ContainsGlyph(codepoint) ? _typeface : null;
}
