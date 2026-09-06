using System.Text;
using MinePainter.Core.Adjustments;
using MinePainter.Core.Compositing;
using MinePainter.Core.Documents;
using MinePainter.Core.Effects;
using MinePainter.Core.History;
using MinePainter.Core.Layers;
using MinePainter.Core.Vectors;
using SkiaSharp;

namespace MinePainter.Core.IO;

/// <summary>
/// 匯出成 Photoshop 文件 —— 目標是「在 Photoshop 裡盡量還能改」：
/// 圖層樹、群組、可見度、不透明度、混合模式原樣；文字寫成可編輯的文字圖層（<see cref="PsdTextWriter"/>）；
/// 文字的外框／陰影／光暈與效果堆疊裡對得上的寫成圖層樣式（<see cref="PsdStyleWriter"/>）；
/// 調整圖層與堆疊裡的調整寫成 Photoshop 的調整圖層（<see cref="PsdAdjustmentWriter"/>）。
///
/// 對不上的一律烙成像素並提示（模糊、扭曲類效果、帶選取遮罩的效果、透視／彎曲文字、形狀、多物件圖層）
/// —— 少一條效果的「可編輯」比整層走樣更糟。快速模式先以輸出解析度複製一份再寫，文字重新排版、效果跟著放大。
///
/// 檔案本身：RGB 8 位元、通道 R/G/B/A、各通道 PackBits；圖層數寫負數＝合成影像的第一個多出來的通道是透明度。
/// 超過 30000 px 的邊自動改寫 PSB。
/// </summary>
public static partial class PsdFormat
{
    private const int PsdMaxDimension = 30000;

    public static void Save(Document doc, string path, IProgress<double>? progress = null) => Save(doc, path, progress, out _);

    /// <summary><paramref name="warnings"/> 收集「寫得出去但語意有損」的地方（烙成像素的圖層、略過的調整…）。</summary>
    public static void Save(Document doc, string path, IProgress<double>? progress, out IReadOnlyList<string> warnings)
    {
        var psb = string.Equals(Path.GetExtension(path), ".psb", StringComparison.OrdinalIgnoreCase);
        // 先寫到記憶體再落地：寫到一半炸掉不會留下半截檔案蓋掉使用者的舊檔
        var buffer = new MemoryStream();
        Save(doc, buffer, progress, out warnings, psb);
        using var file = File.Create(path);
        buffer.WriteTo(file);
    }

    public static void Save(Document doc, Stream stream, IProgress<double>? progress, out IReadOnlyList<string> warnings, bool psb = false)
    {
        var notes = new List<string>();
        warnings = notes;
        Document? scaled = null;
        try
        {
            var source = doc;
            if (doc.IsFastMode)
            {
                scaled = OutputRender.CloneScaled(doc, doc.OutputWidth, doc.OutputHeight, ResampleMode.Bicubic,
                    progress == null ? null : new Progress<double>(v => progress.Report(v * 0.2)), clampEffects: false);
                source = scaled;
            }
            if (source.Width > PsdMaxDimension || source.Height > PsdMaxDimension) psb = true;
            progress?.Report(0.2);

            var layers = new List<OutLayer>();
            var total = Math.Max(1, source.Descendants().Count());
            var done = 0;
            var context = new SaveContext(source, notes, () => progress?.Report(0.2 + 0.6 * ++done / total));
            foreach (var child in source.Root.Children.ToList()) context.Emit(child, layers);

            using var composite = Compositor.RenderComposite(source);
            progress?.Report(0.9);
            WriteFile(stream, source, layers, composite, psb);
            progress?.Report(1);
        }
        finally
        {
            scaled?.Dispose();
        }
    }

    // ---- 圖層記錄 ----

    private sealed class OutLayer
    {
        public SKRectI Rect;
        public readonly List<(int Id, byte[] Samples)> Channels = [];
        public string BlendKey = "norm";
        public byte Opacity = 255;
        public bool Hidden;
        public bool Clipped;
        public string Name = "";
        public int SectionType;
        public readonly List<(string Key, byte[] Data)> Blocks = [];

        /// <summary>直通 alpha 的 BGRA → R／G／B／A 四個通道。</summary>
        public void SetPixels(SKRectI rect, byte[] bgra)
        {
            Rect = rect;
            var count = rect.Width * rect.Height;
            var r = new byte[count];
            var g = new byte[count];
            var b = new byte[count];
            var a = new byte[count];
            for (var i = 0; i < count; i++)
            {
                b[i] = bgra[i * 4];
                g[i] = bgra[i * 4 + 1];
                r[i] = bgra[i * 4 + 2];
                a[i] = bgra[i * 4 + 3];
            }
            Channels.Add((0, r));
            Channels.Add((1, g));
            Channels.Add((2, b));
            Channels.Add((-1, a));
        }

        public void SetEmpty()
        {
            Rect = SKRectI.Empty;
            Channels.Add((-1, []));
            Channels.Add((0, []));
            Channels.Add((1, []));
            Channels.Add((2, []));
        }
    }

    private sealed class SaveContext(Document doc, List<string> notes, Action step)
    {
        public void Emit(LayerNode node, List<OutLayer> output)
        {
            switch (node)
            {
                case GroupLayer group:
                    EmitGroup(group, output);
                    break;
                case AdjustmentLayer adjustment:
                    EmitAdjustment(adjustment.Adjustment, adjustment.Name, adjustment, clipped: false, output);
                    step();
                    break;
                case RasterLayer raster:
                    EmitRaster(raster, output);
                    break;
            }
        }

        private void EmitGroup(GroupLayer group, List<OutLayer> output)
        {
            var style = PsdStyleWriter.Build(group.Effects, null);
            if (style.Unsupported.Count > 0 || style.ClippedAdjustments.Count > 0)
            {
                // 群組上的效果對不上：整組合成後烙成一層（群組結構會丟，但畫面對）
                var reason = style.Unsupported.Count > 0
                    ? $"群組效果「{string.Join("、", style.Unsupported)}」在 Photoshop 沒有對應"
                    : "群組效果堆疊裡的調整在 Photoshop 沒有對應";
                Bake(group, reason, output);
                foreach (var _ in group.Children) step();
                step();
                return;
            }

            output.Add(new OutLayer { Name = "</Layer group>", SectionType = 3, BlendKey = "pass" }.WithEmpty());
            foreach (var child in group.Children.ToList()) Emit(child, output);

            var record = Properties(group);
            record.SectionType = 1;
            record.SetEmpty();
            if (style.ToLfx2() is { } lfx2) record.Blocks.Add(("lfx2", lfx2));
            output.Add(record);
            step();
        }

        private void EmitAdjustment(IAdjustment adjustment, string name, LayerNode? source, bool clipped, List<OutLayer> output)
        {
            var blocks = PsdAdjustmentWriter.Write(adjustment);
            if (blocks == null)
            {
                notes.Add($"調整圖層「{name}」（{adjustment.DisplayName}）在 Photoshop 沒有對應，已略過。");
                return;
            }
            var record = source != null ? Properties(source) : new OutLayer { Name = name };
            record.Clipped = clipped;
            record.SetEmpty();
            record.Blocks.AddRange(blocks);
            output.Add(record);
        }

        private void EmitRaster(RasterLayer layer, List<OutLayer> output)
        {
            TextElement? text = null;
            if (layer.Elements.Count == 1 && layer.Elements[0] is TextElement t
                && t.Deform is not { IsIdentity: false } && !string.IsNullOrWhiteSpace(t.Text))
                text = t;

            if (layer.HasElements && text == null)
            {
                Bake(layer, "含形狀、多個物件或透視／彎曲文字", output);
                step();
                return;
            }

            var style = PsdStyleWriter.Build(layer.Effects, text);
            if (style.Unsupported.Count > 0)
            {
                Bake(layer, $"效果「{string.Join("、", style.Unsupported)}」在 Photoshop 沒有對應", output);
                step();
                return;
            }

            var record = Properties(layer);
            lock (doc.SyncRoot)
            {
                if (text != null)
                {
                    var (rect, bgra) = RenderPlainText(text);
                    if (bgra != null) record.SetPixels(rect, bgra);
                    else record.SetEmpty();
                    record.Blocks.Add(("TySh", PsdTextWriter.Build(text)));
                }
                else
                {
                    var region = layer.Surface.ExactContentBounds();
                    if (region.Width > 0 && region.Height > 0)
                    {
                        var premul = LayerEffectRenderer.ReadPixels(layer.Surface, region);
                        record.SetPixels(Offset(region, layer.Offset), Unpremultiply(premul, region.Width, region.Height));
                    }
                    else
                    {
                        record.SetEmpty();
                    }
                }
            }
            if (style.ToLfx2() is { } lfx2) record.Blocks.Add(("lfx2", lfx2));
            output.Add(record);

            // 堆疊裡的調整：剪裁到這層的調整圖層，疊在它上面（PS 由下往上）
            foreach (var (adjustment, enabled) in style.ClippedAdjustments)
            {
                var before = output.Count;
                EmitAdjustment(adjustment, adjustment.DisplayName, null, clipped: true, output);
                if (output.Count > before && !enabled) output[^1].Hidden = true;
            }
            step();
        }

        /// <summary>整層（含效果、物件）算成像素寫成一般圖層。</summary>
        private void Bake(LayerNode node, string reason, List<OutLayer> output)
        {
            if (node.HasActiveEffects) LayerEffectRenderer.RenderLayerNow(doc, node, Compositor.StaticGroupSourceLocked);

            var record = Properties(node);
            lock (doc.SyncRoot)
            {
                SKRectI rect;
                uint[]? premul = null;
                if (node.HasActiveEffects && node.FxCache.Rendered)
                {
                    var region = node.FxCache.Surface.ExactContentBounds();
                    rect = Offset(region, node.EffectOffset);
                    if (region.Width > 0 && region.Height > 0) premul = LayerEffectRenderer.ReadPixels(node.FxCache.Surface, region);
                }
                else if (node is RasterLayer raster)
                {
                    var region = LayerEffectRenderer.ContentRegion(raster);
                    rect = Offset(region, raster.Offset);
                    if (region.Width > 0 && region.Height > 0) premul = LayerEffectRenderer.ReadPixelsWithElements(raster, region);
                }
                else
                {
                    rect = node.ContentBounds;
                    if (rect.Width > 0 && rect.Height > 0) premul = Compositor.StaticGroupSourceLocked((GroupLayer)node, rect);
                }

                if (premul != null) record.SetPixels(rect, Unpremultiply(premul, rect.Width, rect.Height));
                else record.SetEmpty();
            }
            output.Add(record);
            notes.Add($"圖層「{record.Name}」{reason}，已轉成像素。");
        }

        private static OutLayer Properties(LayerNode node) => new()
        {
            Name = string.IsNullOrEmpty(node.Name) ? (node is GroupLayer ? "群組" : "圖層") : node.Name,
            Hidden = !node.IsVisible,
            Opacity = (byte)Math.Clamp(Math.Round(node.Opacity * 255), 0, 255),
            BlendKey = BlendKeyOf(node.BlendMode),
        };

        /// <summary>
        /// 文字圖層的點陣快照：Photoshop 自己會照 TySh 重排，這份給不認得文字的程式（與縮圖）看。
        /// 只畫字身，外框／陰影／光暈／漸層已經寫成圖層樣式，畫進去會疊兩次。
        /// </summary>
        private static (SKRectI Rect, byte[]? Bgra) RenderPlainText(TextElement text)
        {
            var plain = text with { Stroke = null, Shadow = null, Glow = null, Gradient = null };
            var bounds = plain.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0 || bounds.Width > 16384 || bounds.Height > 16384)
                return (SKRectI.Empty, null);

            var info = new SKImageInfo(bounds.Width, bounds.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            if (surface == null) return (SKRectI.Empty, null);
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            canvas.Translate(-bounds.Left, -bounds.Top);
            plain.Render(canvas);
            canvas.Flush();

            var straight = new byte[bounds.Width * bounds.Height * 4];
            unsafe
            {
                fixed (byte* ptr = straight)
                {
                    if (!surface.ReadPixels(info.WithAlphaType(SKAlphaType.Unpremul), (IntPtr)ptr, bounds.Width * 4, 0, 0))
                        return (SKRectI.Empty, null);
                }
            }
            return (bounds, straight);
        }
    }

    private static OutLayer WithEmpty(this OutLayer layer)
    {
        layer.SetEmpty();
        return layer;
    }

    private static SKRectI Offset(SKRectI rect, SKPointI by) =>
        new(rect.Left + by.X, rect.Top + by.Y, rect.Right + by.X, rect.Bottom + by.Y);

    /// <summary>我們的 tile 是預乘 BGRA，Photoshop 要直通；交給 Skia 轉。</summary>
    private static unsafe byte[] Unpremultiply(uint[] premul, int width, int height)
    {
        var straight = new byte[width * height * 4];
        var sourceInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        fixed (uint* src = premul)
        fixed (byte* dst = straight)
        {
            using var pixmap = new SKPixmap(sourceInfo, (IntPtr)src, width * 4);
            if (!pixmap.ReadPixels(sourceInfo.WithAlphaType(SKAlphaType.Unpremul), (IntPtr)dst, width * 4))
                throw new InvalidOperationException(".psd 像素轉換失敗（預乘 → 直通 alpha）。");
        }
        return straight;
    }

    private static string BlendKeyOf(BlendMode mode) => mode switch
    {
        BlendMode.Multiply => "mul ",
        BlendMode.Screen => "scrn",
        BlendMode.Overlay => "over",
        BlendMode.Darken => "dark",
        BlendMode.Lighten => "lite",
        BlendMode.ColorDodge => "div ",
        BlendMode.ColorBurn => "idiv",
        BlendMode.HardLight => "hLit",
        BlendMode.SoftLight => "sLit",
        BlendMode.Difference => "diff",
        BlendMode.Exclusion => "smud",
        BlendMode.Hue => "hue ",
        BlendMode.Saturation => "sat ",
        BlendMode.Color => "colr",
        BlendMode.Luminosity => "lum ",
        BlendMode.Additive => "lddg",
        BlendMode.LinearBurn => "lbrn",
        BlendMode.LinearLight => "lLit",
        BlendMode.VividLight => "vLit",
        BlendMode.PinLight => "pLit",
        BlendMode.HardMix => "hMix",
        BlendMode.DarkerColor => "dkCl",
        BlendMode.LighterColor => "lgCl",
        BlendMode.Subtract => "fsub",
        BlendMode.Divide => "fdiv",
        _ => "norm",
    };

    // ---- 寫檔 ----

    private static void WriteFile(Stream stream, Document doc, List<OutLayer> layers, SKImage composite, bool psb)
    {
        var w = new PsdByteWriter();

        // 標頭：RGB 8 位元，通道 R/G/B + 透明度
        w.Ascii("8BPS");
        w.U16(psb ? 2 : 1);
        w.Zero(6);
        w.U16(4);
        w.U32(doc.Height);
        w.U32(doc.Width);
        w.U16(8);
        w.U16(3);

        w.U32(0);   // 色彩模式資料（RGB 沒有）
        WriteImageResources(w, doc.Dpi);

        // 圖層與遮罩資訊
        if (layers.Count == 0)
        {
            w.LengthField(0, psb);
        }
        else
        {
            var info = new PsdByteWriter();
            WriteLayerInfo(info, layers, psb);
            if (info.Length % 2 != 0) info.U8(0);   // Photoshop 把這段補到偶數
            var section = new PsdByteWriter();
            section.LengthField(info.Length, psb);
            section.Append(info);
            section.U32(0);   // 全域圖層遮罩：沒有
            w.LengthField(section.Length, psb);
            w.Append(section);
        }

        WriteMergedImage(w, composite, psb);
        w.WriteTo(stream);
    }

    /// <summary>影像資源：解析度 1005、整體光源角度 1037、整體光源高度 1049。</summary>
    private static void WriteImageResources(PsdByteWriter w, float dpi)
    {
        var res = new PsdByteWriter();
        void Header(int id, int size)
        {
            res.Ascii("8BIM");
            res.U16(id);
            res.Zero(2);   // 空的 Pascal 名稱（長度 0 + 補位）
            res.U32(size);
        }

        var fixedDpi = (uint)Math.Round(Math.Clamp(dpi <= 0 ? 72f : dpi, 1f, 30000f) * 65536);
        Header(1005, 16);
        res.U32(fixedDpi); res.U16(1); res.U16(1);   // 每英寸；寬度單位英寸
        res.U32(fixedDpi); res.U16(1); res.U16(1);

        Header(1037, 4);
        res.I32(120);
        Header(1049, 4);
        res.I32(30);

        w.U32(res.Length);
        w.Append(res);
    }

    private static void WriteLayerInfo(PsdByteWriter info, List<OutLayer> layers, bool psb)
    {
        // 負數：合成影像的第一個多出來的通道是透明度
        info.I16(-layers.Count);
        var channelData = new List<byte[]>();

        foreach (var layer in layers)
        {
            info.I32(layer.Rect.Top);
            info.I32(layer.Rect.Left);
            info.I32(layer.Rect.Bottom);
            info.I32(layer.Rect.Right);

            info.U16(layer.Channels.Count);
            foreach (var (id, samples) in layer.Channels)
            {
                var encoded = EncodeChannel(samples, layer.Rect.Width, layer.Rect.Height, psb);
                info.I16(id);
                info.LengthField(encoded.Length, psb);
                channelData.Add(encoded);
            }

            info.Ascii("8BIM");
            info.Ascii(layer.BlendKey);
            info.U8(layer.Opacity);
            info.U8(layer.Clipped ? 1 : 0);
            info.U8(layer.Hidden ? 0x02 : 0x00);   // bit 1 = 隱藏
            info.U8(0);

            var extra = new PsdByteWriter();
            extra.U32(0);   // 遮色片：沒有
            extra.U32(0);   // 混合範圍：沒有

            // Pascal 名稱（系統字碼頁，這裡只能保證 Latin1；真正的名稱在 luni）
            var pascal = Encoding.Latin1.GetBytes(layer.Name);
            if (pascal.Length > 255) pascal = pascal[..255];
            extra.U8(pascal.Length);
            extra.Bytes(pascal);
            extra.Zero((4 - (pascal.Length + 1) % 4) % 4);

            var luni = new PsdByteWriter();
            luni.U32(layer.Name.Length);
            luni.Bytes(Encoding.BigEndianUnicode.GetBytes(layer.Name));
            WriteBlock(extra, "luni", luni.ToArray());

            if (layer.SectionType != 0)
            {
                var lsct = new PsdByteWriter();
                lsct.U32(layer.SectionType);
                lsct.Ascii("8BIM");
                lsct.Ascii(layer.SectionType == 3 ? "pass" : layer.BlendKey);
                WriteBlock(extra, "lsct", lsct.ToArray());
            }
            foreach (var (key, data) in layer.Blocks) WriteBlock(extra, key, data);

            info.U32(extra.Length);
            info.Append(extra);
        }

        foreach (var data in channelData) info.Bytes(data);
    }

    /// <summary>附加資訊區塊；長度補到偶數（Photoshop 的寫法，讀取端照樣跳得過）。</summary>
    private static void WriteBlock(PsdByteWriter extra, string key, byte[] payload)
    {
        extra.Ascii("8BIM");
        extra.Ascii(key);
        extra.U32(payload.Length);
        extra.Bytes(payload);
        if (payload.Length % 2 != 0) extra.U8(0);
    }

    /// <summary>PackBits：先是每一列的壓縮後長度，接著才是資料。空範圍只有 2 位元組的「原始」標記。</summary>
    private static byte[] EncodeChannel(byte[] samples, int width, int height, bool psb)
    {
        var w = new PsdByteWriter();
        if (width <= 0 || height <= 0 || samples.Length == 0)
        {
            w.U16(0);
            return w.ToArray();
        }
        w.U16(1);
        var rows = new byte[height][];
        for (var y = 0; y < height; y++) rows[y] = PackBits(samples.AsSpan(y * width, width));
        foreach (var row in rows)
        {
            if (psb) w.U32(row.Length);
            else w.U16(row.Length);
        }
        foreach (var row in rows) w.Bytes(row);
        return w.ToArray();
    }

    /// <summary>PackBits：連續相同用重複段（最多 128），否則逐段字面（最多 128）。</summary>
    private static byte[] PackBits(ReadOnlySpan<byte> row)
    {
        var output = new MemoryStream(row.Length + row.Length / 64 + 2);
        var i = 0;
        while (i < row.Length)
        {
            var run = 1;
            while (i + run < row.Length && run < 128 && row[i + run] == row[i]) run++;
            if (run >= 2)
            {
                output.WriteByte((byte)(sbyte)(1 - run));
                output.WriteByte(row[i]);
                i += run;
                continue;
            }
            var start = i;
            while (i < row.Length && i - start < 128 && (i + 1 >= row.Length || row[i + 1] != row[i])) i++;
            output.WriteByte((byte)(i - start - 1));
            output.Write(row[start..i]);
        }
        return output.ToArray();
    }

    /// <summary>合成影像：R、G、B、A 四個通道一段接一段，RLE 時所有列的長度先集中放在前面。</summary>
    private static unsafe void WriteMergedImage(PsdByteWriter w, SKImage composite, bool psb)
    {
        var width = composite.Width;
        var height = composite.Height;
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var bgra = new byte[width * height * 4];
        fixed (byte* ptr = bgra)
        {
            if (!composite.ReadPixels(info, (IntPtr)ptr, width * 4, 0, 0))
                throw new InvalidOperationException(".psd 合成影像讀取失敗。");
        }

        var planes = new byte[4][];
        for (var c = 0; c < 4; c++) planes[c] = new byte[width * height];
        for (var i = 0; i < width * height; i++)
        {
            planes[0][i] = bgra[i * 4 + 2];
            planes[1][i] = bgra[i * 4 + 1];
            planes[2][i] = bgra[i * 4];
            planes[3][i] = bgra[i * 4 + 3];
        }

        w.U16(1);
        var rows = new List<byte[]>(4 * height);
        foreach (var plane in planes)
            for (var y = 0; y < height; y++)
                rows.Add(PackBits(plane.AsSpan(y * width, width)));
        foreach (var row in rows)
        {
            if (psb) w.U32(row.Length);
            else w.U16(row.Length);
        }
        foreach (var row in rows) w.Bytes(row);
    }
}
