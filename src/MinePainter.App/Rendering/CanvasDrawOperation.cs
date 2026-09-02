using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using MinePainter.Core.Compositing;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using SkiaSharp;

namespace MinePainter.App.Rendering;

/// <summary>
/// 走 ISkiaSharpApiLeaseFeature 直接拿 GPU-backed SKCanvas 上屏合成結果，
/// 並疊畫選取螞蟻線與工具幾何預覽。在 Avalonia render thread 執行。
/// </summary>
public sealed class CanvasDrawOperation : ICustomDrawOperation
{
    private readonly EditorSession _session;
    private readonly Compositor _compositor;
    private readonly int _docWidth;
    private readonly int _docHeight;
    private readonly ViewportTransform _viewport;
    private readonly FrameStats _stats;

    private readonly bool _highlightSelection;
    private readonly bool _showPixelGrid;
    private readonly float _contentFade;

    public CanvasDrawOperation(Rect bounds, EditorSession session, ViewportTransform viewport,
        FrameStats stats, bool showPixelGrid = false, float contentFade = 1f)
    {
        _contentFade = Math.Clamp(contentFade, 0f, 1f);
        _showPixelGrid = showPixelGrid;
        Bounds = bounds;
        _session = session;
        _compositor = session.Compositor;
        _docWidth = session.Document.Width;
        _docHeight = session.Document.Height;
        _viewport = viewport;
        _stats = stats;
        _highlightSelection = session.ActiveTool == session.RectSelect ||
                              session.ActiveTool == session.Lasso ||
                              session.ActiveTool == session.Wand;
    }

    public Rect Bounds { get; }

    // 透明棋盤格（螢幕固定 8px 方格；render thread 專用）
    private static SKPaint? _checkerPaint;

    private static SKPaint CheckerPaint
    {
        get
        {
            if (_checkerPaint != null) return _checkerPaint;
            var bmp = new SKBitmap(new SKImageInfo(16, 16, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (var c = new SKCanvas(bmp))
            {
                c.Clear(SKColors.White);
                using var grey = new SKPaint { Color = new SKColor(0xCC, 0xCC, 0xCC) };
                c.DrawRect(0, 0, 8, 8, grey);
                c.DrawRect(8, 8, 8, 8, grey);
            }
            _checkerPaint = new SKPaint
            {
                Shader = SKShader.CreateBitmap(bmp, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat),
            };
            return _checkerPaint;
        }
    }

    public bool HitTest(Point p) => Bounds.Contains(p);

    public bool Equals(ICustomDrawOperation? other) => false;

    public void Render(ImmediateDrawingContext context)
    {
        var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
        if (leaseFeature is null)
        {
            // 軟體渲染 fallback（WriteableBitmap 路徑）之後里程碑再補。
            return;
        }

        using var lease = leaseFeature.Lease();
        var canvas = lease.SkCanvas;

        _compositor.CollectRetired();
        _stats.OnFrame();

        var drawn = 0;
        var pending = 0;

        canvas.Save();
        canvas.ClipRect(SKRect.Create(0, 0, (float)Bounds.Width, (float)Bounds.Height));
        canvas.Clear(AppTheme.CanvasSurround);
        DrawBackdrop(canvas);

        // 分頁切換的 fade 由 CanvasView.ContentFade 帶進來（不動 Visual.Opacity ——
        // Opacity=0 時 Avalonia 會把整個子樹剔除不畫，畫面直接變窗底黑色「閃一下」；
        // lease.CurrentOpacity 也未必反映祖先的動畫值）。
        // 只淡文件內容（棋盤格/tiles/覆疊），外圍底色與背景圖維持不動，淡出時工作區不會閃黑。
        var opacity = Math.Min(lease.CurrentOpacity, _contentFade);
        var hasAlphaLayer = opacity < 1;
        if (hasAlphaLayer)
        {
            using var alphaPaint = new SKPaint
            {
                Color = SKColors.White.WithAlpha((byte)Math.Clamp(opacity * 255, 0, 255)),
            };
            canvas.SaveLayer(alphaPaint);
        }

        // 透明棋盤格（view 空間、螢幕固定大小；也標示畫布範圍）
        var docViewRect = SKRect.Create(
            (float)_viewport.OffsetX, (float)_viewport.OffsetY,
            (float)(_docWidth * _viewport.Scale), (float)(_docHeight * _viewport.Scale));
        canvas.DrawRect(docViewRect, CheckerPaint);

        canvas.Save();
        canvas.Translate((float)_viewport.OffsetX, (float)_viewport.OffsetY);
        var s = (float)_viewport.Scale;
        canvas.Scale(s, s);

        // 夾到文件範圍：tile 是 256 對齊的，最後一排會超出畫布邊界，
        // 不裁切的話那截「畫布外」也會顯示出來、還能被塗到。
        // 只裁「內容」—— 選取框與把手要畫在畫布外（見下方 DrawSelectionAndPreview）。
        canvas.Save();
        canvas.ClipRect(SKRect.Create(0, 0, _docWidth, _docHeight));

        // 可見 tile 範圍
        var invS = 1.0 / _viewport.Scale;
        var docL = Math.Max(0, (0 - _viewport.OffsetX) * invS);
        var docT = Math.Max(0, (0 - _viewport.OffsetY) * invS);
        var docR = Math.Min(_docWidth, (Bounds.Width - _viewport.OffsetX) * invS);
        var docB = Math.Min(_docHeight, (Bounds.Height - _viewport.OffsetY) * invS);

        if (docR > docL && docB > docT)
        {
            var c0 = Math.Clamp((int)(docL / Tile.Size), 0, _compositor.TileCols - 1);
            var r0 = Math.Clamp((int)(docT / Tile.Size), 0, _compositor.TileRows - 1);
            var c1 = Math.Clamp((int)((docR - 1) / Tile.Size), 0, _compositor.TileCols - 1);
            var r1 = Math.Clamp((int)((docB - 1) / Tile.Size), 0, _compositor.TileRows - 1);

            _compositor.SetVisibleTiles(new SKRectI(c0, r0, c1, r1));

            var quality = QualityFor(_viewport.Scale);
            using var tilePaint = new SKPaint { FilterQuality = quality };

            // 拖曳中被拆下來的整個圖層：合成結果裡沒有它，由這裡補上。
            // 只有轉場中（剛拆下來、或正在交還）才需要逐格判斷歸誰畫；
            // 拖曳穩定期間可見範圍全是乾淨的，整片畫一次就好。
            var visibleRect = new SKRectI(c0 * Tile.Size, r0 * Tile.Size,
                (c1 + 1) * Tile.Size, (r1 + 1) * Tile.Size);
            var layerOverlay = _session.LayerOverlay;
            var overlayInTransition = layerOverlay != null && !_compositor.IsRegionClean(visibleRect);

            for (var cy = r0; cy <= r1; cy++)
            for (var cx = c0; cx <= c1; cx++)
            {
                var idx = new TileIndex(cx, cy);
                if (_compositor.TryGetTile(idx, out var img))
                {
                    if (img != null)
                    {
                        canvas.DrawImage(img, cx * Tile.Size, cy * Tile.Size, tilePaint);
                        drawn++;
                    }
                }
                else
                {
                    pending++;
                }

                if (overlayInTransition && layerOverlay!.ShouldDraw(_compositor.IsTileClean(idx)))
                {
                    var tileRect = idx.ToPixelRect();
                    canvas.Save();
                    canvas.ClipRect(new SKRect(tileRect.Left, tileRect.Top, tileRect.Right, tileRect.Bottom));
                    layerOverlay.Draw(canvas, tileRect, quality);
                    canvas.Restore();
                }
            }

            if (layerOverlay != null && !overlayInTransition && layerOverlay.ShouldDraw(tileIsClean: true))
                layerOverlay.Draw(canvas, visibleRect, quality);
        }

        DrawFloatingOverlay(canvas);
        DrawTransformOverlay(canvas);
        DrawPixelGrid(canvas);
        canvas.Restore();                       // 文件範圍 clip

        // 覆疊（螞蟻線／選取框／把手）不裁切：物件或選取被拉到畫布外時，
        // 把手還是要看得見才拉得回來（Pinta 把「把手在畫布外就不畫」列為 bug #1955）。
        DrawSelectionAndPreview(canvas);
        canvas.Restore();                       // viewport 變換
        if (hasAlphaLayer) canvas.Restore();    // 合成 alpha layer（fade）
        canvas.Restore();                       // 最外層 clip

        _stats.PendingTiles = pending;
    }

    /// <summary>
    /// 變形手勢（縮放/旋轉拖曳）期間的預覽：像素已從合成結果拿掉，
    /// 由這裡以目前矩陣直接畫（見 TransformSession.BeginGesturePreview）——
    /// 拖曳中一格 tile 都不重寫、不重合成，成本與步數無關。
    /// 手勢結束後殘影（HandingOver）會蓋在剛寫入的 High 蓋章上，等合成器追上才收。
    /// </summary>
    private void DrawTransformOverlay(SKCanvas canvas)
    {
        var overlay = _session.Transform?.Overlay;
        if (overlay == null) return;

        using var paint = new SKPaint { FilterQuality = SKFilterQuality.Low, IsAntialias = true };
        var m = overlay.Matrix;
        canvas.Save();
        canvas.Concat(ref m);
        foreach (var (image, src) in overlay.Items)
            canvas.DrawImage(image, src.Left, src.Top, paint);
        canvas.Restore();
    }

    /// <summary>
    /// 畫布外圍的背景圖（使用者自選）：以「填滿」方式鋪滿整個檢視區、置中裁切，
    /// 疊在外圍底色上（預設 10% 不透明度）。畫布本身之後會被棋盤格＋內容蓋掉。
    /// </summary>
    private void DrawBackdrop(SKCanvas canvas)
    {
        var image = CanvasBackdrop.Image;
        var alpha = CanvasBackdrop.Alpha;
        if (image == null || alpha == 0) return;

        var bw = (float)Bounds.Width;
        var bh = (float)Bounds.Height;
        if (bw <= 0 || bh <= 0) return;

        var scale = Math.Max(bw / image.Width, bh / image.Height);
        var w = image.Width * scale;
        var h = image.Height * scale;
        var dst = SKRect.Create((bw - w) / 2, (bh - h) / 2, w, h);

        using var paint = new SKPaint
        {
            Color = SKColors.White.WithAlpha(alpha),
            FilterQuality = SKFilterQuality.Medium,
        };
        canvas.DrawImage(image, dst, paint);
    }

    /// <summary>
    /// 拖曳中的浮動選取內容：直接畫在合成結果上，不進合成器。
    ///
    /// 合成器逐格重畫的成本正比於選取面積 —— 大片內容一次移動要重合成數十格，
    /// 跟不上滑鼠就會看到內容一格一格追上來。這裡改成一張圖上屏，成本與面積無關。
    /// 只有在「結果完全相同」時 <see cref="EditorSession.FloatingOverlay"/> 才非 null
    /// （判準見 FloatingSelection.CanOverlay），否則仍由合成器畫、這裡什麼都不做。
    ///
    /// Ghost = 已經落地／取消、但合成器還沒重畫完的殘影，少了它畫面會閃一下。
    /// </summary>
    private void DrawFloatingOverlay(SKCanvas canvas)
    {
        // 縮放取樣與 tile 同調（見 QualityFor）
        var quality = QualityFor(_viewport.Scale);

        if (_session.Ghost is { } ghost)
        {
            using var paint = new SKPaint { FilterQuality = quality };
            canvas.DrawImage(ghost.Image, ghost.Rect, paint);
        }

        // 拖曳中的文字物件：一張圖跟著滑鼠走（原件已隱藏）
        if (_session.ElementOverlay is { } element)
        {
            using var paint = new SKPaint { FilterQuality = quality };
            canvas.DrawImage(element.Image, element.CurrentRect, paint);
        }

        if (_session.FloatingOverlay is not { } floating) return;
        var rect = floating.TargetRect; // 只讀一次：UI thread 正在改它
        var scaled = rect.Width != floating.PixelSize.Width ||
                     rect.Height != floating.PixelSize.Height; // 續接時像素是原始那份，比像素不比 SourceBounds
        using (var paint = new SKPaint
               {
                   // 縮放中的預覽用 Low：High+AA 每格要 1.5ms，落地時才用得起
                   FilterQuality = scaled ? SKFilterQuality.Low : quality,
                   IsAntialias = scaled,
               })
        {
            canvas.DrawImage(floating.Pixels, rect, paint);
        }
    }

    /// <summary>在 doc 座標系（canvas 已套 viewport 變換）畫螞蟻線與工具預覽。</summary>
    /// <summary>
    /// 上屏取樣：縮小 → Medium（linear+mipmap）；整數倍放大 → nearest（像素編輯要看到硬邊）；
    /// 非整數倍放大（133%、150%…）→ Low（bilinear），否則抗鋸齒邊緣會被拉成忽粗忽細的階梯。
    /// </summary>
    private static SKFilterQuality QualityFor(double scale)
    {
        if (scale < 1) return SKFilterQuality.Medium;
        var isInteger = Math.Abs(scale - Math.Round(scale)) < 0.001;
        return isInteger ? SKFilterQuality.None : SKFilterQuality.Low;
    }

    /// <summary>
    /// 像素格線（對像素創作是核心功能）。線寬用 1/zoom 讓螢幕上恆為 1px，
    /// 並用同尺度的虛線讓格線夠淡不蓋住內容。格線間距小於 5 螢幕像素時不畫。
    /// </summary>
    private void DrawPixelGrid(SKCanvas canvas)
    {
        if (!_showPixelGrid) return;
        var scale = _viewport.Scale;
        if (scale < 5) return; // 螢幕間距不足 5px 就沒有意義

        var screenPx = (float)(1.0 / scale);
        using var dash = SKPathEffect.CreateDash([screenPx, screenPx], 0);
        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = screenPx,
            Color = new SKColor(0, 0, 0, 0x80),
            PathEffect = dash,
        };

        // 只畫可見範圍內的格線
        var invS = 1.0 / scale;
        var l = Math.Max(0, (int)((0 - _viewport.OffsetX) * invS));
        var t = Math.Max(0, (int)((0 - _viewport.OffsetY) * invS));
        var r = Math.Min(_docWidth, (int)Math.Ceiling((Bounds.Width - _viewport.OffsetX) * invS));
        var b = Math.Min(_docHeight, (int)Math.Ceiling((Bounds.Height - _viewport.OffsetY) * invS));
        if (r <= l || b <= t) return;

        for (var y = t; y <= b; y++) canvas.DrawLine(l, y, r, y, paint);
        for (var x = l; x <= r; x++) canvas.DrawLine(x, t, x, b, paint);
    }

    private void DrawSelectionAndPreview(SKCanvas canvas)
    {
        var screenPx = (float)(1.0 / _viewport.Scale); // 螢幕 1px 對應的 doc 長度
        var dash = 6f * screenPx;
        var phase = Environment.TickCount % 1000 / 1000f * dash * 2;

        // 選取螞蟻線（白底黑蟻雙層，縮放下維持螢幕寬度）。
        // 浮動中時用變換過的輪廓 —— 選取框跟著內容一起走。
        using var floatingOutline = _session.Floating?.GetTransformedOutline();
        var outline = floatingOutline ?? _session.Selection?.OutlinePath;
        if (outline != null)
        {
            // 選取類工具下把選取區填淡藍，讓「正在動框」和「正在動像素」一眼可辨（借鏡 Pinta）
            if (_highlightSelection && floatingOutline == null)
            {
                using var fill = new SKPaint
                {
                    Style = SKPaintStyle.Fill,
                    Color = new SKColor(0xB3, 0xCC, 0xE6, 0x33),
                    IsAntialias = true,
                };
                canvas.DrawPath(outline, fill);
            }

            using var white = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = screenPx,
                Color = SKColors.White,
                IsAntialias = true,
            };
            canvas.DrawPath(outline, white);

            using var dashEffect = SKPathEffect.CreateDash([dash, dash], phase);
            using var black = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = screenPx,
                Color = SKColors.Black,
                PathEffect = dashEffect,
                IsAntialias = true,
            };
            canvas.DrawPath(outline, black);
        }

        // 向量元素選取把手（變形 session 旋轉時整個框跟著轉）
        if (_session.SelectionHandles is { } hr)
        {
            var frameRotation = _session.SelectionHandlesRotation;
            var rotated = Math.Abs(frameRotation) > 0.01f;
            if (rotated)
            {
                canvas.Save();
                canvas.RotateDegrees(frameRotation, hr.MidX, hr.MidY);
            }

            using var frame = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = screenPx,
                Color = new SKColor(0x2A, 0x9D, 0xF4),
                IsAntialias = true,
            };
            canvas.DrawRect(hr, frame);

            using var handleFill = new SKPaint { Color = SKColors.White, IsAntialias = true };
            using var handleStroke = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = screenPx,
                Color = new SKColor(0x2A, 0x9D, 0xF4),
                IsAntialias = true,
            };
            var hs = 4f * screenPx;
            Span<SKPoint> corners =
            [
                new(hr.Left, hr.Top), new(hr.Right, hr.Top),
                new(hr.Right, hr.Bottom), new(hr.Left, hr.Bottom),
            ];
            foreach (var c in corners)
            {
                var box = SKRect.Create(c.X - hs, c.Y - hs, hs * 2, hs * 2);
                canvas.DrawRect(box, handleFill);
                canvas.DrawRect(box, handleStroke);
            }

            if (rotated) canvas.Restore();
        }

        // 對齊模式吸附中的導線（畫布中線/邊；吸到哪條畫哪條）
        if (_session.SnapGuides is { } snapGuides)
        {
            var doc = _session.Document;
            using var guidePaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = screenPx,
                Color = new SKColor(0x2A, 0x9D, 0xF4, 0xD0),
                IsAntialias = true,
            };
            if (snapGuides.X is { } gx) canvas.DrawLine(gx, 0, gx, doc.Height, guidePaint);
            if (snapGuides.Y is { } gy) canvas.DrawLine(0, gy, doc.Width, gy, guidePaint);
        }

        // 工具幾何預覽：形狀工具用元素本身真正渲染（所見即所得），其餘畫折線（選取框/套索軌跡）
        var preview = _session.Preview;
        if (preview?.Element is { } element)
        {
            var docSize = _session.Document;
            canvas.Save();
            canvas.ClipRect(SKRect.Create(0, 0, docSize.Width, docSize.Height));
            if (_session.Selection?.OutlinePath is { } clip) canvas.ClipPath(clip, antialias: true);
            element.Render(canvas);
            canvas.Restore();
        }
        else if (preview is { Points.Count: > 1 })
        {
            using var path = new SKPath();
            path.MoveTo(preview.Points[0]);
            for (var i = 1; i < preview.Points.Count; i++) path.LineTo(preview.Points[i]);
            if (preview.Closed) path.Close();

            using var dashEffect = SKPathEffect.CreateDash([dash, dash], phase);
            using var paint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = screenPx,
                Color = new SKColor(0x2A, 0x9D, 0xF4),
                PathEffect = dashEffect,
                IsAntialias = true,
            };
            canvas.DrawPath(path, paint);
        }
    }

    public void Dispose()
    {
    }
}
