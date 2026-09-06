using System.IO.Compression;
using System.Text;
using MinePainter.Core.Documents;
using MinePainter.Core.History;
using MinePainter.Core.Layers;
using SkiaSharp;

namespace MinePainter.Core.IO;

/// <summary>
/// 匯出成 paint.net 專案檔 —— 每個圖層一層，群組拆開（paint.net 沒有群組）。
/// 圖層的效果堆疊與文字物件烙成像素（<see cref="LayerFlattener"/>）；群組本身有不透明度、混合模式或效果時，
/// 拆開會變樣，就把整組合成一層。調整圖層 paint.net 沒有，略過並提示。快速模式以輸出解析度寫。
/// 混合模式只對得上 paint.net 有的那幾種（一般、色彩增值、相加、加深、加亮、覆蓋、差異化、變亮、變暗、濾色），其餘改一般並提示。
///
/// 物件圖照 paint.net 5.1（5.112）自己存出來的樣子逐欄位寫（類別名、組件名、成員順序一個都不能差，
/// 差一個 paint.net 就當檔案損毀）：Document → LayerList → BitmapLayer[] → Surface → MemoryBlock（延後像素）。
/// 物件的寫出順序模仿 BinaryFormatter 的佇列（先參照到的先寫；同型別第二次起用 ClassWithId 重用中繼資料），
/// 像素段照 MemoryBlock 被寫出的順序接在物件圖後面，256 KiB 一塊、每塊各自 gzip（格式版本 0）。XML 標頭帶縮圖。
/// </summary>
public static partial class PdnFormat
{
    private const string SavedWithVersion = "5.112.9563.32325";
    private const string DataAssembly = "PaintDotNet.Data, Version=5.112.9563.32325, Culture=neutral, PublicKeyToken=null";
    private const string CoreAssembly = "PaintDotNet.Core, Version=5.112.9563.32325, Culture=neutral, PublicKeyToken=null";
    private const string KeyValuePairType =
        "System.Collections.Generic.KeyValuePair`2[[System.String, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089],[System.String, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]]";
    private const int ChunkSize = 256 * 1024;

    public static void Save(Document doc, string path, IProgress<double>? progress = null) => Save(doc, path, progress, out _);

    /// <summary><paramref name="warnings"/>：被烙成像素的群組、略過的調整圖層、改成一般的混合模式。</summary>
    public static void Save(Document doc, string path, IProgress<double>? progress, out IReadOnlyList<string> warnings)
    {
        var buffer = new MemoryStream();
        Save(doc, buffer, progress, out warnings);
        using var file = File.Create(path);
        buffer.WriteTo(file);
    }

    public static void Save(Document doc, Stream stream, IProgress<double>? progress, out IReadOnlyList<string> warnings)
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
            progress?.Report(0.2);

            var layers = new List<PdnLayer>();
            var total = Math.Max(1, source.Descendants().Count());
            var done = 0;
            void Step() => progress?.Report(0.2 + 0.5 * ++done / total);
            foreach (var child in source.Root.Children.ToList())
                Emit(source, child, parentHidden: false, layers, notes, Step);
            if (layers.Count == 0)
                layers.Add(new PdnLayer("背景", true, 255, BlendMode.Normal, new byte[source.Width * source.Height * 4]));

            using var composite = Compositing.Compositor.RenderComposite(source);
            progress?.Report(0.75);

            var body = new MemoryStream();
            WriteObjectGraph(body, source.Width, source.Height, layers);
            progress?.Report(0.9);

            var header = Encoding.UTF8.GetBytes(BuildXmlHeader(composite, source.Width, source.Height, layers.Count));
            stream.Write("PDN3"u8);
            stream.WriteByte((byte)(header.Length & 0xFF));
            stream.WriteByte((byte)((header.Length >> 8) & 0xFF));
            stream.WriteByte((byte)((header.Length >> 16) & 0xFF));
            stream.Write(header);
            stream.WriteByte(0x00);   // 指示子：其後未壓縮
            stream.WriteByte(0x01);
            body.WriteTo(stream);
            progress?.Report(1);
        }
        finally
        {
            scaled?.Dispose();
        }
    }

    // ---- 圖層 ----

    /// <summary>一層 paint.net 圖層：整張畫布大小的 BGRA 直通 alpha。</summary>
    private sealed record PdnLayer(string Name, bool Visible, byte Opacity, BlendMode Blend, byte[] Bgra);

    private static void Emit(Document doc, LayerNode node, bool parentHidden, List<PdnLayer> output, List<string> notes, Action step)
    {
        switch (node)
        {
            case GroupLayer group when group.Opacity >= 0.999f && group.BlendMode == BlendMode.Normal && !group.HasActiveEffects:
                // 拆開：子層各自一層，群組藏著就整組藏
                foreach (var child in group.Children.ToList())
                    Emit(doc, child, parentHidden || !group.IsVisible, output, notes, step);
                step();
                break;
            case GroupLayer group:
                notes.Add($"群組「{group.Name}」有不透明度／混合模式／效果，paint.net 沒有群組，已整組合成一層。");
                output.Add(Flatten(doc, group, parentHidden, notes));
                foreach (var _ in group.Children) step();
                step();
                break;
            case AdjustmentLayer adjustment:
                notes.Add($"調整圖層「{adjustment.Name}」paint.net 沒有對應，已略過。");
                step();
                break;
            default:
                output.Add(Flatten(doc, node, parentHidden, notes));
                step();
                break;
        }
    }

    private static PdnLayer Flatten(Document doc, LayerNode node, bool parentHidden, List<string> notes)
    {
        var bgra = new byte[doc.Width * doc.Height * 4];
        var (rect, premul) = LayerFlattener.Render(doc, node);
        if (premul != null) LayerFlattener.BlitStraight(bgra, doc.Width, doc.Height, rect, premul);

        var blend = node.BlendMode;
        if (BlendOpName(blend) == null)
        {
            notes.Add($"圖層「{node.Name}」的混合模式 paint.net 沒有，已改為一般。");
            blend = BlendMode.Normal;
        }
        var name = string.IsNullOrEmpty(node.Name) ? (node is GroupLayer ? "群組" : "圖層") : node.Name;
        return new PdnLayer(name, node.IsVisible && !parentHidden,
            (byte)Math.Clamp(Math.Round(node.Opacity * 255), 0, 255), blend, bgra);
    }

    /// <summary>PaintDotNet.UserBlendOps 的類別名（不含 BlendOp 字尾）；paint.net 沒有的回 null。</summary>
    private static string? BlendOpName(BlendMode mode) => mode switch
    {
        BlendMode.Normal => "Normal",
        BlendMode.Multiply => "Multiply",
        BlendMode.Additive => "Additive",
        BlendMode.ColorBurn => "ColorBurn",
        BlendMode.ColorDodge => "ColorDodge",
        BlendMode.Overlay => "Overlay",
        BlendMode.Difference => "Difference",
        BlendMode.Lighten => "Lighten",
        BlendMode.Darken => "Darken",
        BlendMode.Screen => "Screen",
        _ => null,
    };

    /// <summary>PaintDotNet.LayerBlendMode 列舉值（順序見 <see cref="LayerBlendModeNames"/>）。</summary>
    private static int BlendModeValue(BlendMode mode) =>
        Math.Max(0, Array.IndexOf(LayerBlendModeNames, BlendOpName(mode) ?? "Normal"));

    /// <summary>XML 標頭：尺寸、圖層數、版本，加一張最長邊 256 的 PNG 縮圖（開啟對話框與檔案總管用）。</summary>
    private static string BuildXmlHeader(SKImage composite, int width, int height, int layerCount)
    {
        var scale = 256f / Math.Max(width, height);
        var tw = Math.Max(1, (int)Math.Round(width * Math.Min(1f, scale)));
        var th = Math.Max(1, (int)Math.Round(height * Math.Min(1f, scale)));
        string thumb;
        using (var surface = SKSurface.Create(new SKImageInfo(tw, th, SKColorType.Bgra8888, SKAlphaType.Premul)))
        {
            surface.Canvas.Clear(SKColors.Transparent);
            using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High })
                surface.Canvas.DrawImage(composite, SKRect.Create(tw, th), paint);
            using var image = surface.Snapshot();
            using var png = image.Encode(SKEncodedImageFormat.Png, 90);
            thumb = Convert.ToBase64String(png.AsSpan());
        }
        return $"<pdnImage width=\"{width}\" height=\"{height}\" layers=\"{layerCount}\" savedWithVersion=\"{SavedWithVersion}\">" +
               $"<custom><thumb png=\"{thumb}\" /></custom></pdnImage>";
    }

    // ---- 物件圖（MS-NRBF）----

    private const byte RecordHeader = 0, RecordClassWithId = 1, RecordSystemClass = 4, RecordClass = 5, RecordString = 6,
        RecordBinaryArray = 7, RecordReference = 9, RecordEnd = 11, RecordLibrary = 12, RecordNullMultiple256 = 13, RecordObjectArray = 16;
    private const byte TypePrimitive = 0, TypeString = 1, TypeSystemClass = 3, TypeClass = 4, TypeObjectArray = 5;
    private const byte PrimBoolean = 1, PrimByte = 2, PrimInt32 = 8, PrimInt64 = 9;
    private const int DataLib = 2, CoreLib = 22;

    private readonly record struct Member(string Name, byte BinaryType, object? Info);

    private static Member Prim(string name, byte primitive) => new(name, TypePrimitive, primitive);
    private static Member Str(string name) => new(name, TypeString, null);
    private static Member Cls(string name, string typeName, int library) => new(name, TypeClass, (typeName, library));
    private static Member Sys(string name, string typeName) => new(name, TypeSystemClass, typeName);
    private static Member ObjArray(string name) => new(name, TypeObjectArray, null);

    /// <summary>
    /// 模仿 BinaryFormatter 的寫出順序：物件被參照到時排進佇列、依序寫；同一型別第二次起寫 ClassWithId；
    /// 組件在第一個用到它的類別記錄之前宣告。像素區塊照 MemoryBlock 被寫出的順序收集。
    /// </summary>
    private sealed class NrbfGraph(BinaryWriter w)
    {
        private readonly Queue<(int Id, Action<int> Write)> _pending = new();
        private readonly Dictionary<string, int> _classMeta = new();
        private readonly HashSet<int> _libraries = [];
        private int _next = 1;

        public List<byte[]> DeferredBlocks { get; } = [];

        /// <summary>所有 LayerProperties 共用同一個空的中繼資料陣列（BinaryFormatter 對同一實例只寫一次）；−1 = 還沒寫。</summary>
        public int SharedMetadata { get; set; } = -1;

        public int Allocate() => _next++;

        /// <summary>排進佇列，回傳這個物件的編號（呼叫端拿去寫 Ref）。</summary>
        public int Defer(Action<int> write)
        {
            var id = _next++;
            _pending.Enqueue((id, write));
            return id;
        }

        public void Flush()
        {
            while (_pending.Count > 0)
            {
                var (id, write) = _pending.Dequeue();
                write(id);
            }
        }

        public void Ref(int id)
        {
            w.Write(RecordReference);
            w.Write(id);
        }

        public void String(string value)
        {
            w.Write(RecordString);
            w.Write(Allocate());
            w.Write(value);
        }

        public void BeginClass(int id, string typeName, int library, Member[] members)
        {
            EnsureLibrary(library);
            foreach (var m in members)
                if (m.BinaryType == TypeClass) EnsureLibrary((((string, int))m.Info!).Item2);
            if (_classMeta.TryGetValue(typeName, out var metaId))
            {
                w.Write(RecordClassWithId);
                w.Write(id);
                w.Write(metaId);
                return;
            }
            _classMeta[typeName] = id;
            w.Write(RecordClass);
            WriteClassInfo(id, typeName, members);
            w.Write(library);
        }

        public void BeginSystemClass(int id, string typeName, Member[] members)
        {
            if (_classMeta.TryGetValue(typeName, out var metaId))
            {
                w.Write(RecordClassWithId);
                w.Write(id);
                w.Write(metaId);
                return;
            }
            _classMeta[typeName] = id;
            w.Write(RecordSystemClass);
            WriteClassInfo(id, typeName, members);
        }

        /// <summary>userMetadataItems：長度 0 的 KeyValuePair&lt;string,string&gt;[]（BinaryArray、單維、無下界）。</summary>
        public void EmptyMetadataArray(int id)
        {
            w.Write(RecordBinaryArray);
            w.Write(id);
            w.Write((byte)0);       // BinaryArrayType.Single
            w.Write(1);             // rank
            w.Write(0);             // length
            w.Write(TypeSystemClass);
            w.Write(KeyValuePairType);
        }

        private void EnsureLibrary(int library)
        {
            if (!_libraries.Add(library)) return;
            w.Write(RecordLibrary);
            w.Write(library);
            w.Write(library == DataLib ? DataAssembly : CoreAssembly);
        }

        /// <summary>ClassInfo + MemberTypeInfo：先全部名稱、再全部型別、再全部附加資訊。</summary>
        private void WriteClassInfo(int objectId, string typeName, Member[] members)
        {
            w.Write(objectId);
            w.Write(typeName);
            w.Write(members.Length);
            foreach (var m in members) w.Write(m.Name);
            foreach (var m in members) w.Write(m.BinaryType);
            foreach (var m in members)
            {
                switch (m.BinaryType)
                {
                    case TypePrimitive:
                        w.Write((byte)m.Info!);
                        break;
                    case TypeSystemClass:
                        w.Write((string)m.Info!);
                        break;
                    case TypeClass:
                        var (name, library) = ((string, int))m.Info!;
                        w.Write(name);
                        w.Write(library);
                        break;
                }
            }
        }
    }

    private static void WriteObjectGraph(Stream stream, int width, int height, IReadOnlyList<PdnLayer> layers)
    {
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        var g = new NrbfGraph(w);

        w.Write(RecordHeader); w.Write(1); w.Write(-1); w.Write(1); w.Write(0);

        var documentId = g.Defer(id =>
        {
            g.BeginClass(id, "PaintDotNet.Document", DataLib,
            [
                Prim("isDisposed", PrimBoolean),
                Cls("layers", "PaintDotNet.LayerList", DataLib),
                Prim("width", PrimInt32),
                Prim("height", PrimInt32),
                Sys("savedWith", "System.Version"),
                Sys("userMetadataItems", KeyValuePairType + "[]"),
            ]);
            var layersId = g.Defer(listId =>
            {
                g.BeginClass(listId, "PaintDotNet.LayerList", DataLib,
                [
                    Cls("parent", "PaintDotNet.Document", DataLib),
                    ObjArray("ArrayList+_items"),
                    Prim("ArrayList+_size", PrimInt32),
                    Prim("ArrayList+_version", PrimInt32),
                ]);
                var itemsId = g.Defer(arrayId =>
                {
                    // ArrayList 的容量陣列：實際數量 + 幾格 null
                    var capacity = Math.Max(4, layers.Count + 2);
                    w.Write(RecordObjectArray); w.Write(arrayId); w.Write(capacity);
                    var layerIds = new int[layers.Count];
                    for (var i = 0; i < layers.Count; i++)
                    {
                        var layer = layers[i];
                        var isBackground = i == 0;
                        layerIds[i] = g.Defer(layerId => WriteBitmapLayer(g, w, layerId, layer, width, height, isBackground));
                    }
                    foreach (var layerId in layerIds) g.Ref(layerId);
                    w.Write(RecordNullMultiple256); w.Write((byte)(capacity - layers.Count));
                });
                g.Ref(id);
                g.Ref(itemsId);
                w.Write(layers.Count);
                w.Write(4);
            });
            var versionId = g.Defer(versionId =>
            {
                g.BeginSystemClass(versionId, "System.Version",
                    [Prim("_Major", PrimInt32), Prim("_Minor", PrimInt32), Prim("_Build", PrimInt32), Prim("_Revision", PrimInt32)]);
                w.Write(5); w.Write(112); w.Write(9563); w.Write(32325);
            });
            var metadataId = g.Defer(g.EmptyMetadataArray);
            w.Write(false);
            g.Ref(layersId);
            w.Write(width);
            w.Write(height);
            g.Ref(versionId);
            g.Ref(metadataId);
        });
        _ = documentId;
        g.Flush();
        w.Write(RecordEnd);
        w.Flush();

        foreach (var block in g.DeferredBlocks) WriteDeferredBlock(stream, block);
    }

    private static void WriteBitmapLayer(NrbfGraph g, BinaryWriter w, int id, PdnLayer layer, int width, int height, bool isBackground)
    {
        g.BeginClass(id, "PaintDotNet.BitmapLayer", DataLib,
        [
            Cls("properties", "PaintDotNet.BitmapLayer+BitmapLayerProperties", DataLib),
            Cls("surface", "PaintDotNet.Surface", CoreLib),
            Prim("Layer+isDisposed", PrimBoolean),
            Prim("Layer+width", PrimInt32),
            Prim("Layer+height", PrimInt32),
            Cls("Layer+properties", "PaintDotNet.Layer+LayerProperties", DataLib),
        ]);

        var opName = "PaintDotNet.UserBlendOps+" + BlendOpName(layer.Blend) + "BlendOp";
        var propertiesId = g.Defer(propsId =>
        {
            g.BeginClass(propsId, "PaintDotNet.BitmapLayer+BitmapLayerProperties", DataLib,
                [Cls("blendOp", "PaintDotNet.UserBlendOps+NormalBlendOp", DataLib)]);
            var opId = g.Defer(blendId => g.BeginClass(blendId, opName, DataLib, []));
            g.Ref(opId);
        });
        var surfaceId = g.Defer(surfId =>
        {
            g.BeginClass(surfId, "PaintDotNet.Surface", CoreLib,
            [
                Prim("width", PrimInt32),
                Prim("height", PrimInt32),
                Prim("stride", PrimInt32),
                Cls("scan0", "PaintDotNet.MemoryBlock", CoreLib),
            ]);
            var blockId = g.Defer(memId =>
            {
                g.BeginClass(memId, "PaintDotNet.MemoryBlock", CoreLib,
                    [Prim("length64", PrimInt64), Prim("hasParent", PrimBoolean), Prim("deferred", PrimBoolean)]);
                w.Write((long)layer.Bgra.Length);
                w.Write(false);
                w.Write(true);
                g.DeferredBlocks.Add(layer.Bgra);
            });
            w.Write(width);
            w.Write(height);
            w.Write(width * 4);
            g.Ref(blockId);
        });
        var layerPropertiesId = g.Defer(lpId =>
        {
            g.BeginClass(lpId, "PaintDotNet.Layer+LayerProperties", DataLib,
            [
                Str("name"),
                Sys("userMetadataItems", KeyValuePairType + "[]"),
                Prim("visible", PrimBoolean),
                Prim("isBackground", PrimBoolean),
                Prim("opacity", PrimByte),
                Cls("blendMode", "PaintDotNet.LayerBlendMode", DataLib),
            ]);
            if (g.SharedMetadata < 0) g.SharedMetadata = g.Defer(g.EmptyMetadataArray);
            g.String(layer.Name);
            g.Ref(g.SharedMetadata);
            w.Write(layer.Visible);
            w.Write(isBackground);
            w.Write(layer.Opacity);
            // 列舉是值型別，BinaryFormatter 就地寫成類別
            g.BeginClass(g.Allocate(), "PaintDotNet.LayerBlendMode", DataLib, [Prim("value__", PrimInt32)]);
            w.Write(BlendModeValue(layer.Blend));
        });
        g.Ref(propertiesId);
        g.Ref(surfaceId);
        w.Write(false);
        w.Write(width);
        w.Write(height);
        g.Ref(layerPropertiesId);
    }

    // ---- 延後像素 ----

    /// <summary>格式版本 0（每塊 gzip）+ 區塊大小，接著每塊：編號、資料長度、資料（全部大端序）。</summary>
    private static void WriteDeferredBlock(Stream stream, byte[] data)
    {
        stream.WriteByte(0);
        WriteUInt32BigEndian(stream, ChunkSize);

        var count = (data.Length + ChunkSize - 1) / ChunkSize;
        for (var i = 0; i < count; i++)
        {
            var offset = i * ChunkSize;
            var length = Math.Min(ChunkSize, data.Length - offset);
            var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
                gzip.Write(data, offset, length);

            WriteUInt32BigEndian(stream, (uint)i);
            WriteUInt32BigEndian(stream, (uint)compressed.Length);
            compressed.WriteTo(stream);
        }
    }

    private static void WriteUInt32BigEndian(Stream stream, uint value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }
}
