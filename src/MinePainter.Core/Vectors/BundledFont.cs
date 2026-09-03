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

    /// <summary>保底字面含這個碼位就給它，否則 null。</summary>
    public static SKTypeface? Match(int codepoint) =>
        _typeface != null && _typeface.ContainsGlyph(codepoint) ? _typeface : null;
}
