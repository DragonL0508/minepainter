using MinePainter.Core.Documents;
using MinePainter.Core.Effects;
using MinePainter.Core.History;
using MinePainter.Core.Layers;
using MinePainter.Core.Vectors;
using SkiaSharp;

namespace MinePainter.Core.IO;

public static partial class PsdFormat
{
    /// <summary>將解析完成的 PSD 記錄轉成文件圖層，集中處理遮罩、剪裁與可編輯物件。</summary>
    private static class LayerBuilder
    {
        // ---- 組圖層樹 ----

        public static void BuildLayerTree(
            Document document, List<LayerRecord> records, Header header, byte[]? palette, List<string> notes)
        {
            var stack = new Stack<GroupLayer>();
            var opened = new List<GroupLayer>();
            GroupLayer Current() => stack.Count > 0 ? stack.Peek() : document.Root;

            // 剪裁的底：最近一個沒被剪裁的點陣圖層（含它的遮色片），或剛收尾的群組（它累加出來的 alpha）。
            // 進入新群組時清掉 —— 群組裡第一層不可能剪裁到群組外面。
            ClipBase? clipBase = null;
            var planes = new Stack<AlphaPlane>();
            planes.Push(new AlphaPlane(document.Width, document.Height));

            try
            {
                foreach (var record in records)
                {
                    switch (record.SectionType)
                    {
                        case 3:
                            var group = new GroupLayer();
                            stack.Push(group);
                            opened.Add(group);
                            planes.Push(new AlphaPlane(document.Width, document.Height));
                            clipBase = null;
                            break;

                        case 1:
                        case 2:
                            if (stack.Count == 0)
                            {
                                notes.Add($"群組「{record.Name}」缺少開頭界線，已略過。");
                                break;
                            }
                            var finished = stack.Pop();
                            opened.Remove(finished);
                            ApplyProperties(finished, record, notes, isGroup: true);
                            finished.IsPassThrough = record.BlendKey == "pass";
                            Current().Add(finished);

                            var groupPlane = planes.Pop();
                            if (finished.Mask != null) groupPlane.ApplyMask(finished.Mask);
                            if (finished.IsVisible) planes.Peek().Accumulate(groupPlane, finished.Opacity);
                            clipBase = new ClipBase(null, SKRectI.Empty, groupPlane, finished.IsVisible);
                            break;

                        default:
                            if ((record.Rect.Width <= 0 || record.Rect.Height <= 0) && record.ParameterBlocks.Count > 0)
                            {
                                // 沒有像素、靠參數呈現的圖層：調整圖層 → 我們的調整圖層；純色／漸層填色 → 整張畫布的像素
                                var special = BuildParameterLayer(record, document, notes, header.ColorSpace, out var fillBgra);
                                if (special == null) break;
                                Current().Add(special);
                                if (special is RasterLayer fill && fillBgra != null)
                                {
                                    if (fill.IsVisible) planes.Peek().Accumulate(fillBgra, new SKRectI(0, 0, document.Width, document.Height), fill.Opacity);
                                    if (!record.Clipped) clipBase = new ClipBase(fillBgra, new SKRectI(0, 0, document.Width, document.Height), null, fill.IsVisible);
                                }
                                break;
                            }
                            var layer = BuildRasterLayer(record, header, palette, notes, record.Clipped ? clipBase : null, out var bgra, out var textGroup);
                            if (textGroup != null)
                            {
                                // 多種樣式的文字：一個群組、每段一層（位置各自算好）；像素快照只給剪裁／群組 alpha 用
                                Current().Add(textGroup);
                                if (textGroup.IsVisible && bgra != null) planes.Peek().Accumulate(bgra, record.Rect, textGroup.Opacity);
                                if (!record.Clipped) clipBase = new ClipBase(bgra, bgra == null ? SKRectI.Empty : record.Rect, null, textGroup.IsVisible);
                                break;
                            }
                            if (layer == null) break;
                            Current().Add(layer);
                            if (record.Clipped && clipBase is { Visible: false }) layer.IsVisible = false;   // 底層藏著，剪裁上去的也看不到
                            if (layer.IsVisible && bgra != null) planes.Peek().Accumulate(bgra, record.Rect, layer.Opacity);
                            if (!record.Clipped)
                                clipBase = new ClipBase(bgra, bgra == null ? SKRectI.Empty : record.Rect, null, layer.IsVisible);
                            break;
                    }
                }

                // 檔案在群組還沒收尾就結束了：把開著的群組原樣掛上去，內容不丟
                while (stack.Count > 0)
                {
                    var group = stack.Pop();
                    opened.Remove(group);
                    group.Name = "群組";
                    Current().Add(group);
                    notes.Add("有群組缺少結尾記錄，已自動補上。");
                }
            }
            catch
            {
                foreach (var group in opened) group.Dispose();
                throw;
            }
        }

        private static void ApplyProperties(LayerNode node, LayerRecord record, List<string> notes, bool isGroup)
        {
            node.Name = string.IsNullOrEmpty(record.Name) ? (isGroup ? "群組" : "圖層") : record.Name;
            node.IsVisible = !record.Hidden;
            node.Opacity = record.Opacity / 255f * (record.FillOpacity / 255f);
            node.BlendMode = MapBlendMode(record.BlendKey, node.Name, isGroup, notes);
            node.RestrictedChannels = record.RestrictedChannels;
            node.Mask = BuildMask(record);
        }

        /// <summary>
        /// 調整圖層（levl／curv／brit…）→ <see cref="AdjustmentLayer"/>；純色／漸層填色（SoCo／GdFl）→ 整張畫布的點陣圖層
        /// （乘上它的遮色片）。對不上的提示後回 null。<paramref name="fillBgra"/> 是填色圖層的像素（剪裁／群組 alpha 用）。
        /// </summary>
        private static LayerNode? BuildParameterLayer(LayerRecord record, Document document, List<string> notes, SKColorSpace? colorSpace, out byte[]? fillBgra)
        {
            fillBgra = null;
            var name = string.IsNullOrEmpty(record.Name) ? "圖層" : record.Name;
            var blocks = record.ParameterBlocks;

            if (blocks.Keys.Any(PsdAdjustmentLayer.Keys.Contains))
            {
                var adjustment = PsdAdjustmentLayer.TryBuild(blocks, notes, out var failure);
                var kind = PsdAdjustmentLayer.DisplayName(blocks.Keys.First(PsdAdjustmentLayer.Keys.Contains));
                if (adjustment == null)
                {
                    notes.Add($"「{name}」是{kind}調整圖層，{failure}，已略過。");
                    return null;
                }
                var layer = new AdjustmentLayer(adjustment);
                ApplyProperties(layer, record, notes, isGroup: false);
                if (record.Clipped)
                    notes.Add($"「{name}」剪裁到下一層的調整改成影響下方所有圖層。");
                return layer;
            }

            var canvas = new SKRectI(0, 0, document.Width, document.Height);
            byte[]? bgra = null;
            if (blocks.TryGetValue("SoCo", out var solid))
            {
                if (PsdAdjustmentLayer.SolidFillColor(solid) is { } color)
                {
                    bgra = new byte[canvas.Width * canvas.Height * 4];
                    for (var i = 0; i < bgra.Length; i += 4)
                    {
                        bgra[i] = color.Blue;
                        bgra[i + 1] = color.Green;
                        bgra[i + 2] = color.Red;
                        bgra[i + 3] = 255;
                    }
                }
            }
            else if (blocks.TryGetValue("GdFl", out var gradientBlock))
            {
                if (PsdAdjustmentLayer.GradientFill(gradientBlock) is { } gradient)
                    bgra = RenderGradientFill(canvas, gradient.Stops, gradient.AngleCcw, gradient.Radial);
            }
            else if (blocks.ContainsKey("PtFl"))
            {
                notes.Add($"「{name}」是圖樣填色圖層，沒有對應，已略過。");
                return null;
            }
            if (bgra == null)
            {
                notes.Add($"「{name}」是填色圖層，參數無法解析，已略過。");
                return null;
            }

            // 填色圖層的形狀就是它的遮色片（沒遮色片＝整張）；向量遮色片（形狀圖層）這裡讀不到
            record.Rect = canvas;

            if (blocks.ContainsKey("vmsk") || blocks.ContainsKey("vsms"))
                notes.Add($"「{name}」的向量遮色片沒有對應，填色會蓋滿整張。");

            var raster = new RasterLayer();
            ApplyProperties(raster, record, notes, isGroup: false);
            if (!IsFullyTransparent(bgra)) CopyUnpremultiplied(raster, bgra, canvas, colorSpace);
            if (record.HasMask) ApplyMask(bgra, record);
            fillBgra = bgra;
            return raster;
        }

        /// <summary>用 Skia 把漸層填滿畫布（直通 alpha 的 BGRA）。PS 角度逆時針、90 = 由下往上。</summary>
        private static byte[] RenderGradientFill(SKRectI canvas, GradientStops stops, float angleCcw, bool radial)
        {
            var info = new SKImageInfo(canvas.Width, canvas.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            using var bitmap = new SKBitmap(info);
            using var skCanvas = new SKCanvas(bitmap);
            var colors = stops.Stops.Select(s => s.Color).ToArray();
            var positions = stops.Stops.Select(s => s.Position).ToArray();
            var cx = canvas.Width / 2f;
            var cy = canvas.Height / 2f;
            using var shader = radial
                ? SKShader.CreateRadialGradient(new SKPoint(cx, cy), MathF.Max(cx, cy), colors, positions, SKShaderTileMode.Clamp)
                : LinearAcrossCanvas(canvas, angleCcw, colors, positions);
            using var paint = new SKPaint { Shader = shader };
            skCanvas.DrawRect(SKRect.Create(canvas.Width, canvas.Height), paint);
            skCanvas.Flush();
            var result = new byte[info.BytesSize];
            System.Runtime.InteropServices.Marshal.Copy(bitmap.GetPixels(), result, 0, result.Length);
            return result;
        }

        private static SKShader LinearAcrossCanvas(SKRectI canvas, float angleCcw, SKColor[] colors, float[] positions)
        {
            var rad = angleCcw * MathF.PI / 180f;
            var dx = MathF.Cos(rad);
            var dy = -MathF.Sin(rad);   // 螢幕 y 往下
            var half = (MathF.Abs(dx) * canvas.Width + MathF.Abs(dy) * canvas.Height) / 2f;
            var cx = canvas.Width / 2f;
            var cy = canvas.Height / 2f;
            return SKShader.CreateLinearGradient(
                new SKPoint(cx - dx * half, cy - dy * half), new SKPoint(cx + dx * half, cy + dy * half),
                colors, positions, SKShaderTileMode.Clamp);
        }

        /// <summary>
        /// 剪裁的底層：點陣圖層給直通 alpha 的 BGRA 與它在文件上的範圍（空範圍＝沒有像素，剪裁後全透明）；
        /// 群組給累加好的畫布 alpha。<paramref name="Visible"/> 是底層本身有沒有顯示。
        /// </summary>
        private sealed record ClipBase(byte[]? Bgra, SKRectI Rect, AlphaPlane? Plane, bool Visible);

        /// <summary>畫布大小的透明度累加器：一層層 src-over 疊上去，就是群組合成後的 alpha。畫布外的不記（剪裁到畫布外看不到）。</summary>
        private sealed class AlphaPlane
        {
            private readonly byte[] _alpha;
            private readonly int _width;
            private readonly int _height;

            public AlphaPlane(int width, int height)
            {
                _width = width;
                _height = height;
                _alpha = new byte[width * height];
            }

            public byte At(int x, int y) =>
                x < 0 || y < 0 || x >= _width || y >= _height ? (byte)0 : _alpha[y * _width + x];

            public void Accumulate(byte[] bgra, SKRectI rect, float opacity)
            {
                var scale = (int)Math.Round(opacity * 255);
                var left = Math.Max(rect.Left, 0);
                var top = Math.Max(rect.Top, 0);
                var right = Math.Min(rect.Right, _width);
                var bottom = Math.Min(rect.Bottom, _height);
                for (var y = top; y < bottom; y++)
                {
                    for (var x = left; x < right; x++)
                    {
                        var a = bgra[((y - rect.Top) * rect.Width + (x - rect.Left)) * 4 + 3] * scale / 255;
                        Over(y * _width + x, a);
                    }
                }
            }

            public void Accumulate(AlphaPlane other, float opacity)
            {
                var scale = (int)Math.Round(opacity * 255);
                for (var i = 0; i < _alpha.Length; i++)
                    Over(i, other._alpha[i] * scale / 255);
            }

            public void ApplyMask(LayerMask mask)
            {
                for (var y = 0; y < _height; y++)
                for (var x = 0; x < _width; x++)
                {
                    var i = y * _width + x;
                    _alpha[i] = (byte)((_alpha[i] * mask.At(x, y) + 127) / 255);
                }
            }

            private void Over(int index, int a)
            {
                if (a == 0) return;
                _alpha[index] = (byte)(a + _alpha[index] * (255 - a) / 255);
            }
        }

        /// <summary>
        /// 組出一個點陣圖層。<paramref name="clipBase"/> 非 null 時把它的 alpha 乘進來（Photoshop 的剪裁遮色片）；
        /// <paramref name="bgra"/> 回傳這層算好的直通 alpha 像素，供下一層當剪裁的底。
        /// </summary>
        private static RasterLayer? BuildRasterLayer(
            LayerRecord record, Header header, byte[]? palette, List<string> notes, ClipBase? clipBase, out byte[]? bgra,
            out GroupLayer? textGroup)
        {
            bgra = null;
            textGroup = null;
            var layer = new RasterLayer();
            ApplyProperties(layer, record, notes, isGroup: false);
            try
            {
                if (record.Rect.Width <= 0 || record.Rect.Height <= 0)
                {
                    if (record.IsAdjustmentOrFill)
                    {
                        notes.Add($"「{layer.Name}」是調整或填色圖層，沒有像素可匯入，已略過。");
                        layer.Dispose();
                        return null;
                    }
                    return layer;   // 真的空白圖層：留一層空的，名字與順序不變
                }

                bgra = ComposeBgra(record, header, palette);

                if (record.Clipped)
                {
                    if (clipBase != null) ApplyClip(bgra, record.Rect, clipBase);
                    else notes.Add($"「{layer.Name}」設了剪裁但底下沒有圖層，已當成一般圖層。");
                }

                // 圖層樣式一律掛成效果堆疊（文字圖層也是），在圖層屬性的效果面板就能改
                var style = ParseStyle(record, header, layer.Name, notes);
                if (style is { IsEmpty: false }) layer.SetEffects(style.ToLayerEffects());
                // With no fill, a lone gradient overlay is the complete layer.
                // Preserve its opacity/blend independently of the invisible fill.
                if (record.FillOpacity == 0 && style?.Gradient is { } gradient && layer.Effects.Count == 1)
                {
                    layer.Opacity = record.Opacity / 255f * gradient.Opacity / 100f;
                    layer.BlendMode = gradient.BlendMode switch
                    {
                        "Mltp" => BlendMode.Multiply,
                        "Scrn" => BlendMode.Screen,
                        "Ovrl" => BlendMode.Overlay,
                        _ => BlendMode.Normal,
                    };
                }
                if (style is { Unsupported.Count: > 0 })
                    notes.Add($"「{layer.Name}」的圖層樣式裡，{string.Join("、", style.Unsupported.Distinct())}沒有對應，已略過。");

                if (record.TextData != null && BuildText(record, layer.Name, notes, header.Dpi) is { Count: > 0 } texts)
                {
                    // 文字圖層不變式：有物件就沒有像素。點陣快照只留給剪裁／群組 alpha 當底用
                    if (record.HasMask) ApplyMask(bgra, record);
                    if (texts.Count == 1)
                    {
                        layer.AddElement(texts[0]);
                        return layer;
                    }

                    // 多段樣式：群組沿用圖層的名字、可見性；每段一層，效果與不透明度各帶一份（與「分離文字」同構）
                    var group = new GroupLayer { Name = layer.Name, IsVisible = layer.IsVisible, Mask = layer.Mask, RestrictedChannels = layer.RestrictedChannels };
                    foreach (var text in texts)
                    {
                        var piece = new RasterLayer
                        {
                            Name = VectorCommands.TextLayerNameFor(text.Text),
                            Opacity = layer.Opacity,
                            BlendMode = layer.BlendMode,
                        };
                        piece.AddElement(text);
                        if (layer.HasEffects) piece.SetEffects([.. layer.Effects.Select(fx => fx with { Id = Guid.NewGuid() })]);
                        group.Add(piece);
                    }
                    layer.Dispose();
                    textGroup = group;
                    return null;
                }

                if (!IsFullyTransparent(bgra)) CopyUnpremultiplied(layer, bgra, record.Rect, header.ColorSpace);
                if (record.HasMask) ApplyMask(bgra, record);
                return layer;
            }
            catch
            {
                layer.Dispose();
                throw;
            }
        }

        /// <summary>把各色彩模式的 planar 通道組成 BGRA 直通 alpha。</summary>
        public static byte[] ComposeBgra(LayerRecord record, Header header, byte[]? palette)
        {
            var width = record.Rect.Width;
            var height = record.Rect.Height;
            var count = width * height;
            var bgra = new byte[count * 4];

            byte[]? Channel(int id) => record.Channels.FirstOrDefault(c => c.Id == id)?.Samples;
            var alpha = Channel(-1);

            switch (header.Mode)
            {
                case ColorMode.Rgb:
                    var r = Channel(0);
                    var g = Channel(1);
                    var b = Channel(2);
                    for (var i = 0; i < count; i++)
                    {
                        bgra[i * 4 + 0] = b?[i] ?? 0;
                        bgra[i * 4 + 1] = g?[i] ?? 0;
                        bgra[i * 4 + 2] = r?[i] ?? 0;
                        bgra[i * 4 + 3] = alpha?[i] ?? 255;
                    }
                    break;

                case ColorMode.Cmyk:
                    // Photoshop 存的 CMYK 是反相的（255 = 沒有油墨），所以直接相乘就是 RGB
                    var c = Channel(0);
                    var m = Channel(1);
                    var y = Channel(2);
                    var k = Channel(3);
                    for (var i = 0; i < count; i++)
                    {
                        var ink = k?[i] ?? 255;
                        bgra[i * 4 + 0] = (byte)((y?[i] ?? 255) * ink / 255);
                        bgra[i * 4 + 1] = (byte)((m?[i] ?? 255) * ink / 255);
                        bgra[i * 4 + 2] = (byte)((c?[i] ?? 255) * ink / 255);
                        bgra[i * 4 + 3] = alpha?[i] ?? 255;
                    }
                    break;

                case ColorMode.Indexed:
                    var index = Channel(0);
                    for (var i = 0; i < count; i++)
                    {
                        var p = index?[i] ?? 0;
                        bgra[i * 4 + 0] = palette![512 + p];
                        bgra[i * 4 + 1] = palette[256 + p];
                        bgra[i * 4 + 2] = palette[p];
                        bgra[i * 4 + 3] = alpha?[i] ?? 255;
                    }
                    break;

                default:    // 灰階、雙色調
                    var gray = Channel(0);
                    for (var i = 0; i < count; i++)
                    {
                        var v = gray?[i] ?? 0;
                        bgra[i * 4 + 0] = v;
                        bgra[i * 4 + 1] = v;
                        bgra[i * 4 + 2] = v;
                        bgra[i * 4 + 3] = alpha?[i] ?? 255;
                    }
                    break;
            }
            return bgra;
        }

        private static LayerMask? BuildMask(LayerRecord record)
        {
            if (!record.HasMask) return null;
            var bounds = record.MaskRect;
            // PSD mask rectangles are stored in document coordinates.
            var alpha = record.Channels.FirstOrDefault(c => c.Id == -2)?.Samples
                ?? Enumerable.Repeat(record.MaskDefault, checked(Math.Max(0, bounds.Width) * Math.Max(0, bounds.Height))).ToArray();
            return new LayerMask(bounds, alpha, record.MaskDefault) {
                Enabled = (record.MaskFlags & 2) == 0, Inverted = (record.MaskFlags & 4) != 0,
                Density = record.MaskDensity, Feather = record.MaskFeather };
        }

        // Coverage for clipping-base bookkeeping only; native pixels stay unmasked.
        private static void ApplyMask(byte[] bgra, LayerRecord record)
        {
            var mask = BuildMask(record)?.Rendered;
            if (mask == null) return;
            for (var y = 0; y < record.Rect.Height; y++)
            for (var x = 0; x < record.Rect.Width; x++)
            {
                var i = (y * record.Rect.Width + x) * 4 + 3;
                bgra[i] = (byte)((bgra[i] * mask.At(record.Rect.Left + x, record.Rect.Top + y) + 127) / 255);
            }
        }

        private static PsdLayerStyle? ParseStyle(LayerRecord record, Header header, string name, List<string> notes)
        {
            if (record.StyleData == null)
            {
                if (record.HasLegacyStyle) notes.Add($"「{name}」用的是舊版圖層樣式，沒有匯入。");
                return null;
            }
            try
            {
                return PsdLayerStyle.Parse(record.StyleData, header.GlobalAngle);
            }
            catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
            {
                notes.Add($"「{name}」的圖層樣式無法解析，已略過。");
                return null;
            }
        }

        /// <summary>解出可編輯文字（多段樣式會是多個）；解不出來提示原因並回 null（呼叫端退回點陣）。</summary>
        private static IReadOnlyList<TextElement>? BuildText(LayerRecord record, string name, List<string> notes, float dpi)
        {
            IReadOnlyList<TextElement>? text;
            string? failure;
            try
            {
                text = PsdTextLayer.TryBuild(record.TextData!, notes, out failure, dpi);
            }
            catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException or FormatException)
            {
                text = null;
                failure = "排版資料無法解析";
            }

            if (text == null)
            {
                notes.Add($"文字圖層「{name}」已轉成像素（{failure}）。");
                return null;
            }
            return text;
        }

        /// <summary>剪裁遮色片：只留底層有像素的地方。底層範圍外一律透明。</summary>
        private static void ApplyClip(byte[] bgra, SKRectI rect, ClipBase clipBase)
        {
            var width = rect.Width;
            for (var y = 0; y < rect.Height; y++)
            {
                var docY = rect.Top + y;
                for (var x = 0; x < width; x++)
                {
                    var docX = rect.Left + x;
                    var i = (y * width + x) * 4 + 3;
                    int baseAlpha;
                    if (clipBase.Plane != null)
                        baseAlpha = clipBase.Plane.At(docX, docY);
                    else if (clipBase.Bgra != null && clipBase.Rect.Contains(docX, docY))
                        baseAlpha = clipBase.Bgra[((docY - clipBase.Rect.Top) * clipBase.Rect.Width + (docX - clipBase.Rect.Left)) * 4 + 3];
                    else
                        baseAlpha = 0;
                    bgra[i] = (byte)((bgra[i] * baseAlpha + 127) / 255);
                }
            }
        }

        /// <summary>
        /// Photoshop 的直通 alpha 交給 Skia 轉成我們 tile 用的預乘，寫到圖層範圍的左上角。
        /// <paramref name="colorSpace"/> 不是 null 時順便從檔案的色彩空間轉成 sRGB（同一次 ReadPixels 做完）。
        /// </summary>
        public static unsafe void CopyUnpremultiplied(RasterLayer layer, byte[] bgra, SKRectI rect, SKColorSpace? colorSpace)
        {
            var sourceInfo = new SKImageInfo(rect.Width, rect.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul, colorSpace);
            var targetInfo = sourceInfo.WithAlphaType(SKAlphaType.Premul);
            if (colorSpace != null) targetInfo = targetInfo.WithColorSpace(SKColorSpace.CreateSrgb());
            using var premultiplied = new SKBitmap(targetInfo);
            using var destination = premultiplied.PeekPixels();

            fixed (byte* scan0 = bgra)
            {
                using var source = new SKPixmap(sourceInfo, (IntPtr)scan0, rect.Width * 4);
                if (!source.ReadPixels(destination))
                    throw new InvalidDataException(".psd 像素轉換失敗（直通 alpha → 預乘）。");
            }

            layer.Surface.CopyFrom(destination, new SKPointI(rect.Left, rect.Top));
        }

        public static bool IsFullyTransparent(byte[] bgra)
        {
            for (var i = 3; i < bgra.Length; i += 4)
                if (bgra[i] != 0) return false;
            return true;
        }

        // ---- 混合模式 ----

        private static BlendMode MapBlendMode(string key, string name, bool isGroup, List<string> notes)
        {
            switch (key)
            {
                case "norm": return BlendMode.Normal;
                case "pass": return BlendMode.Normal;   // GroupLayer.IsPassThrough controls backdrop access.
                case "mul ": return BlendMode.Multiply;
                case "scrn": return BlendMode.Screen;
                case "over": return BlendMode.Overlay;
                case "dark": return BlendMode.Darken;
                case "lite": return BlendMode.Lighten;
                case "div ": return BlendMode.ColorDodge;
                case "idiv": return BlendMode.ColorBurn;
                case "hLit": return BlendMode.HardLight;
                case "sLit": return BlendMode.SoftLight;
                case "diff": return BlendMode.Difference;
                case "smud": return BlendMode.Exclusion;
                case "hue ": return BlendMode.Hue;
                case "sat ": return BlendMode.Saturation;
                case "colr": return BlendMode.Color;
                case "lum ": return BlendMode.Luminosity;
                case "lddg": return BlendMode.Additive;
                case "lbrn": return BlendMode.LinearBurn;
                case "lLit": return BlendMode.LinearLight;
                case "vLit": return BlendMode.VividLight;
                case "pLit": return BlendMode.PinLight;
                case "hMix": return BlendMode.HardMix;
                case "dkCl": return BlendMode.DarkerColor;
                case "lgCl": return BlendMode.LighterColor;
                case "fsub": return BlendMode.Subtract;
                case "fdiv": return BlendMode.Divide;
            }

            // Skia 沒有的算式，挑最接近的頂著並提示
            var (fallback, label) = key switch
            {
                "diss" => (BlendMode.Normal, "溶解"),
                _ => (BlendMode.Normal, key.Trim()),
            };
            notes.Add($"{(isGroup ? "群組" : "圖層")}「{name}」的混合模式「{label}」沒有對應，已改為{Describe(fallback)}。");
            return fallback;
        }

        private static string Describe(BlendMode mode) => mode switch
        {
            BlendMode.Normal => "一般",
            BlendMode.Multiply => "色彩增值",
            BlendMode.Darken => "變暗",
            BlendMode.Lighten => "變亮",
            BlendMode.HardLight => "實光",
            BlendMode.Difference => "差異化",
            BlendMode.ColorDodge => "加亮顏色",
            _ => mode.ToString(),
        };
    }
}
