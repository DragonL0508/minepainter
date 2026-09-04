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
    private readonly GpuLayerRenderer _gpuRenderer;

    private readonly bool _highlightSelection;
    private readonly bool _showPixelGrid;
    private readonly float _contentFade;

    private readonly bool _smoothZoom;

    public CanvasDrawOperation(Rect bounds, EditorSession session, ViewportTransform viewport,
        FrameStats stats, GpuLayerRenderer gpuRenderer, bool showPixelGrid = false, float contentFade = 1f,
        bool smoothZoom = false)
    {
        _gpuRenderer = gpuRenderer;
        _smoothZoom = smoothZoom;
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
                              session.ActiveTool == session.EllipseSelect ||
                              session.ActiveTool == session.Lasso ||
                              session.ActiveTool == session.Wand;
        _showPenPath = session.ActiveTool == session.Pen;
    }

    private readonly bool _showPenPath;

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
        Compositor.CollectGlobalRetired(); // 背景分頁交出來的影像（它們自己沒有幀可以收）
        _stats.OnFrame();
        if (TextBench.Enabled) TextBench.Run(canvas);

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

        // GPU 路徑：直接走圖層樹，效果交給 Skia 濾鏡。處理不了就 false，照舊走下面的 tile。
        var gpuDrew = false;
        if (docR > docL && docB > docT)
        {
            var visible = new SKRectI((int)docL, (int)docT, (int)Math.Ceiling(docR), (int)Math.Ceiling(docB));
            lock (_session.Document.SyncRoot)
            {
                gpuDrew = _gpuRenderer.TryDraw(canvas, _session, visible);
            }
        }

        if (!gpuDrew && docR > docL && docB > docT)
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

        DrawFloatingOverlay(canvas, gpuDrew);
        DrawTransformOverlay(canvas, gpuDrew);
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
    private void DrawTransformOverlay(SKCanvas canvas, bool gpuDrew)
    {
        var overlay = _session.Transform?.Overlay;
        if (overlay == null) return;
        // GPU 路徑已經照層序把手勢中的像素畫進去了（交接中的殘影仍由這裡補，那時層裡已是蓋章後的內容）
        if (gpuDrew && !overlay.HandingOver) return;

        var m = overlay.Matrix;
        if (overlay.Warp is { } warp)
        {
            // 彎曲：矩陣之後再套貝茲網格（三角網格貼圖，拖曳中同樣只換網格不重合成）
            foreach (var (_, image, src) in overlay.Items)
                warp.Draw(canvas, image, src, m, SKFilterQuality.Low);
            return;
        }

        using var paint = new SKPaint { FilterQuality = SKFilterQuality.Low, IsAntialias = true };
        canvas.Save();
        canvas.Concat(ref m);
        foreach (var (_, image, src) in overlay.Items)
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
    private void DrawFloatingOverlay(SKCanvas canvas, bool gpuDrew)
    {
        // 縮放取樣與 tile 同調（見 QualityFor）
        var quality = QualityFor(_viewport.Scale);

        // 殘影仍然要畫：效果翻不成 GPU 濾鏡的圖層，畫面上那份效果還是 CPU 快取算的，
        // 落地後到重算完之間有個空窗，少了殘影就會閃一下（物件先變成沒有效果的樣子）。
        // 效果整串都交給 GPU 的那條路根本不會產生殘影（沒有快照），所以不會重疊。
        if (_session.Ghost is { } ghost)
        {
            using var paint = new SKPaint
            {
                FilterQuality = ghost.Rotation != 0 ? SKFilterQuality.Low : quality,
                IsAntialias = ghost.Rotation != 0,
            };
            if (ghost.Rotation != 0)
            {
                canvas.Save();
                canvas.RotateDegrees(ghost.Rotation, ghost.Rect.MidX, ghost.Rect.MidY);
            }
            canvas.DrawImage(ghost.Image, ghost.Rect, paint);
            if (ghost.Rotation != 0) canvas.Restore();
        }

        // 手勢中的文字物件：一張圖跟著滑鼠走／轉／縮（原件已隱藏）。
        // GPU 路徑會直接把原件套上手勢變換畫出來（效果即時算），這裡就不能再貼一次快照 ——
        // 不然畫面上會同時有兩份文字。
        if (!gpuDrew && _session.ElementOverlay is { Image: not null } element)
        {
            var elementRect = element.CurrentRect; // 只讀一次：UI thread 正在改它
            var rotation = element.Rotation;
            // 太大的物件覆疊圖是降解析度存的（見 EditorSession.OverlayScale），
            // 放大回原本的框時要平滑取樣，不然糊之外還會有硬邊格子
            var reduced = element.Image.Width < element.Bounds.Width;
            var transformed = rotation != 0 || reduced ||
                              elementRect.Width != element.Bounds.Width ||
                              elementRect.Height != element.Bounds.Height;
            using var paint = new SKPaint
            {
                // 縮放／旋轉中的預覽用 Low（放開後合成器會用完整品質重畫）
                FilterQuality = transformed ? SKFilterQuality.Low : quality,
                IsAntialias = transformed,
            };
            if (rotation != 0)
            {
                canvas.Save();
                canvas.RotateDegrees(rotation, elementRect.MidX, elementRect.MidY);
            }
            canvas.DrawImage(element.Image!, elementRect, paint);
            if (rotation != 0) canvas.Restore();
        }

        // 浮動內容在 GPU 路徑是照層序畫進去的（見 GpuLayerRenderer.DrawRaster）
        if (gpuDrew || _session.FloatingOverlay is not { } floating) return;
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
    /// 上屏取樣：縮小 → Medium（linear+mipmap）；放大（含非整數倍）→ nearest。
    /// 像素創作是核心：64×64 的圖放到 12.3 倍時用 bilinear 會整張糊掉，硬邊比階梯抖動重要（與 paint.net 一致）。
    /// </summary>
    private SKFilterQuality QualityFor(double scale) =>
        scale < 1 ? SKFilterQuality.Medium
        : _smoothZoom ? SKFilterQuality.Low // 檢視 → 放大時平滑取樣（雙線性）
        : SKFilterQuality.None;

    /// <summary>
    /// 像素格線（對像素創作是核心功能）。線寬用 1/zoom 讓螢幕上恆為 1px，
    /// 並用同尺度的虛線讓格線夠淡不蓋住內容。放大不到 300% 時不畫。
    /// </summary>
    private void DrawPixelGrid(SKCanvas canvas)
    {
        if (!_showPixelGrid) return;
        var scale = _viewport.Scale;
        if (scale < 3) return; // 放大不到 300%（一格不足 3 螢幕像素）就沒有意義

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

    /// <summary>
    /// 目前把手框的外框路徑（doc 座標）；null＝畫面上沒有框。
    /// 螞蟻線與把手都畫在這條路徑上 —— 兩者是同一個框，位置不會有第二個來源。
    /// </summary>
    private SKPath? FramePath()
    {
        if (_session.SelectionHandlesWarp is { } warp) return warp.BoundaryPath();

        if (_session.SelectionHandlesQuad is { Length: 4 } quad)
        {
            var path = new SKPath();
            path.MoveTo(quad[0]);
            for (var i = 1; i < 4; i++) path.LineTo(quad[i]);
            path.Close();
            return path;
        }

        if (_session.SelectionHandles is { } rect)
        {
            var path = new SKPath();
            path.AddRect(rect);
            var rotation = _session.SelectionHandlesRotation;
            if (Math.Abs(rotation) > 0.01f)
                path.Transform(SKMatrix.CreateRotationDegrees(rotation, rect.MidX, rect.MidY));
            return path;
        }

        return null;
    }

    private void DrawSelectionAndPreview(SKCanvas canvas)
    {
        var screenPx = (float)(1.0 / _viewport.Scale); // 螢幕 1px 對應的 doc 長度
        var dash = 6f * screenPx;
        var phase = Environment.TickCount % 1000 / 1000f * dash * 2;

        // 選取區的淡藍填色（只在選取類工具下）：螞蟻線一律走把手框，套索／魔術棒這種
        // 不規則選取的實際形狀就靠這層填色看得出來（借鏡 Pinta）。
        if (_highlightSelection && _session.Floating == null &&
            _session.Selection?.OutlinePath is { } maskOutline)
        {
            using var fill = new SKPaint
            {
                Style = SKPaintStyle.Fill,
                Color = new SKColor(0xB3, 0xCC, 0xE6, 0x33),
                IsAntialias = true,
            };
            canvas.DrawPath(maskOutline, fill);
        }

        // 螞蟻線（白底黑蟻雙層，縮放下維持螢幕寬度）一律畫在「把手框」上 ——
        // 畫面上只有一個框的概念，螞蟻線與把手是同一個框的兩層外觀，不會再各走各的。
        using var antsPath = FramePath();
        if (antsPath != null)
        {
            using var white = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = screenPx,
                Color = SKColors.White,
                IsAntialias = true,
            };
            canvas.DrawPath(antsPath, white);

            using var dashEffect = SKPathEffect.CreateDash([dash, dash], phase);
            using var black = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = screenPx,
                Color = SKColors.Black,
                PathEffect = dashEffect,
                IsAntialias = true,
            };
            canvas.DrawPath(antsPath, black);
        }

        // 彎曲模式（扭曲）的把手框：3×3 貝茲網格線＋16 個控制點（角＝方塊、切線把手＝圓、內點＝小方塊）
        if (_session.SelectionHandlesWarp is { } warp)
        {
            var accent = new SKColor(0x2A, 0x9D, 0xF4);
            using var gridPaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke, StrokeWidth = screenPx,
                Color = accent.WithAlpha(0xB0), IsAntialias = true,
            };
            using var grid = warp.GridPath();
            canvas.DrawPath(grid, gridPaint);

            using var wfill = new SKPaint { Color = SKColors.White, IsAntialias = true };
            using var wstroke = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = screenPx, Color = accent, IsAntialias = true };
            using var tangent = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = screenPx, Color = accent.WithAlpha(0x90), IsAntialias = true };
            var pts = warp.Points;
            // 角點到切線把手的連線
            foreach (var corner in new[] { 0, 3, 12, 15 })
            {
                var (a, b) = Core.Tools.WarpMesh.CornerHandles(corner);
                canvas.DrawLine(pts[corner], pts[a], tangent);
                canvas.DrawLine(pts[corner], pts[b], tangent);
            }
            var ws = 4f * screenPx;
            for (var i = 0; i < 16; i++)
            {
                var p = pts[i];
                var r = i / 4; var c = i % 4;
                var isCorner = Core.Tools.WarpMesh.IsCorner(i);
                var isInner = r is 1 or 2 && c is 1 or 2;
                if (isCorner)
                {
                    var box = SKRect.Create(p.X - ws, p.Y - ws, ws * 2, ws * 2);
                    canvas.DrawRect(box, wfill);
                    canvas.DrawRect(box, wstroke);
                }
                else if (isInner)
                {
                    var s = ws * 0.7f;
                    var box = SKRect.Create(p.X - s, p.Y - s, s * 2, s * 2);
                    canvas.DrawRect(box, wfill);
                    canvas.DrawRect(box, wstroke);
                }
                else
                {
                    canvas.DrawCircle(p, ws * 0.8f, wfill);
                    canvas.DrawCircle(p, ws * 0.8f, wstroke);
                }
            }
        }
        // 四角模式（透視）的把手框：畫四邊形本身＋四個角把手
        else if (_session.SelectionHandlesQuad is { Length: 4 } quad)
        {
            using var qframe = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = screenPx,
                Color = new SKColor(0x2A, 0x9D, 0xF4),
                IsAntialias = true,
            };
            using var qpath = new SKPath();
            qpath.MoveTo(quad[0]);
            for (var i = 1; i < 4; i++) qpath.LineTo(quad[i]);
            qpath.Close();
            canvas.DrawPath(qpath, qframe);

            using var qfill = new SKPaint { Color = SKColors.White, IsAntialias = true };
            var qs = 4f * screenPx;
            var handles = Core.Tools.QuadGeometry.HandlePoints(quad);
            for (var i = 0; i < 4; i++)
            {
                var box = SKRect.Create(handles[i].X - qs, handles[i].Y - qs, qs * 2, qs * 2);
                canvas.DrawRect(box, qfill);
                canvas.DrawRect(box, qframe);
            }
        }
        // 向量元素選取把手（變形 session 旋轉時整個框跟著轉）
        else if (_session.SelectionHandles is { } hr)
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
            // 四角＋四邊中點（與 MoveTool.HitCorner 同一份位置）
            foreach (var c in Core.Tools.MoveTool.HandlePoints(hr))
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
            // 線段只畫在「參與對齊的兩個框」之間（各端外伸一小截，看得出是同一條線）；
            // 長度未知（NaN）時退回整條畫布
            var overhang = 12f * screenPx;
            foreach (var g in snapGuides.XLines)
            {
                var (a, b) = float.IsNaN(g.Start) ? (0f, (float)doc.Height) : (g.Start - overhang, g.End + overhang);
                canvas.DrawLine(g.Position, a, g.Position, b, guidePaint);
            }
            foreach (var g in snapGuides.YLines)
            {
                var (a, b) = float.IsNaN(g.Start) ? (0f, (float)doc.Width) : (g.Start - overhang, g.End + overhang);
                canvas.DrawLine(a, g.Position, b, g.Position, guidePaint);
            }
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

        if (_showPenPath && _session.PenPath is { IsEmpty: false } pen)
            DrawPenPath(canvas, pen, screenPx);
    }

    /// <summary>
    /// 鋼筆工作路徑：曲線（白底＋主題藍雙層，任何底色都看得見）、錨點方塊（選中＝實心）、
    /// 選中錨點的把手（細線＋小圓）。全部維持螢幕固定大小。
    /// </summary>
    private static void DrawPenPath(SKCanvas canvas, Core.Vectors.PenPath pen, float screenPx)
    {
        var accent = new SKColor(0x2A, 0x9D, 0xF4);
        using var curve = pen.ToSKPath();
        using var under = new SKPaint
        {
            Style = SKPaintStyle.Stroke, StrokeWidth = 3f * screenPx,
            Color = new SKColor(0xFF, 0xFF, 0xFF, 0xB0), IsAntialias = true,
        };
        using var over = new SKPaint
        {
            Style = SKPaintStyle.Stroke, StrokeWidth = screenPx, Color = accent, IsAntialias = true,
        };
        canvas.DrawPath(curve, under);
        canvas.DrawPath(curve, over);

        using var fill = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var fillActive = new SKPaint { Color = accent, IsAntialias = true };
        using var handleLine = new SKPaint
        {
            Style = SKPaintStyle.Stroke, StrokeWidth = screenPx,
            Color = new SKColor(0x2A, 0x9D, 0xF4, 0xC0), IsAntialias = true,
        };
        var hs = 3.5f * screenPx;
        var r = 3f * screenPx;

        // 選中錨點的把手先畫（在錨點底下）
        if (pen.Active >= 0 && pen.Active < pen.Count)
        {
            var a = pen.Anchors[pen.Active];
            foreach (var h in new[] { (a.HasHandleIn, a.HandleIn), (a.HasHandleOut, a.HandleOut) })
            {
                if (!h.Item1) continue;
                canvas.DrawLine(a.Point, h.Item2, handleLine);
                canvas.DrawCircle(h.Item2, r, fill);
                canvas.DrawCircle(h.Item2, r, over);
            }
        }

        for (var i = 0; i < pen.Count; i++)
        {
            var p = pen.Anchors[i].Point;
            var box = SKRect.Create(p.X - hs, p.Y - hs, hs * 2, hs * 2);
            canvas.DrawRect(box, i == pen.Active ? fillActive : fill);
            canvas.DrawRect(box, over);
        }

        // 起點可封閉時（接著畫、≥2 點）給一個提示圈
        if (pen.IsAppendable && pen.Count >= 2)
        {
            var p0 = pen.Anchors[0].Point;
            canvas.DrawCircle(p0, hs * 2f, handleLine);
        }
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// MINEPAINTER_DEBUG_TEXTBENCH=1：每幀在畫布上用 Skia 直接畫三種文字各 30 段並計時
/// （系統字型英文、內嵌 Noto 中文、內嵌 Noto 假粗體中文），找「文字繪製為什麼慢」用。
/// </summary>
public static class TextBench
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("MINEPAINTER_DEBUG_TEXTBENCH") == "1";
    public static double LatinMs, CjkMs, CjkBoldMs, CjkNewFontMs, CjkStreamMs, CjkAvaloniaMs;
    public static long FontCacheUsed, FontCacheLimit;
    private static SKFont? _latin, _cjk, _cjkBold, _cjkStream, _cjkAvalonia;
    private static SKTextBlob? _latinBlob, _cjkBlob, _cjkBoldBlob, _cjkStreamBlob, _cjkAvaloniaBlob;

    public static void Run(SKCanvas canvas)
    {
        var noto = Core.Vectors.BundledFont.Typeface;
        if (noto == null) return;
        _latin ??= new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 13) { Subpixel = true, Edging = SKFontEdging.SubpixelAntialias };
        _cjk ??= new SKFont(noto, 13) { Subpixel = true, Edging = SKFontEdging.SubpixelAntialias };
        _cjkBold ??= new SKFont(noto, 13) { Subpixel = true, Edging = SKFontEdging.SubpixelAntialias, Embolden = true };
        _latinBlob ??= SKTextBlob.Create("Layer Properties Sample Text", _latin);
        _cjkBlob ??= SKTextBlob.Create("圖層屬性範例文字效果調整", _cjk);
        _cjkBoldBlob ??= SKTextBlob.Create("圖層屬性範例文字效果調整", _cjkBold);
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 30; i++) canvas.DrawText(_latinBlob, 10, 20 + i * 2, paint);
        canvas.Flush();
        LatinMs = sw.Elapsed.TotalMilliseconds; sw.Restart();
        for (var i = 0; i < 30; i++) canvas.DrawText(_cjkBlob, 10, 120 + i * 2, paint);
        canvas.Flush();
        CjkMs = sw.Elapsed.TotalMilliseconds; sw.Restart();
        for (var i = 0; i < 30; i++) canvas.DrawText(_cjkBoldBlob, 10, 220 + i * 2, paint);
        canvas.Flush();
        CjkBoldMs = sw.Elapsed.TotalMilliseconds; sw.Restart();
        // 每次都新建 SKFont + blob（模擬「字面每幀重建」的情況）
        for (var i = 0; i < 30; i++)
        {
            using var f = new SKFont(noto, 13) { Subpixel = true, Edging = SKFontEdging.SubpixelAntialias };
            using var b = SKTextBlob.Create("圖層屬性範例文字效果調整", f);
            canvas.DrawText(b, 10, 320 + i * 2, paint);
        }
        canvas.Flush();
        CjkNewFontMs = sw.Elapsed.TotalMilliseconds; sw.Restart();
        // 用 managed Stream 建的字面（Avalonia 的 EmbeddedFontCollection 就是這樣建的）
        if (_cjkStream == null)
        {
            var stream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://MinePainter.App/Assets/Fonts/NotoSansTC-Regular.otf"));
            _cjkStream = new SKFont(SKTypeface.FromStream(stream), 13) { Subpixel = true, Edging = SKFontEdging.SubpixelAntialias };
            _cjkStreamBlob = SKTextBlob.Create("圖層屬性範例文字效果調整", _cjkStream);
        }
        for (var i = 0; i < 30; i++) canvas.DrawText(_cjkStreamBlob, 10, 420 + i * 2, paint);
        canvas.Flush();
        CjkStreamMs = sw.Elapsed.TotalMilliseconds; sw.Restart();
        // 每幀換不同字（讓快取失效），看 stream 字面「新字形」的成本
        if (_cjkAvalonia == null) _cjkAvalonia = _cjkStream;
        var text = string.Concat(Enumerable.Range(0, 12).Select(i => (char)(0x4E00 + (Environment.TickCount / 16 + i * 7) % 3000)));
        using (var b = SKTextBlob.Create(text, _cjkAvalonia))
        {
            for (var i = 0; i < 30; i++) canvas.DrawText(b, 10, 520 + i * 2, paint);
        }
        canvas.Flush();
        CjkAvaloniaMs = sw.Elapsed.TotalMilliseconds;
        FontCacheUsed = SKGraphics.GetFontCacheUsed();
        FontCacheLimit = SKGraphics.GetFontCacheLimit();
    }
}
