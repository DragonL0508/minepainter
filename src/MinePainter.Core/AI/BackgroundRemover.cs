using Microsoft.ML.OnnxRuntime;
using SkiaSharp;

namespace MinePainter.Core.AI;

/// <summary>
/// 可用的去背模型（ONNX 檔）。名稱 = 檔名（不含副檔名），依名稱排序。
/// 模型不隨 app 附帶（動輒上百 MB）：使用者把 .onnx 放進模型資料夾即可。
/// </summary>
public sealed record OnnxModelInfo(string Name, string Path)
{
    /// <summary>
    /// 依檔名推斷前處理：各家 salient-object / matting 模型的輸入尺寸與正規化不同。
    /// 認不出來的一律當 ImageNet 正規化、1024（RMBG／BiRefNet 系的慣例；動態尺寸模型也吃得下）。
    /// </summary>
    public (int Size, float[] Mean, float[] Std, bool MinMax) Preset
    {
        get
        {
            var n = Name.ToLowerInvariant();
            if (n.Contains("u2net") || n.Contains("silueta")) return (320, ImageNetMean, ImageNetStd, true);
            if (n.Contains("isnet") || n.Contains("dis")) return (1024, [0.5f, 0.5f, 0.5f], [1f, 1f, 1f], false);
            if (n.Contains("modnet")) return (512, [0.5f, 0.5f, 0.5f], [0.5f, 0.5f, 0.5f], false);
            return (1024, ImageNetMean, ImageNetStd, false); // rmbg / birefnet / 其他
        }
    }

    private static readonly float[] ImageNetMean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] ImageNetStd = [0.229f, 0.224f, 0.225f];
}

/// <summary>模型資料夾掃描 + InferenceSession 快取（載入 100MB+ 的模型要好幾秒，不能每次重開）。</summary>
public static class OnnxModels
{
    /// <summary>會被掃描的資料夾（App 啟動時設定；不存在的略過）。</summary>
    public static List<string> ModelDirectories { get; } = new();

    private static readonly object Gate = new();
    private static (string Path, bool Gpu, InferenceSession Session)? _cached;

    public static IReadOnlyList<OnnxModelInfo> Scan()
    {
        var list = new List<OnnxModelInfo>();
        foreach (var dir in ModelDirectories)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.onnx"))
                list.Add(new OnnxModelInfo(System.IO.Path.GetFileNameWithoutExtension(file), file));
        }
        return list
            .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>取得（或建立）模型 session。GPU = DirectML；失敗時退回 CPU。</summary>
    public static InferenceSession GetSession(string path, bool gpu)
    {
        lock (Gate)
        {
            if (_cached is { } c && c.Path == path && c.Gpu == gpu) return c.Session;
            _cached?.Session.Dispose();
            _cached = null;

            var session = Create(path, gpu);
            _cached = (path, gpu, session);
            return session;
        }
    }

    /// <summary>丟掉快取的 session：推論因記憶體不足失敗後要放掉 VRAM／系統記憶體，否則下一次更擠。</summary>
    public static void DropCache()
    {
        lock (Gate)
        {
            _cached?.Session.Dispose();
            _cached = null;
        }
    }

    private static InferenceSession Create(string path, bool gpu)
    {
        if (gpu)
        {
            // 筆電常見雙顯卡：DirectML 的裝置 0 多半是內顯（VRAM 只有幾百 MB，大模型會 OOM），
            // 先要求「高效能」偏好挑獨顯；舊版 runtime 不認這組選項時再退回裝置 0。
            try
            {
                var opts = new SessionOptions { LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_FATAL };
                opts.AppendExecutionProvider("DML", new Dictionary<string, string>
                {
                    ["performance_preference"] = "high_performance",
                    ["device_filter"] = "gpu",
                });
                return new InferenceSession(path, opts);
            }
            catch
            {
                // 不支援選項或沒有獨顯：往下試裝置 0
            }
            try
            {
                var opts = new SessionOptions { LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_FATAL };
                opts.AppendExecutionProvider_DML(0);
                return new InferenceSession(path, opts);
            }
            catch
            {
                // 沒有可用的 DirectML 裝置／驅動：退回 CPU
            }
        }
        var cpu = new SessionOptions { LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_FATAL };
        return new InferenceSession(path, cpu);
    }
}

/// <summary>
/// AI 去背的推論與遮罩後處理。做法與 paint.net 的 Background Remover 外掛相同：
/// 縮到模型尺寸 → 正規化 → 推論 → 遮罩縮回原尺寸。
/// 模型只看得到 1024（甚至 320）解析度，直接放大的遮罩邊緣是糊的；
/// <see cref="GuidedFilter"/> 用原圖（全解析度）當引導把遮罩邊緣重新貼回真實的像素邊緣。
/// </summary>
public static class BackgroundRemover
{
    /// <summary>
    /// 推論一次只跑一個：同一個 session 被多執行緒同時 Run 曾在原生層 AccessViolation；
    /// 推論本身已吃滿所有核心，序列化沒有效能損失。
    /// </summary>
    private static readonly object InferGate = new();

    /// <summary>
    /// 跑模型：回傳與來源同尺寸的 8-bit 前景遮罩（0..255；尚未乘上來源 alpha）。
    /// src 為 premul BGRA。
    /// </summary>
    public static byte[] Infer(OnnxModelInfo model, uint[] src, int width, int height, bool gpu,
        CancellationToken ct)
    {
        lock (InferGate)
        {
            ct.ThrowIfCancellationRequested();
            // 開算前先確定這台機器撐得住：不夠就直接丟 InsufficientMemoryException，不要跑到一半把系統拖死
            var plan = InferenceBudget.Plan(model, model.Preset.Size, gpu);
            LastPlanNote = plan.Note;
            return InferCore(model, src, width, height, plan, ct);
        }
    }

    /// <summary>
    /// 最近一次推論的計畫說明（例如「模型太大，改用 CPU」）；沒有話說時是 null。
    /// 給 UI 在完成後顯示用。
    /// </summary>
    public static string? LastPlanNote { get; internal set; }

    private static unsafe byte[] InferCore(OnnxModelInfo model, uint[] src, int width, int height,
        InferencePlan plan, CancellationToken ct)
    {
        var (size, mean, std, minMax) = model.Preset;
        var session = OnnxModels.GetSession(model.Path, plan.Provider == InferenceProvider.DirectMl);

        // 模型若是固定尺寸就用它的
        var inputName = session.InputMetadata.Keys.First();
        var dims = session.InputMetadata[inputName].Dimensions;
        int inW = size, inH = size;
        if (dims.Length == 4 && dims[2] > 0 && dims[3] > 0) { inH = dims[2]; inW = dims[3]; }

        // 來源 → 直接色（透明處補黑）→ 縮到模型尺寸
        using var full = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        fixed (uint* p = src)
            Buffer.MemoryCopy(p, (void*)full.GetPixels(), (long)width * height * 4, (long)width * height * 4);
        using var scaled = full.Resize(new SKImageInfo(inW, inH, SKColorType.Bgra8888, SKAlphaType.Unpremul),
            SKFilterQuality.High) ?? throw new InvalidOperationException("縮放來源影像失敗");

        var input = new float[3 * inH * inW];
        var sp = (byte*)scaled.GetPixels();
        var plane = inH * inW;
        for (var i = 0; i < plane; i++)
        {
            var b = sp[i * 4] / 255f;
            var g = sp[i * 4 + 1] / 255f;
            var r = sp[i * 4 + 2] / 255f;
            input[i] = (r - mean[0]) / std[0];
            input[plane + i] = (g - mean[1]) / std[1];
            input[plane * 2 + i] = (b - mean[2]) / std[2];
        }

        ct.ThrowIfCancellationRequested();
        using var runOptions = new RunOptions();
        using var reg = ct.Register(() => runOptions.Terminate = true);
        using var inValue = OrtValue.CreateTensorValueFromMemory(input, [1, 3, inH, inW]);
        var outputName = session.OutputNames[0];

        float[] pred;
        int outH, outW;
        // 推論全程盯著記憶體，超標就中止（見 MemoryWatchdog）；跑完把實測峰值記進成本表，
        // 下一次就能在開算前判斷這台機器撐不撐得住、要不要用 GPU。
        using var watchdog = new MemoryWatchdog(plan.BudgetBytes, () => runOptions.Terminate = true);
        try
        {
            using var results = session.Run(runOptions, new Dictionary<string, OrtValue> { [inputName] = inValue }, [outputName]);
            ct.ThrowIfCancellationRequested();
            var outValue = results[0];
            var shape = outValue.GetTensorTypeAndShape().Shape;
            outH = (int)shape[^2];
            outW = (int)shape[^1];
            pred = outValue.GetTensorDataAsSpan<float>().Slice(0, outH * outW).ToArray();
        }
        catch (Exception e) when (watchdog.Tripped && e is not OperationCanceledException)
        {
            ModelCostStore.Record(model, plan.Provider, size, watchdog.PeakGrowthBytes, failed: true);
            OnnxModels.DropCache();
            throw new InsufficientMemoryException(
                $"記憶體不足，去背已中止：{model.Name} 在" +
                $"{(plan.Provider == InferenceProvider.DirectMl ? " GPU" : " CPU")}上用掉超過 " +
                $"{plan.BudgetBytes / (double)(1L << 30):0.0} GB。" +
                "已記下這個結果，下次會自動避開；請改用較輕的模型（例如 isnet-general-use）或先關掉一些程式。", e);
        }
        catch (OnnxRuntimeException) when (plan.Provider == InferenceProvider.DirectMl)
        {
            // DirectML 自己回報配置失敗（VRAM 不夠）：跟看門狗中止一樣要記下來，別再選 GPU
            ModelCostStore.Record(model, InferenceProvider.DirectMl, size, watchdog.PeakGrowthBytes, failed: true);
            OnnxModels.DropCache();
            throw;
        }
        watchdog.Dispose(); // 先停，峰值才含推論最後一刻
        ModelCostStore.Record(model, plan.Provider, size, watchdog.PeakGrowthBytes, failed: false);

        // 後處理：不在 0..1 → sigmoid；u2net 系 → min-max 正規化
        float min = float.MaxValue, max = float.MinValue;
        foreach (var v in pred) { if (v < min) min = v; if (v > max) max = v; }
        if (min < -0.01f || max > 1.01f)
        {
            for (var i = 0; i < pred.Length; i++) pred[i] = 1f / (1f + MathF.Exp(-pred[i]));
            min = float.MaxValue; max = float.MinValue;
            foreach (var v in pred) { if (v < min) min = v; if (v > max) max = v; }
        }
        if (minMax && max - min > 1e-6f)
            for (var i = 0; i < pred.Length; i++) pred[i] = (pred[i] - min) / (max - min);

        // 遮罩縮回原尺寸（灰階 8-bit 經 Skia 高品質縮放）
        using var small = new SKBitmap(new SKImageInfo(outW, outH, SKColorType.Gray8, SKAlphaType.Opaque));
        var gp = (byte*)small.GetPixels();
        for (var i = 0; i < pred.Length; i++)
            gp[i] = (byte)Math.Clamp(pred[i] * 255f + 0.5f, 0f, 255f);
        using var big = small.Resize(new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque),
            SKFilterQuality.High) ?? throw new InvalidOperationException("縮放遮罩失敗");

        var mask = new byte[width * height];
        var bp = (byte*)big.GetPixels();
        var rowBytes = big.RowBytes;
        for (var y = 0; y < height; y++)
            new ReadOnlySpan<byte>(bp + y * rowBytes, width).CopyTo(mask.AsSpan(y * width, width));
        return mask;
    }

    /// <summary>遮罩對比 0..100：以 0.5 為中心拉開；0 = 不變、100 ≈ 硬切。</summary>
    public static void ApplyContrast(byte[] mask, int contrast)
    {
        if (contrast <= 0) return;
        var k = 1f + contrast / 100f * 15f;
        var lo = 1f / (1f + MathF.Exp(k));
        var hi = 1f / (1f + MathF.Exp(-k));
        var lut = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            var a = i / 255f;
            a = 1f / (1f + MathF.Exp(-(a - 0.5f) * k * 2f));
            a = Math.Clamp((a - lo) / (hi - lo), 0f, 1f);
            lut[i] = (byte)(a * 255f + 0.5f);
        }
        for (var i = 0; i < mask.Length; i++) mask[i] = lut[mask[i]];
    }

    /// <summary>
    /// Trimap 填實：模型的機率圖在物件內部常只有 0.6～0.9（顏色近背景、紋理），引導濾波又會把
    /// 內部紋理漏進 alpha，結果「去背後物件內部變半透明」。這裡以 model 的 0.5 門檻切二值，
    /// 往內縮 band 的核心一律 255、往外擴 band 之外一律 0，只有邊界一圈保留 soft 的半透明值
    /// （髮絲、毛邊都在這一圈裡）。
    /// </summary>
    /// <param name="edgeContrast">
    /// 邊帶內的 S 曲線強度 0..100：模型在頭髮這類區域常整片給 0.6～0.8、背景給 0.05～0.15，
    /// 直接當 alpha 就是「大片微微透明」與外圈淡暈；把它們推向 1 / 0，只有真正的過渡像素（≈0.5）留著。
    /// </param>
    public static byte[] SolidifyCore(byte[] soft, byte[] model, int w, int h, int band, int edgeContrast = 60)
    {
        var bin = new byte[model.Length];
        for (var i = 0; i < bin.Length; i++) bin[i] = model[i] >= 128 ? (byte)255 : (byte)0;
        FillSmallHoles(bin, w, h, maxArea: band * band * 16); // 身體上的小破洞（模型不確定的高光、皺褶）
        var core = Shift(bin, w, h, -band);  // 內縮：離邊界 ≥ band 的內部
        var outer = Shift(bin, w, h, band);  // 外擴：離邊界 ≥ band 的外部為 0

        var lut = new byte[256];
        for (var i = 0; i < 256; i++) lut[i] = (byte)i;
        ApplyContrast(lut, edgeContrast);

        var result = new byte[soft.Length];
        for (var i = 0; i < result.Length; i++)
            result[i] = core[i] != 0 ? (byte)255 : outer[i] == 0 ? (byte)0 : lut[soft[i]];
        return result;
    }

    /// <summary>
    /// 把二值遮罩裡「被前景包住、面積 ≤ maxArea」的背景區填成前景。
    /// 從影像邊界對背景 flood fill，碰不到的背景就是洞。
    /// </summary>
    public static void FillSmallHoles(byte[] bin, int w, int h, int maxArea)
    {
        var label = new int[w * h]; // 0 = 未訪；1 = 外部背景；2.. = 洞編號
        var stack = new Stack<int>();
        void Flood(int start, int id, out int area)
        {
            area = 0;
            stack.Push(start);
            label[start] = id;
            while (stack.Count > 0)
            {
                var i = stack.Pop();
                area++;
                var x = i % w; var y = i / w;
                if (x > 0 && bin[i - 1] == 0 && label[i - 1] == 0) { label[i - 1] = id; stack.Push(i - 1); }
                if (x < w - 1 && bin[i + 1] == 0 && label[i + 1] == 0) { label[i + 1] = id; stack.Push(i + 1); }
                if (y > 0 && bin[i - w] == 0 && label[i - w] == 0) { label[i - w] = id; stack.Push(i - w); }
                if (y < h - 1 && bin[i + w] == 0 && label[i + w] == 0) { label[i + w] = id; stack.Push(i + w); }
            }
        }
        // 外部：所有邊界上的背景像素
        for (var x = 0; x < w; x++)
        {
            if (bin[x] == 0 && label[x] == 0) Flood(x, 1, out _);
            var b = (h - 1) * w + x;
            if (bin[b] == 0 && label[b] == 0) Flood(b, 1, out _);
        }
        for (var y = 0; y < h; y++)
        {
            var l = y * w; var r = y * w + w - 1;
            if (bin[l] == 0 && label[l] == 0) Flood(l, 1, out _);
            if (bin[r] == 0 && label[r] == 0) Flood(r, 1, out _);
        }
        // 其餘背景 = 洞
        var next = 2;
        var fill = new List<int>();
        for (var i = 0; i < bin.Length; i++)
        {
            if (bin[i] != 0 || label[i] != 0) continue;
            var id = next++;
            Flood(i, id, out var area);
            if (area <= maxArea) fill.Add(id);
        }
        if (fill.Count == 0) return;
        var set = new HashSet<int>(fill);
        for (var i = 0; i < bin.Length; i++)
            if (bin[i] == 0 && set.Contains(label[i])) bin[i] = 255;
    }

    /// <summary>以方框 min/max 濾波收縮（負）或擴張（正）遮罩；回傳新陣列。</summary>
    public static byte[] Shift(byte[] mask, int w, int h, int shift)
    {
        if (shift == 0) return mask;
        var r = Math.Abs(shift);
        var dilate = shift > 0;
        var tmp = new byte[mask.Length];
        var outp = new byte[mask.Length];
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                int v = dilate ? 0 : 255;
                for (var k = -r; k <= r; k++)
                {
                    var m = mask[y * w + Math.Clamp(x + k, 0, w - 1)];
                    v = dilate ? Math.Max(v, m) : Math.Min(v, m);
                }
                tmp[y * w + x] = (byte)v;
            }
        });
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                int v = dilate ? 0 : 255;
                for (var k = -r; k <= r; k++)
                {
                    var m = tmp[Math.Clamp(y + k, 0, h - 1) * w + x];
                    v = dilate ? Math.Max(v, m) : Math.Min(v, m);
                }
                outp[y * w + x] = (byte)v;
            }
        });
        return outp;
    }
}

/// <summary>
/// 快速引導濾波（He et al., Fast Guided Filter）：以高清原圖為引導、模型遮罩為輸入，
/// 輸出 q = a·I + b —— 係數 a、b 在縮小 s 倍的網格上算（記憶體與時間都小），
/// 套用時卻是逐個全解析度像素乘上原圖顏色，所以遮罩邊緣會貼著真實像素邊緣走：
/// 髮絲、毛邊在 1024 遮罩裡糊成一團，經此可拿回原圖的細節。彩色引導（3×3 協方差）比灰階更能分開
/// 亮度相近、色相不同的前景／背景。
/// </summary>
public static class GuidedFilter
{
    /// <summary>
    /// mask（0..255）依 src（premul BGRA）精修；回傳新遮罩。
    /// radius 為全解析度半徑（px），eps 為正則化（顏色以 0..1 計；越小越貼邊緣、越大越平滑）。
    /// </summary>
    public static byte[] Refine(byte[] mask, uint[] src, int width, int height, int radius = 16, float eps = 1e-3f,
        int iterations = 2, CancellationToken ct = default)
    {
        // 單次引導濾波是線性模型的平均，糊得比半徑寬的漸層會留一點殘餘；再跑一次就拉乾淨
        for (var i = 0; i < Math.Max(1, iterations); i++)
            mask = RefineOnce(mask, src, width, height, radius, eps, ct);
        return mask;
    }

    private static byte[] RefineOnce(byte[] mask, uint[] src, int width, int height, int radius, float eps,
        CancellationToken ct)
    {
        // 子取樣倍率：讓係數網格最長邊約 1024
        var s = Math.Max(1, (int)MathF.Ceiling(Math.Max(width, height) / 1024f));
        var sw = (width + s - 1) / s;
        var sh = (height + s - 1) / s;
        var n = sw * sh;
        var r = Math.Max(1, radius / s);

        // 引導 I（直接色 0..1）與輸入 p，先縮小（區塊平均）
        var Ir = new float[n]; var Ig = new float[n]; var Ib = new float[n]; var P = new float[n];
        var cnt = new int[n];
        for (var y = 0; y < height; y++)
        {
            var sy = y / s;
            for (var x = 0; x < width; x++)
            {
                var i = sy * sw + x / s;
                Unpremul(src[y * width + x], out var rr, out var gg, out var bb);
                Ir[i] += rr; Ig[i] += gg; Ib[i] += bb;
                P[i] += mask[y * width + x] / 255f;
                cnt[i]++;
            }
        }
        for (var i = 0; i < n; i++)
        {
            var c = Math.Max(1, cnt[i]);
            Ir[i] /= c; Ig[i] /= c; Ib[i] /= c; P[i] /= c;
        }
        ct.ThrowIfCancellationRequested();

        // 各種均值
        var mIr = Box(Ir, sw, sh, r); var mIg = Box(Ig, sw, sh, r); var mIb = Box(Ib, sw, sh, r);
        var mP = Box(P, sw, sh, r);
        var mIrP = Box(Mul(Ir, P), sw, sh, r); var mIgP = Box(Mul(Ig, P), sw, sh, r); var mIbP = Box(Mul(Ib, P), sw, sh, r);
        var mRR = Box(Mul(Ir, Ir), sw, sh, r); var mRG = Box(Mul(Ir, Ig), sw, sh, r); var mRB = Box(Mul(Ir, Ib), sw, sh, r);
        var mGG = Box(Mul(Ig, Ig), sw, sh, r); var mGB = Box(Mul(Ig, Ib), sw, sh, r); var mBB = Box(Mul(Ib, Ib), sw, sh, r);
        ct.ThrowIfCancellationRequested();

        // 每個像素解 3×3：a = (Σ + eps·I)^-1 · cov(I,p)
        var ar = new float[n]; var ag = new float[n]; var ab = new float[n]; var b = new float[n];
        for (var i = 0; i < n; i++)
        {
            var cRR = mRR[i] - mIr[i] * mIr[i] + eps;
            var cRG = mRG[i] - mIr[i] * mIg[i];
            var cRB = mRB[i] - mIr[i] * mIb[i];
            var cGG = mGG[i] - mIg[i] * mIg[i] + eps;
            var cGB = mGB[i] - mIg[i] * mIb[i];
            var cBB = mBB[i] - mIb[i] * mIb[i] + eps;
            var vR = mIrP[i] - mIr[i] * mP[i];
            var vG = mIgP[i] - mIg[i] * mP[i];
            var vB = mIbP[i] - mIb[i] * mP[i];

            // 對稱矩陣反矩陣（cofactor）
            var i00 = cGG * cBB - cGB * cGB;
            var i01 = cRB * cGB - cRG * cBB;
            var i02 = cRG * cGB - cRB * cGG;
            var i11 = cRR * cBB - cRB * cRB;
            var i12 = cRB * cRG - cRR * cGB;
            var i22 = cRR * cGG - cRG * cRG;
            var det = cRR * i00 + cRG * i01 + cRB * i02;
            if (Math.Abs(det) < 1e-12f) { ar[i] = ag[i] = ab[i] = 0f; b[i] = mP[i]; continue; }
            var inv = 1f / det;
            ar[i] = (i00 * vR + i01 * vG + i02 * vB) * inv;
            ag[i] = (i01 * vR + i11 * vG + i12 * vB) * inv;
            ab[i] = (i02 * vR + i12 * vG + i22 * vB) * inv;
            b[i] = mP[i] - ar[i] * mIr[i] - ag[i] * mIg[i] - ab[i] * mIb[i];
        }
        var mAr = Box(ar, sw, sh, r); var mAg = Box(ag, sw, sh, r); var mAb = Box(ab, sw, sh, r); var mB = Box(b, sw, sh, r);
        ct.ThrowIfCancellationRequested();

        // 全解析度套用：係數雙線性上採樣，乘上原圖顏色
        var outMask = new byte[width * height];
        Parallel.For(0, height, y =>
        {
            var fy = Math.Clamp((y + 0.5f) / s - 0.5f, 0f, sh - 1);
            var y0 = (int)fy; var y1 = Math.Min(y0 + 1, sh - 1); var ty = fy - y0;
            for (var x = 0; x < width; x++)
            {
                var fx = Math.Clamp((x + 0.5f) / s - 0.5f, 0f, sw - 1);
                var x0 = (int)fx; var x1 = Math.Min(x0 + 1, sw - 1); var tx = fx - x0;
                float L(float[] g) =>
                    (g[y0 * sw + x0] * (1 - tx) + g[y0 * sw + x1] * tx) * (1 - ty) +
                    (g[y1 * sw + x0] * (1 - tx) + g[y1 * sw + x1] * tx) * ty;
                Unpremul(src[y * width + x], out var rr, out var gg, out var bb);
                var q = L(mAr) * rr + L(mAg) * gg + L(mAb) * bb + L(mB);
                outMask[y * width + x] = (byte)Math.Clamp(q * 255f + 0.5f, 0f, 255f);
            }
        });
        return outMask;
    }

    private static void Unpremul(uint p, out float r, out float g, out float b)
    {
        var a = (int)(p >> 24);
        if (a == 0) { r = g = b = 0f; return; }
        var inv = 255f / a / 255f;
        b = (p & 0xFF) * inv;
        g = ((p >> 8) & 0xFF) * inv;
        r = ((p >> 16) & 0xFF) * inv;
    }

    private static float[] Mul(float[] a, float[] b)
    {
        var o = new float[a.Length];
        for (var i = 0; i < o.Length; i++) o[i] = a[i] * b[i];
        return o;
    }

    /// <summary>方框均值（邊界以實際涵蓋的像素數正規化），分離式滑動視窗。</summary>
    internal static float[] Box(float[] src, int w, int h, int r)
    {
        var tmp = new float[src.Length];
        var outp = new float[src.Length];
        // 水平
        Parallel.For(0, h, y =>
        {
            var row = y * w;
            var sum = 0f;
            for (var x = 0; x <= Math.Min(r, w - 1); x++) sum += src[row + x];
            for (var x = 0; x < w; x++)
            {
                var lo = x - r; var hi = x + r;
                if (x > 0)
                {
                    if (hi < w) sum += src[row + hi];
                    if (lo - 1 >= 0) sum -= src[row + lo - 1];
                }
                var count = Math.Min(hi, w - 1) - Math.Max(lo, 0) + 1;
                tmp[row + x] = sum / count;
            }
        });
        // 垂直
        Parallel.For(0, w, x =>
        {
            var sum = 0f;
            for (var y = 0; y <= Math.Min(r, h - 1); y++) sum += tmp[y * w + x];
            for (var y = 0; y < h; y++)
            {
                var lo = y - r; var hi = y + r;
                if (y > 0)
                {
                    if (hi < h) sum += tmp[hi * w + x];
                    if (lo - 1 >= 0) sum -= tmp[(lo - 1) * w + x];
                }
                var count = Math.Min(hi, h - 1) - Math.Max(lo, 0) + 1;
                outp[y * w + x] = sum / count;
            }
        });
        return outp;
    }
}
