using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.VisualTree;
using MinePainter.App.Controls;
using Xunit;

namespace MinePainter.App.Tests;

/// <summary>
/// 彈出層畫在哪、置不置頂。
///
/// 浮動面板是主視窗的 owned window，永遠在主視窗之上。所以：
/// 主選單必須是**原生 popup**（畫在 overlay 層＝畫在主視窗裡面，一定被面板蓋住）**且置頂**
/// （不置頂的話，面板被點過之後 z-order 就在它前面）。兩個條件缺一不可 ——
/// 第一次修只補了置頂，畫面上完全沒有改善。
/// 其他彈出層維持 overlay 層（省下每次開 popup 建 HWND＋GPU surface 的成本）。
/// </summary>
public class PopupTopmostTests
{
    private static Popup? PopupIn(Visual root) => root.GetVisualDescendants().OfType<Popup>().FirstOrDefault();

    /// <summary>主視窗那份 Window.Styles（實際出貨的檔案，不是測試自己抄一份）。</summary>
    private static StyleInclude MainWindowPopupStyles() =>
        new(new Uri("avares://MinePainter.App/"))
        {
            Source = new Uri("avares://MinePainter.App/Styles/MainWindowPopups.axaml"),
        };

    private static (MenuItem File, ComboBox Combo) BuildWindow(bool withMainWindowStyles)
    {
        var file = new MenuItem { Header = "檔案" };
        file.Items.Add(new MenuItem { Header = "開啟" });
        var menu = new ClickSubmenuMenu();
        menu.Items.Add(file);

        var combo = new ComboBox();
        combo.Items.Add("一");

        var window = new Window
        {
            Width = 400,
            Height = 300,
            Content = new StackPanel { Children = { menu, combo } },
        };
        if (withMainWindowStyles) window.Styles.Add(MainWindowPopupStyles());
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        file.ApplyTemplate();
        combo.ApplyTemplate();
        return (file, combo);
    }

    [AvaloniaFact]
    public void 主選單用原生_popup_而不是_overlay_層()
    {
        var (file, _) = BuildWindow(withMainWindowStyles: true);
        var popup = PopupIn(file);
        Assert.NotNull(popup);
        Assert.False(popup!.ShouldUseOverlayLayer,
            "主選單畫在主視窗的 overlay 層裡，就一定會被浮動面板蓋住");
    }

    [AvaloniaFact]
    public void 下拉與按鈕的_flyout_也是原生_popup()
    {
        // 使用者 2026-09-06 回報「下拉式選單又被浮窗擋到」：overlay 層畫在主視窗裡，浮動面板一定蓋住它
        var (_, combo) = BuildWindow(withMainWindowStyles: true);
        var popup = PopupIn(combo);
        Assert.NotNull(popup);
        Assert.False(popup!.ShouldUseOverlayLayer, "工具列的下拉畫在 overlay 層就會被浮動面板蓋住");

        var button = new Button { Content = "▾" };
        var window = new Window { Width = 200, Height = 100, Content = button };
        window.Styles.Add(MainWindowPopupStyles());
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var flyoutPopup = new Popup();
        ((ISetLogicalParent)flyoutPopup).SetParent(button);
        Assert.False(flyoutPopup.ShouldUseOverlayLayer, "按鈕的 flyout（ClickSubmenuMenuFlyout）也會被浮動面板蓋住");
        window.Close();
    }

    [AvaloniaFact]
    public void 開在原生_popup_裡的_popup_不能走_overlay_層()
    {
        // 下拉清單項目的工具提示：外層下拉已是原生 popup，PopupRoot 沒有 overlay 層，走 overlay 就當掉
        var item = new TextBlock { Text = "item" };
        var outer = new Popup { Child = item };
        var host = new Button { Content = "host" };
        var window = new Window { Width = 200, Height = 100, Content = host };
        window.Styles.Add(MainWindowPopupStyles());
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        ((ISetLogicalParent)outer).SetParent(host);
        var tip = new Popup();
        ((ISetLogicalParent)tip).SetParent(item);
        Assert.False(tip.ShouldUseOverlayLayer, "原生 popup 裡的工具提示走 overlay 層＝滑過去就當掉");
        window.Close();
    }

    [AvaloniaFact]
    public void 一般控制項的工具提示維持_overlay_層()
    {
        // 滑過就開的工具提示很多，每次都開原生 popup 會卡；不在按鈕上的維持 overlay 層
        var text = new TextBlock { Text = "x" };
        var window = new Window { Width = 200, Height = 100, Content = text };
        window.Styles.Add(MainWindowPopupStyles());
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var tip = new Popup();
        ((ISetLogicalParent)tip).SetParent(text);
        Assert.True(tip.ShouldUseOverlayLayer);
        window.Close();
    }

    /// <summary>
    /// 主選單是原生 popup，而 PopupRoot 的 VisualLayerManager 不提供 overlay 層。
    /// 工具提示開啟時會把自己的 popup 掛在目標控制項底下（邏輯父＝那個 MenuItem），
    /// 要是它也被逼去用 overlay 層，Popup.Open() 就會丟
    /// InvalidOperationException("Unable to create IPopupImpl and no overlay layer is found")
    /// —— 滑鼠移到有工具提示的選單項目上、還沒點，整個 app 就沒了（使用者 2026-09-04 回報）。
    /// </summary>
    [AvaloniaFact]
    public void 選單項目的工具提示不能走_overlay_層()
    {
        var (file, _) = BuildWindow(withMainWindowStyles: true);
        // ToolTip.Open() 就是這樣掛 popup 的
        var tip = new Popup();
        ((ISetLogicalParent)tip).SetParent(file);
        Assert.False(tip.ShouldUseOverlayLayer,
            "選單項目的工具提示走 overlay 層＝滑過去就當掉（原生選單的 PopupRoot 沒有 overlay 層）");
    }

    [AvaloniaFact]
    public void 彈出層一律置頂()
    {
        // 置頂來自 App 層的 Styles/Popups.axaml（TestApp 有掛）
        var (file, combo) = BuildWindow(withMainWindowStyles: false);
        Assert.True(PopupIn(file)?.Topmost, "選單的 popup 沒置頂：面板被點過之後就會蓋住它");
        Assert.True(PopupIn(combo)?.Topmost);
    }
}
