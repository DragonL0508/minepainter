using Avalonia.Platform;
using MinePainter.Core.Vectors;

namespace MinePainter.App.Services;

/// <summary>
/// 內嵌字型（Noto Sans TC，OFL）。英文版 Windows 的中日韓字型屬 Features on Demand，
/// 沒裝中文語言支援的機器上系統一支 CJK 字型都沒有，UI 與文字工具會整片豆腐框；
/// 帶一支進來當最後一關的後備，跟系統語系無關。
///
/// 兩條路都要接：Avalonia 的 UI 文字走 <see cref="Avalonia.Media.Fonts.FontManagerOptions"/>
/// 的 avares 位址（Program.cs），Core 的畫布排版走 <see cref="BundledFont"/>（Skia 那邊看不到
/// avares，得另外把位元組餵進去）。
/// </summary>
public static class EmbeddedFonts
{
    /// <summary>字型檔內的家族名（字型下拉也以這個名字顯示）。</summary>
    public const string FamilyName = "Noto Sans TC";

    /// <summary>Avalonia 用的家族位址；系統沒安裝這支，只能靠這個位址取到。</summary>
    public const string FamilyUri = "avares://MinePainter.App/Assets/Fonts#Noto Sans TC";

    private const string AssetUri = "avares://MinePainter.App/Assets/Fonts/NotoSansTC-Regular.otf";

    /// <summary>把內嵌字型交給 Core（Skia 排版用）。開視窗前呼叫一次。</summary>
    public static void Register()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(AssetUri));
            BundledFont.Register(stream);
        }
        catch
        {
            // 資源不見了也不該擋開機：有系統中文字型的機器照樣正常
        }
    }
}
