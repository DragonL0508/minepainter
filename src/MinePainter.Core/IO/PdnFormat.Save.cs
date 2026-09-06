using System.IO.Compression;
using System.Text;
using MinePainter.Core.Documents;
using SkiaSharp;

namespace MinePainter.Core.IO;

/// <summary>
/// 匯出成 paint.net 專案檔 —— 整份文件合成後寫成<b>單一圖層</b>。
/// paint.net 沒有群組、可編輯文字、效果堆疊，逐層搬過去只會得到一堆烙死的像素；
/// 單層最不會出錯，也是使用者「拿回 paint.net 繼續修」最常見的用法。快速模式以輸出解析度合成。
///
/// 物件圖照 paint.net 5.1（5.112）自己存出來的樣子逐欄位寫（類別名、組件名、成員順序一個都不能差，
/// 差一個 paint.net 就當檔案損毀）：Document → LayerList → BitmapLayer → Surface → MemoryBlock（延後像素），
/// 像素段是 256 KiB 一塊、每塊各自 gzip（格式版本 0）。XML 標頭帶縮圖，檔案總管與開啟對話框會用。
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

    /// <summary><paramref name="warnings"/>：多層／文字／效果被合併成單一圖層時提示一次。</summary>
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

        bool flattened;
        lock (doc.SyncRoot)
        {
            var nodes = doc.Descendants().ToList();
            flattened = nodes.Count > 1 || nodes.Any(n => n.HasEffects || n is Layers.RasterLayer { HasElements: true });
        }
        if (flattened) notes.Add("paint.net 沒有群組、可編輯文字與效果堆疊，已合併成單一圖層。");

        using var composite = OutputRender.Render(doc,
            progress == null ? null : new Progress<double>(v => progress.Report(v * 0.6)));
        progress?.Report(0.6);

        var width = composite.Width;
        var height = composite.Height;
        var bgra = ReadStraightBgra(composite);

        var body = new MemoryStream();
        WriteObjectGraph(body, width, height, "背景", bgra.Length);
        WriteDeferredBlock(body, bgra);
        progress?.Report(0.9);

        var header = Encoding.UTF8.GetBytes(BuildXmlHeader(composite, width, height));
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

    /// <summary>合成影像（預乘）→ paint.net 的 BGRA 直通 alpha。</summary>
    private static unsafe byte[] ReadStraightBgra(SKImage image)
    {
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var bgra = new byte[image.Width * image.Height * 4];
        fixed (byte* ptr = bgra)
        {
            if (!image.ReadPixels(info, (IntPtr)ptr, image.Width * 4, 0, 0))
                throw new InvalidOperationException(".pdn 合成影像讀取失敗。");
        }
        return bgra;
    }

    /// <summary>XML 標頭：尺寸、圖層數、版本，加一張最長邊 256 的 PNG 縮圖（開啟對話框與檔案總管用）。</summary>
    private static string BuildXmlHeader(SKImage composite, int width, int height)
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
        return $"<pdnImage width=\"{width}\" height=\"{height}\" layers=\"1\" savedWithVersion=\"{SavedWithVersion}\">" +
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
    /// 照 paint.net 5.1 存出來的順序與物件編號寫：#1 Document、#3 LayerList、#4 Version、#5 中繼資料陣列、
    /// #7 圖層陣列、#20 BitmapLayer、#23 BitmapLayerProperties、#24 Surface、#25 LayerProperties、
    /// #29 NormalBlendOp、#30 MemoryBlock、#32 空的中繼資料陣列。PaintDotNet.Core 組件要在 Surface 第一次被提到之前宣告。
    /// </summary>
    private static void WriteObjectGraph(Stream stream, int width, int height, string layerName, long pixelBytes)
    {
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        w.Write(RecordHeader); w.Write(1); w.Write(-1); w.Write(1); w.Write(0);
        w.Write(RecordLibrary); w.Write(DataLib); w.Write(DataAssembly);

        WriteClass(w, 1, "PaintDotNet.Document", DataLib,
        [
            Prim("isDisposed", PrimBoolean),
            Cls("layers", "PaintDotNet.LayerList", DataLib),
            Prim("width", PrimInt32),
            Prim("height", PrimInt32),
            Sys("savedWith", "System.Version"),
            Sys("userMetadataItems", KeyValuePairType + "[]"),
        ]);
        w.Write(false);
        Ref(w, 3);
        w.Write(width);
        w.Write(height);
        Ref(w, 4);
        Ref(w, 5);

        WriteClass(w, 3, "PaintDotNet.LayerList", DataLib,
        [
            Cls("parent", "PaintDotNet.Document", DataLib),
            ObjArray("ArrayList+_items"),
            Prim("ArrayList+_size", PrimInt32),
            Prim("ArrayList+_version", PrimInt32),
        ]);
        Ref(w, 1);
        Ref(w, 7);
        w.Write(1);
        w.Write(4);

        WriteSystemClass(w, 4, "System.Version",
            [Prim("_Major", PrimInt32), Prim("_Minor", PrimInt32), Prim("_Build", PrimInt32), Prim("_Revision", PrimInt32)]);
        w.Write(5); w.Write(112); w.Write(9563); w.Write(32325);

        WriteEmptyMetadataArray(w, 5);

        // ArrayList 的容量陣列：4 格，1 個圖層，其餘 null
        w.Write(RecordObjectArray); w.Write(7); w.Write(4);
        Ref(w, 20);
        w.Write(RecordNullMultiple256); w.Write((byte)3);

        w.Write(RecordLibrary); w.Write(CoreLib); w.Write(CoreAssembly);

        WriteClass(w, 20, "PaintDotNet.BitmapLayer", DataLib,
        [
            Cls("properties", "PaintDotNet.BitmapLayer+BitmapLayerProperties", DataLib),
            Cls("surface", "PaintDotNet.Surface", CoreLib),
            Prim("Layer+isDisposed", PrimBoolean),
            Prim("Layer+width", PrimInt32),
            Prim("Layer+height", PrimInt32),
            Cls("Layer+properties", "PaintDotNet.Layer+LayerProperties", DataLib),
        ]);
        Ref(w, 23);
        Ref(w, 24);
        w.Write(false);
        w.Write(width);
        w.Write(height);
        Ref(w, 25);

        WriteClass(w, 23, "PaintDotNet.BitmapLayer+BitmapLayerProperties", DataLib,
            [Cls("blendOp", "PaintDotNet.UserBlendOps+NormalBlendOp", DataLib)]);
        Ref(w, 29);

        WriteClass(w, 24, "PaintDotNet.Surface", CoreLib,
        [
            Prim("width", PrimInt32),
            Prim("height", PrimInt32),
            Prim("stride", PrimInt32),
            Cls("scan0", "PaintDotNet.MemoryBlock", CoreLib),
        ]);
        w.Write(width);
        w.Write(height);
        w.Write(width * 4);
        Ref(w, 30);

        WriteClass(w, 25, "PaintDotNet.Layer+LayerProperties", DataLib,
        [
            Str("name"),
            Sys("userMetadataItems", KeyValuePairType + "[]"),
            Prim("visible", PrimBoolean),
            Prim("isBackground", PrimBoolean),
            Prim("opacity", PrimByte),
            Cls("blendMode", "PaintDotNet.LayerBlendMode", DataLib),
        ]);
        w.Write(RecordString); w.Write(31); w.Write(layerName);
        Ref(w, 32);
        w.Write(true);
        w.Write(true);
        w.Write((byte)255);
        // 列舉是值型別，BinaryFormatter 會就地寫成負編號的類別
        WriteClass(w, -33, "PaintDotNet.LayerBlendMode", DataLib, [Prim("value__", PrimInt32)]);
        w.Write(0);

        WriteClass(w, 29, "PaintDotNet.UserBlendOps+NormalBlendOp", DataLib, []);

        WriteClass(w, 30, "PaintDotNet.MemoryBlock", CoreLib,
            [Prim("length64", PrimInt64), Prim("hasParent", PrimBoolean), Prim("deferred", PrimBoolean)]);
        w.Write(pixelBytes);
        w.Write(false);
        w.Write(true);

        WriteEmptyMetadataArray(w, 32);

        w.Write(RecordEnd);
    }

    private static void Ref(BinaryWriter w, int id)
    {
        w.Write(RecordReference);
        w.Write(id);
    }

    private static void WriteClass(BinaryWriter w, int objectId, string typeName, int library, Member[] members)
    {
        w.Write(RecordClass);
        WriteClassInfo(w, objectId, typeName, members);
        w.Write(library);
    }

    private static void WriteSystemClass(BinaryWriter w, int objectId, string typeName, Member[] members)
    {
        w.Write(RecordSystemClass);
        WriteClassInfo(w, objectId, typeName, members);
    }

    /// <summary>ClassInfo + MemberTypeInfo：先全部名稱、再全部型別、再全部附加資訊。</summary>
    private static void WriteClassInfo(BinaryWriter w, int objectId, string typeName, Member[] members)
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

    /// <summary>userMetadataItems：長度 0 的 KeyValuePair&lt;string,string&gt;[]（BinaryArray、單維、無下界）。</summary>
    private static void WriteEmptyMetadataArray(BinaryWriter w, int objectId)
    {
        w.Write(RecordBinaryArray);
        w.Write(objectId);
        w.Write((byte)0);       // BinaryArrayType.Single
        w.Write(1);             // rank
        w.Write(0);             // length
        w.Write(TypeSystemClass);
        w.Write(KeyValuePairType);
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
