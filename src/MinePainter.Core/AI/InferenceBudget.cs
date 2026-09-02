namespace MinePainter.Core.AI;

/// <summary>推論前決定好的執行計畫。</summary>
/// <param name="Provider">實際要用的提供者（可能跟使用者勾的不一樣）。</param>
/// <param name="BudgetBytes">這次推論允許增加的記憶體上限；超過就中止。</param>
/// <param name="Note">給使用者看的說明（例如「模型太大，改用 CPU」）；沒事回 null。</param>
public sealed record InferencePlan(InferenceProvider Provider, long BudgetBytes, string? Note);

/// <summary>
/// 「這台機器現在撐不撐得住」的判斷。
///
/// 起因：birefnet_lite 在 1024 解析度下，DirectML 實測要約 16 GB（4.9 GB VRAM ＋ 11.5 GB 溢流到
/// 系統記憶體），8 GB 的筆電獨顯裝不下，硬跑會把整台機器拖進 swap 而當機；同一個模型走 CPU
/// 只要 6.3 GB。而模型檔案大小完全無法預測需求（isnet 178 MB 只要 0.5 GB），所以不猜：
/// 沒量過的模型一律先走 CPU（記憶體行為可預測、可中止），量到實測值後才決定能不能上 GPU。
/// </summary>
public static class InferenceBudget
{
    /// <summary>永遠要留給作業系統的實體記憶體；剩下比這少就中止推論。</summary>
    public const long SafetyFloorBytes = 1L << 30; // 1 GB

    /// <summary>沒有實測值時，至少要有這麼多可用記憶體才敢試跑。</summary>
    public const long MinimumProbeBytes = 2L << 30; // 2 GB

    /// <summary>已知成本要留的餘裕（同一個模型換張圖片用量會有些微差異）。</summary>
    private const double CostMargin = 1.2;

    /// <summary>機器目前的記憶體狀況。</summary>
    /// <param name="AvailableRam">可用實體記憶體；0 = 查不到（非 Windows）。</param>
    /// <param name="AvailableVideoMemory">首選顯示卡目前可用的專屬 VRAM。</param>
    /// <param name="HasGpu">有沒有可用的（非軟體）顯示卡。</param>
    public sealed record MachineMemory(long AvailableRam, long AvailableVideoMemory, bool HasGpu);

    /// <summary>量測機器狀態的方式（測試可替換）。</summary>
    public static Func<MachineMemory> ProbeMachine { get; set; } = DefaultProbe;

    private static MachineMemory DefaultProbe()
    {
        var gpu = SystemMemory.PreferredGpu();
        return new MachineMemory((long)SystemMemory.AvailablePhysicalBytes,
            (long)(gpu?.AvailableVideoMemory ?? 0), gpu != null);
    }

    /// <summary>
    /// 給 UI 用的一句話說明：這個模型現在會怎麼跑、跑不跑得動。不丟例外。
    /// </summary>
    public static string Describe(OnnxModelInfo model, int size, bool wantGpu)
    {
        try
        {
            var plan = Plan(model, size, wantGpu);
            var where = plan.Provider == InferenceProvider.DirectMl ? "GPU" : "CPU";
            var cost = ModelCostStore.Get(model, plan.Provider, size);
            var measured = cost is { Failed: false } ? $"，實測約 {Gb(cost.PeakBytes)}" : "";
            return plan.Note ?? $"將以 {where} 執行{measured}。";
        }
        catch (InsufficientMemoryException e) { return e.Message; }
    }

    /// <summary>
    /// 決定用哪個提供者、給多少記憶體預算。
    /// 記憶體不足以任何方式完成時丟 <see cref="InsufficientMemoryException"/>，訊息可直接顯示給使用者。
    /// </summary>
    public static InferencePlan Plan(OnnxModelInfo model, int size, bool wantGpu)
    {
        var machine = ProbeMachine();
        var available = machine.AvailableRam;
        // 查不到可用記憶體（非 Windows）：不擋，但仍走「先 CPU 後 GPU」的保守選擇。
        var unknownRam = available <= 0;

        var cpuCost = ModelCostStore.Get(model, InferenceProvider.Cpu, size);
        var dmlCost = ModelCostStore.Get(model, InferenceProvider.DirectMl, size);
        var hasGpu = wantGpu && machine.HasGpu;

        string? note = null;
        var provider = InferenceProvider.Cpu;

        if (hasGpu)
        {
            if (dmlCost is { Failed: true })
                note = $"{model.Name} 上次在 GPU 上因記憶體不足被中止，改用 CPU。";
            else if (dmlCost != null)
                provider = InferenceProvider.DirectMl; // 成功跑過，照舊
            else if (cpuCost == null)
                note = $"第一次使用 {model.Name}：先用 CPU 量它要多少記憶體，之後才會自動決定能不能用 GPU。";
            else if (FitsInVideoMemory(cpuCost, machine.AvailableVideoMemory))
                provider = InferenceProvider.DirectMl;
            else
                note = $"{model.Name} 需要約 {Gb(cpuCost.PeakBytes)}，超過顯示卡可用的 {Gb(machine.AvailableVideoMemory)}；改用 CPU（較慢但穩）。";
        }

        var cost = provider == InferenceProvider.DirectMl ? dmlCost : cpuCost;

        // 已知成本：不夠就先退 GPU→CPU，再不行就明講不跑。
        // 曾經被中止的組合只知道「至少要比當時的峰值多」，所以門檻抓兩倍——記憶體變寬裕了才讓它再試。
        if (cost != null && !unknownRam)
        {
            var need = (long)(cost.PeakBytes * (cost.Failed ? 2.0 : CostMargin)) + SafetyFloorBytes;
            if (available < need && provider == InferenceProvider.DirectMl && cpuCost != null)
            {
                provider = InferenceProvider.Cpu;
                cost = cpuCost;
                need = (long)(cpuCost.PeakBytes * CostMargin) + SafetyFloorBytes;
                note = $"顯示卡可用記憶體不足，改用 CPU。";
            }
            if (available < need)
                throw new InsufficientMemoryException(
                    (cost.Failed
                        ? $"記憶體不足，沒有開始去背：{model.Name} 上次跑到 {Gb(cost.PeakBytes)} 就因記憶體不足被中止，"
                        : $"記憶體不足，沒有開始去背：{model.Name} 需要約 {Gb(cost.PeakBytes)}，") +
                    $"目前可用 {Gb(available)}。請先關掉一些程式，或改用較輕的模型（例如 isnet-general-use）。");
        }

        // 沒量過的模型：可用記憶體太少就別冒險試跑。
        if (cost == null && !unknownRam && available < MinimumProbeBytes)
            throw new InsufficientMemoryException(
                $"記憶體不足，沒有開始去背：目前可用 {Gb(available)}，" +
                $"還沒量過 {model.Name} 需要多少，至少要有 {Gb(MinimumProbeBytes)} 才敢試跑。請先關掉一些程式。");

        var budget = unknownRam
            ? long.MaxValue
            : Math.Max(available - SafetyFloorBytes, 0);
        // 已知會成功的成本才用來收緊預算；被中止過的峰值不是真正的需求，收緊只會再中止一次。
        if (cost is { Failed: false })
            budget = Math.Min(budget, (long)(cost.PeakBytes * CostMargin) + (256L << 20));

        return new InferencePlan(provider, budget, note);
    }

    /// <summary>
    /// CPU 實測值當作模型的「規模」下限來判斷它裝不裝得進 VRAM。
    /// 抓 1.5 倍是因為 DirectML 的中介緩衝比 CPU 版多，再乘 0.7 是不要把 VRAM 用到見底
    /// （桌面合成、其他程式也要用；用到見底就會溢流到系統記憶體，那正是要避免的情況）。
    /// </summary>
    private static bool FitsInVideoMemory(ModelCost cpuCost, long availableVideoMemory) =>
        cpuCost.PeakBytes * 1.5 < availableVideoMemory * 0.7;

    private static string Gb(long bytes) => $"{bytes / (double)(1L << 30):0.0} GB";
}
