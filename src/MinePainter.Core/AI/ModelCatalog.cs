namespace MinePainter.Core.AI;

/// <summary>
/// 一個可以在 App 內下載的去背模型。
/// </summary>
/// <param name="FileName">存進模型資料夾的檔名。命名要讓 <see cref="OnnxModelInfo.Preset"/> 認得出前處理。</param>
/// <param name="Title">給使用者看的名稱。</param>
/// <param name="Url">下載位置。</param>
/// <param name="SizeBytes">檔案大小（下載前先確認硬碟夠、下載後對得起來）。</param>
/// <param name="Md5">rembg 官方公布的 MD5，用來確認下載沒有壞掉。</param>
/// <param name="Strength">擅長什麼。</param>
/// <param name="Speed">速度。</param>
/// <param name="Memory">記憶體需求。</param>
/// <param name="Recommended">不熟模型的人該選這個。</param>
public sealed record ModelCatalogEntry(
    string FileName,
    string Title,
    string Url,
    long SizeBytes,
    string Md5,
    string Strength,
    string Speed,
    string Memory,
    bool Recommended = false)
{
    /// <summary>檔案大小的可讀寫法。</summary>
    public string SizeText => SizeBytes >= 1L << 30
        ? $"{SizeBytes / (double)(1L << 30):0.0} GB"
        : $"{SizeBytes / (double)(1L << 20):0} MB";
}

/// <summary>
/// 可下載的模型清單。
///
/// 全部取自 rembg 的官方發佈（github.com/danielgatis/rembg release v0.0.0），MD5 也是 rembg
/// 原始碼裡公布的值——本機既有的 u2netp／isnet-general-use／BiRefNet-lite 三個檔案實測與這些值相符。
/// 只收「整張圖找主體」這類直接能用的模型；SAM、vitmatte 那些需要另一套提示／trimap 流程，不列。
/// </summary>
public static class ModelCatalog
{
    private const string Rembg = "https://github.com/danielgatis/rembg/releases/download/v0.0.0/";

    public static IReadOnlyList<ModelCatalogEntry> Entries { get; } =
    [
        new("isnet-general-use.onnx", "ISNet 通用", Rembg + "isnet-general-use.onnx",
            178_648_008, "fc16ebd8b0c10d971d3513d564d01e29",
            Strength: "各種題材都穩，邊緣乾淨，一般照片首選",
            Speed: "中等（1024 解析度，顯示卡上約數秒）",
            Memory: "低，一般顯示卡都跑得動",
            Recommended: true),

        new("birefnet-general-lite.onnx", "BiRefNet 通用（精簡版）", Rembg + "BiRefNet-general-bb_swin_v1_tiny-epoch_232.onnx",
            224_005_088, "4fab47adc4ff364be1713e97b7e66334",
            Strength: "品質最好，髮絲、細碎邊緣明顯優於其他模型",
            Speed: "慢（多半只能用 CPU，1920×1080 約 15 秒）",
            Memory: "很高：CPU 約 6.3 GB；顯示卡要 16 GB 以上才裝得下，8 GB 的卡會自動改用 CPU"),

        new("isnet-anime.onnx", "ISNet 動漫", Rembg + "isnet-anime.onnx",
            176_069_933, "6f184e756bb3bd901c8849220a83e38e",
            Strength: "動漫、插畫、平塗風格；真實照片請改用通用模型",
            Speed: "中等（同 ISNet 通用）",
            Memory: "低"),

        new("u2net.onnx", "U²-Net 通用", Rembg + "u2net.onnx",
            175_997_641, "60024c5c889badc19c04ad937298a77b",
            Strength: "老牌通用模型，主體明確的照片很可靠；細節不如 ISNet",
            Speed: "快（320 解析度）",
            Memory: "低"),

        new("u2net_human_seg.onnx", "U²-Net 人像", Rembg + "u2net_human_seg.onnx",
            175_997_641, "c09ddc2e0104f800e3e1bb4652583d1f",
            Strength: "只認人：人物照很準，其他題材會失準",
            Speed: "快（320 解析度）",
            Memory: "低"),

        new("silueta.onnx", "Silueta", Rembg + "silueta.onnx",
            44_173_029, "55e59e0d8062d2f5d013f4725ee84782",
            Strength: "U²-Net 的瘦身版，品質接近但檔案小很多",
            Speed: "快（320 解析度）",
            Memory: "低"),

        new("u2netp.onnx", "U²-Net 輕量", Rembg + "u2netp.onnx",
            4_574_861, "8e83ca70e441ab06c318d82300c84806",
            Strength: "最小最快，適合先試跑或老機器；邊緣較粗",
            Speed: "最快",
            Memory: "極低"),
    ];

    /// <summary>依檔名找清單裡的說明（使用者自己放的模型找不到，回 null）。</summary>
    public static ModelCatalogEntry? Find(string fileName) =>
        Entries.FirstOrDefault(e => string.Equals(e.FileName, fileName, StringComparison.OrdinalIgnoreCase));
}
