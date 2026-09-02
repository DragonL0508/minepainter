using Microsoft.ML.OnnxRuntime;
using SkiaSharp;
using static MinePainter.Core.Effects.EffectMath;

namespace MinePainter.Core.Effects;

/// <summary>
/// 可用的去背模型（ONNX 檔）。名稱 = 檔名（不含副檔名），依名稱排序，效果以索引參照。
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
            if (n.Contains("u2net")) return (320, ImageNetMean, ImageNetStd, true);
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

    private static InferenceSession Create(string path, bool gpu)
    {
        if (gpu)
        {
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
/// AI 去背：用 salient-object 分割／matting 模型（U²-Net、ISNet、RMBG、BiRefNet…的 ONNX）
/// 估計整張圖層的前景 alpha，乘到原本的 alpha 上。做法與 paint.net 的 Background Remover 外掛相同：
/// 縮到模型尺寸 → 正規化 → 推論 → 遮罩縮回原尺寸 → 套 alpha。
///
/// 推論很貴（CPU 上 ISNet 約數秒），所以以來源內容的雜湊快取最後一次的遮罩：
/// 效果堆疊重繪、對話框改參數、縮圖都不會重跑模型。
/// </summary>
public sealed record BackgroundRemovalEffect : IEffect
{
    /// <summary>模型索引（對應 <see cref="OnnxModels.Scan"/> 的排序）。</summary>
    public int ModelIndex { get; init; }
    public bool UseGpu { get; init; }
    /// <summary>遮罩對比 0..100：0 = 模型原始的軟遮罩；越高越接近硬切（去掉半透明的髒邊）。</summary>
    public int Contrast { get; init; } = 0;
    /// <summary>邊緣收縮／擴張（px，負 = 收縮）。收縮可吃掉殘留的背景色邊。</summary>
    public int Shift { get; init; } = 0;

    public string Name => "AI 去背";
    public string Category => "相片";
    public int SourceMargin => EffectContext.WholeLayer;
    public bool IsPositionIndependent => false;

    private readonly IReadOnlyList<OnnxModelInfo> _models;
    private readonly ParamDef[] _params;

    public BackgroundRemovalEffect() : this(OnnxModels.Scan())
    {
    }

    /// <summary>以明確的模型清單建立（測試／不想掃資料夾時）。</summary>
    public BackgroundRemovalEffect(IReadOnlyList<OnnxModelInfo> models)
    {
        _models = models;
        var names = _models.Count == 0
            ? ["（模型資料夾裡沒有 .onnx）"]
            : _models.Select(m => m.Name).ToArray();
        _params =
        [
            new ChoiceParam("model", "模型", names, o => ((BackgroundRemovalEffect)o).ModelIndex,
                (o, v) => ((BackgroundRemovalEffect)o) with { ModelIndex = v }),
            new BoolParam("gpu", "使用 GPU（DirectML）", o => ((BackgroundRemovalEffect)o).UseGpu,
                (o, v) => ((BackgroundRemovalEffect)o) with { UseGpu = v }),
            new SliderParam("contrast", "遮罩對比", 0, 100, o => ((BackgroundRemovalEffect)o).Contrast,
                (o, v) => ((BackgroundRemovalEffect)o) with { Contrast = (int)v }, "%"),
            new SliderParam("shift", "邊緣收縮/擴張", -20, 20, o => ((BackgroundRemovalEffect)o).Shift,
                (o, v) => ((BackgroundRemovalEffect)o) with { Shift = (int)v }, "px"),
        ];
    }

    public IReadOnlyList<ParamDef> Parameters => _params;

    public IReadOnlyList<OnnxModelInfo> Models => _models;
    public OnnxModelInfo? SelectedModel =>
        _models.Count == 0 ? null : _models[Math.Clamp(ModelIndex, 0, _models.Count - 1)];

    // ---- 遮罩快取（單筆）----
    private static readonly object CacheGate = new();
    private static (string Model, int W, int H, ulong Hash, byte[] Mask)? _maskCache;

    public void Render(EffectContext ctx)
    {
        var model = SelectedModel;
        if (model == null)
        {
            PassThrough(ctx);
            return;
        }

        var mask = GetMask(model, ctx);
        var w = ctx.SrcWidth;

        // 對比：以 0.5 為中心拉開；收縮／擴張：對遮罩做距離門檻
        var k = 1f + Contrast / 100f * 15f; // 1..16
        byte[]? shifted = Shift != 0 ? ShiftMask(mask, w, ctx.SrcHeight, Shift) : null;
        var m = shifted ?? mask;

        ctx.ForRows(y =>
        {
            var sy = y + ctx.SrcOffsetY;
            for (var x = 0; x < ctx.Width; x++)
            {
                var sx = x + ctx.SrcOffsetX;
                var src = ctx.Src[sy * w + sx];
                var a = m[sy * w + sx] / 255f;
                if (k > 1f)
                {
                    a = 1f / (1f + MathF.Exp(-(a - 0.5f) * k * 2f));
                    // 讓 0 與 1 仍能到達端點
                    var lo = 1f / (1f + MathF.Exp(k));
                    var hi = 1f / (1f + MathF.Exp(-k));
                    a = Math.Clamp((a - lo) / (hi - lo), 0f, 1f);
                }
                var mul = (int)(a * 256f + 0.5f);
                ctx.Dst[y * ctx.Width + x] = mul >= 256 ? src : mul <= 0 ? 0 : Scale(src, mul);
            }
        });
    }

    private static void PassThrough(EffectContext ctx)
    {
        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
                ctx.Dst[y * ctx.Width + x] = ctx.SrcOrTransparent(x, y);
        });
    }

    /// <summary>premul 像素四通道整體乘上 mul/256。</summary>
    private static uint Scale(uint p, int mul)
    {
        var b = (int)(p & 0xFF) * mul >> 8;
        var g = (int)((p >> 8) & 0xFF) * mul >> 8;
        var r = (int)((p >> 16) & 0xFF) * mul >> 8;
        var a = (int)(p >> 24) * mul >> 8;
        return (uint)b | ((uint)g << 8) | ((uint)r << 16) | ((uint)a << 24);
    }

    private byte[] GetMask(OnnxModelInfo model, EffectContext ctx)
    {
        var hash = Hash(ctx.Src);
        lock (CacheGate)
        {
            if (_maskCache is { } c && c.Model == model.Path && c.W == ctx.SrcWidth && c.H == ctx.SrcHeight && c.Hash == hash)
                return c.Mask;
        }
        var mask = Infer(model, ctx.Src, ctx.SrcWidth, ctx.SrcHeight, UseGpu, ctx.Cancellation);
        lock (CacheGate) _maskCache = (model.Path, ctx.SrcWidth, ctx.SrcHeight, hash, mask);
        return mask;
    }

    private static ulong Hash(uint[] px)
    {
        var h = 14695981039346656037UL;
        foreach (var p in px)
        {
            h ^= p;
            h *= 1099511628211UL;
        }
        return h ^ (ulong)px.Length;
    }

    /// <summary>
    /// 推論一次只跑一個：同一個 session 被多執行緒同時 Run（效果堆疊快取 + 對話框預覽同時觸發）
    /// 曾在原生層 AccessViolation；推論本身已吃滿所有核心，序列化沒有效能損失。
    /// </summary>
    private static readonly object InferGate = new();

    /// <summary>跑模型：回傳與來源同尺寸的 8-bit 前景遮罩（已乘上來源 alpha）。</summary>
    public static byte[] Infer(OnnxModelInfo model, uint[] src, int width, int height, bool gpu,
        CancellationToken ct)
    {
        lock (InferGate)
        {
            ct.ThrowIfCancellationRequested();
            return InferCore(model, src, width, height, gpu, ct);
        }
    }

    private static unsafe byte[] InferCore(OnnxModelInfo model, uint[] src, int width, int height, bool gpu,
        CancellationToken ct)
    {
        var (size, mean, std, minMax) = model.Preset;
        var session = OnnxModels.GetSession(model.Path, gpu);

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
            SKFilterQuality.High);

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
        using (var results = session.Run(runOptions, new Dictionary<string, OrtValue> { [inputName] = inValue }, [outputName]))
        {
            ct.ThrowIfCancellationRequested();
            var outValue = results[0];
            var shape = outValue.GetTensorTypeAndShape().Shape;
            outH = (int)shape[^2];
            outW = (int)shape[^1];
            pred = outValue.GetTensorDataAsSpan<float>().Slice(0, outH * outW).ToArray();
        }

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
            SKFilterQuality.High);

        var mask = new byte[width * height];
        var bp = (byte*)big.GetPixels();
        for (var i = 0; i < mask.Length; i++)
        {
            var a = (int)(src[i] >> 24);
            mask[i] = a == 255 ? bp[i] : (byte)(bp[i] * a / 255);
        }
        return mask;
    }

    /// <summary>以方框 min/max 濾波收縮（負）或擴張（正）遮罩。</summary>
    private static byte[] ShiftMask(byte[] mask, int w, int h, int shift)
    {
        var r = Math.Abs(shift);
        var dilate = shift > 0;
        var tmp = new byte[mask.Length];
        var outp = new byte[mask.Length];
        // 水平
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            int v = dilate ? 0 : 255;
            for (var k = -r; k <= r; k++)
            {
                var xx = Math.Clamp(x + k, 0, w - 1);
                var m = mask[y * w + xx];
                v = dilate ? Math.Max(v, m) : Math.Min(v, m);
            }
            tmp[y * w + x] = (byte)v;
        }
        // 垂直
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            int v = dilate ? 0 : 255;
            for (var k = -r; k <= r; k++)
            {
                var yy = Math.Clamp(y + k, 0, h - 1);
                var m = tmp[yy * w + x];
                v = dilate ? Math.Max(v, m) : Math.Min(v, m);
            }
            outp[y * w + x] = (byte)v;
        }
        return outp;
    }
}
