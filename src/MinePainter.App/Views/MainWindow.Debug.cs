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
    private void SeedDebugEffect(string name)
    {
        // 筆刷／形狀驗證用小畫布：400% 時整張看得到
        if (name == "stroke") SetDocument(ImageCodec.CreateBlankDocument(250, 160, SKColors.White));

        var session = Canvas.Session;
        if (session?.Document.ActiveLayer is not RasterLayer layer) return;
        var doc = session.Document;

        lock (doc.SyncRoot)
        {
            foreach (var idx in TileIndex.CoveringRect(doc.Bounds))
            {
                var tile = layer.Surface.GetTileForWrite(idx);
                using var surface = SKSurface.Create(Tile.Info, tile.Pixels, Tile.RowBytes);
                var c = surface.Canvas;
                var r = idx.ToPixelRect();
                c.Translate(-r.Left, -r.Top);
                using var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(doc.Width, doc.Height),
                    [new SKColor(0x2A, 0x9D, 0xF4), new SKColor(0xF4, 0xC2, 0x2A), new SKColor(0xE0, 0x40, 0x60)],
                    null, SKShaderTileMode.Clamp);
                using var paint = new SKPaint { Shader = shader };
                c.DrawRect(SKRect.Create(0, 0, doc.Width, doc.Height), paint);
                using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
                c.DrawCircle(doc.Width * 0.3f, doc.Height * 0.5f, doc.Height * 0.18f, white);
                using var dark = new SKPaint { Color = new SKColor(0x20, 0x20, 0x30), IsAntialias = true };
                c.DrawRect(SKRect.Create(doc.Width * 0.55f, doc.Height * 0.3f, doc.Width * 0.25f, doc.Height * 0.4f), dark);
                c.Flush();
            }
        }
        layer.InvalidateAll();

        // 筆刷：一條斜線 + 一條曲線（走工具 API，不注入輸入）
        session.Brush.Settings.Radius = 6;
        session.Brush.Settings.Hardness = 1f;
        session.Foreground = SKColors.Black;
        var ev = (float x, float y) => new ToolPointerEvent(new SKPoint(x, y), 1f, ToolModifiers.None, 1);
        session.Brush.OnPointerDown(ev(doc.Width * 0.1f, doc.Height * 0.85f), session);
        for (var i = 1; i <= 60; i++)
        {
            var t = i / 60f;
            session.Brush.OnPointerMove(ev(doc.Width * (0.1f + 0.35f * t), doc.Height * (0.85f - 0.25f * t) + MathF.Sin(t * 12) * 6), session);
        }
        session.Brush.OnPointerUp(ev(doc.Width * 0.45f, doc.Height * 0.6f), session);

        session.Shape.Kind = Core.Vectors.ShapeKind.Ellipse;
        session.Shape.Filled = false;
        session.Shape.StrokeWidth = 3;
        session.Shape.OnPointerDown(ev(doc.Width * 0.6f, doc.Height * 0.72f), session);
        session.Shape.OnPointerMove(ev(doc.Width * 0.9f, doc.Height * 0.95f), session);
        session.Shape.OnPointerUp(ev(doc.Width * 0.9f, doc.Height * 0.95f), session);

        if (name == "stroke")
        {
            Canvas.SetZoomPercent(400);
            return;
        }

        Avalonia.Threading.DispatcherTimer.RunOnce(() =>
        {
            // =quad：移動工具、透視模式、對整層開變形並拉動兩個角（看四角把手框與工具列「變形」群組）
            // =warp：扭曲（彎曲）模式，拉動幾個網格控制點
            if (name is "quad" or "warp")
            {
                SelectTool("move");
                var mode = name == "warp" ? TransformMode.Warp : TransformMode.Perspective;
                SetTransformMode(mode);
                if (session.EnterTransformMode(mode) is { } t)
                {
                    if (t.Warp is { } w)
                    {
                        var m = Core.Tools.WarpMesh.Drag(w, 5, new SKPoint(0, doc.Height * 0.18f));
                        m = Core.Tools.WarpMesh.Drag(m, 10, new SKPoint(0, -doc.Height * 0.18f));
                        m = Core.Tools.WarpMesh.Drag(m, 3, new SKPoint(-doc.Width * 0.08f, doc.Height * 0.1f));
                        t.SetWarp(m);
                    }
                    else if (t.Quad != null)
                    {
                        t.SetQuad(Core.Tools.QuadGeometry.DistortDrag(t.Quad!, 2, new SKPoint(-doc.Width * 0.15f, doc.Height * 0.12f), false));
                        t.SetQuad(Core.Tools.QuadGeometry.PerspectiveDrag(t.Quad!, 0, new SKPoint(doc.Width * 0.1f, 0)));
                    }
                    t.Apply(preview: false);
                    session.RefreshSelectionHandles();
                }
                RefreshUiState();
                return;
            }
            // =pen：鋼筆工具，種一條含平滑點的開放路徑（看路徑／錨點／把手的繪製與工具列群組）
            if (name == "pen")
            {
                SelectTool("pen");
                var w = doc.Width; var h = doc.Height;
                session.PenPath = new Core.Vectors.PenPath(
                [
                    Core.Vectors.PenAnchor.Corner(new SKPoint(w * 0.15f, h * 0.7f)),
                    new Core.Vectors.PenAnchor(new SKPoint(w * 0.4f, h * 0.25f), new SKPoint(w * 0.28f, h * 0.25f), new SKPoint(w * 0.52f, h * 0.25f)),
                    new Core.Vectors.PenAnchor(new SKPoint(w * 0.7f, h * 0.6f), new SKPoint(w * 0.62f, h * 0.45f), new SKPoint(w * 0.78f, h * 0.75f)),
                    Core.Vectors.PenAnchor.Corner(new SKPoint(w * 0.9f, h * 0.3f)),
                ], Closed: false, Finished: false, Active: 2);
                RefreshUiState();
                return;
            }
            if (name.StartsWith("layer:"))
            {
                var key = name[6..];
                var entry = AdjustmentRegistry.All.FirstOrDefault(a => a.DisplayName == key || a.TypeId == key);
                if (entry != null) _layersContent.AddAdjustment(entry.CreateDefault());
                return;
            }
            if (name == "stack")
            {
                // 兩筆效果進堆疊（一筆限左半選取），開圖層屬性看堆疊 UI
                using var half = new SKPath();
                half.AddRect(SKRect.Create(0, 0, doc.Width / 2f, doc.Height));
                var mask = Core.Selections.SelectionMask.FromPath(half, doc.Bounds).Mask;
                LayerEffectCommands.Add(doc, session.History, layer, LayerEffect.Create(new GaussianBlurEffect { Radius = 12 }, mask));
                LayerEffectCommands.Add(doc, session.History, layer, LayerEffect.Create(new AdjustmentEffect(new HueSaturationAdjustment(Hue: 120)), null, session.Foreground));
                _layersContent.Refresh();
                _layersContent.OpenProperties(layer);
                return;
            }
            if (name == "dialog:resize") { OnResizeImageClicked(null, new RoutedEventArgs()); return; }
            if (name == "dialog:canvas") { OnCanvasSizeClicked(null, new RoutedEventArgs()); return; }

            var adj = AdjustmentRegistry.All.FirstOrDefault(a => a.DisplayName == name || a.TypeId == name);
            if (adj != null)
            {
                _ = ApplyAdjustmentAsync(adj);
                return;
            }
            var fx = EffectRegistry.All.FirstOrDefault(e => e.Name == name);
            if (fx != null) _ = ApplyEffectAsync(Services.EffectParamMemory.Recall(fx.Create(), Canvas.Session?.Foreground ?? SKColors.Black), fx.Name, showDialog: true);
        }, TimeSpan.FromMilliseconds(800));
    }

    /// <summary>開發驗證用：放一段有多層外框／陰影、旋轉過的文字並選取（見 Opened 裡的說明）。</summary>
    private void SeedDebugText()
    {
        var session = Canvas.Session;
        if (session?.Document.ActiveLayer is not RasterLayer layer) return;
        _ = layer;
        var text = new TextElement
        {
            Text = "多層外框 Sample",
            FontFamily = Services.FontCatalog.Families.FirstOrDefault(f => f.Contains("JhengHei") || f.Contains("正黑"))
                         ?? Services.FontCatalog.Families.FirstOrDefault() ?? "Microsoft JhengHei",
            FontSize = 96,
            Bold = true,
            Color = new SKColor(0xFF, 0xD8, 0x38),
            Position = new SKPoint(400, 380),
            Rotation = -12f,
        };
        // 文字一定自己一層；外框／陰影走圖層效果堆疊
        layer = VectorCommands.CreateTextLayerSilently(session.Document);
        lock (session.Document.SyncRoot)
        {
            layer.AddElement(text);
            layer.Name = VectorCommands.TextLayerNameFor(text.Text);
            layer.SetEffects([
                LayerEffect.Create(new ObjectOutlineEffect { Width = 4, Color = new SKColor(0xB3, 0x1E, 0x24) }),
                LayerEffect.Create(new ObjectOutlineEffect { Width = 5, Color = SKColors.White }),
                LayerEffect.Create(new ObjectShadowEffect { OffsetX = 4, OffsetY = 7, Blur = 8, Opacity = 55 }),
            ]);
        }
        SelectTool("text");
        session.SelectedElement = (layer.Id, text.Id);
        RefreshUiState();
    }

    // ---- 整窗 layout 計時（MINEPAINTER_DEBUG_PERF 用；根節點的 Measure/Arrange 就是整棵樹）----
    private static readonly bool PerfEnabled = Environment.GetEnvironmentVariable("MINEPAINTER_DEBUG_PERF") is { Length: > 0 };
    private double _measureMs, _arrangeMs, _measureMax;
    private int _measureCount;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (!PerfEnabled) return base.MeasureOverride(availableSize);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = base.MeasureOverride(availableSize);
        var ms = sw.Elapsed.TotalMilliseconds;
        _measureMs += ms;
        _measureMax = Math.Max(_measureMax, ms);
        _measureCount++;
        return r;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (!PerfEnabled) return base.ArrangeOverride(finalSize);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = base.ArrangeOverride(finalSize);
        _arrangeMs += sw.Elapsed.TotalMilliseconds;
        return r;
    }

    private string TakeLayoutPerf()
    {
        var text = $"measure={_measureMs:F0}ms/{_measureCount}x(max {_measureMax:F0}) arrange={_arrangeMs:F0}ms";
        _measureMs = _arrangeMs = _measureMax = 0;
        _measureCount = 0;
        return text;
    }

    /// <summary>MINEPAINTER_DEBUG_PERF 有設時，把一段流程各步的毫秒寫進同一個記錄檔（沒設就全是空操作）。</summary>
    private static class PerfTrace
    {
        private static readonly string? File = Environment.GetEnvironmentVariable("MINEPAINTER_DEBUG_PERF");
        private static readonly System.Diagnostics.Stopwatch Watch = new();
        private static readonly System.Text.StringBuilder Line = new();
        private static double _last;

        public static void Begin()
        {
            if (File == null) return;
            Watch.Restart();
            _last = 0;
            Line.Clear();
        }

        public static void Lap(string name)
        {
            if (File == null) return;
            var now = Watch.Elapsed.TotalMilliseconds;
            Line.Append($" {name}={now - _last:F1}");
            _last = now;
        }

        public static void End(string what)
        {
            if (File == null) return;
            System.IO.File.AppendAllText(File, $"  [{what}] total={Watch.Elapsed.TotalMilliseconds:F1}{Line}\n");
        }
    }
}
