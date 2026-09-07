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
    // ---- 編輯 ----

    private void OnUndoClicked(object? sender, RoutedEventArgs e)
    {
        Canvas.Session?.Undo();
        RefreshUiState();
    }

    private void OnRedoClicked(object? sender, RoutedEventArgs e)
    {
        Canvas.Session?.Redo();
        RefreshUiState();
    }

    private void OnDeselectClicked(object? sender, RoutedEventArgs e)
    {
        if (Canvas.Session is { Selection: not null } s)
        {
            s.CommitFloating();
            SelectionCommands.SetSelection(s, null, "取消選取");
        }
        RefreshUiState();
    }

    /// <summary>
    /// 所有選單指令的共同前置：把進行中的編輯落地。
    /// Pinta 每一個 handler 開頭都做這件事，漏掉是這類功能最常見的 bug 來源。
    /// （undo/redo/歷史跳轉走 EditorSession.Undo/Redo/JumpTo，它們內部會做同一件事。）
    /// </summary>
    private EditorSession? CommitPending()
    {
        var session = Canvas.Session;
        if (session == null) return null;
        session.CommitPendingEdits();
        return session;
    }

    private void RunCommand(Action<EditorSession> command)
    {
        var session = CommitPending();
        if (session == null) return;
        command(session);
        _layersContent.Refresh();
        RefreshUiState();
    }

    // ---- 編輯：剪貼簿 ----

    private async void OnCopyClicked(object? sender, RoutedEventArgs e)
    {
        var session = CommitPending();
        if (session == null) return;

        // 複製要的是全解析度的樣子：畫面上可能是降解析度的預覽，這裡會整層重算
        // （4K、一堆效果的圖層要好幾秒），所以丟背景跑並在超過 150ms 時顯示進度
        SKImage? image = null;
        SKPointI origin = default;
        await ProgressDialog.RunAsync(this, "複製影像", _ => image = session.CopyToImage(out origin));
        using var copied = image;
        if (image == null)
        {
            Toasts.Show("沒有可複製的內容");
            return;
        }
        Toasts.Show(Platform.ClipboardImage.TrySetImage(image, origin)
            ? $"已複製 {image.Width} × {image.Height}"
            : "複製失敗：無法存取剪貼簿");
    }

    private async void OnCutClicked(object? sender, RoutedEventArgs e)
    {
        var session = CommitPending();
        if (session == null) return;

        SKImage? image = null;
        SKPointI origin = default;
        await ProgressDialog.RunAsync(this, "剪下影像", _ => image = session.CopyToImage(out origin));
        using var cut = image;
        if (image == null)
        {
            Toasts.Show("沒有可剪下的內容");
            return;
        }
        if (!Platform.ClipboardImage.TrySetImage(image, origin))
        {
            Toasts.Show("剪下失敗：無法存取剪貼簿");
            return;
        }

        // 剪下 = 複製 + 挖掉。挖不掉的（文字圖層沒有像素、群組不是繪製對象）就只是複製，
        // 不能報「已剪下」—— 內容其實還在。
        if (session.Document.ActiveLayer is not RasterLayer { IsTextLayer: false })
        {
            Toasts.Show("已複製；這個圖層的內容不能剪下（文字要先平面化、群組要選裡面的圖層）");
            return;
        }
        var hadSelection = session.Selection is { IsEmpty: false };
        RunCommand(EditCommands.EraseSelection);
        Toasts.Show(hadSelection ? "已剪下選取範圍" : "已剪下整個圖層");
    }

    private async void OnPasteClicked(object? sender, RoutedEventArgs e)
    {
        var session = CommitPending();
        if (session == null) return;

        var image = Platform.ClipboardImage.TryGetImage();
        if (image == null)
        {
            // 剪貼簿裡是檔案（檔案總管 Ctrl+C）：一個影像檔＝貼成浮動內容；多個＝各自成一層
            var files = Platform.ClipboardImage.TryGetFilePaths()
                .Where(p => File.Exists(p) && HasExtension(p, ImageExtensions)).ToList();
            if (files.Count == 0)
            {
                Toasts.Show("剪貼簿裡沒有影像或影像檔");
                return;
            }
            if (files.Count > 1)
            {
                var imported = files.Count(f => ImportLayerFromFile(session, f));
                if (imported > 0) Toasts.Show($"已把 {imported} 個影像檔各自貼成一層");
                _layersContent.Refresh();
                RefreshUiState();
                return;
            }
            try
            {
                using var bitmap = ImageCodec.LoadBitmap(files[0]);
                image = SKImage.FromBitmap(bitmap);
            }
            catch (Exception ex)
            {
                Toasts.Show($"無法讀取 {Path.GetFileName(files[0])}（{ex.Message}）");
                return;
            }
            if (image == null) return;
        }

        // 超出畫布：問要延展還是維持（paint.net 的行為）。快速模式下比的是縮到代理之後的尺寸
        var doc = session.Document;
        var (pastedW, pastedH) = session.PastedSize(image.Width, image.Height);
        if (pastedW > doc.Width || pastedH > doc.Height)
        {
            var dialog = new PasteSizeDialog(
                new SKSizeI(pastedW, pastedH), new SKSizeI(doc.Width, doc.Height));
            await dialog.ShowDialog(this);

            switch (dialog.Result)
            {
                case PasteSizeDialog.Choice.Cancel:
                    image.Dispose();
                    return;
                case PasteSizeDialog.Choice.ExpandCanvas:
                    DocumentCommands.ResizeCanvas(session,
                        Math.Max(doc.Width, pastedW), Math.Max(doc.Height, pastedH), "延展畫布（貼上）");
                    Canvas.ZoomToFit();
                    break;
            }
        }

        if (session.PasteImage(image, PastePosition(session, pastedW, pastedH)))
        {
            SelectTool("move"); // 貼上後直接可拖曳（paint.net 行為）
            Toasts.Show("已貼上（可拖曳移動，Enter 套用、Esc 取消）");
        }
        _layersContent.Refresh();
        RefreshUiState();
    }

    /// <summary>
    /// 貼上位置：本程式複製的內容貼回原座標（換圖層、換文件都一樣，位置不會被重置），
    /// 外來影像則放在目前可視範圍的左上角。兩者都夾到「整張影像盡量放得進畫布」的範圍。
    /// </summary>
    private SKPointI PastePosition(EditorSession session, int width, int height)
    {
        var doc = session.Document;
        var topLeft = Platform.ClipboardImage.TryGetCopyOrigin(width, height) is { } copyOrigin
            ? new SKPoint(copyOrigin.X, copyOrigin.Y)
            : Canvas.ViewToDoc(new Point(0, 0));
        var x = Math.Clamp((int)Math.Round(topLeft.X), 0, Math.Max(0, doc.Width - width));
        var y = Math.Clamp((int)Math.Round(topLeft.Y), 0, Math.Max(0, doc.Height - height));
        return new SKPointI(x, y);
    }

    // ---- 編輯：選取 ----

    private void OnSelectAllClicked(object? sender, RoutedEventArgs e) =>
        RunCommand(EditCommands.SelectAll);

    private void OnInvertSelectionClicked(object? sender, RoutedEventArgs e) =>
        RunCommand(EditCommands.InvertSelection);

    private void OnEraseSelectionClicked(object? sender, RoutedEventArgs e) =>
        RunCommand(EditCommands.EraseSelection);

    private void OnFillSelectionClicked(object? sender, RoutedEventArgs e) =>
        RunCommand(EditCommands.FillSelection);
}
