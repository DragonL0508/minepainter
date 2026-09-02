using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using MinePainter.Core.Adjustments;
using MinePainter.Core.Effects;
using MinePainter.Core.Compositing;
using MinePainter.Core.Documents;
using MinePainter.Core.History;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Vectors;
using SkiaSharp;

namespace MinePainter.Core.IO;

/// <summary>
/// .mpp 專案格式：ZIP 容器 + manifest.json + layers/{guid}.png（按 ContentBounds 裁切）+ thumbnail.png。
/// 向量與調整圖層只存參數（非破壞性的自然結果），開檔重建。
/// </summary>
public static class MppFormat
{
    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    // ---- DTO ----

    public sealed class Manifest
    {
        public int FormatVersion { get; set; } = MppFormat.FormatVersion;
        public int Width { get; set; }
        public int Height { get; set; }
        public Node Root { get; set; } = new();
    }

    public sealed class Node
    {
        // group | raster | adjustment（v1 的 "vector" 仍可讀入，會併成 raster）
        public string Type { get; set; } = "group";
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public bool Visible { get; set; } = true;
        public float Opacity { get; set; } = 1f;
        public string Blend { get; set; } = nameof(BlendMode.Normal);

        // group
        public List<Node>? Children { get; set; }
        public bool IsPassThrough { get; set; } // 預留欄位，目前恆為 false

        // raster（像素 + 物件同屬一個圖層）
        public int[]? PixelBounds { get; set; } // [l,t,r,b] 圖層座標
        public int[]? Offset { get; set; }
        public string? PixelsEntry { get; set; }
        public List<Element>? Elements { get; set; }

        // adjustment
        public string? AdjustmentType { get; set; }
        public Dictionary<string, float>? AdjustmentParams { get; set; }

        // raster 的非破壞性效果堆疊（由先到後）
        public List<EffectDto>? Effects { get; set; }
    }

    public sealed class EffectDto
    {
        public Guid Id { get; set; }
        public string Type { get; set; } = "";
        public Dictionary<string, string>? Params { get; set; }
        public bool Enabled { get; set; } = true;
        public uint Color { get; set; } = 0xFF000000;
        public string? MaskEntry { get; set; }
        public int[]? MaskBounds { get; set; } // [l,t,r,b] doc 座標
    }

    public sealed class Outline
    {
        public uint Color { get; set; }
        public float Width { get; set; }
        public uint? GradientStart { get; set; }
        public uint? GradientEnd { get; set; }
        public float? GradientAngle { get; set; }
        public bool? GradientRadial { get; set; }
    }

    public sealed class Element
    {
        public string Type { get; set; } = ""; // text | shape
        public Guid Id { get; set; }

        // text
        public string? Text { get; set; }
        public string? FontFamily { get; set; }
        public float? FontSize { get; set; }
        public float[]? Position { get; set; }
        public float? ScaleX { get; set; }
        public float? BaseFontSize { get; set; }
        public float? Rotation { get; set; }
        public int? Weight { get; set; }
        public bool? Bold { get; set; }
        public bool? Italic { get; set; }
        public bool? Underline { get; set; }
        public bool? Strikethrough { get; set; }
        public string? Align { get; set; } // left | center | right

        // text effects（舊檔沒有這些欄位 → null = 無效果）
        public uint? OutlineColor { get; set; }
        public float? OutlineWidth { get; set; }
        public uint? ShadowColor { get; set; }
        public float? ShadowAngle { get; set; }
        public float? ShadowDistance { get; set; }
        public float? ShadowBlur { get; set; }
        public float? ShadowSpread { get; set; }
        public uint? OutlineGradientStart { get; set; }
        public uint? OutlineGradientEnd { get; set; }
        public float? OutlineGradientAngle { get; set; }
        public bool? OutlineGradientRadial { get; set; }

        /// <summary>多層外框：第 2 層起（由內而外）。第 1 層仍寫在上面的平面欄位，舊版讀得到最內層。</summary>
        public List<Outline>? OutlineLayers { get; set; }
        public uint? GradientStart { get; set; }
        public uint? GradientEnd { get; set; }
        public float? GradientAngle { get; set; }
        public bool? GradientRadial { get; set; }
        public uint? GlowColor { get; set; }
        public float? GlowSize { get; set; }
        public float? GlowSpread { get; set; }
        public float? LetterSpacing { get; set; }
        public float? LineHeight { get; set; }

        // shape
        public string? Kind { get; set; }
        public float[]? Rect { get; set; }
        public uint? FillColor { get; set; }
        public float? StrokeWidth { get; set; }

        public uint? Color { get; set; } // text color / shape stroke color
    }

    // ---- Save ----

    /// <summary>
    /// 可在背景執行緒呼叫：快照階段在鎖內完成（AddRef 零拷貝），之後只讀不可變資料。
    /// <paramref name="progress"/> 回報 0..1（PNG 編碼是大頭，按圖層數均分）。
    /// </summary>
    public static void Save(Document doc, string path, IProgress<double>? progress = null)
    {
        // 在鎖內建 DTO 樹並抓 raster 快照（AddRef 零拷貝）；離鎖後寫檔
        var rasters = new List<(string Entry, TileSnapshot Snapshot, SKRectI Bounds)>();
        var masks = new List<(string Entry, byte[] Alpha, SKRectI Bounds)>();
        Manifest manifest;
        lock (doc.SyncRoot)
        {
            manifest = new Manifest
            {
                Width = doc.Width,
                Height = doc.Height,
                Root = BuildNode(doc.Root, rasters, masks),
            };
        }

        var total = rasters.Count + 2; // 縮圖合成 + 縮圖寫入各算一步
        var done = 0;
        void Step() => progress?.Report(++done / (double)total);

        try
        {
            using var thumbnailSource = Compositor.RenderComposite(doc);
            Step();

            using var file = File.Create(path);
            using var zip = new ZipArchive(file, ZipArchiveMode.Create);

            using (var manifestStream = zip.CreateEntry("manifest.json").Open())
            {
                JsonSerializer.Serialize(manifestStream, manifest, JsonOptions);
            }

            foreach (var (entry, snapshot, bounds) in rasters)
            {
                // PNG 已經壓縮過，ZIP 再 deflate 一次只是浪費 CPU —— 直接存
                using (var stream = zip.CreateEntry(entry, CompressionLevel.NoCompression).Open())
                {
                    EncodeSnapshotPng(snapshot, bounds, stream);
                }
                Step();
            }

            foreach (var (entry, alpha, bounds) in masks)
            {
                using var stream = zip.CreateEntry(entry, CompressionLevel.NoCompression).Open();
                EncodeMaskPng(alpha, bounds, stream);
            }

            WriteThumbnail(zip, thumbnailSource);
            Step();
        }
        finally
        {
            foreach (var (_, snapshot, _) in rasters) snapshot.Dispose();
        }
    }

    private static Node BuildNode(LayerNode layer, List<(string, TileSnapshot, SKRectI)> rasters,
        List<(string, byte[], SKRectI)> masks)
    {
        var node = new Node
        {
            Id = layer.Id,
            Name = layer.Name,
            Visible = layer.IsVisible,
            Opacity = layer.Opacity,
            Blend = layer.BlendMode.ToString(),
        };

        switch (layer)
        {
            case GroupLayer group:
                node.Type = "group";
                node.Children = group.Children.Select(c => BuildNode(c, rasters, masks)).ToList();
                break;

            case RasterLayer raster:
                node.Type = "raster";
                node.Offset = [raster.Offset.X, raster.Offset.Y];
                var bounds = raster.Surface.ContentBounds;
                if (!bounds.IsEmpty)
                {
                    var entry = $"layers/{raster.Id:N}.png";
                    node.PixelBounds = [bounds.Left, bounds.Top, bounds.Right, bounds.Bottom];
                    node.PixelsEntry = entry;
                    rasters.Add((entry, raster.Surface.Snapshot(), bounds));
                }
                if (raster.HasElements)
                    node.Elements = raster.Elements.Select(BuildElement).ToList();
                if (raster.HasEffects)
                {
                    node.Effects = new List<EffectDto>();
                    foreach (var fx in raster.Effects)
                    {
                        var dto = new EffectDto
                        {
                            Id = fx.Id,
                            Type = EffectSerializer.TypeIdOf(fx.Effect),
                            Params = EffectSerializer.Save(fx.Effect),
                            Enabled = fx.Enabled,
                            Color = (uint)fx.Color,
                        };
                        if (fx.Mask is { } mask && !mask.Bounds.IsEmpty)
                        {
                            var mb = mask.Bounds;
                            var entry = $"masks/{fx.Id:N}.png";
                            masks.Add((entry, ReadMaskAlpha(mask, mb), mb));
                            dto.MaskEntry = entry;
                            dto.MaskBounds = [mb.Left, mb.Top, mb.Right, mb.Bottom];
                        }
                        node.Effects.Add(dto);
                    }
                }
                break;

            case AdjustmentLayer adj:
                node.Type = "adjustment";
                node.AdjustmentType = adj.Adjustment.TypeId;
                node.AdjustmentParams = adj.Adjustment.SaveParams();
                break;

            default:
                throw new NotSupportedException($"未知圖層類型：{layer.GetType().Name}");
        }
        return node;
    }

    private static Element BuildElement(VectorElement el) => el switch
    {
        TextElement t => new Element
        {
            Type = "text",
            Id = t.Id,
            Text = t.Text,
            FontFamily = t.FontFamily,
            FontSize = t.FontSize,
            Color = (uint)t.Color,
            Position = [t.Position.X, t.Position.Y],
            ScaleX = t.ScaleX,
            BaseFontSize = t.BaseFontSize,
            Rotation = t.Rotation,
            Weight = t.FontWeight,
            Bold = t.Bold,
            Italic = t.Italic,
            Underline = t.Underline,
            Strikethrough = t.Strikethrough,
            Align = t.Alignment switch
            {
                TextAlign.Center => "center",
                TextAlign.Right => "right",
                _ => "left",
            },
            OutlineColor = t.Stroke is { } ts ? (uint)ts.Color : null,
            OutlineWidth = t.Stroke?.Width,
            ShadowColor = t.Shadow is { } sh ? (uint)sh.Color : null,
            ShadowAngle = t.Shadow?.Angle,
            ShadowDistance = t.Shadow?.Distance,
            ShadowBlur = t.Shadow?.Blur,
            ShadowSpread = t.Shadow?.Spread,
            OutlineGradientStart = t.Stroke?.Gradient is { } sg ? (uint)sg.Start : null,
            OutlineGradientEnd = t.Stroke?.Gradient is { } sg2 ? (uint)sg2.End : null,
            OutlineGradientAngle = t.Stroke?.Gradient?.Angle,
            OutlineGradientRadial = t.Stroke?.Gradient?.Radial,
            OutlineLayers = t.Stroke?.Outer is { } outer
                ? outer.Layers().Select(o => new Outline
                {
                    Color = (uint)o.Color,
                    Width = o.Width,
                    GradientStart = o.Gradient is { } og ? (uint)og.Start : null,
                    GradientEnd = o.Gradient is { } og2 ? (uint)og2.End : null,
                    GradientAngle = o.Gradient?.Angle,
                    GradientRadial = o.Gradient?.Radial,
                }).ToList()
                : null,
            GradientStart = t.Gradient is { } g ? (uint)g.Start : null,
            GradientEnd = t.Gradient is { } g2 ? (uint)g2.End : null,
            GradientAngle = t.Gradient?.Angle,
            GradientRadial = t.Gradient?.Radial,
            GlowColor = t.Glow is { } gl ? (uint)gl.Color : null,
            GlowSize = t.Glow?.Size,
            GlowSpread = t.Glow?.Spread,
            LetterSpacing = t.LetterSpacing,
            LineHeight = t.LineHeightScale,
        },
        ShapeElement s => new Element
        {
            Type = "shape",
            Id = s.Id,
            Kind = s.Kind.ToString(),
            Rect = [s.Rect.Left, s.Rect.Top, s.Rect.Right, s.Rect.Bottom],
            FillColor = s.FillColor is { } f ? (uint)f : null,
            Color = (uint)s.StrokeColor,
            StrokeWidth = s.StrokeWidth,
        },
        _ => throw new NotSupportedException($"未知向量元素：{el.GetType().Name}"),
    };

    private static void EncodeSnapshotPng(TileSnapshot snapshot, SKRectI bounds, Stream output)
    {
        var info = new SKImageInfo(bounds.Width, bounds.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            foreach (var (idx, tile) in snapshot.Tiles)
            {
                var tileRect = idx.ToPixelRect();
                if (!tileRect.IntersectsWith(bounds)) continue;
                using var pixmap = tile.AsPixmap();
                using var img = SKImage.FromPixels(pixmap);
                canvas.DrawImage(img, tileRect.Left - bounds.Left, tileRect.Top - bounds.Top);
            }
        }
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("PNG 編碼失敗。");
        encoded.SaveTo(output);
    }

    private static void WriteThumbnail(ZipArchive zip, SKImage composite)
    {
        const int maxSide = 256;
        var scale = Math.Min(1f, maxSide / (float)Math.Max(composite.Width, composite.Height));
        var w = Math.Max(1, (int)(composite.Width * scale));
        var h = Math.Max(1, (int)(composite.Height * scale));

        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        surface.Canvas.Clear(SKColors.White);
        using (var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium })
        {
            surface.Canvas.DrawImage(composite, SKRect.Create(w, h), paint);
        }
        using var img = surface.Snapshot();
        using var encoded = img.Encode(SKEncodedImageFormat.Png, 90);
        using var stream = zip.CreateEntry("thumbnail.png", CompressionLevel.NoCompression).Open();
        encoded.SaveTo(stream);
    }

    // ---- Load ----

    public static Document Load(string path)
    {
        using var file = File.OpenRead(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);

        var manifestEntry = zip.GetEntry("manifest.json")
            ?? throw new InvalidDataException("缺少 manifest.json，不是有效的 .mpp 檔。");
        Manifest manifest;
        using (var stream = manifestEntry.Open())
        {
            manifest = JsonSerializer.Deserialize<Manifest>(stream, JsonOptions)
                ?? throw new InvalidDataException("manifest.json 解析失敗。");
        }
        if (manifest.FormatVersion > FormatVersion)
            throw new InvalidDataException($"檔案版本 {manifest.FormatVersion} 過新，請更新程式。");

        var doc = new Document(manifest.Width, manifest.Height);
        lock (doc.SyncRoot)
        {
            foreach (var childNode in manifest.Root.Children ?? [])
            {
                var built = BuildLayer(childNode, zip);
                doc.Root.Add(built);
                if (built is RasterLayer raster) MigrateTextLayers(doc.Root, raster);
            }

            doc.ActiveLayer = doc.Root.Children.LastOrDefault();
        }
        return doc;
    }

    private static LayerNode BuildLayer(Node node, ZipArchive zip)
    {
        LayerNode layer = node.Type switch
        {
            "group" => BuildGroup(node, zip),
            // "vector" 是 v1 的獨立向量圖層，現已併入一般圖層
            "raster" or "vector" => BuildRaster(node, zip),
            "adjustment" => BuildAdjustment(node),
            _ => throw new InvalidDataException($"未知圖層類型：{node.Type}"),
        };

        layer.Id = node.Id;
        layer.Name = node.Name;
        layer.IsVisible = node.Visible;
        layer.Opacity = node.Opacity;
        layer.BlendMode = Enum.TryParse<BlendMode>(node.Blend, out var blend) ? blend : BlendMode.Normal;
        return layer;
    }

    private static GroupLayer BuildGroup(Node node, ZipArchive zip)
    {
        var group = new GroupLayer();
        foreach (var child in node.Children ?? [])
        {
            var layer = BuildLayer(child, zip);
            group.Add(layer);
            if (layer is RasterLayer raster) MigrateTextLayers(group, raster);
        }
        return group;
    }

    /// <summary>
    /// 舊檔相容：文字一定自己一層 —— 像素＋文字同層、或一層多段文字的，把文字拆成各自的圖層；
    /// 文字自帶的外框／陰影／光暈／漸層改成該圖層的效果堆疊（元素本身的效果欄位清掉）。
    /// </summary>
    private static void MigrateTextLayers(GroupLayer group, RasterLayer raster)
    {
        var texts = raster.Elements.OfType<TextElement>().ToList();
        if (texts.Count == 0) return;

        var needSplit = raster.Surface.TileCount > 0 || raster.Elements.Count > 1;
        if (!needSplit)
        {
            MigrateTextEffects(raster, texts[0]);
            if (raster.Name.Length == 0) raster.Name = VectorCommands.TextLayerNameFor(texts[0].Text);
            return;
        }

        var index = group.IndexOf(raster);
        foreach (var text in texts)
        {
            raster.RemoveElement(text.Id);
            var layer = new RasterLayer { Name = VectorCommands.TextLayerNameFor(text.Text), Opacity = raster.Opacity, IsVisible = raster.IsVisible };
            layer.AddElement(text);
            MigrateTextEffects(layer, text);
            group.Insert(++index, layer);
        }
    }

    private static void MigrateTextEffects(RasterLayer layer, TextElement text)
    {
        if (text.Stroke == null && text.Shadow == null && text.Glow == null && text.Gradient == null) return;
        var effects = new List<LayerEffect>(layer.Effects);

        if (text.Gradient is { } g)
        {
            effects.Add(LayerEffect.Create(new ObjectGradientEffect
            {
                Stops = GradientStops.Two(g.Start, g.End), Angle = g.Angle, Radial = g.Radial,
            }));
        }
        if (text.Stroke is { } stroke)
        {
            // 由內而外：每層外框以「目前已擴大的形狀」再往外描，等同舊的巢狀外框
            foreach (var s in stroke.Layers())
            {
                if (s.Width <= 0.01f) continue;
                effects.Add(LayerEffect.Create(new ObjectOutlineEffect
                {
                    Width = Math.Max(1, (int)MathF.Round(s.Width)), Color = s.Color,
                }));
            }
        }
        if (text.Shadow is { } sh)
        {
            var rad = sh.Angle * MathF.PI / 180f;
            effects.Add(LayerEffect.Create(new ObjectShadowEffect
            {
                OffsetX = (int)MathF.Round(MathF.Cos(rad) * sh.Distance),
                OffsetY = (int)MathF.Round(MathF.Sin(rad) * sh.Distance),
                Blur = Math.Clamp((int)MathF.Round(sh.Blur), 0, 50),
                Opacity = (int)MathF.Round(sh.Color.Alpha / 2.55f),
                Color = sh.Color.WithAlpha(255),
            }));
        }
        if (text.Glow is { } glow)
        {
            effects.Add(LayerEffect.Create(new ObjectGlowEffect
            {
                Size = Math.Clamp((int)MathF.Round(glow.Size), 1, 100),
                Spread = Math.Clamp((int)MathF.Round(glow.Spread), 0, 30),
                Opacity = (int)MathF.Round(glow.Color.Alpha / 2.55f),
                Color = glow.Color.WithAlpha(255),
            }));
        }

        layer.ReplaceElement(text with { Stroke = null, Shadow = null, Glow = null, Gradient = null });
        layer.SetEffects(effects);
    }

    private static RasterLayer BuildRaster(Node node, ZipArchive zip)
    {
        var layer = new RasterLayer();
        if (node.Offset is [var ox, var oy]) layer.Offset = new SKPointI(ox, oy);

        if (node.PixelsEntry != null && node.PixelBounds is [var l, var t, _, _])
        {
            var entry = zip.GetEntry(node.PixelsEntry)
                ?? throw new InvalidDataException($"缺少像素資料：{node.PixelsEntry}");
            using var stream = entry.Open();
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            ms.Position = 0;

            using var decoded = SKBitmap.Decode(ms)
                ?? throw new InvalidDataException($"像素資料解碼失敗：{node.PixelsEntry}");
            using var converted = EnsureBgraPremul(decoded);
            using var pixmap = converted.PeekPixels();
            layer.Surface.CopyFrom(pixmap, new SKPointI(l, t));
        }

        foreach (var el in node.Elements ?? [])
            layer.AddElement(BuildElement(el));

        if (node.Effects is { Count: > 0 } effects)
        {
            var list = new List<LayerEffect>();
            foreach (var dto in effects)
            {
                IEffect effect;
                try
                {
                    effect = EffectSerializer.Load(dto.Type, dto.Params);
                }
                catch (Exception)
                {
                    continue; // 未知效果（新版檔案）：略過，不擋開檔
                }
                MaskSurface? mask = null;
                if (dto.MaskEntry != null && dto.MaskBounds is [var ml, var mt, var mr, var mb] &&
                    zip.GetEntry(dto.MaskEntry) is { } maskEntry)
                {
                    using var stream = maskEntry.Open();
                    using var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    ms.Position = 0;
                    using var decoded = SKBitmap.Decode(ms);
                    if (decoded != null) mask = DecodeMask(decoded, new SKRectI(ml, mt, mr, mb));
                }
                list.Add(new LayerEffect(dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id, effect, dto.Enabled, mask)
                {
                    Color = new SKColor(dto.Color),
                });
            }
            layer.SetEffects(list);
        }

        return layer;
    }

    // ---- 效果遮罩 PNG（BGRA，值放 alpha） ----

    private static byte[] ReadMaskAlpha(MaskSurface mask, SKRectI bounds)
    {
        var alpha = new byte[bounds.Width * bounds.Height];
        foreach (var (idx, tile) in mask.Tiles)
        {
            var tileRect = idx.ToPixelRect();
            var inter = SKRectI.Intersect(tileRect, bounds);
            if (inter.Width <= 0 || inter.Height <= 0) continue;
            for (var y = inter.Top; y < inter.Bottom; y++)
            {
                Array.Copy(tile.Alpha, (y - tileRect.Top) * MaskTile.Size + (inter.Left - tileRect.Left),
                    alpha, (y - bounds.Top) * bounds.Width + (inter.Left - bounds.Left), inter.Width);
            }
        }
        return alpha;
    }

    private static void EncodeMaskPng(byte[] alpha, SKRectI bounds, Stream stream)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(bounds.Width, bounds.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        var span = bitmap.GetPixelSpan();
        unsafe
        {
            fixed (byte* p = span)
            {
                var px = (uint*)p;
                for (var i = 0; i < alpha.Length; i++) px[i] = (uint)alpha[i] << 24 | 0x00FFFFFF;
            }
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        data.SaveTo(stream);
    }

    private static MaskSurface DecodeMask(SKBitmap decoded, SKRectI bounds)
    {
        var mask = new MaskSurface();
        var w = Math.Min(decoded.Width, bounds.Width);
        var h = Math.Min(decoded.Height, bounds.Height);
        for (var y = 0; y < h; y++)
        {
            var docY = bounds.Top + y;
            for (var x = 0; x < w; x++)
            {
                var a = decoded.GetPixel(x, y).Alpha;
                if (a == 0) continue;
                var docX = bounds.Left + x;
                var idx = TileIndex.FromPixel(docX, docY);
                var tileRect = idx.ToPixelRect();
                mask.GetForWrite(idx).Alpha[(docY - tileRect.Top) * MaskTile.Size + (docX - tileRect.Left)] = a;
            }
        }
        mask.ExtendBounds(bounds);
        return mask;
    }

    private static VectorElement BuildElement(Element el) => el.Type switch
    {
        "text" => new TextElement
        {
            Id = el.Id,
            Text = el.Text ?? "",
            FontFamily = el.FontFamily ?? "Microsoft JhengHei",
            FontSize = el.FontSize ?? 48f,
            Color = new SKColor(el.Color ?? 0xFF000000),
            Position = el.Position is [var x, var y] ? new SKPoint(x, y) : default,
            ScaleX = el.ScaleX ?? 1f,
            BaseFontSize = el.BaseFontSize,
            Rotation = el.Rotation ?? 0f,
            FontWeight = el.Weight ?? 400,
            Bold = el.Bold ?? false,
            Italic = el.Italic ?? false,
            Underline = el.Underline ?? false,
            Strikethrough = el.Strikethrough ?? false,
            Alignment = el.Align switch
            {
                "center" => TextAlign.Center,
                "right" => TextAlign.Right,
                _ => TextAlign.Left,
            },
            Stroke = el.OutlineColor is { } oc
                ? new TextStroke
                {
                    Color = new SKColor(oc),
                    Width = el.OutlineWidth ?? 3f,
                    Gradient = ReadGradient(el.OutlineGradientStart, el.OutlineGradientEnd,
                        el.OutlineGradientAngle, el.OutlineGradientRadial),
                    Outer = ReadOutlineLayers(el.OutlineLayers),
                }
                : null,
            Shadow = el.ShadowColor is { } sc
                ? new TextShadow
                {
                    Color = new SKColor(sc),
                    Angle = el.ShadowAngle ?? 45f,
                    Distance = el.ShadowDistance ?? 6f,
                    Blur = el.ShadowBlur ?? 6f,
                    Spread = el.ShadowSpread ?? 0f,
                }
                : null,
            Gradient = ReadGradient(el.GradientStart, el.GradientEnd,
                el.GradientAngle, el.GradientRadial),
            Glow = el.GlowColor is { } gc
                ? new TextGlow
                {
                    Color = new SKColor(gc),
                    Size = el.GlowSize ?? 12f,
                    Spread = el.GlowSpread ?? 0f,
                }
                : null,
            LetterSpacing = el.LetterSpacing ?? 0f,
            LineHeightScale = el.LineHeight ?? TextElement.DefaultLineHeightScale,
        },
        "shape" => new ShapeElement
        {
            Id = el.Id,
            Kind = Enum.TryParse<ShapeKind>(el.Kind, out var k) ? k : ShapeKind.Rectangle,
            Rect = el.Rect is [var rl, var rt, var rr, var rb] ? new SKRect(rl, rt, rr, rb) : default,
            FillColor = el.FillColor is { } f ? new SKColor(f) : null,
            StrokeColor = new SKColor(el.Color ?? 0xFF000000),
            StrokeWidth = el.StrokeWidth ?? 0f,
        },
        _ => throw new InvalidDataException($"未知物件類型：{el.Type}"),
    };

    /// <summary>漸層欄位缺一（舊檔／手改）就當作沒有漸層。</summary>
    /// <summary>第 2 層起的外框（由內而外）；超過上限的層數捨棄。</summary>
    private static TextStroke? ReadOutlineLayers(List<Outline>? layers)
    {
        if (layers is not { Count: > 0 }) return null;
        var list = layers.Take(TextStroke.MaxLayers - 1).Select(o => new TextStroke
        {
            Color = new SKColor(o.Color),
            Width = Math.Clamp(o.Width, 0f, 500f),
            Gradient = ReadGradient(o.GradientStart, o.GradientEnd, o.GradientAngle, o.GradientRadial),
        }).ToList();
        return TextStroke.FromLayers(list);
    }

    private static TextGradient? ReadGradient(uint? start, uint? end, float? angle, bool? radial) =>
        start is { } s && end is { } e
            ? new TextGradient
            {
                Start = new SKColor(s),
                End = new SKColor(e),
                Angle = angle ?? 90f,
                Radial = radial ?? false,
            }
            : null;

    private static SKBitmap EnsureBgraPremul(SKBitmap source)
    {
        if (source.ColorType == SKColorType.Bgra8888 && source.AlphaType == SKAlphaType.Premul)
            return source.Copy(); // 統一擁有權（caller dispose）

        var info = new SKImageInfo(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var converted = new SKBitmap(info);
        using var canvas = new SKCanvas(converted);
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(source, 0, 0);
        return converted;
    }

    private static AdjustmentLayer BuildAdjustment(Node node)
    {
        var adjustment = AdjustmentRegistry.Load(
            node.AdjustmentType ?? throw new InvalidDataException("調整圖層缺少類型"), node.AdjustmentParams);
        return new AdjustmentLayer(adjustment);
    }

    // ---- 匯出 ----

    /// <summary>
    /// 匯出合成影像。JPEG 會先鋪白底去 alpha。
    /// <paramref name="width"/>/<paramref name="height"/> 指定時會等比（或依呼叫端給的比例）縮放。
    /// 可在背景執行緒呼叫（合成內部自行取鎖）。
    /// </summary>
    public static void Export(Document doc, string path, int jpegQuality = 92,
        int? width = null, int? height = null, IProgress<double>? progress = null)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var isJpeg = ext is ".jpg" or ".jpeg";
        var outW = Math.Max(1, width ?? doc.Width);
        var outH = Math.Max(1, height ?? doc.Height);
        using var composite = Compositor.RenderComposite(doc);
        progress?.Report(0.4);

        SKData encoded;
        if (isJpeg || outW != doc.Width || outH != doc.Height)
        {
            var info = new SKImageInfo(outW, outH, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            surface.Canvas.Clear(isJpeg ? SKColors.White : SKColors.Transparent);
            using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High })
            {
                surface.Canvas.DrawImage(composite, SKRect.Create(outW, outH), paint);
            }
            using var flattened = surface.Snapshot();
            encoded = flattened.Encode(
                isJpeg ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png,
                isJpeg ? Math.Clamp(jpegQuality, 1, 100) : 100)
                ?? throw new InvalidOperationException("影像編碼失敗");
        }
        else
        {
            encoded = composite.Encode(SKEncodedImageFormat.Png, 100)
                ?? throw new InvalidOperationException("影像編碼失敗");
        }
        progress?.Report(0.9);

        using (encoded)
        using (var file = File.Create(path))
        {
            encoded.SaveTo(file);
        }
        progress?.Report(1);
    }
}
