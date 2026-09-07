using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Material.Icons;
using Material.Icons.Avalonia;
using MinePainter.App.Controls;
using MinePainter.App.Services;
using MinePainter.Core.Adjustments;
using MinePainter.Core.AI;
using MinePainter.Core.Effects;
using IEffect = MinePainter.Core.Effects.IEffect;
using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;

namespace MinePainter.App.Views;

public partial class MainWindow
{
    // ---- 檔案 ----

    private async void OnNewClicked(object? sender, RoutedEventArgs e)
    {
        var dialog = new NewDocumentWindow();
        // 剪貼簿有圖就先填它的尺寸（paint.net 的習慣：新增之後直接貼上剛好合身）
        if (Platform.ClipboardImage.TryGetImageSize() is { } clip)
            dialog.SuggestSize(clip.Width, clip.Height, "剪貼簿的影像");
        await dialog.ShowDialog(this);
        if (!dialog.Confirmed) return;

        if (dialog.FastMode)
        {
            // 快速模式：畫布是代理，文件記著真正的輸出解析度（見 Core.Documents.FastMode）
            var proxy = ImageCodec.CreateBlankDocument(dialog.ProxyWidth, dialog.ProxyHeight, dialog.DocBackground, dpi: dialog.Dpi);
            proxy.SetOutputSize(dialog.DocWidth, dialog.DocHeight);
            SetDocument(proxy);
            Toasts.Show($"快速模式：以 {dialog.ProxyWidth} × {dialog.ProxyHeight} 製作，" +
                        $"輸出 {dialog.DocWidth} × {dialog.DocHeight}");
            return;
        }

        SetDocument(ImageCodec.CreateBlankDocument(dialog.DocWidth, dialog.DocHeight, dialog.DocBackground, dpi: dialog.Dpi));
    }

    private async void OnOpenClicked(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "開啟",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("支援的檔案") { Patterns = ["*.mpp", "*.pdn", "*.psd", "*.psb", "*.png", "*.jpg", "*.jpeg", "*.bmp"] },
                new FilePickerFileType("MinePainter 專案") { Patterns = ["*.mpp"] },
                new FilePickerFileType("paint.net 專案") { Patterns = ["*.pdn"] },
                new FilePickerFileType("Photoshop 文件") { Patterns = ["*.psd", "*.psb"] },
                new FilePickerFileType("影像檔") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp"] },
            ],
        });

        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path != null) OpenFile(path);
    }

    /// <summary>把影像檔匯入成目前文件的一層（插在作用中圖層上方）。失敗以 toast 回報，回傳是否成功。</summary>
    private bool ImportLayerFromFile(EditorSession session, string path)
    {
        try
        {
            using var bitmap = ImageCodec.LoadBitmap(path);
            ImageCommands.ImportImageLayer(session, bitmap, Path.GetFileNameWithoutExtension(path));
            return true;
        }
        catch (Exception ex)
        {
            Toasts.Show($"匯入失敗：{Path.GetFileName(path)}（{ex.Message}）");
            return false;
        }
    }

    private async void OpenFile(string path)
    {
        try
        {
            RememberRecentFile(path);
            if (Path.GetExtension(path).Equals(".mpp", StringComparison.OrdinalIgnoreCase))
            {
                var doc = MppFormat.Load(path);
                doc = await AskFastModeOnOpen(doc);
                SetDocument(doc, path);
                WarnAboutMissingFonts(doc, Path.GetFileName(path));
            }
            else if (PdnFormat.IsPdnFile(path))
            {
                OpenPaintDotNetFile(path);
            }
            else if (PsdFormat.IsPsdFile(path))
            {
                await OpenPhotoshopFile(path);
            }
            else
            {
                var image = await AskFastModeOnOpen(ImageCodec.LoadAsDocument(path), "這張圖");
                SetDocument(image, importedName: Path.GetFileName(path));
            }
        }
        catch (Exception ex)
        {
            Title = $"MinePainter — 開啟失敗：{ex.Message}";
        }
    }

    /// <summary>
    /// 開檔時問一次要用哪種解析度模式（兩個方向都問）：
    /// 　• 已經是快速模式的專案 → 繼續用代理畫布，或這次以完整解析度開啟
    /// 　• 一般的大專案／大圖 → 照常開，或改用快速模式（畫布縮到代理級別、輸出仍是原尺寸）
    /// 回傳實際要用的文件（換掉的話舊的會被釋放）。
    /// </summary>
    private async Task<Core.Documents.Document> AskFastModeOnOpen(Core.Documents.Document doc, string what = "這份專案")
    {
        if (doc.IsFastMode)
        {
            var dialog = FastModeOpenDialog.ForFastProject(doc.Width, doc.Height, doc.OutputWidth, doc.OutputHeight);
            await dialog.ShowDialog(this);
            if (dialog.Result == FastModeOpenDialog.Choice.Fast) return doc;

            var width = doc.OutputWidth;
            var height = doc.OutputHeight;
            var full = await ScaleDocumentAsync(doc, width, height, 0, 0, "轉成完整解析度");
            Toasts.Show($"已以完整解析度開啟（{width} × {height}）");
            return full;
        }

        if (!Core.Documents.FastMode.ShouldOffer(doc.Width, doc.Height)) return doc;

        var (proxyW, proxyH) = Core.Documents.FastMode.ProxySize(doc.Width, doc.Height);
        var ask = FastModeOpenDialog.ForLargeDocument(what, doc.Width, doc.Height, proxyW, proxyH);
        await ask.ShowDialog(this);
        if (ask.Result != FastModeOpenDialog.Choice.Fast) return doc;

        var outW = doc.Width;
        var outH = doc.Height;
        var proxy = await ScaleDocumentAsync(doc, proxyW, proxyH, outW, outH, "轉成快速模式");
        Toasts.Show($"快速模式：畫布 {proxyW} × {proxyH}，輸出 {outW} × {outH}（存檔前記得另存新檔）");
        return proxy;
    }

    /// <summary>把整份文件縮放成另一個尺寸（新文件；舊的釋放）。開檔時的模式切換用。</summary>
    private async Task<Core.Documents.Document> ScaleDocumentAsync(Core.Documents.Document doc,
        int width, int height, int outputWidth, int outputHeight, string title)
    {
        Core.Documents.Document? result = null;
        await ProgressDialog.RunAsync(this, title,
            p => result = Core.Documents.OutputRender.CloneScaled(doc, width, height, progress: p));
        result!.SetOutputSize(outputWidth, outputHeight);
        doc.Dispose();
        return result;
    }

    // ---- 最近使用的檔案 ----

    /// <summary>清單長度上限（paint.net 也是 10 個左右）。</summary>
    private const int MaxRecentFiles = 10;

    /// <summary>
    /// 把一個檔案記進「最近使用」（最新在最前面、去重、去掉不存在的）。
    /// 開啟與儲存都會走到這裡 —— 另存新檔之後那個新路徑才是使用者下次要找的。
    /// </summary>
    private void RememberRecentFile(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            return; // 路徑怪到 GetFullPath 都不行：不值得為了這個擋住開檔
        }

        var settings = Services.AppSettings.Instance;
        settings.RecentFiles.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
        settings.RecentFiles.Insert(0, full);
        if (settings.RecentFiles.Count > MaxRecentFiles)
            settings.RecentFiles.RemoveRange(MaxRecentFiles, settings.RecentFiles.Count - MaxRecentFiles);
        settings.Save();
        RefreshRecentFilesMenu();
    }

    /// <summary>
    /// 重建「最近使用的檔案」子選單。檔案被搬走／刪掉就從清單移除 ——
    /// 點下去才發現開不了是最沒用的回饋。
    /// </summary>
    private void RefreshRecentFilesMenu()
    {
        var settings = Services.AppSettings.Instance;
        var alive = settings.RecentFiles.Where(File.Exists).ToList();
        if (alive.Count != settings.RecentFiles.Count)
        {
            settings.RecentFiles = alive;
            settings.Save();
        }

        RecentFilesMenu.Items.Clear();
        RecentFilesMenu.IsEnabled = alive.Count > 0;
        if (alive.Count == 0)
        {
            RecentFilesMenu.Items.Add(new MenuItem { Header = "（沒有記錄）", IsEnabled = false });
            return;
        }

        for (var i = 0; i < alive.Count; i++)
        {
            var path = alive[i];
            // 前 9 個給數字快捷鍵（_1…_9），跟 Windows 的檔案選單一樣好按
            var prefix = i < 9 ? $"_{i + 1}  " : "     ";
            var item = new MenuItem { Header = prefix + Path.GetFileName(path) };
            ToolTip.SetTip(item, path);
            item.Click += (_, _) =>
            {
                if (!File.Exists(path))
                {
                    Toasts.Show($"找不到 {Path.GetFileName(path)}（已從清單移除）");
                    RefreshRecentFilesMenu();
                    return;
                }
                OpenFile(path);
            };
            RecentFilesMenu.Items.Add(item);
        }

        RecentFilesMenu.Items.Add(new Separator());
        var clear = new MenuItem { Header = "清除清單(_C)" };
        clear.Click += (_, _) =>
        {
            settings.RecentFiles.Clear();
            settings.Save();
            RefreshRecentFilesMenu();
        };
        RecentFilesMenu.Items.Add(clear);
    }

    /// <summary>.pdn 只能讀不能寫，所以當成匯入：不記成目前檔案，之後存檔會走「另存為 .mpp」。</summary>
    private void OpenPaintDotNetFile(string path)
    {
        var doc = PdnFormat.Load(path, out var warnings);
        SetDocument(doc, importedName: Path.GetFileName(path));

        Toasts.Show("已匯入 paint.net 專案（儲存時會存成 .mpp）");
        foreach (var warning in warnings.Take(2)) Toasts.Show(warning);
        WarnAboutMissingFonts(doc, Path.GetFileName(path));
    }

    /// <summary>
    /// .psd 同樣只讀不寫，當成匯入。Photoshop 檔常常很大，所以和開影像一樣先問要不要走快速模式。
    /// 匯入時有損的地方（調整圖層略過、混合模式沒對應）最多提示兩則，其餘塞進 Debug 輸出。
    /// </summary>
    private async Task OpenPhotoshopFile(string path)
    {
        var doc = PsdFormat.Load(path, out var warnings);
        doc = await AskFastModeOnOpen(doc, "這份 Photoshop 文件");
        SetDocument(doc, importedName: Path.GetFileName(path));

        Toasts.Show("已匯入 Photoshop 文件（儲存時會存成 .mpp）");
        foreach (var warning in warnings.Take(2)) Toasts.Show(warning);
        if (warnings.Count > 2) Toasts.Show($"另有 {warnings.Count - 2} 處匯入時有調整");
        WarnAboutMissingFonts(doc, Path.GetFileName(path));
    }

    /// <summary>
    /// 專案檔用到的字型這台機器沒裝就跳視窗說明。檔案只記家族名，換一台機器沒裝那支，
    /// Skia 會安靜地換一支畫出來 —— 排版跑掉卻沒有任何提示，所以要主動講。
    /// 對話框在文件已經開好、畫面看得到之後才彈（使用者可以一邊看著那份文件一邊讀）。
    /// </summary>
    private void WarnAboutMissingFonts(MinePainter.Core.Documents.Document doc, string fileName)
    {
        IReadOnlyList<MinePainter.Core.Vectors.MissingFont> missing;
        lock (doc.SyncRoot) missing = MinePainter.Core.Vectors.FontAvailability.MissingIn(doc);
        if (missing.Count == 0) return;

        var projectName = Path.GetFileNameWithoutExtension(fileName);
        Dispatcher.UIThread.Post(async () =>
        {
            if (!IsVisible) return;
            var dialog = new MissingFontsDialog(projectName, missing);
            await dialog.ShowDialog(this);
            if (!dialog.Confirmed || dialog.Replacements.Count == 0) return;

            // 找回那份文件所屬的分頁：對話框開著的時候使用者可能已經切走了
            var tab = _tabs.FirstOrDefault(t => ReferenceEquals(t.Session.Document, doc));
            if (tab == null) return;
            var replaced = VectorCommands.ReplaceFontFamilies(
                doc, tab.Session.History, dialog.Replacements, "替換缺少的字型");
            if (replaced == 0) return;

            doc.NotifyChanged(doc.Bounds);
            _layersContent.Refresh();
            RefreshUiState();
            Toasts.Show($"已替換 {dialog.Replacements.Count} 種字型（{replaced} 段文字）");
        }, DispatcherPriority.Background);
    }

    /// <summary>存檔／匯出對話框的預設檔名：沿用目前檔案，或匯入來源（.pdn／影像）的名字。</summary>
    private string SuggestedName(string fallback) =>
        Path.GetFileNameWithoutExtension(_activeTab?.FilePath ?? _activeTab?.ImportedName) is { Length: > 0 } name
            ? name
            : fallback;

    private async void OnSaveClicked(object? sender, RoutedEventArgs e) => await SaveAsync(saveAs: false);

    private async void OnSaveAsClicked(object? sender, RoutedEventArgs e) => await SaveAsync(saveAs: true);

    /// <summary>存目前作用中的分頁。回傳是否真的存了檔（false = 使用者取消或失敗）。</summary>
    private async Task<bool> SaveAsync(bool saveAs)
    {
        // 以分頁為單位：背景存檔期間就算切到別的分頁，寫檔與旗標更新仍作用在原本那份
        var tab = _activeTab;
        if (tab == null) return false;
        var session = tab.Session;

        CommitCanvasTextEdit();   // 存檔前先把進行中的編輯落地
        session.CommitFloating();

        var path = tab.FilePath;
        if (saveAs || path == null)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "儲存專案",
                DefaultExtension = "mpp",
                SuggestedFileName = SuggestedName("未命名"),
                FileTypeChoices = [new FilePickerFileType("MinePainter 專案") { Patterns = ["*.mpp"] }],
            });
            path = file?.TryGetLocalPath();
            if (path == null) return false;
        }

        try
        {
            // 寫檔丟背景執行緒（快照階段在 Save 內部鎖住文件，之後只讀不可變資料）。
            // 存檔期間使用者可能又畫了東西：完成時不能直接清 dirty，
            // 要看「按下儲存之後」有沒有新變更（快照一定在那之後才拍，此判斷偏保守但安全）。
            var doc = session.Document;
            var changesAtStart = Volatile.Read(ref tab.ChangeCount);
            await ProgressDialog.RunAsync(this, "儲存專案", p => MppFormat.Save(doc, path, p));

            tab.FilePath = path;
            RememberRecentFile(path);
            tab.IsDirty = Volatile.Read(ref tab.ChangeCount) != changesAtStart;
            UpdateTitle();
            UpdateTabVisuals();
            return true;
        }
        catch (Exception ex)
        {
            Title = $"MinePainter — 儲存失敗：{ex.Message}";
            Toasts.Show($"儲存失敗：{ex.Message}");
            LogError("儲存", ex);
            return false;
        }
    }

    /// <summary>
    /// 匯出成別的編輯器的專案檔。.psd：圖層、群組、可編輯文字、圖層樣式、調整圖層盡量保留（見 PsdFormat.Save）；
    /// .pdn：合併成單一圖層（paint.net 沒有這些東西，見 PdnFormat.Save）。寫完把有損的地方講出來。
    /// </summary>
    private async void OnExportProjectClicked(object? sender, RoutedEventArgs e)
    {
        var session = Canvas.Session;
        if (session == null) return;

        CommitCanvasTextEdit();
        session.CommitPendingEdits();

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "匯出為 PSD／PDN",
            DefaultExtension = "psd",
            SuggestedFileName = SuggestedName("匯出"),
            FileTypeChoices =
            [
                new FilePickerFileType("Photoshop 文件") { Patterns = ["*.psd"] },
                new FilePickerFileType("paint.net 專案") { Patterns = ["*.pdn"] },
            ],
        });
        var path = file?.TryGetLocalPath();
        if (path == null) return;

        var isPdn = string.Equals(Path.GetExtension(path), ".pdn", StringComparison.OrdinalIgnoreCase);
        var label = isPdn ? "paint.net 專案" : "Photoshop 檔";
        try
        {
            var doc = session.Document;
            IReadOnlyList<string> warnings = [];
            await ProgressDialog.RunAsync(this, $"匯出{label}", p =>
            {
                if (isPdn) PdnFormat.Save(doc, path, p, out warnings);
                else PsdFormat.Save(doc, path, p, out warnings);
            });
            Toasts.Show(warnings.Count == 0 ? $"已匯出{label}" : $"已匯出{label}，部分內容已轉成像素");
            foreach (var warning in warnings.Take(2)) Toasts.Show(warning);
            if (warnings.Count > 2) Toasts.Show($"另有 {warnings.Count - 2} 處匯出時有調整");
        }
        catch (Exception ex)
        {
            Toasts.Show($"匯出失敗：{ex.Message}");
            LogError($"匯出{label}", ex);
        }
    }

    private async void OnExportClicked(object? sender, RoutedEventArgs e)
    {
        var session = Canvas.Session;
        if (session == null) return;

        CommitCanvasTextEdit();       // 匯出的是合成結果，先把進行中的編輯落地
        session.CommitPendingEdits(); // 浮動內容、變形框等所有進行中編輯一次涵蓋

        var dialog = new ExportWindow(session.Document.OutputWidth, session.Document.OutputHeight);
        await dialog.ShowDialog(this);
        if (!dialog.Confirmed) return;

        // 檔案類型跟著對話框選的格式走，避免「選了 JPEG 卻存成 .png」
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "匯出影像",
            DefaultExtension = dialog.IsJpeg ? "jpg" : "png",
            SuggestedFileName = SuggestedName("匯出"),
            FileTypeChoices = dialog.IsJpeg
                ? [new FilePickerFileType("JPEG") { Patterns = ["*.jpg", "*.jpeg"] }]
                : [new FilePickerFileType("PNG") { Patterns = ["*.png"] }],
        });
        if (file == null) return; // 使用者取消
        var path = file.TryGetLocalPath();
        if (path == null)
        {
            Toasts.Show("匯出失敗：無法取得檔案路徑");
            return;
        }

        try
        {
            var doc = session.Document;
            await ProgressDialog.RunAsync(this, "匯出影像",
                p => MppFormat.Export(doc, path, dialog.Quality, dialog.OutWidth, dialog.OutHeight, p));
            Toasts.Show($"已匯出 {Path.GetFileName(path)}（{dialog.OutWidth} × {dialog.OutHeight}）");
        }
        catch (Exception ex)
        {
            Title = $"MinePainter — 匯出失敗：{ex.Message}";
            Toasts.Show($"匯出失敗：{ex.Message}");
            LogError("匯出", ex);
        }
    }

    /// <summary>
    /// 「複製這張圖片」：把整張畫布的合成結果（＝匯出看到的那張）放進剪貼簿。
    /// 與「編輯 → 複製」不同 —— 那個複製的是選取範圍／作用中圖層。
    /// </summary>
    private async void OnCopyFlattenedClicked(object? sender, RoutedEventArgs e)
    {
        var session = CommitPending();
        if (session == null) return;

        var doc = session.Document;
        SKImage? image = null;
        await ProgressDialog.RunAsync(this, "算出整張圖片",
            p => image = Core.Documents.OutputRender.Render(doc, p));
        using var flattened = image;
        if (image == null)
        {
            Toasts.Show("沒有可複製的內容");
            return;
        }
        Toasts.Show(Platform.ClipboardImage.TrySetImage(image)
            ? $"已複製整張圖片 {image.Width} × {image.Height}"
            : "複製失敗：無法存取剪貼簿");
    }

    /// <summary>
    /// 小工具「YouTube 縮圖預覽」：把目前文件的合成結果塞進一份本機的假 YouTube 頁面，
    /// 用系統預設瀏覽器開起來看縮圖在真實版面裡的樣子（不連網、不上傳）。
    /// </summary>
    private async void OnYouTubePreviewClicked(object? sender, RoutedEventArgs e)
    {
        var session = Canvas.Session;
        if (session == null)
        {
            Toasts.Show("先開一份文件");
            return;
        }

        CommitCanvasTextEdit();       // 預覽的是合成結果，先把進行中的編輯落地
        session.CommitPendingEdits(); // 浮動內容、變形框等所有進行中編輯一次涵蓋

        var dialog = new YouTubePreviewWindow(SuggestedName("我的縮圖"));
        await dialog.ShowDialog(this);
        if (!dialog.Confirmed) return;

        var doc = session.Document;
        var options = dialog.Options;
        try
        {
            // 合成 + base64 內嵌對大圖不算便宜，丟背景執行緒免得視窗卡住
            var path = await Task.Run(() => Gadgets.YouTubeMockup.Render(doc, options));
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            Toasts.Show("已在瀏覽器開啟 YouTube 縮圖預覽");
        }
        catch (Exception ex)
        {
            Toasts.Show("縮圖預覽失敗：" + ex.Message);
            LogError("YouTube 縮圖預覽", ex);
        }
    }

    /// <summary>把例外完整寫進 %APPDATA%\MinePainter\error.log（回報問題用）。</summary>
    private static void LogError(string operation, Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MinePainter");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {operation} 失敗{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private void OnCloseTabClicked(object? sender, RoutedEventArgs e)
    {
        if (_activeTab is { } tab) _ = CloseTabAsync(tab);
    }

    private void OnExitClicked(object? sender, RoutedEventArgs e) => Close();

    // 未存檔提示
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_forceClose || _tabs.All(t => !t.IsDirty))
        {
            // 先掛上關閉旗標：子視窗的退場動畫會 Cancel 掉一次 Closing，
            // 那會連帶中止整個關閉流程（症狀＝要按兩次才關得掉）。
            Controls.WindowAnimator.IsShuttingDown = true;
            SavePanelLayout(withWindowState: true); // 面板還在才問得到位置
            foreach (var (panel, _) in PanelPairs()) panel.AllowClose();
            foreach (var owned in OwnedWindows.ToList()) owned.Close(); // 圖層屬性等臨時視窗
            if (_pendingUpdaterScript is { } script)
            {
                // 更新：程式結束後由 updater 覆蓋 exe 並重啟
                try { Services.UpdateService.Launch(script); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"updater launch failed: {ex.Message}"); }
                _pendingUpdaterScript = null;
            }
            return;
        }

        // 逐一問每個未儲存的分頁（先切過去讓使用者看見在問哪份）；任何一步取消就中止關閉。
        // 期間框架已經把浮窗真的關掉了 —— 自我修復（EnsurePanelsVisible）要先暫停，
        // 不然它會在對話框還開著時就把面板重建回來、搶走焦點。
        e.Cancel = true;
        _closingPrompt = true;
        try
        {
            foreach (var tab in _tabs.Where(t => t.IsDirty).ToList())
            {
                ActivateTab(tab);
                var choice = await ShowUnsavedDialog(tab.Name);
                if (choice == UnsavedChoice.Cancel ||
                    (choice == UnsavedChoice.Save && !await SaveAsync(saveAs: false)))
                {
                    _closingPrompt = false;
                    RecreateFloatingPanels(); // 取消關閉：把框架已經關掉的浮窗接回來
                    return;
                }
                // Discard：略過這份，繼續問下一份
            }
        }
        finally
        {
            _closingPrompt = false;
        }
        _forceClose = true;
        Close();
    }

    private enum UnsavedChoice
    {
        Save,
        Discard,
        Cancel,
    }

    private async Task<UnsavedChoice> ShowUnsavedDialog(string? docName = null)
    {
        var result = UnsavedChoice.Cancel;
        var dialog = new Window
        {
            Title = "未儲存的變更",
            Width = 380,
            Height = 140,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };

        Button Make(string text, UnsavedChoice choice, bool primary = false)
        {
            var b = new Button { Content = text, Padding = new Thickness(14, 6) };
            if (primary) b.Classes.Add("accent");
            b.Click += (_, _) => { result = choice; dialog.Close(); };
            return b;
        }

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = docName != null ? $"「{docName}」有未儲存的變更，要儲存嗎？" : "文件有未儲存的變更，要儲存嗎？",
                    FontSize = 14,
                    TextWrapping = TextWrapping.Wrap,
                },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Children = { Make("儲存", UnsavedChoice.Save, primary: true), Make("不儲存", UnsavedChoice.Discard), Make("取消", UnsavedChoice.Cancel) },
                },
            },
        };

        await dialog.ShowDialog(this);
        return result;
    }
}
