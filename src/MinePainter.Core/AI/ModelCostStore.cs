using System.Text.Json;

namespace MinePainter.Core.AI;

/// <summary>執行提供者：同一個模型在 CPU 與 DirectML 上的記憶體需求可以差好幾倍，要分開記。</summary>
public enum InferenceProvider
{
    Cpu,
    DirectMl,
}

/// <summary>一個模型在某個提供者上實測到的記憶體峰值。</summary>
/// <param name="PeakBytes">推論期間本行程增加的記憶體峰值（含 DirectML 溢流到系統記憶體的部分）。</param>
/// <param name="Failed">曾經因為記憶體不夠被中止：以後別再用這個組合。</param>
public sealed record ModelCost(long PeakBytes, bool Failed);

/// <summary>
/// 模型的記憶體成本表。
///
/// 檔案大小完全無法預測記憶體需求（實測：isnet-general-use 178 MB 只要 0.5 GB，
/// birefnet_lite 224 MB 要 6.3 GB），所以不猜——第一次跑完把實測峰值記下來，
/// 之後就用實測值決定「這台機器撐不撐得住」以及「能不能上 GPU」。
/// 存在使用者設定資料夾，跨啟動保留。
/// </summary>
public static class ModelCostStore
{
    private static readonly object Gate = new();
    private static Dictionary<string, ModelCost>? _entries;

    /// <summary>成本表檔案位置（測試可覆寫）。</summary>
    public static string FilePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MinePainter", "model-cost.json");

    /// <summary>查某個模型／提供者／輸入尺寸的實測成本；沒跑過回 null。</summary>
    public static ModelCost? Get(OnnxModelInfo model, InferenceProvider provider, int size)
    {
        lock (Gate)
        {
            Load();
            return _entries!.TryGetValue(Key(model, provider, size), out var cost) ? cost : null;
        }
    }

    /// <summary>記下一次實測結果。失敗（記憶體不夠被中止）也要記，否則下次還會再撞一次。</summary>
    public static void Record(OnnxModelInfo model, InferenceProvider provider, int size, long peakBytes, bool failed)
    {
        lock (Gate)
        {
            Load();
            var key = Key(model, provider, size);
            // 峰值取歷來最大：同一個模型在不同圖片上的用量會有些微差異，寧可高估。
            if (!failed && _entries!.TryGetValue(key, out var old) && !old.Failed && old.PeakBytes > peakBytes)
                peakBytes = old.PeakBytes;
            _entries![key] = new ModelCost(peakBytes, failed);
            Save();
        }
    }

    /// <summary>忘掉所有實測值（換顯卡、換驅動後可能想重測）。</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            _entries = new Dictionary<string, ModelCost>(StringComparer.Ordinal);
            Save();
        }
    }

    // 檔名可能被改，所以 key 帶檔案大小：換了內容就當作不同模型重新量。
    private static string Key(OnnxModelInfo model, InferenceProvider provider, int size)
    {
        long length = 0;
        try { length = new FileInfo(model.Path).Length; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return $"{Path.GetFileName(model.Path)}|{length}|{provider}|{size}";
    }

    private static void Load()
    {
        if (_entries != null) return;
        _entries = new Dictionary<string, ModelCost>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(FilePath)) return;
            var parsed = JsonSerializer.Deserialize<Dictionary<string, ModelCost>>(File.ReadAllText(FilePath));
            if (parsed != null) _entries = new Dictionary<string, ModelCost>(parsed, StringComparer.Ordinal);
        }
        catch (JsonException) { /* 壞掉的表就當作空的重新量 */ }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_entries));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
